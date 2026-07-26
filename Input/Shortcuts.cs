namespace CouchMode.Input;

/// <summary>
/// A gate between the settings window and the live shortcuts.
///
/// This exists because of a problem that only appears once the feature works. A registered
/// global hotkey is consumed by Windows before any window sees it — that is the whole point of
/// it — which means that while someone is trying to *record* a new keyboard shortcut, pressing
/// the one already in force does nothing at all. The keypress goes to the hotkey, the hotkey
/// toggles the display, and the box waiting to record it never hears a thing. The controller
/// side has the same shape: the poll that watches for the combination would fire while the user
/// is holding buttons to choose it.
///
/// So capture and listening are made mutually exclusive. The settings window announces that it
/// is recording, whatever holds the shortcuts lets go for the duration, and takes them back
/// afterwards.
///
/// Static because there is exactly one keyboard and one set of pads, and threading a pair of
/// callbacks from the tray through the settings form into two controls would be more plumbing
/// than the fact deserves.
/// </summary>
public static class Shortcuts
{
    /// <summary>Raised when a shortcut box starts or stops listening. True means "let go".</summary>
    public static event Action<bool>? CaptureChanged;

    private static int _capturing;

    /// <summary>Whether anything is currently recording a shortcut.</summary>
    public static bool Capturing => _capturing > 0;

    /// <summary>
    /// Counted rather than a plain flag. Two boxes can both be on screen, and a box losing
    /// focus while another gains it would otherwise release the shortcuts and immediately be
    /// told to take them back — or worse, in the wrong order, leaving them released.
    /// </summary>
    public static void BeginCapture()
    {
        if (Interlocked.Increment(ref _capturing) == 1) CaptureChanged?.Invoke(true);
    }

    public static void EndCapture()
    {
        if (Interlocked.Decrement(ref _capturing) == 0) CaptureChanged?.Invoke(false);

        // Never below zero. An unmatched release would make the next capture look like it was
        // already in progress, and the shortcuts would stay live through it.
        if (Volatile.Read(ref _capturing) < 0) Interlocked.Exchange(ref _capturing, 0);
    }
}
