using System.Reflection;

namespace CouchMode.Ui;

/// <summary>
/// The application icon, loaded once from the embedded .ico.
///
/// Using the real icon file rather than drawing shapes at runtime means the tray icon, the
/// window icon and the icon Explorer shows on the exe are all the same artwork, and the
/// multi-resolution .ico lets Windows pick the right size for each context instead of
/// scaling one bitmap badly.
/// </summary>
internal static class AppIcon
{
    private static Icon? _cached;

    /// <summary>Full multi-resolution icon. Do not dispose — it is shared.</summary>
    public static Icon Shared => _cached ??= Load();

    /// <summary>
    /// A new icon sized for the notification area, honouring the user's DPI.
    ///
    /// When idle it is desaturated, so the tray shows at a glance whether a session is
    /// running without needing two separate pieces of artwork to keep in sync.
    /// </summary>
    public static Icon ForTray(bool active)
    {
        var size = SystemInformation.SmallIconSize;

        try
        {
            using var colour = Monogram(Math.Max(size.Width, size.Height));

            if (active) return Icon.FromHandle(colour.GetHicon());

            using var dimmed = Desaturate(colour);
            return Icon.FromHandle(dimmed.GetHicon());
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not build the tray icon: {ex.Message}");
            return new Icon(Shared, size.Width, size.Height);
        }
    }

    private static Bitmap Desaturate(Bitmap source)
    {
        var result = new Bitmap(source.Width, source.Height);

        // Standard luminance weights, then a slight lift so it reads as "off" rather than
        // "broken" against both light and dark taskbars.
        var matrix = new System.Drawing.Imaging.ColorMatrix(
        [
            [0.299f, 0.299f, 0.299f, 0, 0],
            [0.587f, 0.587f, 0.587f, 0, 0],
            [0.114f, 0.114f, 0.114f, 0, 0],
            [0,      0,      0,      1, 0],
            [0.12f,  0.12f,  0.12f,  0, 1],
        ]);

        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(matrix);

        using var g = Graphics.FromImage(result);
        g.DrawImage(source,
                    new Rectangle(0, 0, source.Width, source.Height),
                    0, 0, source.Width, source.Height,
                    GraphicsUnit.Pixel, attributes);

        return result;
    }


    /// <summary>
    /// The beer mug for the donate button, sized to fit. Shipped as an image rather than an
    /// emoji glyph because emoji colour rendering depends on the font stack and can silently
    /// fall back to a flat monochrome outline.
    /// </summary>
    /// <summary>
    /// A heart for the support button.
    ///
    /// Drawn rather than shipped as an image: it is two circles and a triangle, and adding
    /// another embedded resource for that would be more moving parts than the shape is worth.
    /// </summary>
    public static Image Heart(int size, Color colour)
    {
        // Rendered at 4x and scaled down; GDI+ antialiasing alone leaves the point ragged.
        const int Scale = 4;
        int big = size * Scale;

        using var large = new Bitmap(big, big);

        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float w = big, h = big;
            using var path = new System.Drawing.Drawing2D.GraphicsPath();

            path.AddArc(w * 0.04f, h * 0.10f, w * 0.48f, h * 0.48f, 160, 200);
            path.AddArc(w * 0.48f, h * 0.10f, w * 0.48f, h * 0.48f, 180, 200);
            path.AddLine(w * 0.50f, h * 0.94f, w * 0.04f, h * 0.36f);
            path.CloseFigure();

            using var brush = new SolidBrush(colour);
            g.FillPath(brush, path);
        }

