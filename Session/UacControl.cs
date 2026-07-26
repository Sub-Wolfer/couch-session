using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>
/// Reads and sets whether User Account Control (UAC) interrupts with a prompt.
///
/// Why this is here: a UAC prompt appears on the secure desktop, which no ordinary program can
/// draw on or send input to — including this one. So when a game or launcher asks for elevation, a
/// controller-only setup is stuck: the pad cannot move the secure-desktop cursor and there is no
/// way to answer it from the couch.
///
/// It uses the "never notify" settings rather than the master <c>EnableLUA</c> switch, on purpose.
/// EnableLUA=0 turns UAC off wholesale — but it needs a restart to take effect and it breaks Store
/// and other packaged apps. Setting ConsentPromptBehaviorAdmin to 0 (and taking the prompt off the
/// secure desktop) makes admin elevation happen silently instead: no prompt, nothing for the couch
/// to get stuck on, effective the instant it is written and with no restart. It is still a real
/// reduction in security, so the caller warns first and it goes through one elevation prompt.
/// </summary>
public static class UacControl
{
    private const string KeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    /// <summary>
    /// Whether UAC prompts still interrupt. True when UAC is on *and* set to prompt — the state
    /// where a game asking for elevation stops everything. Reads the same values it writes, and
    /// also honours a full EnableLUA=0 disable done elsewhere, so it matches what Windows actually
    /// does rather than assuming from a toggle.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(KeyPath);
            if (key is null) return true;

            // A full disable done in Windows (or by an older version of this app) wins outright.
            if (key.GetValue("EnableLUA") is int lua && lua == 0) return false;

            // Otherwise it is the admin prompt behaviour that decides: 0 means "elevate without
            // prompting", i.e. no interrupting dialog. Anything else prompts. Absent means the
            // Windows default, which prompts.
            return key.GetValue("ConsentPromptBehaviorAdmin") is not int cpba || cpba != 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the UAC setting: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Turn UAC prompting on or off. Writes to HKLM, so it goes through one elevation prompt —
    /// both values in a single elevated command so it is asked for just once.
    /// </summary>
    /// <returns>
    /// True if applied. False if the user dismissed the elevation prompt or it failed — the caller
    /// re-reads the real state and matches the control to it.
    /// </returns>
    public static bool SetEnabled(bool prompting)
    {
        // Prompting on = the Windows defaults (prompt for consent, on the secure desktop).
        // Prompting off = never notify (elevate silently, off the secure desktop).
        int consent = prompting ? 5 : 0;
        int secure = prompting ? 1 : 0;

        string reg(string name, int data) =>
            $@"reg add ""HKLM\{KeyPath}"" /v {name} /t REG_DWORD /d {data} /f";

        // EnableLUA is forced back to 1 as well, on both paths. This is the master UAC switch, and
        // it is normalised here to undo a full EnableLUA=0 disable — including one an earlier version
        // of this toggle wrote — which otherwise sticks: the state read below sees UAC as off no
        // matter what the prompt values say, so the switch could never be turned back off. With it
        // at 1, whether prompts appear is decided purely by the two values we set, which is what the
        // never-notify approach wants. All in one elevated shell, so it is still a single prompt.
        var psi = new ProcessStartInfo("cmd.exe",
            $"/c {reg("EnableLUA", 1)} & {reg("ConsentPromptBehaviorAdmin", consent)} & {reg("PromptOnSecureDesktop", secure)}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;

            p.WaitForExit(10_000);
            bool ok = p.HasExited && p.ExitCode == 0;

            Log.Info(ok
                ? $"UAC prompts turned {(prompting ? "on" : "off")}."
                : "The UAC change did not complete.");

            return ok;
        }
        catch (Win32Exception)
        {
            Log.Info("The UAC change was cancelled at the elevation prompt.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Changing the UAC setting failed", ex);
            return false;
        }
    }
}
