using System.Runtime.InteropServices;

namespace CouchMode.Input;

/// <summary>
/// Moves the real mouse pointer and presses its buttons, from synthesised input.
///
/// SendInput rather than SetCursorPos: SetCursorPos teleports the pointer and games that read raw
/// mouse motion never see it, whereas SendInput with a relative move enters the same input stream
/// a real mouse does, so a window cannot tell the difference. That is the whole point here — a
/// launcher that ignores a controller has to believe a mouse is present.
///
/// It cannot reach a UAC prompt or the lock screen: those live on the secure desktop, which by
/// design accepts input from nothing running as an ordinary program. Nothing to be done about
/// that, and it is the one place a physical mouse is still required.
/// </summary>
internal static class MouseInjector
{
    /// <summary>Move the pointer by a pixel delta, the way a mouse reports motion.</summary>
    public static void Move(int dx, int dy)
    {
        if (dx == 0 && dy == 0) return;

        Send(new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = MOUSEEVENTF_MOVE },
            },
        });
    }

    public static void LeftDown() => Button(MOUSEEVENTF_LEFTDOWN);
    public static void LeftUp() => Button(MOUSEEVENTF_LEFTUP);
    public static void RightDown() => Button(MOUSEEVENTF_RIGHTDOWN);
    public static void RightUp() => Button(MOUSEEVENTF_RIGHTUP);

    /// <summary>The wheel, in notches. Positive scrolls up.</summary>
    public static void Wheel(int notches)
    {
        if (notches != 0) WheelRaw(notches * WheelDelta);
    }

    /// <summary>
    /// The wheel, in raw units where one notch is 120. Sending amounts smaller than a notch is
    /// what makes controller scrolling smooth — modern apps treat a stream of small deltas as
    /// high-resolution scroll rather than one line at a time.
    /// </summary>
    public static void WheelRaw(int units)
    {
        if (units == 0) return;

        Send(new INPUT
        {
            type = InputMouse,
            U = new InputUnion
            {
                mi = new MOUSEINPUT { mouseData = units, dwFlags = MOUSEEVENTF_WHEEL },
            },
        });
    }

    private static void Button(uint flag)
    {
        Send(new INPUT
        {
            type = InputMouse,
            U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flag } },
        });
    }

    private static DateTime _lastLog = DateTime.MinValue;

    private static void Send(INPUT input)
    {
        var one = new[] { input };
        SendInput(1, one, Marshal.SizeOf<INPUT>());

        // Every injection — move, wheel, or click, from any path — passes through here, and each one
        // resets the display idle timer. Logged (throttled) with the flag bits so the exact kind is
        // unmistakable: 0x1 move, 0x800 wheel, 0x2/0x4 left, 0x8/0x10 right. This catches whatever is
        // holding a screen awake even if it comes from a path with no logging of its own.
        if (DateTime.UtcNow - _lastLog > TimeSpan.FromSeconds(4))
        {
            _lastLog = DateTime.UtcNow;
            Log.Info($"MouseInjector sent input (flags 0x{input.U.mi.dwFlags:X}); this resets the display idle timer.");
        }
    }

    // ---- interop ----

    private const int InputMouse = 0;
    private const uint MOUSEEVENTF_MOVE = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_WHEEL = 0x0800;
    private const int WheelDelta = 120;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public int mouseData;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
