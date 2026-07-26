using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// One trademark-free way to point at the guide button on any controller.
///
/// This replaces two console logos joined by "or". Those had three problems. They are registered
/// trademarks, so the real artwork cannot ship and the app was drawing lookalikes, which is the same
/// problem wearing a hat. They named two brands when the app supports anything Windows recognises, so
/// a Switch Pro or 8BitDo owner was left to guess whether either applied to them. And a logo says
/// *which console* rather than *which button*, which is the only thing the reader actually needs.
///
/// What every guide button genuinely has in common is not its symbol but its **position**: the big one
/// alone in the middle, between and below the sticks. So that is what gets drawn — an abstract pad
/// with the centre button lit and haloed. It reads instantly, needs no brand knowledge, and stays
/// accurate for controllers that have not been made yet.
///
/// The shape is deliberately generic: a rounded body, two grips, two sticks and a d-pad suggestion,
/// none of it matching any particular manufacturer's outline. It is a diagram, not a portrait.
/// </summary>
internal static class GuideMark
{
    /// <summary>
    /// Draw the pad into <paramref name="box"/>, with the guide button picked out in
    /// <paramref name="accent"/>.
    ///
    /// Everything is proportional to the box so one routine serves the banner, a settings row, or
    /// anything later that needs it.
    /// </summary>
    public static void Draw(Graphics g, RectangleF box, Color accent)
    {
        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // The pad sits in the middle of the box, wider than it is tall, with room left around it for
        // the halo to breathe.
        float w = box.Width * 0.86f;
        float h = w * 0.62f;
        float x = box.X + (box.Width - w) / 2f;
        float y = box.Y + (box.Height - h) / 2f;

        var body = new RectangleF(x, y, w, h);

        using (var shell = Body(body))
        {
            using var fill = new SolidBrush(Theme.Mix(Theme.Surface, Color.White, 0.16f));
            g.FillPath(fill, shell);

            using var edge = new Pen(Theme.Mix(Theme.Surface, Color.White, 0.34f), Math.Max(1f, w * 0.016f));
            g.DrawPath(edge, shell);
        }

        float cx = body.X + body.Width / 2f;
        float cy = body.Y + body.Height * 0.42f;

        // The two sticks and the d-pad, drawn faintly. They exist only to make the middle read as the
        // middle — take them away and the centre button is just a dot in a rounded rectangle.
        float stick = w * 0.115f;
        float inset = w * 0.235f;
        float below = body.Height * 0.60f;

        using (var dim = new SolidBrush(Theme.Mix(Theme.Surface, Color.White, 0.28f)))
        {
            g.FillEllipse(dim, body.X + inset - stick / 2f, body.Y + below - stick / 2f, stick, stick);
            g.FillEllipse(dim, body.Right - inset - stick / 2f, body.Y + below - stick / 2f, stick, stick);

            // A suggestion of a d-pad and a face-button cluster, one small shape each side.
            float dot = w * 0.055f;
            g.FillEllipse(dim, body.X + inset - dot / 2f, body.Y + body.Height * 0.30f - dot / 2f, dot, dot);
            g.FillEllipse(dim, body.Right - inset - dot / 2f, body.Y + body.Height * 0.30f - dot / 2f, dot, dot);
        }

        // The halo. Two soft rings rather than a glow, because a blur at this size just looks smudged.
        float guide = w * 0.145f;

        for (int ring = 2; ring >= 1; ring--)
        {
            float grow = guide + guide * 0.55f * ring;
            int alpha = ring == 2 ? 26 : 52;

            using var halo = new Pen(Color.FromArgb(alpha, accent), Math.Max(1f, w * 0.02f));
            g.DrawEllipse(halo, cx - grow / 2f, cy - grow / 2f, grow, grow);
        }

        using (var lit = new SolidBrush(accent))
            g.FillEllipse(lit, cx - guide / 2f, cy - guide / 2f, guide, guide);

        // A highlight on the button so it reads as raised rather than as a hole.
        using (var sheen = new SolidBrush(Color.FromArgb(70, 255, 255, 255)))
            g.FillEllipse(sheen, cx - guide / 2f + guide * 0.18f, cy - guide / 2f + guide * 0.12f,
                          guide * 0.42f, guide * 0.34f);

        g.SmoothingMode = mode;
    }

    /// <summary>The pad outline: a rounded body with a grip dropping from each end.</summary>
    private static GraphicsPath Body(RectangleF r)
    {
        var path = new GraphicsPath();

        float top = r.Height * 0.52f;
        float radius = top * 0.42f;

        // The main slab.
        var slab = new RectangleF(r.X, r.Y, r.Width, top);
        path.AddArc(slab.X, slab.Y, radius * 2, radius * 2, 180, 90);
        path.AddArc(slab.Right - radius * 2, slab.Y, radius * 2, radius * 2, 270, 90);
        path.AddArc(slab.Right - radius * 2, slab.Bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        path.AddArc(slab.X, slab.Bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        path.CloseFigure();

        // A grip under each end, angled outward the way every pad's are.
        float gw = r.Width * 0.30f;
        float gh = r.Height * 0.72f;

        path.AddEllipse(r.X, r.Y + r.Height - gh, gw, gh);
        path.AddEllipse(r.Right - gw, r.Y + r.Height - gh, gw, gh);

        return path;
    }
}
