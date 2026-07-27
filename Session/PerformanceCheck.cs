using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>One thing that was tried, and what actually happened.</summary>
/// <param name="Setting">What was being changed.</param>
/// <param name="Worked">Whether the machine ended up in the state that was asked for.</param>
/// <param name="Detail">What was seen, in plain words. Shown whether it worked or not.</param>
public sealed record CheckResult(string Setting, bool Worked, string Detail)
{


}

/// <summary>
/// Applies each performance setting, reads the machine back, and puts everything as it was.
///
/// This exists because the performance settings are the only part of the app whose effect is
/// invisible. A display switch is obvious and HDR is obvious; whether the power plan actually
/// changed is not, and the APIs behind these fail quietly on machines with unusual power
/// configurations, managed policies, or a vendor utility of their own. Without a check, a
/// setting that silently does nothing looks exactly like one that works.
///
/// Every step reads the state back rather than trusting the call's return value. Several of
/// these APIs report success and change nothing.
/// </summary>
public static class PerformanceCheck
{


    // ---------------------------------------------------------------- power

    internal static CheckResult CheckPowerPlan(string chosenPlan)
    {
        const string Name = "Power plan";

        try
        {
            var before = PowerPlans.Active();
            if (before is null) return new(Name, false, "Windows would not say which power plan is active.");

            // A named plan is checked as a named plan. Reporting on the automatic choice while a
            // session would use a different one is a check that answers the wrong question.
            if (Guid.TryParse(chosenPlan, out var wanted))
            {
                var plan = PowerPlans.Available().FirstOrDefault(p => p.Id == wanted);

                if (plan is null)
                    return new(Name, false, "The power plan you chose is no longer on this machine. "
                                          + "Pick another, or leave it on Automatic.");

                if (before.Id == plan.Id)
                    return new(Name, true, $"Already on {plan}, which is the plan you chose.")
                          ;

                bool switched = PowerPlans.SetActive(plan.Id);
                PowerPlans.SetActive(before.Id);

                return switched
                     ? new(Name, true, $"{plan} can be switched on, and your {before} plan put back.")
                     : new(Name, false, $"Windows refused to switch to {plan}. Something else is "
                                      + "managing power on this machine.");
            }

            // Reported by name, so the row says what you would see in Windows rather than
            // "your own power plan" — which is what it said when a duplicated Ultimate
            // Performance scheme could not be matched by GUID.
            if (!before.Known)
            {
                return new(Name, true, $"You are on {before}, which is not one of the plans this "
                                     + "app knows how to compare. It is left exactly as it is, "
                                     + "since changing a plan we cannot rank could easily make "
                                     + "things slower rather than faster.")
                      ;
            }

            var target = PowerPlans.Best();
            if (target is null)
                return new(Name, false, "This machine offers neither the High Performance nor the "
                                      + "Ultimate Performance plan. Some laptops and OEM power "
                                      + "tools remove both.");

            // Ranked, not compared for equality, so a machine already on something faster than
            // the target counts as a pass rather than being switched down to meet it.
            if (before.Rank >= target.Rank)
            {
                return new(Name, true, $"Already on {before}, the fastest plan on this machine, so "
                                     + "there is nothing for couch mode to change.")
                      ;
            }

            bool took = PowerPlans.SetActive(target.Id);
            PowerPlans.SetActive(before.Id);

            return took
                 ? new(Name, true, $"{target} can be switched on, and your {before} plan put back.")
                 : new(Name, false, "The switch was accepted but the plan did not change. "
                                  + "Something else is managing power on this machine.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }


    // ---------------------------------------------------------------- sleep


    // ------------------------------------------------------- notifications

    internal static CheckResult CheckNotifications()
    {
        const string Name = "Silence Windows notifications";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(QuietHoursKey);
            if (key is null) return new(Name, false, "The notification setting could not be opened.");

            int before = key.GetValue(QuietHoursValue) as int? ?? 1;

            key.SetValue(QuietHoursValue, 0, RegistryValueKind.DWord);
            int now = key.GetValue(QuietHoursValue) as int? ?? 1;
            key.SetValue(QuietHoursValue, before, RegistryValueKind.DWord);

            return now == 0
                 ? new(Name, true, "Notifications can be switched off and back on.")
                 : new(Name, false, "The setting did not take. A workplace policy may own it.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }

    private const string QuietHoursKey =
        @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";

    private const string QuietHoursValue = "NOC_GLOBAL_SETTING_TOASTS_ENABLED";

    // ----------------------------------------------------------- game mode

    internal static CheckResult CheckGameMode()
    {
        const string Name = "Windows Game Mode";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            if (key is null) return new(Name, false, "The Game Mode setting could not be opened.");

            int before = key.GetValue("AutoGameModeEnabled") as int? ?? 1;

            key.SetValue("AutoGameModeEnabled", 1, RegistryValueKind.DWord);
            int now = key.GetValue("AutoGameModeEnabled") as int? ?? 0;
            key.SetValue("AutoGameModeEnabled", before, RegistryValueKind.DWord);

            // Written for a switch that turned Game Mode on for a session and never off. It is a
            // live mirror of the Windows setting now, so "can be switched back off" described a
            // behaviour that no longer exists — this says whether the setting can be written, which
            // is the only thing this check has ever actually established.
            return now == 1
                 ? new(Name, true, before == 1
                       ? "Already on in Windows."
                       : "Off in Windows right now, and this app can change it.")
                 : new(Name, false, "Windows would not accept the setting.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }

    // ------------------------------------------------------------ game bar

    internal static CheckResult CheckGameBar()
    {
        const string Name = "Xbox Game Bar";

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\GameBar");
            if (key is null) return new(Name, false, "The Game Bar setting could not be opened.");

            // The controller-button binding is the one that puts the overlay over a game; the
            // other two Couch Session clears are the same shape and stand or fall with it.
            int before = key.GetValue("UseNexusForGameBarEnabled") as int? ?? 1;

            key.SetValue("UseNexusForGameBarEnabled", 0, RegistryValueKind.DWord);
            int now = key.GetValue("UseNexusForGameBarEnabled") as int? ?? 1;
            key.SetValue("UseNexusForGameBarEnabled", before, RegistryValueKind.DWord);

            return now == 0
                 ? new(Name, true, before == 0
                     ? "The Game Bar's controller-button overlay is switched off."
                     : "The Game Bar's controller-button overlay can be switched off and back on.")
                 : new(Name, false, "The setting did not take.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }

    // ------------------------------------------------------------ priority

    internal static CheckResult CheckPriority()
    {
        const string Name = "Raise the game's priority";

        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();

            var before = me.PriorityClass;
            me.PriorityClass = System.Diagnostics.ProcessPriorityClass.AboveNormal;

            var now = me.PriorityClass;
            me.PriorityClass = before;

            // Tested on this process, because a game is not running to test on. If Windows lets
            // us raise our own priority it will let us raise a game's — the difference is only
            // whether anti-cheat in that particular game objects, which cannot be known here.
            return now == System.Diagnostics.ProcessPriorityClass.AboveNormal
                 ? new(Name, true, "Priority can be changed. Some games block this individually — "
                                 + "anti-cheat often refuses, and that is skipped quietly.")
                 : new(Name, false, "Priority could not be changed on this machine.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }

    // -------------------------------------------------------------- security

    internal static CheckResult CheckUac()
    {
        const string Name = "User Account Control (UAC)";

        try
        {
            bool off = !UacControl.IsEnabled();

            // Worked tracks the couch-friendly state, not a pass/fail: off is good (green tick),
            // on is the one that can strand a session (red cross). The line just states the fact —
            // the mark, the colour and the toggle's own description carry the rest.
            return new(Name, off, (off ? "UAC is off." : "UAC is on.")
                                + " Checked against current Windows settings.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }

    internal static CheckResult CheckFirewall()
    {
        const string Name = "Windows Defender Firewall";

        try
        {
            bool off = !FirewallControl.IsEnabled();

            return new(Name, off, (off ? "The firewall is off." : "The firewall is on.")
                                + " Checked against current Windows settings.");
        }
        catch (Exception ex) { return new(Name, false, $"Could not be checked: {ex.Message}"); }
    }

    // ---------------------------------------------------------------- shared




}
