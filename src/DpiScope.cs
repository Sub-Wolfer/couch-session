using System.Runtime.InteropServices;

namespace CouchMode;

/// <summary>
/// Runs a block of window calls in real screen pixels, whatever the app's own DPI mode.
///
/// Windows lies to processes about coordinates, on purpose and helpfully, and it is the single
/// most confusing thing about moving other people's windows around.
///
/// This app is system-DPI-aware: Windows fixes its idea of DPI to whatever the primary display was
/// at launch. Any window coordinate crossing that boundary is silently rescaled to match — so on a
/// 4K television at 150% with a 100% desk monitor, asking for a window 3840 wide produces one
/// 2560 wide, and reading it back reports 3840 again. Both numbers look right. The window is
/// visibly cut off.
///
/// It gets worse across processes: the documentation is explicit that when one application asks
/// about another with a different awareness level, the system rescales the answer to suit the
/// caller. Steam's Big Picture is per-monitor aware and we are not, so every rectangle we exchange
/// with it was being converted twice on the way through.
///
/// The display code has no such problem — EnumDisplaySettings and the CCD API always speak in real
/// pixels — which is why the two disagreed and why the mismatch only appeared on a display running
/// at something other than the primary's scale.
///
/// Scoped to the thread rather than fixed for the process, deliberately. Making the whole app
/// per-monitor aware would also change how its own window scales, and this window is drawn by hand
/// at fixed pixel sizes throughout — that is a large and unrelated piece of work. Here only the
/// calls that manipulate other people's windows need the truth.
/// </summary>
/// <remarks>
/// Must not be held across an await. The context belongs to the thread, and a continuation can
/// resume on a different one — which would leave the scope restoring a context on a thread that
/// never had it. Every use below wraps a synchronous block for exactly that reason.
/// </remarks>
internal readonly struct DpiScope : IDisposable
{
    private readonly IntPtr _previous;

    private DpiScope(IntPtr previous) => _previous = previous;

    /// <summary>Speak in real pixels until this is disposed.</summary>
    public static DpiScope PerMonitor()
    {
        try
        {
            return new DpiScope(SetThreadDpiAwarenessContext(PerMonitorAwareV2));
        }
        catch (EntryPointNotFoundException)
        {
            // Before Windows 10 1607. Nothing to switch to, and the old behaviour is what the
            // app had anyway, so this degrades to doing nothing rather than failing.
            return default;
        }
        catch (DllNotFoundException)
        {
            return default;
        }
    }

    public void Dispose()
    {
        if (_previous == IntPtr.Zero) return;

        try { SetThreadDpiAwarenessContext(_previous); }
        catch { /* restoring a context can only fail if setting it did */ }
    }

    /// <summary>DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, which is the handle value -4.</summary>
    private static readonly IntPtr PerMonitorAwareV2 = new(-4);

    [DllImport("user32.dll")]
    private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
