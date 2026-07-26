using System.Runtime.InteropServices;

namespace CouchMode.Input;

/// <summary>
/// Which buttons are held on a controller Windows exposes as a plain HID device.
///
/// This is the answer for pads XInput cannot see — a DualSense, or a DualSense arriving through a
/// passthrough dongle, is invisible to XInput no matter how many buttons are pressed.
///
/// **Nothing here opens the controller.** That is the whole design, and the second attempt at it.
///
/// The obvious approach is to open the device and read it, and it works — until it meets Steam
/// Input or DS4Windows. Those take *exclusive* access to a pad so that games see their remapped
/// version rather than the real one, and exclusivity cuts both ways: they cannot take the device
/// while we hold it open, and we cannot read it while they do. Whoever starts first wins, which
/// makes for a feature that works on Monday and not on Tuesday, with nothing on screen to say
/// why.
///
/// Raw input has no such argument. Windows delivers HID reports to any window that asks for them,
/// alongside whatever else is going on with the device, and RIDEV_INPUTSINK means they arrive
/// without focus. No handle, no sharing mode, nothing to contend over. The controller watcher is
/// already registered for exactly these reports, so this listens to what it receives rather than
/// registering a second time — a second registration in the same process replaces the first, and
/// would have quietly broken the watcher.
///
/// The reports are parsed with HidP_GetUsages against preparsed data fetched by device handle,
/// which again needs no open handle. Generic rather than DualSense-specific: every HID device
/// describes its own layout, so the same code reads a DualSense, a DualShock 4, an arcade stick
/// and a flight yoke.
///
/// Buttons, plus the D-pad. Sticks and triggers are left out — they are analogue and a shortcut
/// wants things that are unambiguously pressed or not — but the D-pad only looks analogue. On a
/// DualSense it is a *hat switch*, one value from 0 to 7 naming a direction, which is why it did
/// not appear alongside the buttons: HidP_GetUsages never sees it. It is read separately and
/// folded into the same mask as four synthetic buttons, because to anyone pressing it, up is a
/// button.
/// </summary>
public static class HidPad
{
    /// <summary>What each device last reported, and when.</summary>
    private sealed record State(uint Buttons, ushort Vendor, ushort Product, DateTime At,
                                PadPower Power = PadPower.Unknown, PadTouch Touch = default,
                                float LX = 0, float LY = 0, float RX = 0, float RY = 0,
                                PadTouch Touch2 = default);

    private static readonly Dictionary<IntPtr, State> Live = [];
    private static readonly Dictionary<IntPtr, IntPtr> Preparsed = [];

    // Each pad's resting stick offset, learned once when it first reports. Subtracted from every
    // later reading so a stick that physically sits off-centre — drift — reads as zero, and a
    // resting controller injects nothing (which is what was quietly holding the display awake).
    private static readonly Dictionary<IntPtr, (float LX, float LY, float RX, float RY)> Centers = [];

    private static readonly object Gate = new();

    private static DateTime _lastActivity = DateTime.UtcNow;

    /// <summary>
    /// When a pad last did something a person would recognise as input — a button, a real stick
    /// move, a finger on the trackpad. Streaming alone does not count; see OnReport.
    /// </summary>
    public static DateTime LastActivityUtc { get { lock (Gate) return _lastActivity; } }

    /// <summary>
    /// How long a report keeps counting as current.
    ///
    /// Raw input says what changed, not what is still true — a pad sends nothing at all while
    /// buttons are held down. So the last report stands until it is superseded or the pad goes
    /// quiet for longer than any real press would last. Held buttons do produce repeats on every
    /// controller worth supporting, because the sticks are never perfectly still.
    /// </summary>
    private static readonly TimeSpan Fresh = TimeSpan.FromMilliseconds(600);

    /// <summary>Start listening. Safe to call more than once.</summary>
    public static void Listen(Games.ControllerWatcher watcher)
    {
        _watcher = watcher;
        watcher.ReportReceived -= OnReport;
        watcher.ReportReceived += OnReport;
    }

