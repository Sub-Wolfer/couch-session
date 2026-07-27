using System.Runtime.InteropServices;
using System.Text;

namespace CouchMode.Session;

/// <summary>A power scheme installed on this machine.</summary>
/// <param name="Id">Its GUID, which is not necessarily one of the well-known ones.</param>
/// <param name="Name">The name Windows shows for it in Settings.</param>
public sealed record PowerPlan(Guid Id, string Name)
{
    /// <summary>
    /// How much performance this plan asks for, so two can be compared.
    ///
    /// Matched on the GUID first and the name second, and the name half is not a nicety — it is
    /// the only thing that works on most machines that have Ultimate Performance at all.
    ///
    /// Ultimate Performance is hidden by default. The documented way to get it is
    /// <c>powercfg -duplicatescheme e9a42b02-...</c>, and duplicating a scheme creates a copy
    /// with a **freshly generated GUID**. So the canonical Ultimate GUID is usually not present
    /// even when Ultimate Performance is running: the plan is real, selected, and named right in
    /// Settings, while its id matches nothing. That is exactly what happened here — the check
    /// reported "your own power plan" and switched down to High Performance.
    ///
    /// Name matching is English-only, which is a real limitation rather than an oversight: the
    /// friendly names are localised and there is no stable identifier underneath them. A plan
    /// that cannot be identified is therefore left alone rather than guessed at — see Rank 0
    /// handling in PerformanceTuning.
    /// </summary>
    public int Rank =>
        Id == PowerPlans.Ultimate || Matches("ultimate") ? 4
      : Id == PowerPlans.High || Matches("high performance") ? 3
      : Id == PowerPlans.Balanced || Matches("balanced") ? 2
      : Id == PowerPlans.Saver || Matches("power saver") ? 1
      : 0;

    /// <summary>Whether this plan could be identified at all.</summary>
    public bool Known => Rank > 0;

    private bool Matches(string word) =>
        Name.Contains(word, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Name.Length > 0 ? Name : "an unnamed power plan";
}

/// <summary>
/// Which Windows power plans this machine has, and which of them is the fastest.
///
/// Exists because "switch to High Performance" is not the same as "make this machine as fast as
/// it goes", and treating them as the same was actively harmful. Ultimate Performance sits above
/// High Performance — it removes the remaining latency-saving idle states rather than merely
/// raising the clock floor.
///
/// The original code set High Performance unconditionally, so a machine already on something
/// faster was switched *down* for the session and then "restored" afterwards, making the loss
/// invisible. Two rules now: pick the best plan the machine actually has, and never move to a
/// plan that is not clearly better than the one already running.
/// </summary>
public static class PowerPlans
{
    public static readonly Guid Ultimate = new("e9a42b02-d5df-448d-aa00-03f14749eb61");
    public static readonly Guid High = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid Saver = new("a1841308-3541-4fab-bc81-f71556f20b4a");

    /// <summary>The fastest plan installed, or null if nothing better than Balanced exists.</summary>
    public static PowerPlan? Best()
    {
        var best = Available().Where(p => p.Rank >= 3).MaxBy(p => p.Rank);
        return best;
    }

    /// <summary>The plan running right now, or null if Windows would not say.</summary>
    public static PowerPlan? Active()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out IntPtr buffer) != 0 || buffer == IntPtr.Zero)
            return null;

        try
        {
            var id = Marshal.PtrToStructure<Guid>(buffer);
            return new PowerPlan(id, FriendlyName(id));
        }
        finally { LocalFree(buffer); }
    }

    /// <summary>Switch plans, reporting whether it actually took.</summary>
    public static bool SetActive(Guid plan)
    {
        var target = plan;
        if (PowerSetActiveScheme(IntPtr.Zero, ref target) != 0) return false;

        // Read back rather than trusting the return code. This call reports success on machines
        // where a policy or a vendor utility owns the power configuration and silently wins.
        return Active()?.Id == plan;
    }

    /// <summary>
    /// Every scheme installed, with its name.
    ///
    /// Enumerated rather than probed: testing for a plan by trying to select it would change the
    /// user's power plan as a side effect of asking a question.
    /// </summary>
    public static List<PowerPlan> Available()
    {
        var found = new List<PowerPlan>();

        try
        {
            for (uint index = 0; ; index++)
            {
                uint size = (uint)Marshal.SizeOf<Guid>();
                var buffer = new byte[size];

                if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                                   AccessScheme, index, buffer, ref size) != 0)
                    break;

                var id = new Guid(buffer);
                found.Add(new PowerPlan(id, FriendlyName(id)));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list the power plans: {ex.Message}");
        }

        return found;
    }

    /// <summary>The name Windows shows for a scheme, or empty if it will not say.</summary>
    private static string FriendlyName(Guid plan)
    {
        try
        {
            var id = plan;
            uint size = 0;

            // Called twice, as these APIs want: once to be told the size, once to fill it.
            if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, null, ref size) != 0
                || size == 0)
                return "";

            var buffer = new byte[size];

            if (PowerReadFriendlyName(IntPtr.Zero, ref id, IntPtr.Zero, IntPtr.Zero, buffer, ref size) != 0)
                return "";

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the name of a power plan: {ex.Message}");
            return "";
        }
    }

    private const uint AccessScheme = 16;

    [DllImport("powrprof.dll")] private static extern uint PowerGetActiveScheme(IntPtr user, out IntPtr scheme);
    [DllImport("powrprof.dll")] private static extern uint PowerSetActiveScheme(IntPtr user, ref Guid scheme);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootKey, IntPtr schemeGuid, IntPtr subGroupGuid,
                                              uint accessFlags, uint index,
                                              byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootKey, ref Guid schemeGuid,
                                                     IntPtr subGroupGuid, IntPtr powerSettingGuid,
                                                     byte[]? buffer, ref uint bufferSize);

    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr mem);
}
