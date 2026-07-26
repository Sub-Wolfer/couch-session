using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>
/// Reads and sets Windows Game Mode — the same switch as Settings ▸ Gaming ▸ Game Mode.
///
/// Split out of PerformanceTuning, where it was only ever applied at the start of a session and
/// never read at all. That is the wrong shape for what this setting is.
///
/// [BUG] Everything else PerformanceTuning does is scoped to a session: the power plan goes back
/// afterwards, so does notification silencing. Game Mode does not — the code even said so, in a
/// comment explaining that it is "a Windows-wide preference the user is as likely to want left on"
/// and is therefore never switched off again. So the toggle in this app was a Windows-wide setting
/// with a session-shaped trigger: turning it on wrote nothing, and did not take effect until the
/// next time the display moved to the television. Someone who switched Game Mode off in Windows,
/// came here, flicked our toggle and looked back at Windows saw absolutely nothing happen — the
/// setting was doing exactly what it was built to do, and what it was built to do was wrong.
///
/// It is now what it always looked like: a live mirror of the Windows setting. Reading it means the
/// toggle tells the truth even when Windows was changed elsewhere, and writing it means pressing it
/// does the thing on the spot.
///
/// Both values are written together. AutoGameModeEnabled is what modern Windows reads;
/// AllowAutoGameMode is the older name and is still honoured on some builds. Writing one and not
/// the other is how this ends up on for one Windows version and off for another.
/// </summary>
public static class GameModeControl
{
    private const string KeyPath = @"Software\Microsoft\GameBar";

    /// <summary>
    /// Whether Windows Game Mode is on right now.
    ///
    /// Defaults to true when the value is absent, because that is what Windows does: Game Mode ships
    /// on, and the value only appears once somebody has changed it.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            if (key is null) return true;

            return (key.GetValue("AutoGameModeEnabled") as int?
                 ?? key.GetValue("AllowAutoGameMode") as int?
                 ?? 1) != 0;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read Windows Game Mode: {ex.Message}");
            return true;
        }
    }

    /// <summary>Turn Game Mode on or off. Returns false when Windows would not have it.</summary>
    public static bool Set(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
            if (key is null) return false;

            key.SetValue("AutoGameModeEnabled", on ? 1 : 0, RegistryValueKind.DWord);
            key.SetValue("AllowAutoGameMode", on ? 1 : 0, RegistryValueKind.DWord);

            // Read back rather than trusting the write. This key is per-user and unprotected, so a
            // failure here is unlikely — but "it said it worked" is the one thing this app cannot
            // afford to say about a setting the user is standing in front of.
            bool now = IsEnabled();

            Log.Info(now == on
                ? $"Windows Game Mode turned {(on ? "on" : "off")}."
                : $"Windows Game Mode would not turn {(on ? "on" : "off")}; it is still {(now ? "on" : "off")}.");

            return now == on;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set Windows Game Mode: {ex.Message}");
            return false;
        }
    }
}
