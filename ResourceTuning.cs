using System.Diagnostics;
using System.Runtime;
using System.Runtime.InteropServices;

namespace CouchMode;

/// <summary>
/// Keeps the app out of the way of whatever is actually using the machine.
///
/// This runs in the background while people game, so the goal is to be effectively invisible:
/// no measurable CPU while idle, and a working set small enough that it never competes with a
/// game for memory.
/// </summary>
internal static class ResourceTuning
{
    /// <summary>Applied once at startup.</summary>
    public static void Apply()
    {
        try
        {
            // Below-normal priority: nothing here is latency-sensitive, and a game should
            // always win a scheduling contest against a tray utility.
            using var me = Process.GetCurrentProcess();
            me.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not lower process priority: {ex.Message}");
        }

        try
        {
            // Sustained low latency is the wrong mode for a mostly-idle app: it defers
            // collections and grows the heap. Batch mode favours small footprint instead.
            GCSettings.LatencyMode = GCLatencyMode.Batch;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set the GC latency mode: {ex.Message}");
        }

        ReleaseMemory();
    }

    /// <summary>
    /// Compact the heap and hand the working set back to Windows.
    ///
    /// Called after the settings window closes and when a session starts — the two moments
    /// when this process has just finished doing something and is about to sit idle for hours.
    /// Windows pages the memory back in on demand if it is ever needed again, so the only cost
    /// is a few page faults on the rare occasion the window is reopened.
    /// </summary>
    public static void ReleaseMemory()
    {
        try
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

            using var me = Process.GetCurrentProcess();
            SetProcessWorkingSetSizeEx(me.Handle, -1, -1, 0);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not release memory: {ex.Message}");
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSizeEx(
        IntPtr process, nint minimumWorkingSetSize, nint maximumWorkingSetSize, uint flags);
}