        var small = new Bitmap(size, size);

        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(large, new Rectangle(0, 0, size, size));
        }

        return small;
    }



    /// <summary>
    /// A beetle, for the bug report button.
    ///
    /// Drawn rather than shipped, like everything else here: a single-file build with no bitmaps beside
    /// it cannot lose an icon, and this scales to whatever the button asks for. Same 4x-and-scale-down
    /// trick as the heart, because thin legs alias badly at 16 pixels.
    /// </summary>
    public static Image Bug(int size, Color colour)
    {
        const int Scale = 4;
        int big = size * Scale;

        using var large = new Bitmap(big, big);

        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            float w = big, h = big;

            using var pen = new Pen(colour, big * 0.075f)
            {
                StartCap = System.Drawing.Drawing2D.LineCap.Round,
                EndCap = System.Drawing.Drawing2D.LineCap.Round,
            };

            using var brush = new SolidBrush(colour);

            // Legs first, so the body sits over where they meet it.
            for (int i = 0; i < 3; i++)
            {
                float y = h * (0.38f + i * 0.16f);
                float spread = h * (i == 1 ? 0.06f : 0.10f);

                g.DrawLine(pen, w * 0.30f, y, w * 0.10f, y + (i == 0 ? -spread : spread));
                g.DrawLine(pen, w * 0.70f, y, w * 0.90f, y + (i == 0 ? -spread : spread));
            }

            // Antennae.
            g.DrawLine(pen, w * 0.42f, h * 0.24f, w * 0.30f, h * 0.08f);
            g.DrawLine(pen, w * 0.58f, h * 0.24f, w * 0.70f, h * 0.08f);

            // Head, then the shell.
            g.FillEllipse(brush, w * 0.36f, h * 0.14f, w * 0.28f, h * 0.20f);
            g.FillEllipse(brush, w * 0.24f, h * 0.28f, w * 0.52f, h * 0.62f);

            // The seam down the back, punched out so the shell reads as two wing cases.
            using (var seam = new Pen(Color.FromArgb(0, 0, 0, 0), big * 0.045f))
            {
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceCopy;
                g.DrawLine(seam, w * 0.50f, h * 0.34f, w * 0.50f, h * 0.84f);
                g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            }
        }

        var small = new Bitmap(size, size);

        using (var g = Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(large, new Rectangle(0, 0, size, size));
        }

        return small;
    }


    /// <summary>
    /// The brand mark, drawn at runtime: a "CS" monogram on a rounded, vivid violet-to-blue gradient
    /// tile, with a glossy top sheen, a bright accent bar under the letters, and a crisp rim. The extra
    /// flair — the sheen and the accent stroke — gives it a memorable, app-store look while staying
    /// legible from a tray glyph up to the sidebar badge, and it stays free of any shipped bitmap.
    /// </summary>
    public static Bitmap Monogram(int size)
    {
        // Rendered large and scaled down; the rounded corners and the letters both stay crisp.
        const int Scale = 4;
        int big = Math.Max(1, size) * Scale;

        using var large = new Bitmap(big, big);
        using (var g = Graphics.FromImage(large))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            float pad = big * 0.05f;
            var tile = new RectangleF(pad, pad, big - 2 * pad, big - 2 * pad);
            float radius = big * 0.26f;

            using var path = Rounded(tile, radius);

            // Livelier three-stop gradient — violet into indigo into a bright blue — so the mark has
            // more life and reads as ours at a glance, not just another dark square.
            using (var fill = new System.Drawing.Drawing2D.LinearGradientBrush(
                       tile, Color.Black, Color.Black,
                       System.Drawing.Drawing2D.LinearGradientMode.ForwardDiagonal))
            {
                fill.InterpolationColors = new System.Drawing.Drawing2D.ColorBlend
                {
                    Colors =
                    [
                        Color.FromArgb(0x8B, 0x5C, 0xFF),   // vivid violet
                        Color.FromArgb(0x5B, 0x4B, 0xE8),   // indigo
                        Color.FromArgb(0x2F, 0x86, 0xFF),   // bright blue
                    ],
                    Positions = [0f, 0.55f, 1f],
                };
                g.FillPath(fill, path);
            }

            // Glossy sheen over the top half — a soft white fade clipped to the tile, the classic
            // app-icon highlight that gives the mark depth.
            var saved = g.Save();
            g.SetClip(path);
            var glossArea = new RectangleF(tile.X, tile.Y, tile.Width, tile.Height * 0.55f);
            using (var gloss = new System.Drawing.Drawing2D.LinearGradientBrush(
                       glossArea,
                       Color.FromArgb(70, 255, 255, 255),
                       Color.FromArgb(0, 255, 255, 255),
                       System.Drawing.Drawing2D.LinearGradientMode.Vertical))
                g.FillRectangle(gloss, glossArea);
            g.Restore(saved);

            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            using var font = new Font("Segoe UI", big * 0.40f, FontStyle.Bold, GraphicsUnit.Pixel);

            // Nudge up a hair; optical centring of caps sits slightly high of the geometric middle,
            // and lift a touch more to leave room for the accent bar beneath.
            var textArea = new RectangleF(tile.X, tile.Y - big * 0.05f, tile.Width, tile.Height);

            // Soft shadow under the letters so the white pops off the gradient.
            using (var shadow = new SolidBrush(Color.FromArgb(70, 0, 0, 0)))
            {
                var shadowArea = textArea;
                shadowArea.Offset(big * 0.012f, big * 0.02f);
                g.DrawString("CS", font, shadow, shadowArea, format);
            }

            using (var ink = new SolidBrush(Color.White))
                g.DrawString("CS", font, ink, textArea, format);

            // A short bright accent bar under the letters — the little flourish that makes it stick.
            float barW = tile.Width * 0.34f, barH = big * 0.028f;
            var bar = new RectangleF(tile.X + (tile.Width - barW) / 2f, tile.Y + tile.Height * 0.72f, barW, barH);
            using (var barPath = Rounded(bar, barH / 2f))
            using (var barBrush = new System.Drawing.Drawing2D.LinearGradientBrush(
                       bar,
                       Color.FromArgb(0x6D, 0xE6, 0xFF),   // cyan
                       Color.FromArgb(0xB9, 0x8C, 0xFF),   // light violet
                       System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                g.FillPath(barBrush, barPath);

            // Crisp inner rim for a clean, modern edge.
            using var rim = new Pen(Color.FromArgb(40, 255, 255, 255), big * 0.008f);
            g.DrawPath(rim, path);
        }

        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.DrawImage(large, new Rectangle(0, 0, size, size));
        }
        return bmp;
    }

    /// <summary>The monogram as an icon, for the window and tray. Caller owns it.</summary>
    public static Icon MonogramIcon(int size)
    {
        using var bmp = Monogram(size);
        return Icon.FromHandle(bmp.GetHicon());
    }

    private static System.Drawing.Drawing2D.GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        path.AddArc(r.Left, r.Top, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Top, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.Left, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static Icon Load()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("CouchPotato.ico");
            if (stream is not null) return new Icon(stream);

            Log.Warn("Embedded icon not found; falling back to the system application icon.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not load the embedded icon: {ex.Message}");
        }

        return SystemIcons.Application;
    }
}
