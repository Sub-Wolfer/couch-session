using System.Runtime.InteropServices;

namespace CouchMode.Input;

/// <summary>Whether a controller is running on its battery or on a cable.</summary>
public enum PadPower
{
    /// <summary>Nothing dependable to say. Shown as nothing at all rather than as a guess.</summary>
    Unknown,

    OnBattery,
    Charging,
}

/// <param name="PlayStation">
/// Which console's button art belongs to this pad.
///
/// Carried here rather than worked out from the name by whoever is drawing it. The name is very often
/// the user's own — someone who calls their DualSense "PS5 Wireless" was being shown an Xbox mark,
/// because the string they chose contains neither "PlayStation" nor "DualSense". The vendor id is the
/// only thing that actually knows, and this is where it is known.
/// </param>
public readonly record struct PadStatus(string Name, PadPower Power, bool PlayStation = false);

/// <summary>
/// Which controllers are connected, and whether each is on a cable.
///
/// Deliberately not a charge level. Reading a percentage means trusting a byte offset into a
/// layout Sony never published, and an offset one byte out still produces a plausible number —
/// there is no way to tell a correct reading from a confident wrong one without a pad whose real
/// level is already known. Charge *state* has no such problem: it is a two-bit field with three
/// meanings, and a wrong read shows up immediately as a controller that claims to be charging
/// while sitting on the sofa.
///
/// So this answers the two questions that can be answered honestly — what is plugged in, and is it
/// on power — and declines the one that cannot.
/// </summary>
public static class PadPresence
{
    /// <param name="names">
    /// Names the user has given their controllers, keyed by device id. A pad with one shows that
    /// instead of its model — which is the whole point of the setting, and pointless if the place
    /// the names are most visible ignores them.
    /// </param>
    public static IReadOnlyList<PadStatus> All(IReadOnlyDictionary<string, string>? names = null)
    {
        var found = new List<PadStatus>();

        // The device list comes from the controller watcher's own enumeration rather than from
        // XInput or HID directly. It is the same list the settings page shows, it is already
        // de-duplicated, and it names pads properly — an Xbox controller is visible to both XInput
        // and raw HID, and counting it twice is exactly the sort of thing this avoids.
        List<Games.ControllerDevice> devices;

        try { devices = Games.ControllerWatcher.Attached(); }
        catch { return found; }

        if (devices.Count == 0) return found;

        var sony = HidPad.Power();
        bool xinputOnCable = AnyXInputOnCable();

        // Only meaningful when there is one pad it could refer to. XInput reports per slot and
        // never says which physical controller a slot is, so with two non-Sony pads attached
        // "something is on a cable" cannot be pinned to either of them.
        int others = devices.Count(d => !IsSony(d));

        foreach (var device in devices)
        {
            var power = PadPower.Unknown;

            if (IsSony(device))
            {
                foreach (var (vendor, product, state) in sony)
                {
                    if (!device.Id.Contains($"VID_{vendor:X4}", StringComparison.OrdinalIgnoreCase) ||
                        !device.Id.Contains($"PID_{product:X4}", StringComparison.OrdinalIgnoreCase))
                        continue;

                    power = state;
                    break;
                }
            }
            else if (others == 1)
            {
                power = xinputOnCable ? PadPower.Charging : PadPower.OnBattery;
            }

            string label = names is not null
                        && names.TryGetValue(device.Id, out var mine)
                        && mine.Length > 0
                           ? mine
                           : Short(device.Name);

            found.Add(new PadStatus(label, power, IsSony(device)));
        }

        return found;
    }

    private static bool IsSony(Games.ControllerDevice device)
        => device.Id.Contains("VID_054C", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether XInput says any pad is running off a cable.
    ///
    /// BatteryType is the reliable half of what XInput reports here. "Wired" means exactly that,
    /// and disposable cells report as alkaline and so are never charging — no interpretation
    /// needed for either. The battery *level* is the part that only comes in four vague bands, and
    /// nothing here asks for it.
    /// </summary>
    private static bool AnyXInputOnCable()
    {
        for (uint slot = 0; slot < 4; slot++)
        {
            if (!TryGetBattery(slot, out var info)) continue;
            if (info.BatteryType == BatteryTypeWired) return true;
        }

        return false;
    }

    /// <summary>The model, without the hardware tag the settings list appends to tell two apart.</summary>
    private static string Short(string name)
    {
        int tag = name.IndexOf("  ·  ", StringComparison.Ordinal);
        return tag > 0 ? name[..tag] : name;
    }

    // ---- interop ----

    private const byte BatteryTypeWired = 0x01;
    private const byte BatteryDeviceTypeGamepad = 0x00;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputBatteryInformation
    {
        public byte BatteryType;
        public byte BatteryLevel;
    }

    /// <summary>
    /// 1.4 only, deliberately.
    ///
    /// XInputGetBatteryInformation does not exist in xinput9_1_0.dll — the version that ships with
    /// everything older — so unlike the button reading there is no fallback to try. A machine
    /// without 1.4 simply reports no power state, and the answer is remembered so a missing export
    /// is not a thrown exception on every poll.
    /// </summary>
    private static bool TryGetBattery(uint slot, out XInputBatteryInformation info)
    {
        info = default;

        if (_available == false) return false;

        try
        {
            uint rc = XInputGetBatteryInformation(slot, BatteryDeviceTypeGamepad, out info);
            _available = true;
            return rc == 0;
        }
        catch (DllNotFoundException) { _available = false; }
        catch (EntryPointNotFoundException) { _available = false; }

        if (_available == false)
            Log.Info("This machine's XInput cannot report controller power state.");

        return false;
    }

    private static bool? _available;

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
    private static extern uint XInputGetBatteryInformation(uint index, byte deviceType,
                                                           out XInputBatteryInformation info);
}
