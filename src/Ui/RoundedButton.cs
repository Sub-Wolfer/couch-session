using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A flat button with genuinely smooth rounded corners and properly centred icon + text.
///
/// Two things this fixes over a stock Button:
///
/// Corners. The obvious approach — clipping the control to a rounded <see cref="Region"/> —
/// produces visibly jagged edges, because a Region is a hard-edged bitmap mask with no
/// antialiasing. Here the shape is painted instead, with the parent's colour behind it, so
/// the curve is anti-aliased against the real background.
///
/// Alignment. WinForms positions image and text independently, so ImageAlign/TextAlign can
/// never centre the pair as a unit — you get an off-centre look no matter what you pick.
/// This measures both and centres the group.
/// </summary>
internal sealed class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 9;

    /// <summary>Gap between the icon and the text, in pixels.</summary>
    public int IconGap { get; set; } = 9;

    public Color NormalColor { get; set; } = Color.Gray;
    public Color HoverColor { get; set; } = Color.DarkGray;
    public Color PressedColor { get; set; } = Color.DimGray;

    private bool _hovering;
    private bool _pressing;

    public RoundedButton()
    {
        // UserPaint + AllPaintingInWmPaint + DoubleBuffer: we take over drawing entirely and
        // want it flicker-free. ResizeRedraw keeps the curve correct when the size changes.
        SetStyle(ControlStyles.UserPaint
               | ControlStyles.AllPaintingInWmPaint
               | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);

        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        BackColor = Color.Transparent;
        ForeColor = Color.White;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hovering = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovering = false; _pressing = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressing = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressing = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Paint the parent's colour first so the area outside the curve blends correctly
        // instead of showing the control's own rectangle.
        Theme.PaintBackdrop(g, this);

        var fill = !Enabled ? Color.FromArgb(214, 216, 220)
                 : _pressing ? PressedColor
                 : _hovering ? HoverColor
                 : NormalColor;

        // Inset by half a pixel so the anti-aliased edge lands inside the control and is not
        // clipped flat on the outermost row of pixels.
        var bounds = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);

        using (var path = RoundedPath(bounds, CornerRadius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        DrawContent(g);
    }

    /// <summary>Measure icon and text together, then centre the pair.</summary>
    private void DrawContent(Graphics g)
    {
        var textSize = TextRenderer.MeasureText(g, Text, Font, Size.Empty, TextFormatFlags.NoPadding);
        int iconWidth = Image?.Width ?? 0;
        int gap = Image is not null && Text.Length > 0 ? IconGap : 0;

        int totalWidth = iconWidth + gap + textSize.Width;
        int left = (Width - totalWidth) / 2;

        if (Image is not null)
        {
            var iconRect = new Rectangle(left, (Height - Image.Height) / 2, Image.Width, Image.Height);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.DrawImage(Image, iconRect);
        }

        if (Text.Length == 0) return;

        var textRect = new Rectangle(
            left + iconWidth + gap,
            (Height - textSize.Height) / 2,
            textSize.Width,
            textSize.Height);

        TextRenderer.DrawText(g, Text, Font, textRect,
                              Enabled ? ForeColor : Color.FromArgb(120, 122, 126),
                              TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }

    private static GraphicsPath RoundedPath(RectangleF bounds, int radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2f, Math.Min(bounds.Width, bounds.Height));

        if (d <= 0)
        {
            path.AddRectangle(bounds);
            return path;
        }

        path.AddArc(bounds.X, bounds.Y, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Y, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