    /// <summary>
    /// The watcher whose stream feeds this, kept only to ask whether that stream is currently released.
    /// When it is, there are no reports to read and the buttons have to be fetched a different way —
    /// see <see cref="HidPoll"/>.
    /// </summary>
    private static Games.ControllerWatcher? _watcher;

    /// <summary>Whether the pad's stream is released, so state here is stale and must not be trusted.</summary>
    private static bool Released => _watcher?.ReportsReleased == true;

    /// <summary>Buttons held on any HID pad, as a mask where bit 0 is button 1.</summary>
    public static uint Held()
    {
        var all = HeldAll();
        return all.Count == 0 ? 0 : all[0].Buttons;
    }

    /// <summary>
    /// One mask per pad that currently has anything pressed, rather than the first one found.
    ///
    /// Two controllers are two answers, and collapsing them to one meant the second pad could
    /// never satisfy a shortcut while the first had any button down at all.
    /// </summary>
    public static IReadOnlyList<(uint Buttons, ushort Vendor, ushort Product)> HeldAll()
    {
        // Nothing is arriving, so anything remembered here is only what the pad last said before we
        // stopped listening — which for a button means "not pressed", forever. Ask the device instead.
        // Every caller above this, shortcut matching included, carries on unaware.
        if (Released) return HidPoll.HeldAll();

        lock (Gate)
        {
            Sweep();

            var all = new List<(uint, ushort, ushort)>();

            // The device comes back with the mask. HID numbers buttons per device, so a mask
            // without the pad it came from cannot be turned into which buttons were pressed.
            foreach (var state in Live.Values)
                if (state.Buttons != 0) all.Add((state.Buttons, state.Vendor, state.Product));

            return all;
        }
    }

    /// <summary>Whether any HID pad has been heard from recently.</summary>
    public static bool AnyConnected()
    {
        lock (Gate)
        {
            Sweep();
            return Live.Count > 0;
        }
    }

    /// <summary>Vendor and product of the pad heard from most recently, for naming its buttons.</summary>
    public static (ushort Vendor, ushort Product) FirstIds()
    {
        lock (Gate)
        {
            Sweep();

            State? newest = null;
            foreach (var state in Live.Values)
                if (newest is null || state.At > newest.At) newest = state;

            return newest is null ? ((ushort)0, (ushort)0) : (newest.Vendor, newest.Product);
        }
    }

    /// <summary>Forget everything heard so far. Used when the set of devices changes.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            foreach (var handle in Preparsed.Values)
                if (handle != IntPtr.Zero) Marshal.FreeHGlobal(handle);

