using System.Diagnostics;
using Microsoft.Win32;

namespace CouchMode;

/// <summary>
/// Start-with-Windows via a per-user logon scheduled task.
///
/// A task is used rather than the classic Run key because Windows deliberately holds ordinary Run
/// items back — roughly ten seconds after the desktop appears — to keep sign-in feeling snappy. A
/// logon task fires immediately instead, so Couch Session is sitting in the tray ready to throw the
/// picture on the TV the moment the user is in, which is the whole point for a controller-first
/// couch setup. The task runs with the interactive token at the ordinary (non-elevated) level, so
/// no admin rights are needed to register it — important for an exe people just download and run.
///
/// The old Run-key entry is cleared whenever the task is set, so a machine upgraded from an earlier
/// build never launches the app twice.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "CouchSession";

    /// <summary>Task Scheduler name (lives in the root folder).</summary>
    private const string TaskName = "Couch Session";

    /// <summary>
    /// What this app called itself before, so any leftover startup entries can be cleared out.
    ///
    /// A renamed app leaves the old entry behind pointing at an executable that no longer exists,
    /// and Windows keeps trying to launch it at every sign-in — failing silently, but leaving the
    /// user with a startup item they cannot account for.
    /// </summary>
    private static readonly string[] PreviousNames =
        ["CouchLauncher", "Couch Launcher", "CouchPotato", "Couch Potato", "CouchModeGaming", "CouchMode", "TvMode"];

    /// <summary>The flag that tells a startup launch to go straight to the tray, no dialog.</summary>
    public const string StartupArg = "--startup";

    /// <summary>Drop any startup entry left by an earlier name. Safe to call whenever.</summary>
    public static void ForgetOldNames()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key is not null)
            {
                foreach (var name in PreviousNames)
                {
                    if (key.GetValue(name) is not string) continue;
                    key.DeleteValue(name, throwOnMissingValue: false);
                    Log.Info($"Removed the startup entry left by the previous name ({name}).");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not tidy up old startup entries: {ex.Message}");
        }

        foreach (var name in PreviousNames)
            DeleteTask(name, quiet: true);
    }

    public static bool IsEnabled()
    {
        // The task is the real switch; the Run key is only ever a leftover from an older build.
        if (TaskExists(TaskName)) return true;

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the startup setting: {ex.Message}");
            return false;
        }
    }

    public static void Set(bool enabled)
    {
        // Whichever way this goes, the Run key must not survive — otherwise a machine that has both
        // the task and the key would launch Couch Session twice at sign-in.
        RemoveRunKey();

        if (!enabled)
        {
            DeleteTask(TaskName, quiet: false);
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe))
        {
            Log.Warn("Couldn't determine the executable path; skipping startup registration.");
            return;
        }

        if (RegisterLogonTask(exe))
            Log.Info("Start with Windows enabled (logon task, no startup delay).");
        else
            FallBackToRunKey(exe);
    }

    /// <summary>
    /// Write a logon-trigger task and hand it to schtasks. Returns false if registration fails, so
    /// the caller can fall back to the Run key rather than leave the user with no startup at all.
    /// </summary>
    private static bool RegisterLogonTask(string exe)
    {
        string user = $"{Environment.UserDomainName}\\{Environment.UserName}";
        string xmlPath = Path.Combine(Path.GetTempPath(), $"couchlauncher-startup-{Guid.NewGuid():N}.xml");

        try
        {
            // UTF-16 WITH the byte-order mark. The XML declares encoding="UTF-16", and schtasks
            // relies on the BOM to detect it — written without one it reads the file as ANSI, sees
            // garbage, and rejects the whole thing as "malformed", which is what sent startup back to
            // the delayed Run key. Encoding.Unicode emits the BOM; a bare UnicodeEncoding did not.
            File.WriteAllText(xmlPath, BuildTaskXml(exe, user), System.Text.Encoding.Unicode);

            // /F overwrites any existing task; /XML pulls in every setting above (no delay, interactive,
            // limited rights) so nothing depends on schtasks' own defaults.
            var (code, output) = RunSchtasks($"/Create /TN \"{TaskName}\" /XML \"{xmlPath}\" /F");
            if (code == 0) return true;

            Log.Warn($"Registering the startup task failed (exit {code}): {output.Trim()}");
            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Registering the startup task threw: {ex.Message}");
            return false;
        }
        finally
        {
            try { if (File.Exists(xmlPath)) File.Delete(xmlPath); } catch { /* temp file, ignore */ }
        }
    }

    /// <summary>
    /// LogonType InteractiveToken keeps the app in the user's own session with its tray icon; RunLevel
    /// LeastPrivilege keeps registration admin-free. Delay PT0S and no ExecutionTimeLimit are what make
    /// it launch at once and stay running for the whole session.
    /// </summary>
    private static string BuildTaskXml(string exe, string user) =>
        $"""
        <?xml version="1.0" encoding="UTF-16"?>
        <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <RegistrationInfo>
            <Description>Launches Couch Session to the tray at sign-in.</Description>
          </RegistrationInfo>
          <Triggers>
            <LogonTrigger>
              <Enabled>true</Enabled>
              <UserId>{Escape(user)}</UserId>
              <Delay>PT0S</Delay>
            </LogonTrigger>
          </Triggers>
          <Principals>
            <Principal id="Author">
              <UserId>{Escape(user)}</UserId>
              <LogonType>InteractiveToken</LogonType>
              <RunLevel>LeastPrivilege</RunLevel>
            </Principal>
          </Principals>
          <Settings>
            <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
            <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
            <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
            <AllowHardTerminate>false</AllowHardTerminate>
            <StartWhenAvailable>false</StartWhenAvailable>
            <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
            <IdleSettings>
              <StopOnIdleEnd>false</StopOnIdleEnd>
              <RestartOnIdle>false</RestartOnIdle>
            </IdleSettings>
            <AllowStartOnDemand>true</AllowStartOnDemand>
            <Enabled>true</Enabled>
            <Hidden>false</Hidden>
            <RunOnlyIfIdle>false</RunOnlyIfIdle>
            <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
            <Priority>5</Priority>
          </Settings>
          <Actions Context="Author">
            <Exec>
              <Command>{Escape(exe)}</Command>
              <Arguments>{StartupArg}</Arguments>
            </Exec>
          </Actions>
        </Task>
        """;

    private static void FallBackToRunKey(string exe)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            key.SetValue(ValueName, $"\"{exe}\" {StartupArg}");
            Log.Info("Start with Windows enabled via the Run key (task registration was unavailable).");
        }
        catch (Exception ex)
        {
            Log.Error("Updating the startup setting failed", ex);
        }
    }

    private static void RemoveRunKey()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not clear the old Run-key startup entry: {ex.Message}");
        }
    }

    private static bool TaskExists(string name)
    {
        var (code, _) = RunSchtasks($"/Query /TN \"{name}\"");
        return code == 0;
    }

    private static void DeleteTask(string name, bool quiet)
    {
        if (!TaskExists(name)) return;

        var (code, output) = RunSchtasks($"/Delete /TN \"{name}\" /F");
        if (code == 0)
        {
            if (!quiet) Log.Info("Start with Windows disabled.");
            else Log.Info($"Removed the startup task left by the previous name ({name}).");
        }
        else if (!quiet)
        {
            Log.Warn($"Removing the startup task failed (exit {code}): {output.Trim()}");
        }
    }

    private static (int Code, string Output) RunSchtasks(string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return (-1, "schtasks did not start");

            string output = proc.StandardOutput.ReadToEnd() + proc.StandardError.ReadToEnd();
            proc.WaitForExit(10_000);
            return (proc.HasExited ? proc.ExitCode : -1, output);
        }
        catch (Exception ex)
        {
            return (-1, ex.Message);
        }
    }

    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
