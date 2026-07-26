using System.Runtime.InteropServices;
using System.Threading;
using CouchMode.Games;

namespace CouchMode.Input;

/// <summary>
/// Reads a controller's buttons by asking the device directly, rather than by subscribing to its
/// stream.
///
/// This exists to settle a conflict the app could not otherwise win. A DualSense streams input reports
/// continuously, even sitting untouched on a table, and while the app is subscribed to that stream via
/// raw input, Windows treats it as someone using the machine — so the display's idle timer never runs
/// and the screen never sleeps. Letting go of the subscription fixes that, and the log showed exactly
/// what it costs: every PS-button event ever recorded happened during a session, where the stream is
/// held open, and not one ever fired on the desktop, where it is not. Released means invisible. The
/// button that is supposed to start a session could not be seen.
///
/// The way out is that a subscription is not the only way to read a controller, and there are two of
/// them here.
///
/// The first is HidD_GetInputReport: ask the device for its current state as a one-off request. Clean,
/// cheap, and refused by a DualSense — the log said so plainly, and that refusal is what left this
/// whole mechanism unusable on the one controller it was written for.
///
/// The second is a direct read of the device's own report stream, which is how every library that
/// really does read a DualSense works — hidapi, SDL and DS4Windows all do exactly this. Instead of
/// asking the pad a question, the reports it is already sending are read straight off the device
/// handle. It is the same data raw input would deliver, taken before Windows' input stack sees it, so
/// nothing is delivered to the input queue and the idle timer is left alone.
///
/// Both are initiated by us. That is the property that matters: the device is never pushing anything
/// at Windows on our behalf, which is the difference between listening to someone talk and asking them
/// a question.
///
/// So the stream is still released when everything goes quiet, and the buttons are still readable. The
/// screen sleeps and the controller still works.
///
/// Everything here is read-only and uses the documented HID API: open the device, ask for a report,
/// parse it with the layout Windows itself supplies. Nothing is written to the controller, no other
/// process is touched, and the handle is opened shared so Steam and any game keep their own access.
/// </summary>
public static class HidPoll
{
    private const int MaxButtons = 32;

    /// <summary>How long a cached open handle is kept before being reopened.</summary>
    private static readonly TimeSpan HandleLife = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Whether this can actually read the pads attached right now — checked, not assumed.
    ///
    /// Nothing may depend on direct polling until it has demonstrated it works on the hardware in front
    /// of it. HidD_GetInputReport is not honoured by every device, and a controller that quietly
    /// declines it reads as "no buttons pressed" forever — which, for the code that decides whether the
    /// controller's stream can be released, means releasing it and going deaf. That is precisely what
    /// happened: the hotkey and the PS button both stopped working, because the thing meant to cover
    /// for the released stream had never been shown to work at all.
    ///
    /// So this asks the question directly and caches the answer: open every attached pad and see
    /// whether a report comes back. A "no" is not a failure, it just means the stream stays open.
    /// </summary>
    public static bool ProvenOn(ControllerWatcher watcher)
    {
        // While reports are flowing the answer cannot be trusted either way — a device already being
        // streamed may answer differently — so only ever established once, and never mid-release.
        if (_proven is { } known) return known;

        if (_unavailable) { _proven = false; return false; }

        bool any = false;

        try
        {
            foreach (var device in ControllerWatcher.Attached())
            {
                if (string.IsNullOrEmpty(device.Path)) continue;

                if (Read(device.Path) is not null) { any = true; break; }
            }
        }
        catch { any = false; }

        _proven = any;

        Log.Info(any
            ? "Direct controller polling works on this hardware; the input stream can be released "
            + "when idle without losing the buttons."
            : "Direct controller polling is answered by neither route on this controller, so the "
            + "input stream will be kept open while the pad is what starts a session.");

        return any;
    }

    private static bool? _proven;

    /// <summary>
    /// Buttons currently held on every HID pad, in the same shape and numbering
    /// <see cref="HidPad.HeldAll"/> produces — bit 0 is button 1 — so a caller cannot tell which way
    /// the answer was obtained.
    /// </summary>
    public static IReadOnlyList<(uint Buttons, ushort Vendor, ushort Product)> HeldAll()
    {
        var result = new List<(uint, ushort, ushort)>();

        if (_unavailable) return result;

        List<ControllerDevice> devices;
        try { devices = ControllerWatcher.Attached(); }
        catch { return result; }

        foreach (var device in devices)
        {
            if (string.IsNullOrEmpty(device.Path)) continue;

            try
            {
                if (Read(device.Path) is { } reading && reading.Buttons != 0)
                    result.Add(reading);
            }
            catch { /* one awkward device must not stop the others */ }
        }

        return result;
    }