            Preparsed.Clear();
            Live.Clear();
            Described.Clear();
            HatRanges.Clear();
        }
    }

    /// <summary>Drop anything that has gone quiet, so an unplugged pad stops counting as held.</summary>
    private static void Sweep()
    {
        if (Live.Count == 0) return;

        var cutoff = DateTime.UtcNow - Fresh;
        List<IntPtr>? stale = null;

        foreach (var (handle, state) in Live)
            if (state.At < cutoff) (stale ??= []).Add(handle);

        if (stale is null) return;

        foreach (var handle in stale) { Live.Remove(handle); Centers.Remove(handle); }
    }

    /// <summary>
    /// Devices already reported on, so the diagnostics below are written once each rather than
    /// forty times a second.
    /// </summary>
    private static readonly HashSet<IntPtr> Described = [];

    /// <summary>
    /// Say once, per device, how far a report got.
    ///
    /// There are four places this can quietly stop — no report, no preparsed data, a parse
    /// failure, or a parse that finds no buttons — and from outside all four look identical:
    /// a box saying no controller is detected. One line per device turns "it does not work"
    /// into a specific answer.
    /// </summary>
    private static void Describe(IntPtr device, string what)
    {
        lock (Gate)
        {
            if (!Described.Add(device)) return;
        }

        Log.Info($"Controller input: {what}");
    }

    private static void OnReport(IntPtr device, byte[] report)
    {
        try
        {
            IntPtr preparsed = PreparsedFor(device);

            if (preparsed == IntPtr.Zero)
            {
                Describe(device, $"report of {report.Length} bytes arrived, but Windows would "
                               + "not describe the device's layout, so its buttons cannot be read.");
                return;
            }

            ushort count = MaxButtons;
            var usages = new ushort[MaxButtons];

            int rc = HidP_GetUsages(HidpInput, UsagePageButton, 0, usages, ref count,
                                    preparsed, report, report.Length);

            if (rc != HidpStatusSuccess)
            {
                Describe(device, $"report of {report.Length} bytes arrived but could not be "
                               + $"parsed (HidP_GetUsages returned 0x{rc:X8}).");
                return;
            }

            Describe(device, $"reading buttons from a {report.Length}-byte report, "
                           + $"{count} pressed on the first read.");

            uint mask = 0;

            for (int i = 0; i < count; i++)
            {
                // Usages are 1-based button numbers. Past 32 is beyond what a mask can carry and
                // beyond what any pad this is for actually has.
                int button = usages[i];
                if (button is >= 1 and <= 24) mask |= 1u << (button - 1);
            }

            mask |= HatOf(preparsed, report);

            var (vendor, product) = IdsFor(device);
            var power = PowerIn(report, vendor);
            var touch = TouchIn(report, vendor, 0);
            var touch2 = TouchIn(report, vendor, 1);
            var (lx, ly, rx, ry) = SticksIn(report, vendor);

            lock (Gate)
            {
                // Learn the resting offset once, the first time this pad reports. Only a plausibly
                // small offset is taken as rest; if a stick is being pushed as the pad connects, that
                // axis is left uncorrected rather than baking a large offset in.
                if (!Centers.ContainsKey(device))
                {
                    static float Rest(float v) => MathF.Abs(v) < 0.6f ? v : 0f;
                    Centers[device] = (Rest(lx), Rest(ly), Rest(rx), Rest(ry));
                }

                // Note *changes*, not reports. A DualSense streams every frame whether or not anyone
                // is holding it, so the stream itself proves nothing — but a button going down, a
                // stick actually moving, or a finger landing on the pad is a person. This timestamp
                // is what lets the idle logic tell a pad in use from one lying on the couch.
                if (Live.TryGetValue(device, out var prev))
                {
                    const float Eps = 0.08f;
                    if (prev.Buttons != mask
                        || MathF.Abs(prev.LX - lx) > Eps || MathF.Abs(prev.LY - ly) > Eps
                        || MathF.Abs(prev.RX - rx) > Eps || MathF.Abs(prev.RY - ry) > Eps
                        || prev.Touch.Touching != touch.Touching
                        || prev.Touch2.Touching != touch2.Touching)
                        _lastActivity = DateTime.UtcNow;
                }
                else _lastActivity = DateTime.UtcNow;   // a pad appearing is itself an event

                Live[device] = new State(mask, vendor, product, DateTime.UtcNow, power, touch,
                                         lx, ly, rx, ry, touch2);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read a controller report: {ex.Message}");
        }
    }

    /// <summary>
    /// The most recent trackpad touch from any DualSense heard from recently.
    ///
    /// The first pad with a finger down wins, which is all a single pointer needs. Nothing touching
    /// comes back as the default — not touching, no coordinates — and the caller uses that to know
    /// when to stop moving the cursor.
    /// </summary>
    public static PadTouch Touch()
    {
        lock (Gate)
        {
            Sweep();

            foreach (var state in Live.Values)
                if (state.Touch.Touching) return state.Touch;

            return default;
        }
    }

    /// <summary>
    /// One finger on a DualSense trackpad, out of the input report.
    ///
    /// Sony packs each finger into four bytes: a flag bit for "not touching", then a 12-bit X and
    /// a 12-bit Y sharing a middle byte. The first finger sits at offset 32 into the report body
    /// and the second immediately after it, so <paramref name="finger"/> steps four bytes on. The
    /// trackpad is 1920 by 1080, which is what the coordinates run to. The body offset is the same
    /// USB-versus-Bluetooth split as the battery byte, so the report ID decides it.
    ///
    /// Verifiable the moment it is wrong, unlike a charge level read from a guessed offset: a
    /// finger dragged across the pad either moves the cursor or it does not.
    /// </summary>
    private static PadTouch TouchIn(byte[] report, ushort vendor, int finger)
    {
        if (vendor != 0x054C) return default;

        int body = report[0] switch { 0x01 => 1, 0x31 => 2, _ => -1 };
        if (body < 0) return default;

        int at = body + 32 + finger * 4;
        if (at + 3 >= report.Length) return default;

        // Bit 7 of the first byte is set while the finger is *off* the pad.
        if ((report[at] & 0x80) != 0) return default;

        int x = ((report[at + 2] & 0x0F) << 8) | report[at + 1];
        int y = (report[at + 3] << 4) | (report[at + 2] >> 4);

        return new PadTouch(true, x, y);
    }

    /// <summary>
    /// The pointer vector from a DualSense's sticks, honouring which sticks the caller wants used.
    ///
    /// Up-positive and right-positive, dead-zoned, from the larger of the enabled sticks — see
    /// PadSticks. Disabled sticks are passed in as zero, so they simply cannot win.
    /// </summary>
    public static (float X, float Y) SonyStick(bool leftStick, float dead)
    {
        lock (Gate)
        {
            Sweep();

            // Every Sony pad, not the first one — with two attached, the idle one used to answer for
            // both and the stick being pushed read as centred on alternate polls, which the pointer
            // showed as stutter. Whichever is pushed hardest is the one being held.
            (float X, float Y) best = (0f, 0f);
            float bestMagnitude = -1f;

            foreach (var (device, state) in Live)
            {
                if (state.Vendor != 0x054C) continue;

                var c = Centers.TryGetValue(device, out var centre) ? centre : default;
                float x = (leftStick ? state.LX : state.RX) - (leftStick ? c.LX : c.RX);
                float y = (leftStick ? state.LY : state.RY) - (leftStick ? c.LY : c.RY);

                var applied = PadSticks.Apply(x, y, dead);
                float magnitude = applied.X * applied.X + applied.Y * applied.Y;

                if (magnitude > bestMagnitude) { bestMagnitude = magnitude; best = applied; }
            }

            return best;
        }
    }

    /// <summary>Both trackpad fingers of the first DualSense, for telling a one-finger drag from a
    /// two-finger scroll.</summary>
    public static (PadTouch A, PadTouch B) Touches()
    {
        lock (Gate)
        {
            Sweep();

            foreach (var state in Live.Values)
                if (state.Vendor == 0x054C)
                    return (state.Touch, state.Touch2);

            return (default, default);
        }
    }

    /// <summary>
    /// Both DualSense sticks out of the report, up-positive.
    ///
    /// Sticks are the first four bytes of the state body, each 0-255 centred on 128. HID counts Y
    /// downward, so it is flipped here to the up-positive convention every caller expects.
    /// </summary>
    private static (float LX, float LY, float RX, float RY) SticksIn(byte[] report, ushort vendor)
    {
        if (vendor != 0x054C) return (0f, 0f, 0f, 0f);

        int body = report[0] switch { 0x01 => 1, 0x31 => 2, _ => -1 };
        if (body < 0 || body + 3 >= report.Length) return (0f, 0f, 0f, 0f);

        static float N(byte v) => (v - 128) / 127f;

        return (N(report[body + 0]), -N(report[body + 1]),
                N(report[body + 2]), -N(report[body + 3]));
    }

    /// <summary>The DualSense trackpad's size, so a caller can scale a delta.</summary>
    public const int TrackpadWidth = 1920;
    public const int TrackpadHeight = 1080;

    /// <summary>
    /// Whether each Sony pad is on a cable, for the pads that say so.
    ///
    /// Only Sony's, and only the power *state* — not a level. The state lives in the high nibble
    /// of a documented byte and has three meanings, so a misread shows up at once as a pad
    /// claiming to charge while it sits on the sofa. A level read from the same byte would be a
    /// plausible-looking number with no way to tell right from wrong, which is why it is gone.
    /// </summary>
    public static IReadOnlyList<(ushort Vendor, ushort Product, PadPower Power)> Power()
    {
        lock (Gate)
        {
            Sweep();

            var all = new List<(ushort, ushort, PadPower)>();

            foreach (var state in Live.Values)
                if (state.Power != PadPower.Unknown)
                    all.Add((state.Vendor, state.Product, state.Power));

            return all;
        }
    }

    /// <summary>
    /// Cable state out of a DualSense report.
    ///
    /// Sony packs it into the high nibble of the byte 52 into the report body. The body's offset
    /// depends on how the pad is connected — over USB the report is ID 0x01 and the body follows
    /// immediately, while over Bluetooth it is ID 0x31 with a flags-and-sequence byte in between —
    /// so the report ID is checked rather than assumed.
    ///
    /// Anything other than the three documented states reads as unknown, which shows as nothing at
    /// all. That is the point of only reporting state: a value out of range is obviously wrong,
    /// where a charge level out of range still looks like a battery reading.
    /// </summary>
    private static PadPower PowerIn(byte[] report, ushort vendor)
    {
        if (vendor != 0x054C) return PadPower.Unknown;
        if (report.Length < 2) return PadPower.Unknown;

        int body = report[0] switch
        {
            0x01 => 1,      // USB, and the truncated Bluetooth report
            0x31 => 2,      // Bluetooth, with a flags and sequence byte first
            _ => -1,
        };

        if (body < 0) return PadPower.Unknown;

        int at = body + 52;
        if (at >= report.Length) return PadPower.Unknown;

        return ((report[at] >> 4) & 0x0F) switch
        {
            0x00 => PadPower.OnBattery,
            0x01 => PadPower.Charging,
            0x02 => PadPower.Charging,   // charged, and still on the cable
            _ => PadPower.Unknown,       // abnormal voltage or temperature, or a charging fault
        };
    }

    /// <summary>
    /// The D-pad, as four bits in the button mask.
    ///
    /// A hat switch reports one value for eight directions plus a centre, so the diagonals are
    /// their own values rather than two directions at once — north-east is 1, not "north and
    /// east". Expanding it back into the two directions it means is what makes a D-pad behave
    /// like the four buttons everyone thinks it is.
    ///
    /// The declared range is read rather than assumed, and that is the whole difficulty. A
    /// DualSense numbers its hat 0-7 clockwise from north and reports 8 when centred. An Xbox pad
    /// numbers the same eight directions 1-8 and reports 0 when centred — the same wire value, 0,
    /// meaning "up" on one pad and "nothing" on the other.
    ///
    /// Assuming the DualSense's numbering therefore did not merely mis-read an Xbox D-pad, it
    /// reported it as permanently held up. Every reading from that pad had a button in it, so a
    /// shortcut being recorded never saw the buttons come back up and was never committed — which
    /// is why Xbox pads could not be mapped at all while PlayStation pads were fine. Nothing about
    /// it looked like a D-pad problem from outside.
    ///
    /// So the range comes from the device, and anything outside it is centred.
    /// </summary>
    private static uint HatOf(IntPtr preparsed, byte[] report)
    {
        if (HidP_GetUsageValue(HidpInput, UsagePageGenericDesktop, 0, UsageHatSwitch,
                               out uint value, preparsed, report, report.Length) != HidpStatusSuccess)
            return 0;

        var (min, max) = HatRange(preparsed);

        // Outside the declared range is the null state: centred, nothing held.
        if (value < min || value > max) return 0;

        // Normalised to 0-7 clockwise from north, whatever the device counts from.
        return (value - min) switch
        {
            0 => HatUp,
            1 => HatUp | HatRight,
            2 => HatRight,
            3 => HatRight | HatDown,
            4 => HatDown,
            5 => HatDown | HatLeft,
            6 => HatLeft,
            7 => HatLeft | HatUp,
            _ => 0,
        };
    }

    /// <summary>What the device says its hat counts from and to, or (1, 0) — an empty range — if
    /// it will not say, which reads as "no direction is ever held".</summary>
    private static readonly Dictionary<IntPtr, (uint Min, uint Max)> HatRanges = [];

    /// <summary>
    /// The hat's declared logical range, read once per device.
    ///
    /// Failing to read it deliberately disables the D-pad rather than falling back to a guess.
    /// A guess that is wrong in the direction seen here does not lose the D-pad, it jams it —
    /// and a permanently held direction breaks shortcut recording for the whole pad, which is far
    /// worse than a D-pad that simply does not register.
    /// </summary>
    private static (uint Min, uint Max) HatRange(IntPtr preparsed)
    {
        lock (Gate)
        {
            if (HatRanges.TryGetValue(preparsed, out var known)) return known;
        }

        var range = ReadHatRange(preparsed);

        lock (Gate) HatRanges[preparsed] = range;

        Log.Info(range.Min <= range.Max
                 ? $"Controller D-pad numbers its directions {range.Min}-{range.Max}."
                 : "Controller D-pad range could not be read; its D-pad will not be used.");

        return range;
    }

    private static (uint Min, uint Max) ReadHatRange(IntPtr preparsed)
    {
        // Empty range: Min above Max, so every value falls outside it.
        var none = (Min: 1u, Max: 0u);

        try
        {
            var caps = new HIDP_VALUE_CAPS[MaxValueCaps];
            ushort count = MaxValueCaps;

            if (HidP_GetValueCaps(HidpInput, caps, ref count, preparsed) != HidpStatusSuccess)
                return none;

            for (int i = 0; i < count && i < caps.Length; i++)
            {
                if (caps[i].UsagePage != UsagePageGenericDesktop) continue;
                if (caps[i].Usage != UsageHatSwitch) continue;
                if (caps[i].LogicalMax <= caps[i].LogicalMin) continue;

                return ((uint)caps[i].LogicalMin, (uint)caps[i].LogicalMax);
            }

            return none;
        }
        catch
        {
            return none;
        }
    }

    /// <summary>
    /// The D-pad's four bits, kept well above the real buttons.
    ///
    /// Bits 24-27, so a pad with a great many buttons cannot collide with them. Buttons are
    /// therefore capped at 24 rather than 32, which is more than any controller this is for.
    /// </summary>
    private const uint HatUp = 1u << 24;
    private const uint HatDown = 1u << 25;
    private const uint HatLeft = 1u << 26;
    private const uint HatRight = 1u << 27;

    /// <summary>Every D-pad bit, for callers that need to treat the pad as one control.</summary>
    public const uint HatBits = HatUp | HatDown | HatLeft | HatRight;

    /// <summary>
    /// The parsed report layout for a device, fetched by handle rather than by opening it.
    ///
    /// Cached because it never changes for a given device and fetching it allocates. Freed in
    /// Invalidate, which the watcher triggers whenever devices come or go.
    /// </summary>
    private static IntPtr PreparsedFor(IntPtr device)
    {
        lock (Gate)
        {
            if (Preparsed.TryGetValue(device, out var cached)) return cached;
        }

        uint size = 0;
        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size) != 0 || size == 0)
            return IntPtr.Zero;

        IntPtr buffer = Marshal.AllocHGlobal((int)size);

        if (GetRawInputDeviceInfo(device, RIDI_PREPARSEDDATA, buffer, ref size) == uint.MaxValue)
        {
            Marshal.FreeHGlobal(buffer);
            return IntPtr.Zero;
        }

        lock (Gate)
        {
            // Another report for the same device can have raced in here. Keep one, free the other.
            if (Preparsed.TryGetValue(device, out var existing))
            {
                Marshal.FreeHGlobal(buffer);
                return existing;
            }

            Preparsed[device] = buffer;
            return buffer;
        }
    }

    /// <summary>Vendor and product for a device handle, from its raw input info.</summary>
    private static (ushort Vendor, ushort Product) IdsFor(IntPtr device)
    {
        uint size = (uint)Marshal.SizeOf<RID_DEVICE_INFO>();
        var info = new RID_DEVICE_INFO { cbSize = size };

        if (GetRawInputDeviceInfo(device, RIDI_DEVICEINFO, ref info, ref size) == uint.MaxValue)
            return (0, 0);

        return info.dwType == RIM_TYPEHID
             ? ((ushort)info.hid.dwVendorId, (ushort)info.hid.dwProductId)
             : ((ushort)0, (ushort)0);
    }

    /// <summary>
    /// Names for the buttons of pads worth naming, keyed by vendor and product.
    ///
    /// HID gives numbers, not names — button 3 is button 3 whatever is printed on it. For the
    /// controllers most people own the mapping is known and stable, and "Circle + Options" reads
    /// a great deal better than "Button 3 + Button 10". Anything unrecognised falls back to
    /// numbers, which is honest rather than wrong.
    /// </summary>
    // Declared before the table that references them, and that ordering is load-bearing.
    //
    // Static field initializers run in declaration order, so with these below the table the
    // table was built while they were still null — every lookup would have succeeded and handed
    // back nothing, and every button would have fallen through to "Button 9 + Button 10" with
    // no error anywhere. The compiler caught it as a nullable warning; it would otherwise have
    // looked like the name table simply not working.
    private static readonly string[] DualSense =
    [
        "Square", "Cross", "Circle", "Triangle", "L1", "R1", "L2", "R2",
        "Create", "Options", "L3", "R3", "PS", "Touchpad", "Mute",
    ];

    private static readonly string[] DualShock4 =
    [
        "Square", "Cross", "Circle", "Triangle", "L1", "R1", "L2", "R2",
        "Share", "Options", "L3", "R3", "PS", "Touchpad",
    ];

    /// <summary>
    /// Where each numbered button sits on the pad.
    ///
    /// Parallel to the name arrays above, and in the same order, because they describe the same
    /// buttons — the names are for reading, these are for drawing. Both DualSense and DualShock
    /// share this order for the first fourteen; only the mute button beyond it is new.
    /// </summary>
    private static readonly PadControl[] PlayStationControls =
    [
        PadControl.FaceLeft,    // Square
        PadControl.FaceDown,    // Cross
        PadControl.FaceRight,   // Circle
        PadControl.FaceUp,      // Triangle
        PadControl.L1, PadControl.R1, PadControl.L2, PadControl.R2,
        PadControl.Select, PadControl.Start, PadControl.L3, PadControl.R3,
        PadControl.Home, PadControl.Touchpad, PadControl.Mute,
    ];

    private sealed record Layout(string[] Names, PadControl[] Controls, PadFamily Family);

    private static readonly Dictionary<(ushort, ushort), Layout> Layouts = new()
    {
        [(0x054C, 0x0CE6)] = new(DualSense,  PlayStationControls, PadFamily.PlayStation),   // DualSense
        [(0x054C, 0x0DF2)] = new(DualSense,  PlayStationControls, PadFamily.PlayStation),   // DualSense Edge
        [(0x054C, 0x05C4)] = new(DualShock4, PlayStationControls, PadFamily.PlayStation),   // DualShock 4, first revision
        [(0x054C, 0x09CC)] = new(DualShock4, PlayStationControls, PadFamily.PlayStation),   // DualShock 4, second revision
    };

    /// <summary>
    /// Which set of symbols to draw for a pad.
    ///
    /// A known PlayStation layout gets PlayStation shapes. Everything else — Xbox pads, and the
    /// third-party pads that overwhelmingly imitate them — gets Xbox symbols, which is both the
    /// commoner case and the safer guess.
    /// </summary>
    public static PadFamily FamilyOf(ushort vendor, ushort product)
        => Layouts.TryGetValue((vendor, product), out var layout) ? layout.Family : PadFamily.Xbox;

    /// <summary>
    /// A mask broken into the controls it names, in a fixed order so one combination always reads
    /// the same way.
    /// </summary>
    public static IReadOnlyList<PadPress> Decode(uint mask, ushort vendor, ushort product)
    {
        var presses = new List<PadPress>();
        if (mask == 0) return presses;

        Layouts.TryGetValue((vendor, product), out var layout);

        for (int bit = 0; bit < 24; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;

            presses.Add(layout is not null && bit < layout.Controls.Length
                        ? new PadPress(layout.Controls[bit])
                        : new PadPress(PadControl.Unknown, bit + 1));
        }

        if ((mask & HatUp) != 0) presses.Add(new PadPress(PadControl.DpadUp));
        if ((mask & HatDown) != 0) presses.Add(new PadPress(PadControl.DpadDown));
        if ((mask & HatLeft) != 0) presses.Add(new PadPress(PadControl.DpadLeft));
        if ((mask & HatRight) != 0) presses.Add(new PadPress(PadControl.DpadRight));

        return presses;
    }

    /// <summary>Human form of a mask, using the pad's own button names where they are known.</summary>
    public static string Describe(uint mask, ushort vendor, ushort product)
    {
        if (mask == 0) return "";

        Layouts.TryGetValue((vendor, product), out var layout);
        var names = layout?.Names;

        var parts = new List<string>();

        for (int bit = 0; bit < 24; bit++)
        {
            if ((mask & (1u << bit)) == 0) continue;

            parts.Add(names is not null && bit < names.Length ? names[bit] : $"Button {bit + 1}");
        }

        // Named rather than numbered whatever the pad is: a hat switch is a D-pad on every
        // controller that has one, so there is nothing to look up.
        if ((mask & HatUp) != 0) parts.Add("D-pad Up");
        if ((mask & HatDown) != 0) parts.Add("D-pad Down");
        if ((mask & HatLeft) != 0) parts.Add("D-pad Left");
        if ((mask & HatRight) != 0) parts.Add("D-pad Right");

        return string.Join(" + ", parts);
    }

    // ---- interop ----

    private const int MaxButtons = 64;

    private const ushort UsagePageButton = 0x09;
    private const ushort UsagePageGenericDesktop = 0x01;
    private const ushort UsageHatSwitch = 0x39;
    private const int HidpInput = 0;
    private const int HidpStatusSuccess = 0x00110000;

    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const uint RIDI_DEVICEINFO = 0x2000000b;
    private const uint RIM_TYPEHID = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_HID
    {
        public uint dwVendorId, dwProductId, dwVersionNumber;
        public ushort usUsagePage, usUsage;
    }

    /// <summary>
    /// Declared with the union arm that is actually the largest.
    ///
    /// The keyboard arm is bigger than the HID one, and a struct sized to HID alone would have
    /// Windows write past the end of it — the same 24-versus-32-byte mistake that made controller
    /// detection fail intermittently the first time round. Explicit layout with every arm at the
    /// same offset says what is meant and gets the size right.
    /// </summary>
    [StructLayout(LayoutKind.Explicit)]
    private struct RID_DEVICE_INFO
    {
        [FieldOffset(0)] public uint cbSize;
        [FieldOffset(4)] public uint dwType;
        [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
        [FieldOffset(8)] public RID_DEVICE_INFO_KEYBOARD keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RID_DEVICE_INFO_KEYBOARD
    {
        public uint dwType, dwSubType, dwKeyboardMode,
                    dwNumberOfFunctionKeys, dwNumberOfIndicators, dwNumberOfKeysTotal;
    }

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command,
                                                     IntPtr data, ref uint size);

    [DllImport("user32.dll", EntryPoint = "GetRawInputDeviceInfoW")]
    private static extern uint GetRawInputDeviceInfo(IntPtr device, uint command,
                                                     ref RID_DEVICE_INFO data, ref uint size);

    private const ushort MaxValueCaps = 32;

    /// <summary>
    /// Declared by offset, and sized exactly.
    ///
    /// 72 bytes on both 32- and 64-bit — it contains no pointers — and hid.dll writes all of it
    /// per entry, so a shorter declaration would have it write past the end of the array. Only
    /// the four fields actually read are named; the rest is padding held by the explicit size.
    /// The last is the NotRange arm of a union, which is the arm in use for a hat switch since
    /// IsRange is false for one.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    private struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(56)] public ushort Usage;
    }

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [In, Out] HIDP_VALUE_CAPS[] caps,
                                                ref ushort capsLength, IntPtr preparsed);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage,
                                                 ushort linkCollection, ushort usage,
                                                 out uint value, IntPtr preparsed,
                                                 byte[] report, int reportLength);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
                                             ushort[] usageList, ref ushort usageLength,
                                             IntPtr preparsed, byte[] report, int reportLength);
}
