using System.Diagnostics;

namespace CouchMode.Session;

/// <summary>
/// Raises the running game's scheduling priority, and puts it back when it exits.
///
/// Modest by nature, and worth being honest about: on an idle machine this changes nothing at
/// all, because there is nothing to take time away from the game. It earns its place when
/// something else is competing — a browser with thirty tabs, a sync client indexing, a chat app
/// — which on a machine used at a desk and then on a television is most of the time.
///
/// Above Normal rather than High, deliberately. High starves audio and input threads that
/// belong to other processes, and the resulting stutter looks exactly like the problem this is
/// supposed to solve. Realtime is never appropriate from a utility.
/// </summary>
internal static class GamePriority
{
    private static ProcessPriorityClass? _previous;
    private static int _pid;

    /// <summary>Raise the given process, remembering what it was.</summary>
    public static void Raise(Process process)
    {
        try
        {
            _previous = process.PriorityClass;
            _pid = process.Id;

            if (_previous == ProcessPriorityClass.AboveNormal
             || _previous == ProcessPriorityClass.High
             || _previous == ProcessPriorityClass.RealTime)
            {
                // The game already asked for more than this. Leave it be.
                _previous = null;
                return;
            }

            process.PriorityClass = ProcessPriorityClass.AboveNormal;
            Log.Info($"Performance: raised process {_pid} to above normal priority.");
        }
        catch (Exception ex)
        {
            // Anti-cheat commonly refuses to let anything touch the game process. Expected.
            Log.Info($"Performance: could not raise the game's priority ({ex.Message}).");
            _previous = null;
        }
    }

    /// <summary>
    /// Restore the priority, if the process is still alive.
    ///
    /// Usually it is not — this runs because the game exited — and that is fine: priority dies
    /// with the process, so there is nothing to undo. The restore matters for the case where
    /// the session ends while the game is still running.
    /// </summary>
    public static void Restore()
    {
        if (_previous is not { } previous) { _pid = 0; return; }

        try
        {
            using var process = Process.GetProcessById(_pid);
            if (!process.HasExited) process.PriorityClass = previous;
        }
        catch
        {
            // Gone already, which is the normal case and needs no comment.
        }
        finally
        {
            _previous = null;
            _pid = 0;
        }
    }
}