    /// <summary>
    /// Ask one device for its current buttons.
    ///
    /// Null when the device cannot be read — unplugged mid-poll, or one of the rare pads that does not
    /// answer a GET_REPORT request. A null is not an error worth reporting every 80ms; the caller
    /// simply learns nothing about that pad this time round.
    /// </summary>
    private static (uint Buttons, ushort Vendor, ushort Product)? Read(string path)
    {
        var entry = EntryFor(path);
        if (entry is null) return null;

        var report = entry.Report;
        report[0] = entry.ReportId;

        // Asked first, read second.
        //
        // A device that answers GET_REPORT answers it immediately and costs nothing, so it stays the
        // first choice. A device that refuses — every DualSense tested — falls through to reading the
        // stream it is already sending, which is the route that actually works on Sony hardware.
        bool got = false;

        if (!entry.AskRefused)
        {
            got = HidD_GetInputReport(entry.Handle, report, report.Length);

            // Asked once and told no. A device that refuses this refuses it every time, and repeating
            // a control transfer twelve times a second to be told the same thing is pure waste on the
            // one pad this whole class exists for.
            if (!got && entry.CanStream) entry.AskRefused = true;
        }

        if (!got && entry.CanStream) got = ReadStream(entry, report);

        if (!got)
        {
            // A stale handle is the ordinary reason: the pad was unplugged, or the device was
            // re-enumerated underneath us. Drop it and let the next poll open a fresh one.
            Forget(path);
            return null;
        }

        ushort count = MaxButtons;
        var usages = new ushort[MaxButtons];

        if (HidP_GetUsages(HidpInput, UsagePageButton, 0, usages, ref count,
                           entry.Preparsed, report, report.Length) != HidpStatusSuccess)
            return null;

        uint mask = 0;
        for (int i = 0; i < count; i++)
        {
            int button = usages[i];
            if (button is >= 1 and <= 24) mask |= 1u << (button - 1);
        }

        return (mask, entry.Vendor, entry.Product);
    }

    /// <summary>
    /// How long to wait for the pad to send something. A DualSense reports around 250 times a second,
    /// so anything alive answers in single-digit milliseconds; this is generous by an order of
    /// magnitude and still short enough to sit inside an 80ms poll without being felt.
    /// </summary>
    private const int StreamWaitMs = 40;

    /// <summary>
    /// Read one report straight from the device.
    ///
    /// Overlapped, and with a timeout, because a plain ReadFile on a HID device blocks until the device
    /// has something to say — and a pad that has gone to sleep or dropped its link has nothing to say
    /// ever again. A blocking read on the UI thread's poll timer would freeze the app outright, so the
    /// wait is bounded and an unanswered read is cancelled and treated as "learned nothing this time".
    /// </summary>
    private static bool ReadStream(Entry entry, byte[] report)
    {
        if (!entry.CanStream) return false;

        ResetEvent(entry.Event);

        // The OVERLAPPED and the buffer both live in unmanaged memory, allocated once with the device.
        //
        // This is not tidiness. Windows writes to both of them *after* ReadFile returns, and keeps
        // doing so until the read completes — so neither may be anything the garbage collector is
        // entitled to move, and neither may live on a stack frame that could be gone by then. A
        // cancelled read that has not finished draining is exactly the case where that would bite, and
        // it would bite as memory corruption somewhere else entirely.
        Marshal.StructureToPtr(new NativeOverlapped { EventHandle = entry.Event }, entry.Overlapped,
                               false);

        try
        {
            bool done = ReadFile(entry.Handle, entry.Buffer, entry.BufferSize, out int read,
                                 entry.Overlapped);

            if (!done)
            {
                if (Marshal.GetLastWin32Error() != ErrorIoPending) return false;

                if (WaitForSingleObject(entry.Event, StreamWaitMs) != WaitObject0)
                {
                    // Nothing came. Ask for the read to be cancelled — CancelIoEx only asks — and give
                    // the cancellation a moment to land so the next read starts from a clean slate.
                    CancelIoEx(entry.Handle, entry.Overlapped);
                    WaitForSingleObject(entry.Event, 200);
                    return false;
                }

                if (!GetOverlappedResult(entry.Handle, entry.Overlapped, out read, false)) return false;
            }

            if (read <= 0) return false;

            Array.Clear(report);
            Marshal.Copy(entry.Buffer, report, 0, Math.Min(read, report.Length));

            return true;
        }
        catch { return false; }
    }

