using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace CouchMode.Session;

/// <summary>
/// Reads and sets whether Windows Defender Firewall is on.
///
/// The reason this is here: the first time a game tries to listen for a connection — hosting, peer
/// to peer, some anti-cheat services — Windows throws up "Allow this app to communicate?". That
/// dialog wants a mouse click a controller cannot give, and until it is answered the game's
/// networking is blocked. Turning the firewall off removes the prompt and lets that traffic
/// through. It is a real reduction in security, so the caller warns first and it takes an elevation
/// prompt to change — but unlike UAC it takes effect at once, with no restart.
/// </summary>
public static class FirewallControl
{
    // Live state lives per profile under here; the standard and public profiles are the ones a
    // home machine actually uses. "On" means any of them still has the firewall enabled.
    private const string PolicyRoot =
        @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";

    private static readonly string[] Profiles = { "StandardProfile", "PublicProfile", "DomainProfile" };

    /// <summary>Whether the firewall is on for any profile. Unreadable is treated as on, the default.</summary>
    public static bool IsEnabled()
    {
        try
        {
            foreach (var profile in Profiles)
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"{PolicyRoot}\{profile}");
                if (key?.GetValue("EnableFirewall") is int v && v != 0) return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the firewall setting: {ex.Message}");
            return true;
        }
    }

    /// <summary>
    /// Turn the firewall on or off for every profile, through an elevated netsh call so the one
    /// prompt is all it takes.
    /// </summary>
    /// <returns>True if it was applied; false if the elevation prompt was dismissed or it failed.</returns>
    public static bool SetEnabled(bool enabled)
    {
        var psi = new ProcessStartInfo("netsh.exe",
            $"advfirewall set allprofiles state {(enabled ? "on" : "off")}")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
        };

        try
        {
            using var p = Process.Start(psi);
            if (p is null) return false;

            p.WaitForExit(10_000);
            bool ok = p.HasExited && p.ExitCode == 0;

            Log.Info(ok
                ? $"Firewall turned {(enabled ? "on" : "off")}."
                : "The firewall change did not complete.");

            return ok;
        }
        catch (Win32Exception)
        {
            Log.Info("The firewall change was canceled at the elevation prompt.");
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("Changing the firewall setting failed", ex);
            return false;
        }
    }
}
