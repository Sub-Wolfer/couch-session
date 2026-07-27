using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// One trademark-free way to point at the guide button on any controller.
///
/// This replaced two console logos joined by "or". Those had three problems. They are registered
/// trademarks, so the real artwork cannot ship and the app was drawing lookalikes, which is the same
/// problem wearing a hat. They named two brands when the app supports anything Windows recognises, so
/// a Switch Pro or 8BitDo owner was left to guess whether either applied to them. And a logo says
/// *which console* rather than *which button*, which is the only thing the reader actually needs.
///
/// The first replacement drew a whole gamepad with its centre button lit, on the reasoning that what
/// every guide button has in common is its position rather than its symbol. That reasoning is right
/// and the drawing was wrong. At the size this is used, a pad silhouette is a grey blob: the outline
/// is the biggest, highest-contrast thing in the picture, so the eye lands on the shape of the body
/// and the two grips, while the one element that matters is a small dot lost in the middle of it. It
/// also invites a comparison it cannot win, because every reader owns a real controller and this one
/// looks like none of them.
///
/// So the pad is gone and only the button is left, drawn large: a lit cap sitting in a dark well,
/// with two rings spreading out from it. The rings are what make it an instruction rather than a
/// decoration — a circle on its own is a dot, and a circle with something radiating out of it is
/// something being pressed. Nothing here is brand-specific, nothing has to be recognised, and it
/// stays true for controllers that have not been made yet.
/// </summary>
internal static class GuideMark
{
    /// <summary>
    /// Draw the mark into <paramref name="box"/>, lit in <paramref name="accent"/>.
    ///
    /// Everything is proportional to the smaller side of the box, so one routine serves the banner,
    /// a settings row, or anything later that needs it, at any size and any DPI.
    /// </summary>
    public static void Draw(Graphics g, RectangleF box, Color accent)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;
        float span = Math.Min(box.Width, box.Height);

        // The press, spreading outward. Two rings rather than a blur: a real glow at this size just
        // looks like the drawing is out of focus, while two clean rings read as movement.
        //
        // The fainter one is outside, so the whole thing fades away from the centre and the eye is
        // walked inward to the button rather than out to the edge of the box.
        Ring(g, cx, cy, span * 0.98f, Color.FromArgb(28, accent), span * 0.030f);
        Ring(g, cx, cy, span * 0.76f, Color.FromArgb(62, accent), span * 0.034f);

        // The well the button sits in. Darker than the panel behind it, which is what makes the cap
        // look raised — without it the cap is a sticker rather than a button.
        float seat = span * 0.60f;

        using (var well = new SolidBrush(Theme.Mix(Theme.Surface, Color.Black, 0.38f)))
            g.FillEllipse(well, cx - seat / 2f, cy - seat / 2f, seat, seat);

        using (var lip = new Pen(Theme.Mix(Theme.Surface, Color.White, 0.20f), Math.Max(1f, span * 0.018f)))
            g.DrawEllipse(lip, cx - seat / 2f, cy - seat / 2f, seat, seat);

        // The cap itself. Lit top to bottom so it has a direction of light, which is the cheapest way
        // to make a flat circle read as a physical thing.
        float cap = span * 0.44f;
        var face = new RectangleF(cx - cap / 2f, cy - cap / 2f, cap, cap);

        // Inflated by a pixel for the gradient only: a LinearGradientBrush maps its colours across
        // exactly the rectangle it is given, and using the same one it fills leaves a hairline of the
        // wrong colour along the edge where the mapping and the shape disagree.
        var ramp = RectangleF.Inflate(face, 1f, 1f);

        using (var lit = new LinearGradientBrush(ramp,
                                                 Theme.Mix(accent, Color.White, 0.38f),
                                                 Theme.Mix(accent, Color.Black, 0.20f),
                                                 LinearGradientMode.Vertical))
            g.FillEllipse(lit, face);

        using (var edge = new Pen(Theme.Mix(accent, Color.White, 0.55f), Math.Max(1f, span * 0.020f)))
            g.DrawEllipse(edge, face);

        // A highlight up and to the left, where the light is coming from.
        using (var sheen = new SolidBrush(Color.FromArgb(95, 255, 255, 255)))
            g.FillEllipse(sheen, face.X + cap * 0.19f, face.Y + cap * 0.13f, cap * 0.46f, cap * 0.30f);

        g.SmoothingMode = mode;
    }

    private static void Ring(Graphics g, float cx, float cy, float diameter, Color ink, float width)
    {
        using var pen = new Pen(ink, Math.Max(1.2f, width));
        g.DrawEllipse(pen, cx - diameter / 2f, cy - diameter / 2f, diameter, diameter);
    }
}
