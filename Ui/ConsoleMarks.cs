namespace CouchMode.Ui;

/// <summary>
/// The PlayStation and Xbox button artwork, if it has been supplied — otherwise the drawn stand-ins.
///
/// Split out so there is exactly one answer to "what does a PS button look like", used by the home
/// page banner and the title-bar strip alike. Before this they each drew their own, which is how two
/// parts of the same app end up disagreeing about the same thing.
///
/// The images are optional on purpose. Both marks are trademarks and neither can be shipped in this
/// repository without a licence, so the build works with or without them: drop <c>ps-button.png</c> and
/// <c>xbox-button.png</c> beside the csproj and every mark in the app becomes the real thing, leave them
/// out and the drawn ones are used. Nothing else has to change either way.
///
/// PNG with transparency, square, and at least 128px — they are scaled down to 18px in the title bar
/// and 46px on the home page, and downscaling is kind while upscaling is not.
/// </summary>
internal static class ConsoleMarks
{
    private static Image? _ps, _xbox;
    private static bool _looked;

    /// <summary>The artwork for one family, or null when it was not supplied.</summary>
    public static Image? For(bool playStation)
    {
        if (!_looked)
        {
            _looked = true;
            _ps = Load("ps-button.png");
            _xbox = Load("xbox-button.png");

            Log.Info(_ps is null && _xbox is null
                ? "Console button artwork not embedded; drawing the marks instead."
                : $"Console button artwork loaded (PlayStation: {_ps is not null}, Xbox: {_xbox is not null}).");
        }

        return playStation ? _ps : _xbox;
    }

    /// <summary>
    /// Draw the mark into the given box: the real artwork when there is some, the drawn one when not.
    ///
    /// One call for both cases, so no caller has to know which it got — and so adding the artwork later
    /// changes every mark in the app at once, with no code to revisit.
    /// </summary>
    public static void Draw(Graphics g, Rectangle box, bool playStation)
    {
        var art = For(playStation);

        if (art is not null)
        {
            var was = g.InterpolationMode;
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;

            // Fitted to the square without distorting, whatever proportions the file happens to have.
            float scale = Math.Min(box.Width / (float)art.Width, box.Height / (float)art.Height);
            float w = art.Width * scale, h = art.Height * scale;

            g.DrawImage(art, box.X + (box.Width - w) / 2f, box.Y + (box.Height - h) / 2f, w, h);
            g.InterpolationMode = was;
            return;
        }

        DrawFallback(g, box, playStation);
    }

    /// <summary>The stand-in: the PS lettering, or the Xbox ring with its two crossing arcs.</summary>
    private static void DrawFallback(Graphics g, Rectangle box, bool playStation)
    {
        var ink = playStation ? Color.FromArgb(140, 180, 255) : Color.FromArgb(124, 208, 92);

        if (playStation)
        {
            // Sized to the box rather than fixed, so the same code serves the title bar and the banner.
            using var font = new Font("Segoe UI", box.Height * 0.42f, FontStyle.Bold);
            TextRenderer.DrawText(g, "PS", font, box, ink,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.NoPrefix);
            return;
        }

        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;
        float r = box.Height * 0.40f;

        using var pen = new Pen(ink, Math.Max(1.2f, box.Height * 0.09f))
        {
            StartCap = System.Drawing.Drawing2D.LineCap.Round,
            EndCap = System.Drawing.Drawing2D.LineCap.Round,
        };

        g.DrawEllipse(pen, cx - r, cy - r, r * 2, r * 2);
        g.DrawArc(pen, cx - r * 1.45f, cy - r * 0.85f, r * 1.7f, r * 1.7f, -55f, 110f);
        g.DrawArc(pen, cx - r * 0.25f, cy - r * 0.85f, r * 1.7f, r * 1.7f, 125f, 110f);
    }

    private static Image? Load(string resource)
    {
        try
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();

            // Matched on the end of the name: the SDK prefixes resources with the assembly name, and
            // that has changed with every rename this app has been through.
            var name = Array.Find(assembly.GetManifestResourceNames(),
                                  n => n.EndsWith(resource, StringComparison.OrdinalIgnoreCase));

            if (name is null) return null;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) return null;

            // Copied out before decoding: Image keeps the stream it was made from, and a resource
            // stream disposed underneath a live image is a crash waiting to be drawn.
            var memory = new MemoryStream();
            stream.CopyTo(memory);
            memory.Position = 0;

            return Image.FromStream(memory);
        }
        catch (Exception ex)
        {
            Log.Warn($"Console mark '{resource}' could not be loaded: {ex.Message}");
            return null;
        }
    }
}
