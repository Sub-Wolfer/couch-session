using CouchMode.Ui;

namespace CouchMode;

internal static class Program
{
    // Held for the process lifetime so a second launch can't start a competing switch.
    private static Mutex? _singleInstance;

    private static void Fatal(Exception? ex)
    {
        Log.Error("Couch Session could not start", ex ?? new Exception("Unknown error"));
        MessageBox.Show(string.Format(Ui.Words.FatalStart, ex?.Message, AppConfig.Directory),
                        AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
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
    /// [BUG] Two faults here hid each other, and together they let two copies run.
    ///
    /// The live copy never took the mutex. It was created with <c>initiallyOwned: false</c> and the
    /// ownership attempt sat behind <c>createdNew ||</c>, so the first process short-circuited and
    /// never called WaitOne — the mutex existed, was owned by nobody, and blocked nothing.
    ///
    /// That left the fallback as the only real check, and the fallback compared full executable
    /// *paths* on purpose, so a build in another folder was deliberately let through. Which means the
    /// one arrangement guaranteed to produce two running copies — a freshly published exe sitting in
    /// build\out beside the one already running from the project root — was the exact case it was
    /// written to permit.
    ///
    /// Now ownership is the test, taken properly and held for the life of the process, and the
    /// fallback matches on process name so a second copy is a second copy wherever it was launched
    /// from. Both are kept, because a named mutex can fail to be shared for reasons that have nothing
    /// to do with intent — one copy elevated and another not, a different logon session, a policy
    /// refusing global names — and every one of those failures looks like "nobody else is here".
    /// </summary>
    private static bool AlreadyRunning()
    {
        try
        {
            _singleInstance = new Mutex(initiallyOwned: false,
                                        @"Global\CouchSession.SingleInstance", out _);

            bool mine;

            try
            {
                mine = _singleInstance.WaitOne(TimeSpan.Zero);
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died without releasing it. Ownership passes to this process,
                // which is the right answer: whoever held it is gone.
                Log.Info("The previous copy exited without releasing the single-instance lock.");
                mine = true;
            }

            if (!mine)
            {
                Log.Info("Another copy holds the single-instance lock.");
                return true;
            }

            // Held from here until this process exits. Nothing gives it back deliberately — the app
            // that is running is the app, and it stops being that only by dying.
            if (SameExeRunning())
            {
                Log.Info("Another copy is running despite the single-instance lock being free.");
                try { _singleInstance.ReleaseMutex(); } catch { /* nothing to give back */ }
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            // Most likely an access error on the global name. Fall back rather than give up:
            // the point is to avoid two copies, and there is another way to check.
            Log.Warn($"Single-instance lock unavailable ({ex.Message}); checking processes.");
            return SameExeRunning();
        }
    }

    /// <summary>
    /// Another process of this app, launched from anywhere.
    ///
    /// [BUG] This used to compare full paths, so a copy in a different folder was let through on
    /// purpose — the reasoning being that a developer testing a second build should not be blocked.
    /// In practice the folder that produces a second executable is build\out, three seconds after
    /// every publish, and two copies of this app is not a harmless situation: they both watch for
    /// Big Picture, both grab hotkeys, both drive the displays, and they fight.
    ///
    /// Matching on process name instead means a second copy is a second copy however it was started.
    /// The cost is that a genuinely separate build can no longer be run side by side, which is a
    /// thing worth doing roughly never, and is what --quit is for when it is.
    /// </summary>
    private static bool SameExeRunning()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();

            foreach (var other in System.Diagnostics.Process.GetProcessesByName(me.ProcessName))
            {
                try
                {
                    if (other.Id != me.Id) return true;
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
                // Environment.ProcessPath, not AppContext.BaseDirectory.
                //
                // [BUG] BaseDirectory looks like the obvious answer and is wrong for this app. The
                // csproj sets IncludeNativeLibrariesForSelfExtract, so a single-file build unpacks
                // itself into a temporary folder at startup and BaseDirectory points *there* — which
                // meant every run of this wrote a perfectly good icon into a temp directory Windows
                // then cleaned up. It reported success each time, and the icon beside the exe never
                // changed, which is exactly what it looked like from the outside: nothing happening.
                //
                // ProcessPath is the actual executable, extraction or no extraction.
                string beside = Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;
                string path = Path.Combine(beside, "CouchSession.ico");

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
