using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using static CouchMode.Audio.AudioCom;

namespace CouchMode.Audio;

/// <summary>
/// Silence a game that has been left running while the user is back at their desk.
///
/// A session that drops to the desktop deliberately leaves the game alive — that is the whole point
/// of it, and of the disconnect swap — but a game does not know it has been put down. It keeps
/// playing its music, its menu loops and its gunfire into whatever speakers are now in front of the
/// person trying to answer an email. Muting it is the difference between "left running" and
/// "left running and unusable".
///
/// Done through the Windows audio session, which is the same thing the volume mixer drives. Nothing
/// is injected and the game's process is not touched: a session belongs to the audio engine rather
/// than to the application, so this is exactly as external as dragging the game's slider down by
/// hand. That matters, because the obvious alternatives — hooking the game or messing with its
/// process — are the sort of thing anti-cheat exists to notice.
///
/// Three things this deliberately gets right, because they are where a feature like this goes wrong:
///
///  • The mute is written down on disk before it is applied. If this app is killed while a game is
///    muted, nothing else on the machine will ever unmute it, and the user finds a permanently
///    silent game hours later with no idea why. <see cref="RecoverFromCrash"/> is the answer.
///  • The whole process tree is muted, not just the game's own process id. Plenty of games play
///    their audio from a child process — a launcher, a separate audio host, a Unreal helper — and
///    matching one pid would silence nothing at all.
///  • Only sessions this app muted are ever unmuted. Restoring by "unmute everything" would undo a
///    mute the user set by hand in the mixer, which they would rightly call a bug.
///
/// A game using WASAPI in exclusive mode cannot be muted this way, and nothing can do it from
/// outside. That is rare outside audiophile media players, and the failure is quiet: the sound
/// simply keeps playing.
/// </summary>
public static class GameAudio
{
    /// <summary>Process ids this app has muted and has not yet put back.</summary>
    private static readonly HashSet<uint> Muted = [];

    private static readonly object Gate = new();

    /// <summary>
    /// Where the muted process ids are parked so a crash cannot lose them.
    ///
    /// Beside the config rather than in it: this is a scribble about the current moment, not a
    /// setting, and it must not end up in a file the user might copy to another machine or restore
    /// from a backup.
    /// </summary>
    private static string RecoveryFile => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CouchSession", "muted-audio.json");

    private sealed record Held(uint Pid, string Name);

    /// <summary>
    /// Mute every audio session belonging to <paramref name="pid"/> or anything it started.
    ///
    /// Takes a process id rather than a Process. [BUG] It took a Process, supplied by the HDR game
    /// watcher — and that watcher only follows games on the HDR list, which is empty on a fresh
    /// install now that nothing pre-ticks it. So the common case was a game running, the user at
    /// their desk, and this returning at the first line because the app had no Process to hand.
    /// A window handle is always available for a running game; a Process often is not.
    ///
    /// Safe to call with a dead pid, with a game that has no audio, and twice in a row.
    /// </summary>
    public static void MuteProcess(uint pid, string label)
    {
        if (pid == 0) return;

        try
        {
            var family = Family(pid);
            if (family.Count == 0) return;

            // Written down first, applied second. The order is the whole safety net: a crash between
            // the two leaves a stale entry that recovery will find nothing to do about, which is
            // harmless, while a crash the other way round leaves a muted game nobody knows about.
            lock (Gate)
            {
                foreach (uint one in family) Muted.Add(one);
                SaveRecovery();
            }

            int hit = Apply(family, mute: true);

            // Named counts on both branches, because "it did not mute my game" is answerable from
            // the log only if the log distinguishes "found no session" from "never got here".
            Log.Info(hit > 0
                ? $"Muted {hit} audio session(s) for {label} (pid {pid}, {family.Count} process(es))."
                : $"{label} (pid {pid}) has no audio session to mute right now; "
                  + $"looked at {family.Count} process(es) across every playback device.");
        }
        catch (Exception ex)
        {
            // Never worth failing a session switch over. The worst case is a game that keeps talking.
            Log.Warn($"Could not mute the game's audio: {ex.Message}");
        }
    }

