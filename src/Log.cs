using System.Text;

namespace CouchMode;

/// <summary>
/// Minimal append-only log next to the config. Logging must never throw: it runs inside the
/// recovery path, and an exception there would defeat the point of having a recovery path.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();   // System.Threading.Lock is .NET 9+
    private static string FilePath => Path.Combine(AppConfig.Directory, "couchsession.log");

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message) => Write("ERROR", message);

    /// <summary>
    /// How many warnings and errors have been written since the app started, and the last one's text.
    ///
    /// Counted in memory rather than read back from the file: the point is to be able to say "something
    /// went wrong while you were playing" on a page someone is looking at, without parsing a log on a
    /// timer. Reset only by restarting, which is the same lifetime as the run it describes.
    /// </summary>
    public static int TroubleCount { get; private set; }

    /// <summary>The most recent warning or error, or null when the run has been clean.</summary>
    public static string? LastTrouble { get; private set; }

    /// <summary>
    /// One logged warning or error, kept in memory so the home page can list them.
    /// </summary>
    /// <param name="Count">How many times this same message has been logged.</param>
    /// <param name="Seq">Which warning of the run this most recently was, for the dismissal mark.</param>
    public readonly record struct TroubleNote(string Level, DateTime When, string Message,
                                              int Count, int Seq);

    /// <summary>
    /// The warnings and errors of this run, newest last, capped.
    ///
    /// Held in memory rather than read back out of the file. The home page says how many there have
    /// been; being able to open that and read them is the difference between "something went wrong"
    /// and knowing what — without sending anyone to a text file in AppData, which is precisely the
    /// thing a person on a sofa with a controller cannot do.
    ///
    /// Capped because this is a diagnostic aid, not a second log: a run that produces hundreds is
    /// telling you something the newest twenty-five already say.
    /// </summary>
    private const int TroubleKept = 25;

    private static readonly List<TroubleNote> Troubles = new();

    private static readonly object TroubleGate = new();

    /// <summary>A snapshot, safe to enumerate while more are being written.</summary>
    public static IReadOnlyList<TroubleNote> RecentTrouble
    {
        get { lock (TroubleGate) return Troubles.ToArray(); }
    }

    public static void Error(string message, Exception ex) =>
        Write("ERROR", $"{message}: {ex.GetType().Name}: {ex.Message}");

    public static void Session(string marker) => Write("----", marker);

    private static void Write(string level, string message)
    {
        // Noted before anything that can fail. A warning that could not be written to disk is still a
        // warning that happened, and is arguably the more important one to surface.
        if (level is "WARN" or "ERROR")
        {
            TroubleCount++;
            LastTrouble = message;

            lock (TroubleGate)
            {
                // Counted, not repeated.
                //
                // The same three warnings arrive once per session — a TV that was slow to wake, a
                // window that will not move — so a list of every occurrence is the same sentence four
                // times over and the one thing that only happened once is pushed off the end. One row
                // per distinct message, carrying how often and how recently, says strictly more in
                // less space.
                int at = Troubles.FindIndex(t => t.Message == message);

                if (at >= 0)
                {
                    var seen = Troubles[at];

                    Troubles.RemoveAt(at);
                    Troubles.Add(seen with
                    {
                        // An error that also happened as a warning is an error.
                        Level = seen.Level == "ERROR" ? "ERROR" : level,
                        When = DateTime.Now,
                        Count = seen.Count + 1,
                        Seq = TroubleCount,
                    });
                }
                else
                {
                    Troubles.Add(new TroubleNote(level, DateTime.Now, message, 1, TroubleCount));
                    if (Troubles.Count > TroubleKept) Troubles.RemoveAt(0);
                }
            }
        }

        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Directory);
                TrimIfLarge();
                File.AppendAllText(
                    FilePath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {level,-5}  {message}{Environment.NewLine}",
                    Encoding.UTF8);
            }
        }
        catch
        {
            // Deliberately swallowed. See summary above.
        }
    }

    private static void TrimIfLarge()
    {
        try
        {
            var info = new FileInfo(FilePath);
            if (!info.Exists || info.Length < 512 * 1024) return;

            // Keep the most recent half; old runs aren't worth unbounded disk.
            var lines = File.ReadAllLines(FilePath);
            File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
        }
        catch { }
    }
}