    // ---- open device handles, kept between polls ----

    private sealed class Entry
    {
        public IntPtr Handle;
        public IntPtr Preparsed;
        public byte[] Report = [];
        public byte ReportId;
        public ushort Vendor, Product;
        public DateTime OpenedAt;

        /// <summary>The completion event for the overlapped read, or zero when this handle cannot
        /// stream — which is the case for a handle opened without read access.</summary>
        public IntPtr Event;

        /// <summary>The OVERLAPPED Windows writes to while a read is in flight. Unmanaged, and one per
        /// device, so it cannot move or go out of scope underneath an outstanding read.</summary>
        public IntPtr Overlapped;

        /// <summary>Where the report lands, for the same reason.</summary>
        public IntPtr Buffer;

        public int BufferSize;

        public bool CanStream => Event != IntPtr.Zero && Overlapped != IntPtr.Zero
                              && Buffer != IntPtr.Zero;

        /// <summary>Set once this device has declined a GET_REPORT, so it is not asked again.</summary>
        public bool AskRefused;
    }

    private static readonly Dictionary<string, Entry> Open = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Set once if the HID libraries are not there at all, so this stops trying.</summary>
    private static bool _unavailable;

    private static Entry? EntryFor(string path)
    {
        lock (Gate)
        {
            if (Open.TryGetValue(path, out var existing))
            {
                // Reopened periodically. A handle to a device that has been through a sleep cycle or a
                // reconnect can survive as a handle while no longer answering, and a stale one that
                // never expires means a pad that silently stops responding until the app restarts.
                if (DateTime.UtcNow - existing.OpenedAt < HandleLife) return existing;

                Close(existing);
                Open.Remove(path);
            }

            var entry = OpenDevice(path);
            if (entry is not null) Open[path] = entry;

            return entry;
        }
    }

    private static Entry? OpenDevice(string path)
    {
        IntPtr handle = IntPtr.Zero;
        IntPtr wait = IntPtr.Zero;

        try
        {
            // Read access and overlapped, so the report stream can be read as well as asked for.
            //
            // Shared every way, and never written to. A controller is already open in Steam and often
            // in a game as well; taking it exclusively would break both, and is exactly the sort of
            // thing this app must never do. Read-only and shared is what every HID library uses, and
            // what makes several readers of the same pad ordinary rather than a conflict.
            handle = CreateFile(path, GenericRead, FileShareReadWrite, IntPtr.Zero,
                                OpenExisting, FileFlagOverlapped, IntPtr.Zero);

            if (handle != InvalidHandle && handle != IntPtr.Zero)
            {
                // Manual reset, starting unsignalled: one event per device, reused by every read.
                wait = CreateEvent(IntPtr.Zero, true, false, null);

                if (wait == IntPtr.Zero)
                {
                    // No event means no bounded wait, and an unbounded read on a sleeping pad hangs
                    // the poll. Drop back to the ask-only handle below rather than risk it.
                    CloseHandle(handle);
                    handle = InvalidHandle;
                }
            }

            if (handle == InvalidHandle || handle == IntPtr.Zero)
            {
                // Read access refused — a pad another process has opened more jealously than it should
                // have. A zero-access handle is still enough for GET_REPORT and for the device details,
                // so the pads that do answer that still work.
                handle = CreateFile(path, 0, FileShareReadWrite, IntPtr.Zero,
                                    OpenExisting, 0, IntPtr.Zero);
            }

            if (handle == InvalidHandle || handle == IntPtr.Zero) return null;

            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
            {
                if (wait != IntPtr.Zero) CloseHandle(wait);
                CloseHandle(handle);
                return null;
            }

            if (HidP_GetCaps(preparsed, out var caps) != HidpStatusSuccess || caps.InputReportByteLength <= 0)
            {
                HidD_FreePreparsedData(preparsed);
                if (wait != IntPtr.Zero) CloseHandle(wait);
                CloseHandle(handle);
                return null;
            }

            var attributes = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            HidD_GetAttributes(handle, ref attributes);

            return new Entry
            {
                Handle = handle,
                Preparsed = preparsed,
                Report = new byte[caps.InputReportByteLength],
                ReportId = 0,   // 0 asks for the only/first report; devices with ids fill it in themselves
                Vendor = attributes.VendorID,
                Product = attributes.ProductID,
                OpenedAt = DateTime.UtcNow,
                Event = wait,

                Overlapped = wait == IntPtr.Zero
                             ? IntPtr.Zero
                             : Marshal.AllocHGlobal(Marshal.SizeOf<NativeOverlapped>()),

                Buffer = wait == IntPtr.Zero
                         ? IntPtr.Zero
                         : Marshal.AllocHGlobal(caps.InputReportByteLength),

                BufferSize = caps.InputReportByteLength,
            };
        }
        catch (DllNotFoundException)
        {
            // No hid.dll on this machine, which should be impossible on Windows — but if it happens,
            // stop trying rather than throwing every poll.
            _unavailable = true;
            if (wait != IntPtr.Zero) CloseHandle(wait);
            if (handle != IntPtr.Zero && handle != InvalidHandle) CloseHandle(handle);
            return null;
        }
        catch
        {
            if (wait != IntPtr.Zero) CloseHandle(wait);
            if (handle != IntPtr.Zero && handle != InvalidHandle) CloseHandle(handle);
            return null;
        }
    }