    /// <summary>Put back everything this app muted. Safe to call when nothing is muted.</summary>
    public static void Restore()
    {
        uint[] pids;

        lock (Gate)
        {
            if (Muted.Count == 0) return;
            pids = [.. Muted];
        }

        try
        {
            int hit = Apply([.. pids], mute: false);
            if (hit > 0) Log.Info($"Unmuted {hit} audio session(s).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not unmute the game's audio: {ex.Message}");
        }
        finally
        {
            // Cleared even when the unmute failed. The sessions are gone in the ordinary case —
            // the game closed — and holding onto ids forever would mean a later game inheriting a
            // recycled pid gets unmuted for no reason.
            lock (Gate)
            {
                Muted.Clear();
                ClearRecovery();
            }
        }
    }

    /// <summary>
    /// Undo a mute left behind by a previous run that did not get to finish.
    ///
    /// Called once at startup. Only touches processes that are still alive *and* still have the name
    /// they had when they were muted — Windows recycles process ids, and unmuting a stranger's audio
    /// because it inherited a number is precisely the bug this guards against.
    /// </summary>
    public static void RecoverFromCrash()
    {
        try
        {
            if (!File.Exists(RecoveryFile)) return;

            var held = JsonSerializer.Deserialize<List<Held>>(File.ReadAllText(RecoveryFile)) ?? [];

            ClearRecovery();
            if (held.Count == 0) return;

            var alive = new List<uint>();

            foreach (var one in held)
            {
                try
                {
                    using var process = Process.GetProcessById((int)one.Pid);
                    if (string.Equals(process.ProcessName, one.Name, StringComparison.OrdinalIgnoreCase))
                        alive.Add(one.Pid);
                }
                catch (ArgumentException) { /* gone, and its session went with it */ }
            }

            if (alive.Count == 0)
            {
                Log.Info("A previous run left audio muted, but none of those processes are still running.");
                return;
            }

            int hit = Apply(alive, mute: false);
            Log.Info($"Unmuted {hit} audio session(s) left muted by a previous run.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check for audio left muted by a previous run: {ex.Message}");
        }
    }

    /// <summary>Whether anything is muted right now — for the log and the bug report.</summary>
    public static bool AnythingMuted
    {
        get { lock (Gate) return Muted.Count > 0; }
    }

    /// <summary>
    /// Set or clear mute on every session belonging to one of <paramref name="pids"/>. Returns how
    /// many sessions were actually touched.
    ///
    /// Enumerated fresh each time rather than holding onto session objects. A game opens and closes
    /// audio sessions as it runs — a cutscene, a menu, a voice channel — so a session captured at
    /// mute time may not be the one making noise a minute later.
    /// </summary>
    /// <summary>What the last pass walked past, for the "nothing was muted" line only.</summary>
    private static int Seen;
    private static readonly SortedSet<uint> Owners = [];

    private static int Apply(IReadOnlyCollection<uint> pids, bool mute)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        int touched = 0;

        Seen = 0;
        Owners.Clear();

        try
        {
            // Every playback device, not just the default one.
            //
            // [BUG] This looked at the default endpoint alone, on the reasoning that Windows moves
            // default-device streams with the default, so after the swap the game would be on the
            // desk device. That is true of a stream opened on the default and not of one opened on a
            // named endpoint, which plenty of games and every audio middleware with a device picker
            // do — and it is not true at all in the window before Windows has finished moving them.
            // The session lives on whichever device it was opened against, so the honest thing is to
            // ask all of them. There are rarely more than a handful.
            if (enumerator.EnumAudioEndpoints(DataFlow.Render, DeviceState.Active, out var devices) != 0)
                return 0;

            if (devices.GetCount(out int deviceCount) != 0) return 0;

            for (int d = 0; d < deviceCount; d++)
            {
                if (devices.Item(d, out var device) != 0) continue;

                try { touched += ApplyOn(device, pids, mute); }
                catch (Exception ex) { Log.Warn($"Could not read sessions on one playback device: {ex.Message}"); }
                finally { Marshal.ReleaseComObject(device); }
            }

            // Only when a mute found nothing, and only then. "No session" is indistinguishable in the
            // log from "no devices", "sessions but none ours" and "ours, but the mute was refused",
            // and those want completely different fixes — so the one case worth explaining explains
            // itself rather than being reasoned about from the outside afterwards.
            if (mute && touched == 0)
                Log.Info($"Nothing was muted: {deviceCount} active playback device(s), "
                       + $"{Seen} session(s) seen, owners {(Owners.Count == 0 ? "(none)" : string.Join(", ", Owners))}.");

            Marshal.ReleaseComObject(devices);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return touched;
    }

