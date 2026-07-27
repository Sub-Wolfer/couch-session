using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>
/// Windows-level tuning applied for the duration of a couch session, and undone afterwards.
///
/// Everything here is reversible and everything here records what it found before changing it.
/// That is the whole design rule: a utility that leaves a machine in High Performance with
/// notifications suppressed after it has been closed is worse than one that does nothing.
///
/// Nothing needs administrator rights. Each piece is independent — one failing (an unusual
/// power configuration, a locked-down policy) does not stop the others.
/// </summary>
public sealed class PerformanceTuning
{
    private Guid? _previousPlan;
    private Guid? _previousOverlay;
    private int? _previousQuietHours;
    private IntPtr _awakeRequest = IntPtr.Zero;
    private bool _awakeHeld;
    private bool _overlayApplied;
    private bool _applied;

    /// <summary>Apply whatever the config asks for. Safe to call twice.</summary>
    public void Apply(AppConfig config)
    {
        if (_applied) return;
        _applied = true;

        if (config.ChangePowerPlan) ApplyPower(config.SessionPowerPlan);
        // Not honoured while the setting is withdrawn from the UI. Left in place rather than deleted
        // because the machinery is fine — it is the default that was wrong for this app, where far more
        // reports are about a display refusing to sleep than one sleeping mid-cutscene.
        // if (config.KeepDisplayAwake) HoldAwake();
        if (config.SilenceNotifications) SilenceWindows();

        // Game Mode is no longer touched here. It is a Windows-wide preference that this class never
        // undid, which made it the one thing in a session-scoped tuner that was not session-scoped —
        // and the visible cost was a toggle that appeared to do nothing, because it did nothing until
        // the next session began. It is applied the moment it is pressed now. See GameModeControl.
    }

    /// <summary>Put everything back. Safe to call when nothing was applied.</summary>
    public void Restore()
    {
        if (!_applied) return;
        _applied = false;
        _overlayApplied = false;

        RestorePower();
        ReleaseAwake();
        RestoreNotifications();

        // Game Mode is not here either, and no longer applied above. See GameModeControl.
    }

    // ---------------------------------------------------------------- power

