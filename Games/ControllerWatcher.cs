using System.Runtime.InteropServices;

namespace CouchMode.Games;

/// <summary>One attached controller, as the settings list shows it.</summary>
/// <param name="Path">Windows device path. Not an identity — it varies per interface.</param>
/// <param name="Key">
/// Vendor, product and hardware instance. Every HID collection of one physical controller
/// shares this, which is what lets an input report be matched to the device it came from.
/// </param>
/// <param name="Arrived">
/// When Windows last saw this device turn up, or null if it would not say. Used to tell a pad
/// that was already plugged in from one plugged in while the app was starting.
/// </param>
/// <param name="Wireless">
/// Whether Windows reached this pad over Bluetooth.
///
/// Read from the device path, which names the bus. It is right about Bluetooth and about a pad on
/// a USB lead, and wrong about a proprietary dongle — an Xbox wireless adapter is itself a USB
/// device, so a pad behind one reads as wired. There is nothing in the path to say otherwise, so
/// the setting that uses this says "cable" rather than claiming to know about radios.
/// </param>
public sealed record ControllerDevice(string Id, string Name, string Path, string Key = "",
                                      DateTime? Arrived = null, bool Wireless = false)
{
    public override string ToString() => Name;
}

/// <summary>
/// Notices a game controller being switched on or off.
///
/// Devices are identified, not counted: each look enumerates what is present and diffs the set
/// of ids against the last look, so it knows exactly which pad arrived or left. That is what
/// makes it possible to let one particular controller start a session and ignore the rest.
///
/// Enumeration goes through SetupDi with DIGCF_PRESENT (see <see cref="Attached"/>), and
/// XInput is not used at all — it cannot see PlayStation or Nintendo pads, and the virtual
/// Xbox pad that DS4Windows-style tools create fools it into reporting a controller that is
/// never switched off.
///
/// Only device presence starts or ends a session. Input activity is never treated as intent in
/// either direction: a pad going quiet is not a disconnect, because a long cutscene looks
/// identical to a switched-off controller — and a pad that starts talking again is not a
/// connect, because a controller lying on a desk drifts, gets knocked, and sends heartbeats.
/// Reports are still received here, but only so button combinations can be read from them.
/// </summary>
public sealed class ControllerWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1500 };
    private readonly DeviceChangeWindow _deviceChanges;

    /// <summary>Devices Windows listed on the previous look, by id.</summary>
    private HashSet<string> _present = [];

    private bool _started;

    /// <summary>When this process began, in UTC, to compare device arrivals against.</summary>
    private static readonly DateTime AppStarted =
        System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime();

    /// <summary>
    /// How far before the app started a pad may have arrived and still count as arriving with it.
    ///
    /// Someone plugging a controller in and then launching from the desktop is doing the same
    /// thing as someone launching and then plugging in; only the order differs, and by a second
    /// or two. Small enough that a pad plugged in earlier and forgotten about does not qualify.
    /// </summary>
    private static readonly TimeSpan JustBeforeStart = TimeSpan.FromSeconds(6);

    /// <summary>
    /// How long after launch a call to Start still counts as the app's own initial one.
    ///
    /// Generous compared with the tolerance above because it covers the app getting as far as
    /// starting the watcher, not a person plugging something in. Past this, a start is something
    /// else asking for the watcher later on, and whatever is attached then is simply the state of
    /// the world rather than a set of arrivals.
    /// </summary>
    private static readonly TimeSpan InitialStart = TimeSpan.FromSeconds(20);

    /// <summary>Raw input device handles resolved to hardware keys once, since they never change.</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<IntPtr, string> _handleKeys = new();

    /// <summary>Raised whenever the set of attached controllers changes, for the settings list.</summary>
    public event Action? DevicesChanged;

    /// <summary>Set while the Controller page is showing, so choosing a pad cannot start a session.</summary>
    public bool Suspended { get; set; }

    private bool _reportsReleased;

    /// <summary>Whether the raw-input stream is currently released (so the display can sleep).</summary>
    public bool ReportsReleased => _reportsReleased;

    /// <summary>
    /// Stop taking the controller's raw-input stream. A pad that streams every frame even at rest —
    /// a DualSense does — keeps resetting the display idle timer just by being subscribed, so the
    /// screen never sleeps. Releasing the subscription lets it. Device-connect messages still come
    /// through, and <see cref="ResumeReports"/> puts it back.
    /// </summary>
    public void SuspendReports()
    {
        if (_reportsReleased) return;
        _reportsReleased = true;
        _deviceChanges.SetActive(false);
        Log.Info("Controller input released so the display can sleep; wake with mouse/keyboard to resume it.");
    }

    /// <summary>Take the controller's raw-input stream again, after it was released.</summary>
    public void ResumeReports()
    {
        if (!_reportsReleased) return;
        _reportsReleased = false;
        _deviceChanges.SetActive(true);
        Log.Info("Controller input resumed.");
    }

    /// <summary>Until when changes are ignored, for a machine that has just woken up.</summary>
    private DateTime _settlingUntil = DateTime.MinValue;

    /// <summary>
    /// Ignore controller changes for a moment, then carry on as if nothing happened.
    ///
    /// For waking from sleep. Windows re-enumerates USB on resume, so every attached pad is
    /// removed and added again — which is the same evidence as somebody switching one on, and
    /// there is nothing in the device change itself to tell the two apart. The context is what
    /// distinguishes them, and the only place that context exists is here.
    ///
    /// Rebaselining at the end is what makes it a pause rather than a delay: whatever is
    /// attached once the dust settles becomes the new normal, so nothing fires late either.
    /// </summary>
    public void Settle(TimeSpan quiet, string why)
    {
        _settlingUntil = DateTime.UtcNow + quiet;

        Log.Info($"{why}: ignoring controller changes for {quiet.TotalSeconds:0} seconds.");

        var timer = new System.Windows.Forms.Timer { Interval = (int)quiet.TotalMilliseconds };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();

            if (!_started) return;

            Rebaseline();
            Log.Info("Controllers settled; watching again.");
        };

        timer.Start();
    }

    /// <summary>
    /// Which controller may start a session. Empty means any of them.
    ///
    /// Identity is the vendor and product id, not the Windows device path: the path changes
    /// between USB and Bluetooth and between ports, so a pad chosen while plugged in would stop
    /// matching the moment it was used wirelessly. Vendor and product stay put.
    /// </summary>
    public string OnlyDeviceId { get; set; } = "";

    /// <summary>A controller came online.</summary>
    public event Action<ControllerDevice>? Connected;

    /// <summary>A controller went away.</summary>
    public event Action<ControllerDevice>? Disconnected;


    public ControllerWatcher()
    {
        _timer.Tick += (_, _) => Poll();

        // Windows announces device arrival, so the poll is only a safety net rather than the
        // means of detection. Bluetooth pads in particular can take a moment to enumerate, so
        // the announcement is followed by a second look shortly after.
        _deviceChanges = new DeviceChangeWindow(OnDeviceChange, OnRawInput);
    }

    /// <summary>A device arrived or left: refresh the list and re-check straight away.</summary>
    private void OnDeviceChange()
    {
        DevicesChanged?.Invoke();
        Poll();
    }

    /// <summary>
    /// Raised with the device handle and the report it just sent.
    ///
    /// Exposed so button state can be read without opening the controller. Opening it, even for
    /// shared reading, prevents Steam Input and DS4Windows from taking the exclusive access they
    /// need — and their taking it first stops us reading. Raw input sidesteps the whole argument:
    /// the reports arrive here whoever else has the device.
    /// </summary>
    public event Action<IntPtr, byte[]>? ReportReceived;

    /// <summary>A controller sent a report. Which one, when, and what it said.</summary>
    private void OnRawInput(IntPtr device, byte[] report)
    {
        if (report.Length > 0) ReportReceived?.Invoke(device, report);

        // Matched by hardware key, not by path.
        //
        // A controller exposes several HID collections and each has its own interface path.
        // SetupDi enumerates interfaces and takes the first that looks like a gamepad; raw
        // input reports whichever collection actually sent the data. Keying on the path meant
        // the two only lined up when those happened to be the same one — so waking the pad
        // worked or did not depending on enumeration order, which is exactly this sort of
        // intermittent. The instance is shared by every collection of one device.
        var key = _handleKeys.GetOrAdd(device, handle => KeyFromPath(PathOfRawDevice(handle)));
        if (key.Length == 0) return;

        // Nothing else happens here any more.
        //
        // This used to call Poll the moment a pad broke a long silence, on the reading that a
        // pad going quiet then talking again had been switched on. It had not — it had been
        // knocked, or its sticks had drifted. The report is still forwarded above, because the
        // controller shortcut reads buttons out of it, but no session starts from input.
    }

    public void Start()
    {
        if (_started) return;
        _started = true;

        var attached = Attached();

        // Whatever was already plugged in is the starting state, not a set of arrivals. Without
        // this every pad connected when the app launches would look like it had just appeared.
        //
        // "Already" is the word doing the work, and it used to mean "present at this instant",
        // which was wrong. Starting the app takes a moment, and a pad plugged in during that
        // moment finishes enumerating before this line runs — so it went into the baseline as
        // pre-existing and never announced itself. Plugging a controller in as the app launches
        // is a completely ordinary thing to do, and it was the one case that did nothing.
        //
        // Windows records when each device last arrived, so the question can be asked properly.
        //
        // Only on the app's own first start, though, and that restriction is the whole safety of
        // it. This is not only called at launch — the Shortcuts page starts the watcher so it can
        // read buttons, and saving the settings starts it too. At those later calls the launch
        // window is hours in the past, so *every* pad switched on since the app opened looks like
        // it arrived "during startup", drops out of the baseline, and starts a session the moment
        // nothing is suspending it. Which is precisely the unasked-for session this whole
        // mechanism exists to avoid.
        bool initial = DateTime.UtcNow - AppStarted < InitialStart;

        var since = AppStarted - JustBeforeStart;

        var during = initial
                   ? attached.Where(d => d.Arrived is { } at && at >= since).ToList()
                   : [];

        _present = attached.Select(d => d.Id).Except(during.Select(d => d.Id)).ToHashSet();
        _timer.Start();

        Log.Info($"Controller watch started{(initial ? "" : " (late; everything attached is the baseline)")}. "
               + $"{attached.Count} attached: "
               + (attached.Count == 0 ? "none" : string.Join(", ", attached.Select(d => $"{d.Name} [{d.Id}]")))
               + (OnlyDeviceId.Length > 0 ? $". Only {OnlyDeviceId} starts a session." : "."));

        // Left out of the baseline above, so the next look treats them as arrivals — which is
        // what they are. Announced through the same path as any other connect, so the chosen-pad
        // rule and everything else applies unchanged.
        foreach (var device in during)
            Log.Info($"{device.Name} was plugged in while the app was starting; treating it as a connection.");
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    /// <summary>Re-read what is attached, without treating the difference as an event.</summary>
    public void Rebaseline()
    {
        if (!_started) return;

        var attached = Attached();

        _present = attached.Select(d => d.Id).ToHashSet();
    }

    private void Poll()
    {
        if (!_started) return;

        List<ControllerDevice> attached;
        try { attached = Attached(); }
        catch (Exception ex) { Log.Warn($"Controller check failed: {ex.Message}"); return; }

        var ids = attached.Select(d => d.Id).ToHashSet();

        var arrived = attached.Where(d => !_present.Contains(d.Id)).ToList();
        var left = _present.Except(ids).ToList();

        _present = ids;

        if (arrived.Count > 0 || left.Count > 0) DevicesChanged?.Invoke();

        // Just woken up, so none of this is the user doing anything.
        if (DateTime.UtcNow < _settlingUntil)
        {
            if (arrived.Count > 0 || left.Count > 0)
                Log.Info("Controller change ignored — the machine has just woken up.");

            return;
        }

        // Suspended while the Controller page is showing: plugging a pad in to choose it from
        // the list must not also fling the display over to the television. Only that page, and
        // never during a session — see SettingsForm.UpdatePadSuspension.
        if (Suspended)
        {
            if (arrived.Count > 0 || left.Count > 0)
                Log.Info("Controller change ignored — the settings window is open.");

            return;
        }

        // ── connecting ──────────────────────────────────────────────────
        //
        // One thing counts as a controller arriving: Windows reporting it as newly present.
        // Safe to act on twice — the caller ignores a start request when a session is running.
        foreach (var device in arrived)
        {
            Log.Info($"Controller connected: {device.Name} ({device.Id}), "
                   + (device.Wireless ? "wireless" : "wired") + ".");

            if (!LinkAllows(device))
            {
                Log.Info($"Ignored — only {(Link == PadLink.WiredOnly ? "wired" : "wireless")} "
                       + "controllers may start a session.");
                continue;
            }

            Announce(device);
        }

        // Input activity is deliberately *not* a second way in.
        //
        // There used to be one: a pad already attached that broke a long silence was treated as
        // having just been switched on, for the wireless-adapter case where the adapter never
        // leaves and only the pad powers on and off. It fired on a controller lying still on a
        // desk — one report with no buttons pressed in it, stick noise or the pad's own
        // heartbeat — and threw the display over to the television in the middle of someone's
        // work.
        //
        // The same objection was already accepted on the disconnect side and should have been
        // applied here at the same time: input activity cannot distinguish intent from noise,
        // in either direction. A session now starts only when Windows says a device arrived.
        //
        // The cost is real and is the right trade: a pad on a wireless adapter that stays
        // enumerated will not start a session by being switched on. The shortcut on the
        // Shortcuts page covers that deliberately, which is the difference — it is something
        // the user does on purpose.

        // ── disconnecting ───────────────────────────────────────────────
        //
        // Only ever when Windows says the device has gone.
        //
        // Silence is not a disconnect. A controller that has not been touched is
        // indistinguishable from one that has been switched off, and the two need opposite
        // responses — a long cutscene, a paused game or a film would all end the session. So
        // going quiet is allowed to *arm* the wake above, and nothing more.
        foreach (var id in left)
        {
            Log.Info($"Controller disconnected: {id}.");

            if (Wanted(id)) Disconnected?.Invoke(new ControllerDevice(id, id, ""));
        }
    }

    private void Announce(ControllerDevice device)
    {
        if (Wanted(device.Id))
        {
            Log.Info(OnlyDeviceId.Length == 0
                ? "Any controller is allowed to start a session, so this one counts."
                : "This is the chosen controller.");

            Connected?.Invoke(device);
            return;
        }

        Log.Info($"Ignored — only {OnlyDeviceId} starts a session.");
    }

    /// <summary>Whether this device is the one allowed to drive a session.</summary>
    private bool Wanted(string id)
        => OnlyDeviceId.Length == 0 || string.Equals(id, OnlyDeviceId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Which kinds of connection may start a session. Set from the config.
    ///
    /// Applied to connects only, never to disconnects. A pad that started a session and is then
    /// switched off should end it whatever it was plugged into — refusing on connection type there
    /// would strand somebody on the television with no way back.
    /// </summary>
    public PadLink Link { get; set; } = PadLink.Either;

    private bool LinkAllows(ControllerDevice device) => Link switch
    {
        PadLink.WiredOnly => !device.Wireless,
        PadLink.WirelessOnly => device.Wireless,
        _ => true,
    };



    // ------------------------------------------------------------------ HID

    /// <summary>
    /// Every controller attached and powered right now.
    ///
    /// Enumerated through SetupDi with DIGCF_PRESENT — the same route DS4Windows and hidapi
    /// use — because presence is Windows' own call there. Raw input turned out to be the wrong
    /// tool for this question: it kept listing the node for a paired Bluetooth pad after the
    /// pad powered off, and every attempt to out-clever that (open the node, query it) was
    /// answered from the driver's cache rather than the hardware. DIGCF_PRESENT is maintained
    /// by the device stack itself, which is told directly when the radio link drops.
    /// </summary>
    public static List<ControllerDevice> Attached()
    {
        var found = new List<ControllerDevice>();

        HidD_GetHidGuid(out var hid);

        IntPtr set = SetupDiGetClassDevs(ref hid, IntPtr.Zero, IntPtr.Zero,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (set == InvalidHandle) return found;

        try
        {
            var iface = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint index = 0;
                 SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hid, index, ref iface);
                 index++)
            {
                var path = InterfacePath(set, ref iface, out var node);
                if (path.Length == 0) continue;

                if (!Inspect(path, out ushort vendor, out ushort product, out var serial))
                    continue;

                var model = $"VID_{vendor:X4}&PID_{product:X4}";

                // The unit tag prefers the serial (stable across USB and Bluetooth). A pad
                // that reports none falls back to the interface's instance segment — unique
                // per attached device, so two identical pads still get separate entries, at
                // the cost of changing if the pad moves to a different port.
                var unit = serial.Length > 0 ? serial : InstanceOf(path);
                var id = unit.Length > 0 ? $"{model}#{unit}" : model;

                if (found.Any(d => d.Id == id)) continue;

                var name = Describe(vendor, product, model);
                if (unit.Length >= 4) name += $"  ·  {unit[^4..].ToUpperInvariant()}";

                found.Add(new ControllerDevice(id, name, path, KeyFromPath(path),
                                              ArrivedAt(set, ref node), IsWireless(path)));

                // Once per device, not once per poll.
                //
                // This ran every 1.5 seconds and buried the log: 98% of a 480 KB file was two
                // lines repeated for a controller that had not changed. A log nobody can read
                // is the same as no log, and this one is the only way some of these problems
                // can be diagnosed at all.
                if (Described.Add(id))
                    Log.Info($"Controller present: {name}, serial "
                           + (serial.Length > 0 ? $"'{serial}'" : "not reported") + $", id {id}.");
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    /// <summary>
    /// The interface's device path, via the size-then-fetch dance SetupDi requires.
    ///
    /// The device node comes back too. It is filled in by the same call that returns the path,
    /// and it is what device properties have to be looked up against — an interface on its own
    /// cannot be asked anything.
    /// </summary>
    private static string InterfacePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA iface,
                                        out SP_DEVINFO_DATA node)
    {
        node = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };

        SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out uint needed, IntPtr.Zero);
        if (needed == 0) return "";

        var detail = Marshal.AllocHGlobal((int)needed);

        try
        {
            // cbSize is the size of the *fixed part* of the detail struct, not the allocation:
            // 8 on x64 (a 4-byte count padded to pointer alignment). Getting this wrong is the
            // classic SetupDi failure and it fails with INVALID_USER_BUFFER, not a bad string.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);

            if (!SetupDiGetDeviceInterfaceDetailWithNode(set, ref iface, detail, needed, out _, ref node))
                return "";

            return Marshal.PtrToStringUni(detail + 4) ?? "";
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }

    /// <summary>
    /// When Windows last saw this device arrive, or null if it will not say.
    ///
    /// DEVPKEY_Device_LastArrivalDate is maintained by the device stack itself and is exactly the
    /// question worth asking: not "is this pad here", which cannot distinguish a controller
    /// plugged in three seconds ago from one plugged in three days ago, but "when did it get
    /// here". Present since Windows 8.
    ///
    /// Null on any failure, which reads as "already attached" at the call site — the cautious
    /// answer, since it means a session has to be started deliberately rather than one starting
    /// by itself off a value that could not be read.
    /// </summary>
    private static DateTime? ArrivedAt(IntPtr set, ref SP_DEVINFO_DATA node)
    {
        try
        {
            var key = LastArrivalDate;
            var buffer = new byte[8];

            if (!SetupDiGetDeviceProperty(set, ref node, ref key, out uint type,
                                          buffer, buffer.Length, out _, 0))
                return null;

            if (type != DEVPROP_TYPE_FILETIME) return null;

            long ticks = BitConverter.ToInt64(buffer, 0);
            if (ticks <= 0) return null;

            return DateTime.FromFileTimeUtc(ticks);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the interface is a gamepad, and its identity if so.
    ///
    /// A handle that cannot be opened because someone holds the device exclusively still counts:
    /// tools like DS4Windows open pads exactly that way, and only controllers get opened like
    /// that — the OS will not give anyone an exclusive keyboard. Identity then comes from the
    /// path, which carries the vendor and product ids.
    /// </summary>
    private static bool Inspect(string path, out ushort vendor, out ushort product, out string serial)
    {
        vendor = 0; product = 0; serial = "";

        IntPtr handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero,
                                   OpenExisting, 0, IntPtr.Zero);

        if (handle == InvalidHandle)
        {
            int error = Marshal.GetLastWin32Error();
            if (error is not (ErrorAccessDenied or ErrorSharingViolation)) return false;
            if (!ParseIds(path, out vendor, out product)) return false;

            Log.Info($"Controller is held exclusively by another program; counting it as connected.");
            return true;
        }

        try
        {
            var attributes = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(handle, ref attributes)) return false;

            vendor = attributes.VendorID;
            product = attributes.ProductID;

            // Only gamepads and joysticks. Everything HID passes through here — keyboards,
            // mice, headset buttons — and the usage codes are what separate a pad from those.
            if (!HidD_GetPreparsedData(handle, out var parsed)) return false;

            try
            {
                var caps = new HIDP_CAPS();
                if (HidP_GetCaps(parsed, ref caps) != HidpStatusSuccess) return false;
                if (caps.UsagePage != HidGenericDesktop) return false;
                if (caps.Usage is not (HidUsageJoystick or HidUsageGamepad)) return false;
            }
            finally
            {
                HidD_FreePreparsedData(parsed);
            }

            var buffer = new char[128];
            if (HidD_GetSerialNumberString(handle, buffer, buffer.Length * 2))
                serial = new string(buffer).TrimEnd('\0').Trim();

            // Logged once per device, for the same reason as above.
            //
            // Logged, not used for matching.
            //
            // An adapter that presents itself as a DualSense reports Sony's ids, because that
            // is the whole point of it — Windows, Steam and every game are meant to be fooled.
            // Its own maker and product strings are the only place the truth tends to survive,
            // so they go in the log where they can identify a bridge that is otherwise
            // indistinguishable from the controller it stands in for.
            // Named apart from the product *id* parameter above, which is a different thing.
            ReadStrings(handle, out var maker, out var productName);

            if ((maker.Length > 0 || productName.Length > 0) && Described.Add("strings:" + path))
                Log.Info($"  reports itself as '{productName}' by '{maker}'.");

            return true;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// The device path behind a raw input handle, resolved once and cached.
    ///
    /// Raw input identifies a device by handle; everything else here works in paths, and this
    /// is the only place the two meet.
    /// </summary>
    private static string PathOfRawDevice(IntPtr device)
    {
        uint size = 0;
        if (GetRawInputDeviceInfo(device, RIDI_DEVICENAME, IntPtr.Zero, ref size) != 0 || size == 0)
            return "";

        var buffer = Marshal.AllocHGlobal((int)size * 2);

        try
        {
            return GetRawInputDeviceInfo(device, RIDI_DEVICENAME, buffer, ref size) == uint.MaxValue
                 ? ""
                 : Marshal.PtrToStringUni(buffer) ?? "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The manufacturer and product strings a device reports, where it reports them.</summary>
    private static void ReadStrings(IntPtr handle, out string maker, out string product)
    {
        maker = Read(HidD_GetManufacturerString);
        product = Read(HidD_GetProductString);

        string Read(Func<IntPtr, char[], int, bool> get)
        {
            try
            {
                var buffer = new char[128];
                return get(handle, buffer, buffer.Length * 2)
                     ? new string(buffer).TrimEnd('\0').Trim()
                     : "";
            }
            catch { return ""; }
        }
    }

    /// <summary>
    /// Devices already written to the log, so each is described once rather than every poll.
    ///
    /// Never cleared: a controller that comes and goes is announced by the connect and
    /// disconnect lines, which are the interesting ones. Repeating its description adds
    /// nothing and is what made the log unreadable.
    /// </summary>
    private static readonly HashSet<string> Described = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the path says this pad arrived over Bluetooth.
    ///
    /// BTHENUM is the classic Bluetooth enumerator and BTHLEDEVICE the low-energy one; a wired pad
    /// enumerates under USB or HID. Only these two are treated as wireless, so anything unfamiliar
    /// falls back to "wired" — which is the answer that lets a session start under the default
    /// setting rather than silently refusing to.
    /// </summary>
    private static bool IsWireless(string path)
        => path.Contains("BTHENUM", StringComparison.OrdinalIgnoreCase)
        || path.Contains("BTHLE", StringComparison.OrdinalIgnoreCase);

    /// <summary>Vendor and product parsed out of a device path, for devices that cannot be opened.</summary>
    private static bool ParseIds(string path, out ushort vendor, out ushort product)
    {
        vendor = 0; product = 0;

        var m = System.Text.RegularExpressions.Regex.Match(
            path, @"vid_([0-9a-f]{4}).*?pid_([0-9a-f]{4})",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        if (!m.Success) return false;

        vendor = Convert.ToUInt16(m.Groups[1].Value, 16);
        product = Convert.ToUInt16(m.Groups[2].Value, 16);
        return true;
    }

    /// <summary>
    /// The vendor, product and hardware instance of a device path.
    ///
    /// Shared by every HID collection of one physical controller, so it is the right thing to
    /// match an input report against.
    /// </summary>
    private static string KeyFromPath(string path)
    {
        if (path.Length == 0) return "";
        if (!ParseIds(path, out var vendor, out var product)) return "";

        var instance = InstanceOf(path);
        return instance.Length == 0 ? "" : $"{vendor:X4}:{product:X4}#{instance}";
    }

    /// <summary>
    /// The part of an interface path that actually identifies this attachment.
    ///
    /// A path looks like \\?\hid#vid_054c&amp;pid_0ce6#8&amp;1ba13e35&amp;1&amp;0000#{guid}, and the
    /// third block is what Windows assigned this device. Its pieces are not equally useful:
    /// they run bus number, instance id, then collection indices. Flattening the block and
    /// taking the last four characters — which is what this did — always lands on the trailing
    /// 0000, so every controller was tagged "0000" and no two could be told apart.
    ///
    /// The instance id is the longest piece, so that is the one to keep.
    /// </summary>
    private static string InstanceOf(string path)
    {
        var parts = path.Split('#');
        if (parts.Length < 3) return "";

        var pieces = parts[2].Split('&', StringSplitOptions.RemoveEmptyEntries);
        if (pieces.Length == 0) return "";

        var instance = pieces.OrderByDescending(p => p.Length).First();

        // A single short piece is no better than nothing; fall back to the whole block so two
        // devices still differ, even if the tag is less pretty.
        return instance.Length >= 4 ? instance : parts[2].Replace("&", "");
    }

    /// <summary>
    /// A readable name for a pad.
    ///
    /// Windows will happily hand back "HID-compliant game controller" for almost everything,
    /// which is useless when the whole point is telling two pads apart. The common vendors are
    /// named outright, and anything else falls back to its ids — still unique, still selectable.
    /// </summary>
    private static string Describe(uint vendor, uint product, string id) => (vendor, product) switch
    {
        (0x054C, 0x0CE6) => "PlayStation DualSense",
        (0x054C, 0x0DF2) => "PlayStation DualSense Edge",
        (0x054C, 0x09CC) => "PlayStation DualShock 4",
        (0x054C, 0x05C4) => "PlayStation DualShock 4",
        (0x057E, 0x2009) => "Nintendo Switch Pro Controller",
        (0x28DE, _)      => "Steam Controller",
        (0x045E, 0x02FF) => "Xbox Controller",
        (0x045E, 0x0B12) => "Xbox Series Controller",
        (0x045E, 0x0B13) => "Xbox Series Controller (Bluetooth)",
        (0x045E, _)      => "Xbox Controller",
        (0x054C, _)      => "PlayStation Controller",
        (0x057E, _)      => "Nintendo Controller",
        _                => $"Controller ({id})",
    };

    public void Dispose()
    {
        Stop();
        _timer.Dispose();
        _deviceChanges.Dispose();
    }

    /// <summary>
    /// A message-only window, purely to hear WM_DEVICECHANGE.
    ///
    /// Cheaper and far more responsive than shortening the poll: Windows says when something is
    /// plugged in, so there is no reason to keep asking.
    /// </summary>
    /// <summary>
    /// Hears the shell announce device changes, and hears the pads themselves.
    ///
    /// Raw input is used here for what it is actually good at: being told, without polling and
    /// without opening anything, that a controller has sent data. RIDEV_INPUTSINK means the
    /// reports arrive even though this window never has focus, which is the whole point — the
    /// user is in a game or on the desktop, not looking at us.
    /// </summary>
    private sealed class DeviceChangeWindow : NativeWindow, IDisposable
    {
        private readonly Action _changed;
        private readonly Action<IntPtr, byte[]> _activity;

        private readonly System.Windows.Forms.Timer _settle =
            new() { Interval = 700 };

        public DeviceChangeWindow(Action changed, Action<IntPtr, byte[]> activity)
        {
            _changed = changed;
            _activity = activity;

            _settle.Tick += (_, _) => { _settle.Stop(); _changed(); };

            CreateHandle(new CreateParams { Parent = HWND_MESSAGE });

            Register();
        }

        /// <summary>Ask for every gamepad and joystick report, focused or not.</summary>
        private void Register()
        {
            var devices = new[]
            {
                new RAWINPUTDEVICE
                {
                    usUsagePage = HidGenericDesktop,
                    usUsage = HidUsageGamepad,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = Handle,
                },
                new RAWINPUTDEVICE
                {
                    usUsagePage = HidGenericDesktop,
                    usUsage = HidUsageJoystick,
                    dwFlags = RIDEV_INPUTSINK,
                    hwndTarget = Handle,
                },
            };

            if (!RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RAWINPUTDEVICE>()))
                Log.Warn("Could not subscribe to controller input; activity detection is off.");
        }

        private bool _active = true;

        /// <summary>
        /// Turn the raw-input subscription on or off. Off means Windows stops delivering the pad's
        /// reports here entirely — which is the only thing that stops a continuously-streaming pad
        /// (a DualSense reports every frame) from resetting the display idle timer. Device-change
        /// messages are unaffected, so a pad connecting still gets noticed.
        /// </summary>
        public void SetActive(bool on)
        {
            if (on == _active) return;
            _active = on;

            if (on) { Register(); return; }

            var devices = new[]
            {
                new RAWINPUTDEVICE { usUsagePage = HidGenericDesktop, usUsage = HidUsageGamepad,
                                     dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
                new RAWINPUTDEVICE { usUsagePage = HidGenericDesktop, usUsage = HidUsageJoystick,
                                     dwFlags = RIDEV_REMOVE, hwndTarget = IntPtr.Zero },
            };

            RegisterRawInputDevices(devices, devices.Length, Marshal.SizeOf<RAWINPUTDEVICE>());
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DEVICECHANGE = 0x0219;
            const int WM_INPUT = 0x00FF;

            base.WndProc(ref m);

            if (m.Msg == WM_INPUT)
            {
                ReadReport(m.LParam);
                return;
            }

            if (m.Msg != WM_DEVICECHANGE) return;

            // Restarted rather than started: a single pad often produces several messages as its
            // interfaces enumerate, and this collapses them into one look afterwards.
            _settle.Stop();
            _settle.Start();
        }

        /// <summary>
        /// Pull one raw input message apart into the device that sent it and the bytes it sent.
        ///
        /// This used to read only the header — which device spoke, not what it said — on the
        /// grounds that the contents were nobody's business and copying them would allocate on
        /// every frame of stick movement. Both were true, and the second still is, which is why
        /// the buffer below is reused rather than allocated per report.
        ///
        /// What changed is that reading buttons any other way means opening the controller, and
        /// opening the controller is what fights with Steam Input.
        /// </summary>
        private void ReadReport(IntPtr lParam)
        {
            uint size = 0;

            if (GetRawInputData(lParam, RID_INPUT, IntPtr.Zero, ref size,
                                (uint)Marshal.SizeOf<RAWINPUTHEADER>()) != 0 || size == 0)
                return;

            if (size > _buffer.Length) _buffer = new byte[size];

            var pinned = System.Runtime.InteropServices.GCHandle.Alloc(_buffer,
                             System.Runtime.InteropServices.GCHandleType.Pinned);

            try
            {
                IntPtr raw = pinned.AddrOfPinnedObject();

                if (GetRawInputData(lParam, RID_INPUT, raw, ref size,
                                    (uint)Marshal.SizeOf<RAWINPUTHEADER>()) == uint.MaxValue)
                    return;

                var header = Marshal.PtrToStructure<RAWINPUTHEADER>(raw);

                // RAWHID lays out as: header, then dwSizeHid, dwCount, then the reports.
                int offset = Marshal.SizeOf<RAWINPUTHEADER>();

                int hidSize = Marshal.ReadInt32(raw, offset);
                int hidCount = Marshal.ReadInt32(raw, offset + 4);

                if (hidSize <= 0 || hidCount <= 0) return;

                // Only the first report of the batch. Windows can coalesce several, but for a
                // button state the newest would do and the first is what the rest describe
                // changes from — and a shortcut is not a thing you can miss by one frame.
                var report = new byte[hidSize];
                Marshal.Copy(raw + offset + 8, report, 0, hidSize);

                _activity(header.hDevice, report);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not read a controller report: {ex.Message}");
            }
            finally
            {
                pinned.Free();
            }
        }

        /// <summary>Reused across reports. These arrive constantly while a stick is moving.</summary>
        private byte[] _buffer = new byte[256];

        public void Dispose()
        {
            _settle.Dispose();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }

        private static readonly IntPtr HWND_MESSAGE = new(-3);
    }

    // ---- interop ----

    private const ushort HidGenericDesktop = 0x01;
    private const ushort HidUsageJoystick = 0x04;
    private const ushort HidUsageGamepad = 0x05;
    private const int HidpStatusSuccess = 0x00110000;

    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint DIGCF_DEVICEINTERFACE = 0x00000010;

    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;

    private const uint FileShareReadWrite = 0x00000001 | 0x00000002;
    private const uint OpenExisting = 3;
    private static readonly IntPtr InvalidHandle = new(-1);

    /// <summary>DEVPKEY_Device_LastArrivalDate, from devpkey.h.</summary>
    private static Guid LastArrivalDateFmtid =
        new("83da6326-97a6-4088-9453-a1923f573b29");

    private static DEVPROPKEY LastArrivalDate =>
        new() { fmtid = LastArrivalDateFmtid, pid = 102 };

    private const uint DEVPROP_TYPE_FILETIME = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    private struct DEVPROPKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    /// <summary>
    /// Declared in full even though only the first two fields are read: HidP_GetCaps writes
    /// the whole 64-byte struct, and a short declaration would let it write past the end.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
                                                     IntPtr parent, uint flags);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info,
                                                           ref Guid classGuid, uint index,
                                                           ref SP_DEVICE_INTERFACE_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set,
                                                               ref SP_DEVICE_INTERFACE_DATA data,
                                                               IntPtr detail, uint size,
                                                               out uint required, IntPtr devInfo);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW",
               CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetailWithNode(
        IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, uint size,
        out uint required, ref SP_DEVINFO_DATA devInfo);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDevicePropertyW",
               CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(
        IntPtr set, ref SP_DEVINFO_DATA node, ref DEVPROPKEY key, out uint type,
        byte[] buffer, int size, out int required, uint flags);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr data);

    [DllImport("hid.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_FreePreparsedData(IntPtr data);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr data, ref HIDP_CAPS caps);

    private const uint RIDEV_INPUTSINK = 0x00000100;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RID_HEADER = 0x10000005;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDI_DEVICENAME = 0x20000007;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, int count, int size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr input, uint command, IntPtr data,
                                               ref uint size, uint headerSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr input, uint command, ref RAWINPUTHEADER data,
                                               ref uint size, uint headerSize);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW", CharSet = CharSet.Unicode)]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, IntPtr data,
                                                     ref uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share,
                                            IntPtr security, uint disposition,
                                            uint flags, IntPtr template);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    // BOOLEAN return — a single byte — so the marshalling must say U1, or the result is read
    // from register leftovers.
    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetSerialNumberString(IntPtr device, char[] buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetManufacturerString(IntPtr device, char[] buffer, int length);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool HidD_GetProductString(IntPtr device, char[] buffer, int length);


}
