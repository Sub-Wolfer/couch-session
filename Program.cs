using CouchMode.Ui;

namespace CouchMode;

internal static class Program
{
    // Held for the process lifetime so a second launch can't start a competing switch.
    private static Mutex? _singleInstance;

    private static void Fatal(Exception? ex)
    {
        Log.Error("Couch Session could not start", ex ?? new Exception("Unknown error"));
        MessageBox.Show($"Couch Session hit an error and had to stop.\n\n{ex?.Message}\n\n"
                      + $"Details were written to:\n{AppConfig.Directory}",
                        "Couch Session", MessageBoxButtons.OK, MessageBoxIcon.Error);
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet =
        System.Runtime.InteropServices.CharSet.Auto)]
    private static extern uint RegisterWindowMessage(string message);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);

    /// <summary>
    /// Whether another copy of this app is already running.
    ///
    /// Two independent checks, because the mutex on its own is not reliable enough here. A
    /// named mutex can fail to be shared for reasons that have nothing to do with intent —
    /// one copy started elevated and another not, a different logon session, a policy that
    /// refuses global names — and every one of those failures looks like "no other instance"
    /// and quietly starts a second app that fights the first over the user's displays.
    ///
    /// So the mutex is tried first, and if it does not settle the question the process list
    /// does. Looking for another process running the same executable cannot be fooled by any
    /// of the above.
    /// </summary>
    private static bool AlreadyRunning()
    {
        try
        {
            _singleInstance = new Mutex(initiallyOwned: false,
                                        @"Global\CouchSession.SingleInstance",
                                        out bool createdNew);

            // Owning it is what marks this copy as the live one. A mutex that exists but is
            // held by nobody means the previous owner died without cleaning up, and this copy
            // is entitled to take over.
            if (createdNew || _singleInstance.WaitOne(TimeSpan.Zero))
            {
                if (!SameExeRunning()) return false;

                Log.Info("Another copy is running despite the mutex being free.");
                return true;
            }

            Log.Info("Another copy holds the single-instance mutex.");
            return true;
        }
        catch (Exception ex)
        {
            // Most likely an access error on the global name. Fall back rather than give up:
            // the point is to avoid two copies, and there is another way to check.
            Log.Warn($"Single-instance mutex unavailable ({ex.Message}); checking processes.");
            return SameExeRunning();
        }
    }

    /// <summary>Another process running the same executable file.</summary>
    private static bool SameExeRunning()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();

            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path)) return false;

            foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                try
                {
                    if (other.Id == me.Id) continue;

                    // Compared by path so a different build in another folder is left alone;
                    // two copies of the same file are the case worth stopping.
                    if (string.Equals(other.MainModule?.FileName, path,
                                      StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch
                {
                    // A process we cannot inspect is one we cannot claim is ours.
                }
                finally { other.Dispose(); }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not check for another copy: {ex.Message}");
        }

        return false;
    }

    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // A crash before the tray icon exists leaves nothing on screen and nothing in the
        // taskbar, so the app simply appears not to start. Catch it and say what happened.
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Fatal(e.ExceptionObject as Exception);

        // A second launch opens the running copy's settings instead of starting another.
        //
        // Clicking the icon is a request to see the app, and answering it with "it is already
        // running" is a non-answer: the user knows what they wanted, and being told to go and
        // find a tray icon instead is worse than useless when the tray icon is what they were
        // trying to avoid hunting for.
        // Asked to shut the running copy down and go away.
        //
        // Checked before the single-instance test, and deliberately so: this copy is not trying
        // to become the app, it is trying to talk to the one that already is. Broadcast for the
        // same reason the settings message is — this process has no idea which window belongs
        // to the other one, and the two share nothing but an agreed message name.
        //
        // If nothing is running, this is a no-op and a success. "Make sure it is not running"
        // is the thing being asked for, and it is already true.
        // Redraw the file icon from the app's own mark.
        //
        // Before the single-instance test and before anything else starts, because this is not the app
        // running — it is a one-shot that writes a file and leaves. See Ui/IconFile.cs for why it lives
        // in the app rather than in a separate tool.
        if (args.Contains("--write-icon", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                // Beside the exe, which is the project root during development — where the csproj
                // expects to find it.
                string path = Path.Combine(AppContext.BaseDirectory, "CouchSession.ico");

                Ui.IconFile.WriteMonogram(path);

                Log.Info($"Wrote the application icon to {path}.");
                Console.WriteLine($"Wrote {path}. Rebuild to pick it up.");
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Could not write the application icon", ex);
                Console.WriteLine($"Could not write the icon: {ex.Message}");
                return 1;
            }
        }

        if (args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            uint quit = RegisterWindowMessage(TrayApp.BroadcastWatcher.QuitMessage);

            if (quit != 0) PostMessage(HWND_BROADCAST, quit, IntPtr.Zero, IntPtr.Zero);

            Log.Info("Asked any running copy to quit.");
            return 0;
        }

        if (AlreadyRunning())
        {
            // The two processes agree on a message id through its name — nothing else is
            // shared between them — and it is broadcast, since this copy has no idea which
            // window belongs to the other one.
            uint message = RegisterWindowMessage(TrayApp.BroadcastWatcher.ShowSettingsMessage);

            if (message != 0) PostMessage(HWND_BROADCAST, message, IntPtr.Zero, IntPtr.Zero);

            Log.Info("Already running; asked the running copy to show its settings.");
            return 0;
        }

        Log.Session("=== Couch Session started ===");

        // The previous build, left behind by an update, is only removable once it has exited —
        // which by now it has.
        Updates.CleanUp();

        // Likewise the Run entry from an earlier name. Renaming the app orphans it pointing at an
        // executable that no longer exists, and Windows goes on trying to launch it at every
        // sign-in — failing quietly, and leaving a startup item nobody can account for.
        StartupRegistration.ForgetOldNames();

        ResourceTuning.Apply();

        var config = AppConfig.Load();
        bool launchedByWindows = args.Contains(StartupRegistration.StartupArg, StringComparer.OrdinalIgnoreCase);

        // Re-register the logon task if start-with-Windows is meant to be on but the task is missing.
        // A rename clears the old-named task in ForgetOldNames above without ever creating a new one
        // (the switch is only written when the user opens Settings), so start-with-Windows silently
        // broke after the app was renamed. This heals it, and also re-points the task at the current
        // executable if the app was moved.
        if (config.IsConfigured && config.StartWithWindows && !StartupRegistration.IsEnabled())
        {
            StartupRegistration.Set(true);
            Log.Info("Start-with-Windows was set but the task was missing (likely after a rename); re-registered it.");
        }

        // Settings open on every manual launch — it's the confirmation step before the app can
        // take over your displays. A boot-time launch skips it and goes straight to the tray,
        // since nobody wants a dialog in their face every time they log in.
        //
        // Closing this window normally leaves the app running in the notification area — see
        // MinimizeToTrayOnClose. Without that, dismissing settings on first run would silently
        // leave nothing running at all.
        bool showSettings = !launchedByWindows || !config.IsConfigured;

        // The tray comes first, always.
        //
        // It owns every background feature — Auto HDR, the Big Picture watcher, the controller
        // triggers — so building it only after the settings window closed meant none of them
        // existed while that window was open. Launching a game from the desktop with settings
        // on screen did nothing at all, which is indistinguishable from the feature being
        // broken. Opening settings is now something a running app does, not a precondition for
        // becoming one.
        using var tray = new TrayApp(config, launchedByWindows);

        if (showSettings) tray.ShowSettingsOnLaunch();

        // --tv starts a session immediately, for a shortcut or Steam launcher entry.
        if (args.Contains("--tv", StringComparer.OrdinalIgnoreCase))
            tray.RequestTvMode();

        Application.Run();

        Log.Session("=== Couch Session exited ===");
        GC.KeepAlive(_singleInstance);
        return 0;
    }
}
