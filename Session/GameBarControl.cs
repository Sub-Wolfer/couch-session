using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>
/// Switches the Xbox Game Bar off while Couch Session is running, and puts it back on the
/// way out.
///
/// The narrower version of this only cleared UseNexusForGameBarEnabled, the "open Game Bar with
/// the controller button" switch. That was not enough in practice: the overlay still appeared
/// and then dismissed itself a moment later, because the shell reads that value when the button
/// is pressed but the Game Bar has already begun opening by then. A flash of overlay over a
/// game is the thing being complained about, so half-preventing it is no use.
///
/// Disabling the feature outright is both simpler and complete. Three values, all per-user, none
/// needing administrator rights:
///
///   AppCaptureEnabled       background recording and the capture overlay
///   GameDVR_Enabled         the game-DVR half of the same feature
///   UseNexusForGameBarEnabled   the controller button binding
///
/// Held for the lifetime of the app rather than the session. The button has to do nothing at the
/// instant it is pressed, and a setting applied when a session starts is applied too late to be
/// certain of that. It also means one restore point instead of one per session, which is the
/// safer shape for something that edits a user's Windows settings.
/// </summary>
public static class GameBarControl
{
    private sealed record Saved(string Key, string Value, int? Previous);

    private static readonly List<Saved> Restore = [];
    private static bool _applied;

    private static readonly (string Key, string Value)[] Switches =
    [
        (@"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled"),
        (@"System\GameConfigStore",                            "GameDVR_Enabled"),
        (@"Software\Microsoft\GameBar",                        "UseNexusForGameBarEnabled"),
    ];

    /// <summary>Switch it off, remembering what each value was. Safe to call twice.</summary>
    public static void Disable()
    {
        if (_applied) return;
        _applied = true;

        foreach (var (key, value) in Switches)
        {
            try
            {
                using var registry = Registry.CurrentUser.CreateSubKey(key);
                if (registry is null) continue;

                // Null previous means the value was absent, which is different from it being
                // zero: putting a 0 back where nothing was would leave the machine changed.
                Restore.Add(new Saved(key, value, registry.GetValue(value) as int?));
                registry.SetValue(value, 0, RegistryValueKind.DWord);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not switch off {value}: {ex.Message}");
            }
        }

        Log.Info($"Xbox Game Bar disabled ({Restore.Count} setting(s) changed).");
    }

    /// <summary>
    /// Put every value back exactly as it was found.
    ///
    /// Called from the app's exit paths, including the process-exit handler, so a crash or a
    /// sign-out still hands the machine back in the state it was borrowed in. Anything that
    /// turns a Windows feature off has to be more careful about the way back than the way in.
    /// </summary>
    public static void Enable()
    {
        if (!_applied) return;
        _applied = false;

        foreach (var saved in Restore)
        {
            try
            {
                using var registry = Registry.CurrentUser.CreateSubKey(saved.Key);
                if (registry is null) continue;

                if (saved.Previous is { } previous)
                    registry.SetValue(saved.Value, previous, RegistryValueKind.DWord);
                else
                    registry.DeleteValue(saved.Value, throwOnMissingValue: false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Could not restore {saved.Value}: {ex.Message}");
            }
        }

        Log.Info("Xbox Game Bar settings put back.");
        Restore.Clear();
    }

    /// <summary>Follow the setting, whichever way it was just changed.</summary>
    public static void Apply(AppConfig config)
    {
        if (config.DisableGameBarButton) Disable();
        else Enable();
    }
}