    /// <summary>
    /// High Performance plan plus the Windows 11 "Best performance" slider.
    ///
    /// Two separate things that are easy to confuse. The plan is the classic power scheme; the
    /// overlay is the slider in the battery flyout, which sits on top of it and is what actually
    /// governs boost behaviour on most modern laptops. Setting only the plan is why the old
    /// advice to "switch to High Performance" often does nothing on a recent machine.
    /// </summary>
    /// <param name="wanted">
    /// A scheme GUID the user picked by name, or empty to choose automatically.
    /// </param>
    private void ApplyPower(string wanted)
    {
        try
        {
            var current = PowerPlans.Active();

            // An explicit choice is obeyed, full stop.
            //
            // No ranking, no "is this actually faster" check. The whole reason the picker exists
            // is that the app cannot tell whether someone's own plan is faster than the ones
            // Windows ships — so once they have said which one they want, second-guessing it
            // would be the same mistake in a politer form.
            if (Guid.TryParse(wanted, out var chosen))
            {
                var plan = PowerPlans.Available().FirstOrDefault(p => p.Id == chosen);

                if (plan is null)
                {
                    Log.Warn("Performance: the chosen power plan no longer exists; "
                           + "choosing automatically instead.");
                }
                else if (current?.Id == plan.Id)
                {
                    Log.Info($"Performance: already on {plan}; leaving it alone.");
                    _previousPlan = null;
                    ApplyOverlay();
                    return;
                }
                else if (PowerPlans.SetActive(plan.Id))
                {
                    _previousPlan = current?.Id;
                    Log.Info($"Performance: switched to {plan} as chosen.");
                    ApplyOverlay();
                    return;
                }
                else
                {
                    Log.Warn($"Performance: {plan} was refused by Windows.");
                    _previousPlan = null;
                    ApplyOverlay();
                    return;
                }
            }

            var target = PowerPlans.Best();

            if (current is null || target is null)
            {
                // Some OEM images ship neither performance plan.
                Log.Info("Performance: no faster power plan is available on this machine.");
                _previousPlan = null;
            }
            else if (!current.Known)
            {
                // An unidentifiable plan is left alone.
                //
                // Previously anything unrecognised was assumed to be slow and switched away
                // from. That assumption caused real harm: a duplicated Ultimate Performance
                // scheme has a generated GUID, so it looked unrecognised, and the app switched
                // a machine down from Ultimate to High every session. Doing nothing to a plan
                // we cannot identify is the only choice that cannot make things worse.
                Log.Info($"Performance: {current} is not a plan this app recognizes; leaving it alone.");
                _previousPlan = null;
            }
            else if (current.Rank >= target.Rank)
            {
                Log.Info($"Performance: already on {current}; leaving it alone.");
                _previousPlan = null;
            }
            else if (PowerPlans.SetActive(target.Id))
            {
                _previousPlan = current.Id;
                Log.Info($"Performance: switched from {current} to {target}.");
            }
            else
            {
                Log.Info($"Performance: {target} was refused; staying on {current}.");
                _previousPlan = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Performance: could not change the power plan: {ex.Message}");
            _previousPlan = null;
        }

        ApplyOverlay();
    }

    /// <summary>
    /// The Windows 11 "Best performance" slider, which is a separate thing from the plan.
    ///
    /// Easy to confuse with the power plan and not the same at all: the plan is the classic
    /// scheme, while the overlay sits on top of it and is what actually governs boost behaviour
    /// on most modern machines. Setting only the plan is why the old advice to "switch to High
    /// Performance" often does nothing on a recent laptop.
    ///
    /// In a method of its own because the plan logic above returns from several branches, and
    /// this has to happen on every one of them.
    /// </summary>
    private void ApplyOverlay()
    {
        if (_overlayApplied) return;
        _overlayApplied = true;

        try
        {
            if (PowerGetActualOverlayScheme(out var current) == 0 && current != BestPerformance)
            {
                _previousOverlay = current;

                if (PowerSetActiveOverlayScheme(BestPerformance) == 0)
                    Log.Info("Performance: power mode set to Best Performance.");
                else
                    _previousOverlay = null;
            }
        }
        catch (Exception ex)
        {
            // The overlay API is undocumented and absent on Windows 10 builds before 1809.
            Log.Info($"Performance: power mode slider unavailable ({ex.Message}).");
            _previousOverlay = null;
        }
    }

    private void RestorePower()
    {
        try
        {
            if (_previousPlan is { } plan)
            {
                PowerPlans.SetActive(plan);
                Log.Info("Performance: power plan restored.");
            }
        }
        catch (Exception ex) { Log.Warn($"Performance: power plan not restored: {ex.Message}"); }
        finally { _previousPlan = null; }

        try
        {
            if (_previousOverlay is { } overlay)
            {
                PowerSetActiveOverlayScheme(overlay);
                Log.Info("Performance: power mode restored.");
            }
        }
        catch (Exception ex) { Log.Warn($"Performance: power mode not restored: {ex.Message}"); }
        finally { _previousOverlay = null; }
    }

    // ---------------------------------------------------------------- sleep

    /// <summary>
    /// Stop the display blanking.
    ///
    /// A gamepad is not an input device as far as the idle timer is concerned, so a long
    /// cutscene, a menu, or a slow turn in a strategy game will put the TV to sleep mid-session.
    /// The flag is held for as long as the session lasts and dropped the moment it ends, rather
    /// than changing any of the user's timeout settings.
    /// </summary>
    private void HoldAwake()
    {
        if (_awakeHeld) return;

        try
        {
            // A power request, not SetThreadExecutionState. The execution-state flag is per-thread —
            // it only lifts when the same thread clears it — so a hold taken on the session thread and
            // released from the hotkey thread (as parking does) never actually cleared, and the screens
            // stayed on. A power request is a process-wide handle that can be set and cleared from any
            // thread, which is exactly what park/resume need.
            var reason = new REASON_CONTEXT
            {
                Version = POWER_REQUEST_CONTEXT_VERSION,
                Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING,
                SimpleReasonString = "Couch Session session in progress",
            };

            _awakeRequest = PowerCreateRequest(ref reason);
            if (_awakeRequest == IntPtr.Zero || _awakeRequest == InvalidHandle)
            {
                _awakeRequest = IntPtr.Zero;
                Log.Warn("Performance: could not hold the display awake.");
                return;
            }

            PowerSetRequest(_awakeRequest, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
            PowerSetRequest(_awakeRequest, POWER_REQUEST_TYPE.PowerRequestSystemRequired);

            _awakeHeld = true;
            Log.Info("Performance: holding the display awake for this session.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Performance: could not hold the display awake: {ex.Message}");
            _awakeRequest = IntPtr.Zero;
        }
    }

    private void ReleaseAwake()
    {
        if (!_awakeHeld) return;
        _awakeHeld = false;

        try
        {
            if (_awakeRequest != IntPtr.Zero)
            {
                PowerClearRequest(_awakeRequest, POWER_REQUEST_TYPE.PowerRequestDisplayRequired);
                PowerClearRequest(_awakeRequest, POWER_REQUEST_TYPE.PowerRequestSystemRequired);
                CloseHandle(_awakeRequest);
            }
            Log.Info("Performance: the display can sleep again.");
        }
        catch (Exception ex) { Log.Warn($"Performance: could not release the display hold: {ex.Message}"); }
        finally { _awakeRequest = IntPtr.Zero; }
    }

    // ------------------------------------------------------- notifications

    private const string QuietHoursKey =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    private const string QuietHoursValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    /// <summary>
    /// Stop Windows toasts appearing over the game.
    ///
    /// A notification that is a small corner card on a monitor is a banner across a television
    /// two metres away, and there is no way to dismiss it with a controller.
    ///
    /// This clears the global "show toasts" flag rather than driving Focus Assist. Focus Assist
    /// has no supported API at all and its registry layout has changed repeatedly between
    /// Windows releases, whereas this value has been stable and is the one the Settings app
    /// itself writes.
    /// </summary>
    private void SilenceWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(QuietHoursKey);
            if (key is null) return;

            _previousQuietHours = key.GetValue(QuietHoursValue) as int? ?? 1;

            if (_previousQuietHours == 0)
            {
                // Already silent — leave it, and do not claim it back later.
                _previousQuietHours = null;
                return;
            }

            key.SetValue(QuietHoursValue, 0, RegistryValueKind.DWord);
            Log.Info("Performance: Windows notifications silenced for this session.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Performance: could not silence notifications: {ex.Message}");
            _previousQuietHours = null;
        }
    }

