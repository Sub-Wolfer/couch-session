namespace CouchMode.Steam;

/// <summary>
/// Polls for the Big Picture window so TV mode can trigger however Steam was started —
/// the tray menu, a controller's Steam button, a desktop shortcut, or Steam's own autostart.
///
/// Polling rather than a window hook: a hook would mean injecting into another process or
/// running a global CBT hook, which is both heavier and exactly the kind of behaviour that
/// gets an unsigned binary flagged by anti-cheat and antivirus. A 1-second EnumWindows scan
/// costs nothing measurable.
/// </summary>
public sealed class BigPictureWatcher : IDisposable
{
    private readonly System.Windows.Forms.Timer _timer;
    private IntPtr _current = IntPtr.Zero;

    /// <summary>Big Picture just appeared. Argument is the window handle.</summary>
    public event Action<IntPtr>? Opened;

    /// <summary>Big Picture just went away.</summary>
    public event Action? Closed;


    /// <summary>Interval used while on the desktop, waiting for Big Picture to appear.</summary>
    private const int IdleIntervalMs = 1500;

    /// <summary>
    /// Interval used during a session. The only thing left to detect once we are on the TV is Big
    /// Picture going away — but that is the event that ends the session and closes the game, so it
    /// wants noticing promptly rather than up to four seconds later, which reads as the app having
    /// missed it entirely. An EnumWindows scan costs nothing measurable, so there is no reason to
    /// wait longer than this.
    /// </summary>
    private const int SessionIntervalMs = 1000;

    public BigPictureWatcher()
    {
        _timer = new System.Windows.Forms.Timer { Interval = IdleIntervalMs };
        _timer.Tick += (_, _) => Poll();
    }

    /// <summary>Back off polling while a session is running.</summary>
    public void SetSessionActive(bool active)
    {
        int wanted = active ? SessionIntervalMs : IdleIntervalMs;
        if (_timer.Interval != wanted) _timer.Interval = wanted;
    }

    /// <summary>
    /// Begin watching, adopting whatever is already on screen rather than reacting to it.
    ///
    /// A Big Picture that is already running when this app starts was not opened by anyone just now —
    /// most often the app was restarted while a session was up, so Steam is still exactly where the
    /// last run left it. Treating that as "Big Picture just opened" dragged the user into a session a
    /// second after launching the app, which the log caught: "=== Couch Session started ===" and
    /// "Big Picture opened" two seconds apart, with no button pressed and no controller connected.
    ///
    /// So the first look only records what is there. Opening it after that means what it always meant.
    /// </summary>
    private bool _adopted;

    public void Start()
    {
        // Only the very first time. Start is also called on every settings save, and adopting there
        // could swallow a Big Picture that opened in the same moment — rare, but it would look exactly
        // like the app ignoring the thing it exists to notice.
        if (_adopted) { _timer.Start(); return; }

        _adopted = true;

        try { _current = BigPictureLauncher.FindWindow(); }
        catch { _current = IntPtr.Zero; }

        if (_current != IntPtr.Zero)
            Log.Info("Big Picture was already open when the app started; adopting it rather than "
                   + "treating it as newly opened. Starting a session by hand takes it over — see "
                   + "TrayApp.LaunchBigPicture.");

        _timer.Start();
    }
    public void Stop() => _timer.Stop();

    /// <summary>
    /// Ignore the window for now — used after the user manually returns to desktop while
    /// Big Picture is still running, so we don't immediately switch them back to the TV.
    /// </summary>
    public void Suppress()
    {
        _suppressedUntilClosed = _current != IntPtr.Zero ? _current : IntPtr.Zero;
    }

    /// <summary>
    /// Start paying attention to Big Picture again.
    ///
    /// Suppression names a window to ignore "until closed", which is the right idea for the desktop —
    /// the user walked away from a Big Picture that is still running and must not be dragged back. But
    /// nothing clears it while that window stays open, so a new session would begin with its own shell
    /// already on the ignore list, and Big Picture closing during it — quit from the menu, Alt+F4, or a
    /// crash — was noticed and then discarded. Every session starts by calling this.
    /// </summary>
    public void ClearSuppression() => _suppressedUntilClosed = IntPtr.Zero;

    private IntPtr _suppressedUntilClosed = IntPtr.Zero;

    private void Poll()
    {
        IntPtr found;
        try
        {
            found = BigPictureLauncher.FindWindow();
        }
        catch (Exception ex)
        {
            Log.Warn($"Big Picture poll failed: {ex.Message}");
            return;
        }

        if (found != IntPtr.Zero && _current == IntPtr.Zero)
        {
            _current = found;
            if (found == _suppressedUntilClosed) return;   // user opted out of this session
            Log.Info("Big Picture opened.");
            Opened?.Invoke(found);
        }
        else if (found == IntPtr.Zero && _current != IntPtr.Zero)
        {
            bool wasSuppressed = _current == _suppressedUntilClosed;
            _current = IntPtr.Zero;
            _suppressedUntilClosed = IntPtr.Zero;

            if (wasSuppressed) return;
            Log.Info("Big Picture closed.");
            Closed?.Invoke();
        }
    }

    public void Dispose() => _timer.Dispose();
}
