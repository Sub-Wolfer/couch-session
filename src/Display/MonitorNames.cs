using Microsoft.Win32;

namespace CouchMode.Display;

/// <summary>
/// Better display names than CCD gives us.
///
/// DisplayConfigGetDeviceInfo's monitorFriendlyDeviceName comes from the driver and is often
/// just "Display" or "Generic PnP Monitor" — useless when you have several. The real name is
/// in the monitor's EDID, which Windows caches in the registry under the device's enum key,
/// so this reads and parses that instead.
/// </summary>
internal static class MonitorNames
{
    /// <summary>Best available name for a monitor device path, or null if nothing better exists.</summary>
    public static string? FromEdid(string monitorDevicePath)
    {
        try
        {
            var edid = ReadEdid(monitorDevicePath);
            if (edid is null) return null;

            return DescriptorName(edid) ?? ManufacturerAndProduct(edid);
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading EDID failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Whether the monitor itself advertises HDR, or null when that cannot be established.
    ///
    /// Needed because Windows will not answer this question on its own. The CCD call the app uses
    /// reports <c>advancedColorSupported</c>, and advanced colour is not HDR — it also covers wide
    /// colour gamut, which plenty of SDR panels claim. The log proves it on this very machine:
    /// three displays, every one of them reporting supported, including a 1080p screen with no HDR
    /// at all. Windows 11 24H2 added a call that separates the two, but this machine is on 22631
    /// and does not have it.
    ///
    /// So the display is asked directly. A monitor that can do HDR says so in its EDID, in the
    /// CTA-861 HDR Static Metadata Data Block, and that claim is about the panel rather than about
    /// whatever mode it happens to be in — which is the whole point, since a capable display sitting
    /// in SDR has to still read as capable.
    ///
    /// Null rather than false when the EDID cannot be read or carries no CTA extension at all. Not
    /// knowing is not the same as knowing there is no HDR, and only one of those is worth printing.
    /// </summary>
    public static bool? AdvertisesHdr(string? monitorDevicePath)
    {
        if (string.IsNullOrWhiteSpace(monitorDevicePath)) return null;

        try
        {
            var edid = ReadEdid(monitorDevicePath);
            if (edid is null || edid.Length < 128) return null;

            int extensions = edid[126];
            if (extensions == 0) return null;   // nothing that could carry the answer

            bool sawCta = false;

            for (int block = 1; block <= extensions; block++)
            {
                int at = block * 128;
                if (at + 128 > edid.Length) break;

                // 0x02 marks a CTA-861 extension; revision 3 is the first that carries data blocks.
                if (edid[at] != 0x02 || edid[at + 1] < 3) continue;

                sawCta = true;

                // Byte 2 is where the detailed timings begin, so the data blocks run from byte 4 up
                // to it. Zero means there are no timings and, with them, no data block collection.
                int end = edid[at + 2];
                if (end <= 4 || end > 128) continue;

                for (int offset = 4; offset < end;)
                {
                    int header = edid[at + offset];
                    int tag = header >> 5;
                    int length = header & 0x1F;

                    if (length == 0) break;   // malformed; refuse to loop forever

                    // Tag 7 means the real tag is in the next byte. Extended tag 6 is the HDR
                    // Static Metadata Data Block, which needs three payload bytes to be meaningful.
                    if (tag == 7 && length >= 3 && at + offset + 2 < edid.Length
                     && edid[at + offset + 1] == HdrStaticMetadata)
                    {
                        int eotf = edid[at + offset + 2];

                        // Bit 2 is SMPTE ST 2084 (PQ) and bit 3 is Hybrid Log-Gamma. The two below
                        // them are plain gamma, SDR and HDR, which every display sets and neither of
                        // which is the HDR Windows means.
                        return (eotf & ((1 << 2) | (1 << 3))) != 0;
                    }

                    offset += 1 + length;
                }
            }

            // A CTA extension with no HDR block is a real answer: the display had somewhere to say
            // it does HDR and did not. No CTA extension at all is not.
            return sawCta ? false : null;
        }
        catch (Exception ex)
        {
            Log.Warn($"Reading HDR support from EDID failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>CTA-861 extended tag for the HDR Static Metadata Data Block.</summary>
    private const int HdrStaticMetadata = 6;

    /// <summary>
    /// Device path looks like \\?\DISPLAY#BOE2556#5&amp;3a9290dc&amp;2&amp;UID4355#{guid}.
    /// The first three '#'-separated parts are the registry key under Enum.
    /// </summary>
    private static byte[]? ReadEdid(string devicePath)
    {
        var trimmed = devicePath.TrimStart('\\', '?').TrimStart('\\');
        var parts = trimmed.Split('#');
        if (parts.Length < 3) return null;

        var keyPath = $@"SYSTEM\CurrentControlSet\Enum\{parts[0]}\{parts[1]}\{parts[2]}\Device Parameters";

        using var key = Registry.LocalMachine.OpenSubKey(keyPath);
        return key?.GetValue("EDID") as byte[];
    }

    /// <summary>
    /// EDID 1.x has four 18-byte descriptor blocks starting at offset 54. A block whose first
    /// three bytes are zero and whose fourth is 0xFC holds the monitor name, newline-terminated.
    /// </summary>
    private static string? DescriptorName(byte[] edid)
    {
        if (edid.Length < 128) return null;

        for (int offset = 54; offset + 18 <= 126; offset += 18)
        {
            if (edid[offset] != 0 || edid[offset + 1] != 0 || edid[offset + 2] != 0) continue;
            if (edid[offset + 3] != 0xFC) continue;

            var chars = new List<char>(13);
            for (int i = offset + 5; i < offset + 18; i++)
            {
                byte b = edid[i];
                if (b == 0x0A) break;                    // descriptor terminator
                if (b is < 0x20 or > 0x7E) continue;     // skip padding / junk
                chars.Add((char)b);
            }

            var name = new string(chars.ToArray()).Trim();
            if (name.Length > 0) return name;
        }
        return null;
    }

    /// <summary>
    /// Fallback for panels with no name descriptor (laptop and OEM panels usually have none):
    /// decode the manufacturer's PnP code and product id from the EDID header.
    /// </summary>
    private static string? ManufacturerAndProduct(byte[] edid)
    {
        if (edid.Length < 12) return null;

        // Bytes 8-9: three 5-bit letters, big-endian, 'A' = 1.
        int packed = (edid[8] << 8) | edid[9];
        var letters = new char[3];
        letters[0] = (char)('A' + ((packed >> 10) & 0x1F) - 1);
        letters[1] = (char)('A' + ((packed >> 5) & 0x1F) - 1);
        letters[2] = (char)('A' + (packed & 0x1F) - 1);

        if (letters.Any(c => c is < 'A' or > 'Z')) return null;

        var vendor = new string(letters);
        int product = edid[10] | (edid[11] << 8);

        return $"{FriendlyVendor(vendor)} {product:X4}";
    }

    /// <summary>Expand the common PnP vendor codes; unknown ones are shown as-is.</summary>
    private static string FriendlyVendor(string code) => code switch
    {
        "GSM" => "LG",
        "DEL" => "Dell",
        "BOE" => "BOE",
        "AUO" => "AU Optronics",
        "SAM" => "Samsung",
        "SDC" => "Samsung",
        "LGD" => "LG Display",
        "ACI" => "Asus",
        "AUS" => "Asus",
        "ACR" => "Acer",
        "BNQ" => "BenQ",
        "HWP" => "HP",
        "LEN" => "Lenovo",
        "MSI" => "MSI",
        "AOC" => "AOC",
        "VSC" => "ViewSonic",
        "GBT" => "Gigabyte",
        "PHL" => "Philips",
        "SNY" => "Sony",
        "NEC" => "NEC",
        "EIZ" => "EIZO",
        "IVM" => "Iiyama",
        _ => code,
    };
}
