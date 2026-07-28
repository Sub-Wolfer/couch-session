using System.Runtime.InteropServices;

namespace CouchMode.Session;

/// <summary>
/// Where the machine's power is coming from, and whether it has a battery at all.
///
/// One call, no handles to keep and nothing to dispose, so it is asked at the moment it matters
/// rather than watched. A session lasts an evening and somebody can plug a laptop in halfway
/// through; the answer this gives is the answer at the moment the question was asked, which for
/// deciding whether to raise a power plan at session start is exactly the right scope.
///
/// Unknown is a real answer and not an error. Some desktops report no battery and a line status
/// Windows describes as unknown, and treating that as "on battery" would silently disable a
/// setting on a machine that has no battery to protect.
/// </summary>
internal static class PowerLine
{
    internal enum State
    {
        /// <summary>Mains, or a machine with no battery to run from.</summary>
        PluggedIn,

        /// <summary>Running down a battery.</summary>
        OnBattery,

        /// <summary>Windows would not say. Treated as plugged in by callers.</summary>
        Unknown,
    }

    public static State Now()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return State.Unknown;

            // 128 means "no system battery", which is a desktop. Mains by definition.
            if ((status.BatteryFlag & NoBattery) != 0) return State.PluggedIn;

            return status.ACLineStatus switch
            {
                1 => State.PluggedIn,
                0 => State.OnBattery,
                _ => State.Unknown,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the power source: {ex.Message}");
            return State.Unknown;
        }
    }

    /// <summary>True when this machine runs on a battery at all, so the setting is worth offering.</summary>
    public static bool HasBattery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var status)) return false;
            return (status.BatteryFlag & NoBattery) == 0;
        }
        catch { return false; }
    }

    private const byte NoBattery = 128;

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}
