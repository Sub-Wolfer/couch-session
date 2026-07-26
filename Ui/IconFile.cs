using System.Drawing.Imaging;

namespace CouchMode.Ui;

/// <summary>
/// Writes a multi-size .ico from the app's own monogram.
///
/// Exists because the mark lives in two places that had drifted apart. <see cref="AppIcon.Monogram"/>
/// draws it at run time — it is what the settings window, the tray and the session prompt show — while
/// the file icon Explorer and the taskbar use comes from CouchSession.ico, a static file compiled in as
/// the ApplicationIcon. Redraw the monogram and only half the app changes; the .ico keeps whatever it
/// was and the executable goes on wearing the old design.
///
/// Rather than hand-export the icon whenever the mark changes, the app writes it. That way there is one
/// definition of what the mark looks like — the drawing code — and the .ico is generated from it.
///
/// Run it with:  CouchSession.exe --write-icon
///
/// The sizes are the standard Windows set. 16 through 48 are what Explorer and the taskbar pick from,
/// 256 is what the large-icon views and the Store-style surfaces use, and the odd ones between exist
/// because Windows scales a nearby size badly if the exact one is absent.
/// </summary>
internal static class IconFile
{
    private static readonly int[] Sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

    /// <summary>Write the monogram to <paramref name="path"/> as a multi-size icon.</summary>
    public static void WriteMonogram(string path)
    {
        var images = new List<byte[]>();

        foreach (int size in Sizes)
        {
            using var bmp = AppIcon.Monogram(size);
            using var buffer = new MemoryStream();

            // PNG for every entry, not just the large one.
            //
            // Windows has accepted PNG-compressed icon entries since Vista, and it keeps the alpha
            // channel intact without the separate AND mask a BMP entry needs — which is the part that
            // usually goes wrong by hand and leaves a black fringe around the rounded corners.
            bmp.Save(buffer, ImageFormat.Png);
            images.Add(buffer.ToArray());
        }

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(file);

        // ICONDIR
        w.Write((ushort)0);                    // reserved
        w.Write((ushort)1);                    // 1 = icon
        w.Write((ushort)images.Count);

        // Each entry's data follows the whole directory, so the first offset is past all of it.
        int offset = 6 + images.Count * 16;

        for (int i = 0; i < images.Count; i++)
        {
            int size = Sizes[i];

            // 256 is written as 0: the field is one byte, so 256 does not fit and zero is the agreed
            // way of saying "the big one". Getting this wrong is why a hand-made icon often shows
            // every size except the largest.
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);                  // palette entries — 0 for a true-colour image
            w.Write((byte)0);                  // reserved
            w.Write((ushort)1);                // colour planes
            w.Write((ushort)32);               // bits per pixel
            w.Write(images[i].Length);
            w.Write(offset);

            offset += images[i].Length;
        }

        foreach (var image in images) w.Write(image);
    }
}
