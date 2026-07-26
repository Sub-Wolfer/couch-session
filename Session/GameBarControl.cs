using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>
/// Reads and sets whether the Xbox Game Bar is switched on — the same thing as Windows Settings ▸
/// Gaming ▸ Xbox Game Bar, plus the capture half that lives beside it.
///
/// Why this app cares: the Xbox Guide button opens the Game Bar over whatever is on the television,
/// and dismissing an overlay nobody asked for is awkward from a sofa.
///
/// Three values, all per-user, none needing administrator rights:
///
///   AppCaptureEnabled           background recording and the capture overlay
///   GameDVR_Enabled             the game-DVR half of the same feature
///   UseNexusForGameBarEnabled   the controller button binding
///
/// All three, because clearing only the button binding was tried and is not enough: the overlay
/// still appeared and then dismissed itself a moment later, since the shell reads that value when
/// the button is pressed but the Game Bar has already begun opening by then. A flash of overlay
/// over a game is the thing being complained about, so half-preventing it is no use.
///
/// [BUG] This used to hold the setting for the lifetime of the app and put it back on the way out,
/// remembering each previous value. That shape was wrong for what this is. It is a Windows-wide
/// preference, like Game Mode beside it — somebody's answer to "do I want the Game Bar" does not
/// stop being their answer when they quit this app, and restoring it meant the setting silently
/// reverted every time. It also meant the switch showed what this app last wrote rather than what
/// Windows actually says, so changing it in Windows left the toggle lying. It is a plain mirror
/// now: read the real state, write it on the press, and leave it alone afterwards.
/// </summary>
public static class GameBarControl
{
    private static readonly (string Key, string Value)[] Switches =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        (@"System\GameConfigStore",                            "GameDVR_Enabled"),
        (@"Software\Microsoft\GameBar",                        "UseNexusForGameBarEnabled"),
    ];

    /// <summary>
    /// Whether the Game Bar is on right now.
    ///
    /// On unless something has switched it off: an absent value means Windows' own default, which is
    /// enabled. Any one of the three being zero counts as off, because any one of them being zero is
    /// enough to change what the Guide button does.
    /// </summary>
    public static bool IsEnabled()
    {
        try
        {
            foreach (var (key, value) in Switches)
            {
                using var registry = Registry.CurrentUser.OpenSubKey(key);

                if (registry?.GetValue(value) is int set && set == 0) return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the Xbox Game Bar setting: {ex.Message}");
            return true;
        }
    }

    /// <summary>Turn the Game Bar on or off. Returns false when Windows would not have it.</summary>
    public static bool Set(bool on)
    {
        int wanted = on ? 1 : 0;

        try
        {
            foreach (var (key, value) in Switches)
            {
                using var registry = Registry.CurrentUser.CreateSubKey(key);
                registry?.SetValue(value, wanted, RegistryValueKind.DWord);
            }

            // Read back rather than trusting the write, for the same reason Game Mode does: this is a
            // setting the user is standing in front of, and "it said it worked" is not good enough.
            bool now = IsEnabled();

            Log.Info(now == on
                ? $"Xbox Game Bar turned {(on ? "on" : "off")}."
                : $"Xbox Game Bar would not turn {(on ? "on" : "off")}; it is still {(now ? "on" : "off")}.");

            return now == on;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not set the Xbox Game Bar: {ex.Message}");
            return false;
        }
    }
}