    /// <summary>The same, for one endpoint.</summary>
    private static int ApplyOn(IMMDevice device, IReadOnlyCollection<uint> pids, bool mute)
    {
        var iid = typeof(IAudioSessionManager2).GUID;

        if (device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out object raw) != 0) return 0;

        var manager = (IAudioSessionManager2)raw;
        int touched = 0;

        try
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0) return 0;

            try
            {
                if (sessions.GetCount(out int count) != 0) return 0;

                for (int i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var control) != 0) continue;

                    try
                    {
                        if (control is not IAudioSessionControl2 identified) continue;
                        if (identified.GetProcessId(out uint pid) != 0) continue;

                        Seen++;
                        if (pid != 0) Owners.Add(pid);

                        if (!pids.Contains(pid)) continue;

                        // The system sounds session belongs to no application and must never be
                        // touched: silencing it takes the user's notification and error sounds with
                        // it. IsSystemSoundsSession returns S_OK for that one and S_FALSE otherwise,
                        // so zero is the value to skip on.
                        if (identified.IsSystemSoundsSession() == 0) continue;

                        if (control is not ISimpleAudioVolume volume) continue;

                        var context = Guid.Empty;
                        if (volume.SetMute(mute, ref context) == 0) touched++;
                    }
                    finally
                    {
                        if (control is not null && Marshal.IsComObject(control))
                            Marshal.ReleaseComObject(control);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(sessions);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(manager);
        }

        return touched;
    }

    private const uint ClsCtxAll = 23;

    /// <summary>
    /// The game's process id and every descendant's.
    ///
    /// Walked through the parent chain rather than asked for directly, because Windows has no "give
    /// me my children" call. One pass over every process on the machine, building parent to child,
    /// is cheap enough at the once-per-session rate this runs at.
    /// </summary>
    private static List<uint> Family(uint pid)
    {
        var wanted = new List<uint> { pid };

        try
        {
            var parents = new Dictionary<uint, uint>();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    uint parent = ParentOf((uint)process.Id);
                    if (parent != 0) parents[(uint)process.Id] = parent;
                }
                catch { /* a process that ended between the list and the question */ }
                finally { process.Dispose(); }
            }

            // Anything whose parent chain reaches the game. Bounded, so a cycle in a corrupt snapshot
            // of the process table cannot spin here forever.
            foreach (var (child, _) in parents)
            {
                uint at = child;

                for (int depth = 0; depth < 12; depth++)
                {
                    if (!parents.TryGetValue(at, out uint up)) break;

                    if (up == pid) { wanted.Add(child); break; }
                    at = up;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not walk the game's child processes: {ex.Message}");
        }

        return wanted;
    }

    private static uint ParentOf(uint pid)
    {
        IntPtr handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return 0;

        try
        {
            var info = new PROCESS_BASIC_INFORMATION();

            int status = NtQueryInformationProcess(handle, 0, ref info,
                                                   Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _);

            return status == 0 ? (uint)info.InheritedFromUniqueProcessId.ToInt64() : 0;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static void SaveRecovery()
    {
        try
        {
            var held = new List<Held>();

            foreach (uint pid in Muted)
            {
                try
                {
                    using var process = Process.GetProcessById((int)pid);
                    held.Add(new Held(pid, process.ProcessName));
                }
                catch (ArgumentException) { /* already gone */ }
            }

            Directory.CreateDirectory(Path.GetDirectoryName(RecoveryFile)!);
            File.WriteAllText(RecoveryFile, JsonSerializer.Serialize(held));
        }
        catch (Exception ex)
        {
            // The mute still happens. This only costs the crash safety net, which is worth a line in
            // the log and not worth abandoning the feature over.
            Log.Warn($"Could not record which audio sessions were muted: {ex.Message}");
        }
    }

    private static void ClearRecovery()
    {
        try { if (File.Exists(RecoveryFile)) File.Delete(RecoveryFile); }
        catch (Exception ex) { Log.Warn($"Could not clear the muted-audio record: {ex.Message}"); }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr ExitStatus;
        public IntPtr PebBaseAddress;
        public IntPtr AffinityMask;
        public IntPtr BasePriority;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass,
                                                        ref PROCESS_BASIC_INFORMATION info,
                                                        int size, out int returned);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
