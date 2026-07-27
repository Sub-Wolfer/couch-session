using System.Runtime.InteropServices;
using static CouchMode.Audio.AudioCom;

namespace CouchMode.Audio;

public sealed record AudioDeviceInfo(string Id, string FriendlyName, bool IsActive)
{
    public override string ToString() => FriendlyName;
}

/// <summary>
/// Replaces SoundVolumeView. Enumerates render endpoints via Core Audio and switches the
/// system default through IPolicyConfig — no external binary, no shelling out.
/// </summary>
public static class AudioManager
{
    /// <summary>All playback devices that are plugged in and enabled.</summary>
    public static IReadOnlyList<AudioDeviceInfo> ListPlaybackDevices()
        => Enumerate(DeviceState.Active, activeOnly: true);

    /// <summary>
    /// Playback devices including ones currently unplugged — a TV switched off leaves its HDMI audio
    /// endpoint here, marked inactive, so it can still be chosen for Couch Mode while the TV is off.
    /// Kept separate from the active-only list so the session's own device checks are not fooled into
    /// switching audio to something that is not actually there.
    /// </summary>
    public static IReadOnlyList<AudioDeviceInfo> ListPlaybackDevicesIncludingOff()
        => Enumerate(DeviceState.Active | DeviceState.Unplugged, activeOnly: false);

    private static IReadOnlyList<AudioDeviceInfo> Enumerate(DeviceState mask, bool activeOnly)
    {
        var result = new List<AudioDeviceInfo>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();

        try
        {
            Check(enumerator.EnumAudioEndpoints(DataFlow.Render, mask, out var collection),
                  "enumerate audio endpoints");
            Check(collection.GetCount(out int count), "count audio endpoints");

            for (int i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                try
                {
                    if (collection.Item(i, out device) != S_OK) continue;
                    if (device.GetId(out string id) != S_OK) continue;

                    bool active = activeOnly
                               || (device.GetState(out var state) == S_OK && state == DeviceState.Active);

                    result.Add(new AudioDeviceInfo(id, GetFriendlyName(device) ?? id, active));
                }
                catch (COMException ex)
                {
                    Log.Warn($"Skipped audio endpoint {i}: {ex.Message}");
                }
                finally
                {
                    if (device is not null) Marshal.ReleaseComObject(device);
                }
            }
            Marshal.ReleaseComObject(collection);
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }

        return result.OrderBy(d => d.FriendlyName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Make <paramref name="deviceId"/> the default for every role, matching what the
    /// Windows sound flyout does when you click a device.
    /// </summary>
    public static void SetDefaultPlaybackDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("No audio device configured.", nameof(deviceId));

        var policy = (IPolicyConfig)new CPolicyConfigClient();
        try
        {
            foreach (var role in new[] { Role.Console, Role.Multimedia, Role.Communications })
                Check(policy.SetDefaultEndpoint(deviceId, role), $"set default audio device ({role})");

            Log.Info($"Default playback device set to {deviceId}");
        }
        finally
        {
            Marshal.ReleaseComObject(policy);
        }
    }

    /// <summary>Current default playback device id, or null if none/unavailable.</summary>
    public static string? GetDefaultPlaybackDeviceId()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        try
        {
            if (enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var device) != S_OK)
                return null;
            try
            {
                return device.GetId(out string id) == S_OK ? id : null;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch (COMException)
        {
            return null;   // no playback devices at all
        }
        finally
        {
            Marshal.ReleaseComObject(enumerator);
        }
    }

    /// <summary>True if the device is still present — device ids survive unplugging, so check before use.</summary>
    public static bool DeviceExists(string deviceId) =>
        !string.IsNullOrWhiteSpace(deviceId) &&
        ListPlaybackDevices().Any(d => string.Equals(d.Id, deviceId, StringComparison.OrdinalIgnoreCase));

    private static string? GetFriendlyName(IMMDevice device)
    {
        if (device.OpenPropertyStore(STGM_READ, out var store) != S_OK)
            return null;
        try
        {
            var key = PkeyDeviceFriendlyName;
            if (store.GetValue(ref key, out var value) != S_OK)
                return null;
            try { return value.AsString(); }
            finally { PropVariantClear(ref value); }
        }
        finally
        {
            Marshal.ReleaseComObject(store);
        }
    }

    private static void Check(int hr, string what)
    {
        if (hr != S_OK)
            throw new InvalidOperationException($"Failed to {what} (HRESULT 0x{hr:X8}).");
    }
}