    private static void Forget(string path)
    {
        lock (Gate)
        {
            if (!Open.TryGetValue(path, out var entry)) return;

            Close(entry);
            Open.Remove(path);
        }
    }

    private static void Close(Entry entry)
    {
        try
        {
            if (entry.Preparsed != IntPtr.Zero) HidD_FreePreparsedData(entry.Preparsed);

            // The handle before the event: closing a handle with a read outstanding completes it, and
            // the event has to still exist for that completion to signal.
            if (entry.Handle != IntPtr.Zero && entry.Handle != InvalidHandle) CloseHandle(entry.Handle);
            if (entry.Event != IntPtr.Zero) CloseHandle(entry.Event);

            // Only once the handle is closed, so nothing can still be writing into them.
            if (entry.Overlapped != IntPtr.Zero) Marshal.FreeHGlobal(entry.Overlapped);
            if (entry.Buffer != IntPtr.Zero) Marshal.FreeHGlobal(entry.Buffer);
        }
        catch { /* going away regardless */ }
    }

    /// <summary>Let every handle go — on a device change, and on the way out.</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            foreach (var entry in Open.Values) Close(entry);
            Open.Clear();
        }
    }

    // ---- interop ----

    private const uint GenericRead = 0x80000000;
    private const uint FileShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private const uint FileFlagOverlapped = 0x40000000;
    private const int ErrorIoPending = 997;
    private const uint WaitObject0 = 0;
    private static readonly IntPtr InvalidHandle = new(-1);

    private const int HidpInput = 0;
    private const ushort UsagePageButton = 0x09;
    private const int HidpStatusSuccess = 0x00110000;

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDD_ATTRIBUTES
    {
        public int Size;
        public ushort VendorID, ProductID, VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HIDP_CAPS
    {
        public ushort Usage, UsagePage;
        public ushort InputReportByteLength, OutputReportByteLength, FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps, NumberInputValueCaps, NumberInputDataIndices;
        public ushort NumberOutputButtonCaps, NumberOutputValueCaps, NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps, NumberFeatureValueCaps, NumberFeatureDataIndices;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr security,
                                            uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr file, IntPtr buffer, int count, out int read,
                                        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(IntPtr file, IntPtr overlapped,
                                                   out int transferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(IntPtr file, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEvent(IntPtr security, bool manualReset, bool initialState,
                                             string? name);

    [DllImport("kernel32.dll")] private static extern bool ResetEvent(IntPtr handle);

    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle,
                                                                              uint milliseconds);

    [DllImport("hid.dll")] private static extern bool HidD_GetPreparsedData(IntPtr device, out IntPtr preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_FreePreparsedData(IntPtr preparsed);
    [DllImport("hid.dll")] private static extern bool HidD_GetAttributes(IntPtr device, ref HIDD_ATTRIBUTES attributes);

    /// <summary>
    /// The whole point of this file: a request for the device's current state, rather than a
    /// subscription to everything it says. Nothing reaches the input queue, so the idle timer is
    /// untouched.
    /// </summary>
    [DllImport("hid.dll")] private static extern bool HidD_GetInputReport(IntPtr device, byte[] report, int length);

    [DllImport("hid.dll")] private static extern int HidP_GetCaps(IntPtr preparsed, out HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection,
                                             ushort[] usages, ref ushort usageLength,
                                             IntPtr preparsed, byte[] report, int reportLength);
}