    private void RestoreNotifications()
    {
        if (_previousQuietHours is not { } previous) return;

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(QuietHoursKey);
            key?.SetValue(QuietHoursValue, previous, RegistryValueKind.DWord);

            Log.Info("Performance: Windows notifications turned back on.");
        }
        catch (Exception ex) { Log.Warn($"Performance: notifications not restored: {ex.Message}"); }
        finally { _previousQuietHours = null; }
    }

    // ------------------------------------------------------------- interop


    /// <summary>The "Best performance" end of the Windows 11 power slider.</summary>
    private static readonly Guid BestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveOverlayScheme(Guid overlay);
    [DllImport("powrprof.dll")] private static extern uint PowerGetActualOverlayScheme(out Guid overlay);

    // Keep-awake via a process-wide power request, which — unlike SetThreadExecutionState — can be
    // cleared from a different thread than the one that set it, so parking can drop it cleanly.
    private static readonly IntPtr InvalidHandle = new(-1);
    private const uint POWER_REQUEST_CONTEXT_VERSION = 0;
    private const uint POWER_REQUEST_CONTEXT_SIMPLE_STRING = 0x1;

    private enum POWER_REQUEST_TYPE
    {
        PowerRequestDisplayRequired = 0,
        PowerRequestSystemRequired = 1,
        PowerRequestAwayModeRequired = 2,
        PowerRequestExecutionRequired = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct REASON_CONTEXT
    {
        public uint Version;
        public uint Flags;
        [MarshalAs(UnmanagedType.LPWStr)] public string SimpleReasonString;
    }

    [DllImport("kernel32.dll")] private static extern IntPtr PowerCreateRequest(ref REASON_CONTEXT context);
    [DllImport("kernel32.dll")] private static extern bool PowerSetRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);
    [DllImport("kernel32.dll")] private static extern bool PowerClearRequest(IntPtr powerRequest, POWER_REQUEST_TYPE requestType);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
}
