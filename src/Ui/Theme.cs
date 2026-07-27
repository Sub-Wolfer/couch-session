using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// One place for every colour, font and shape in the app.
///
/// Centralised so the whole UI can be re-skinned by editing this file, and so nothing drifts
/// into hard-coded colours the way the earlier light theme did.
/// </summary>
internal static class Theme
{
    // ---- surfaces, back to front ----
    public static readonly Color Window = Color.FromArgb(18, 20, 26);
    public static readonly Color Rail = Color.FromArgb(23, 26, 33);

    /// <summary>
    /// The title strip, a shade below the rail.
    ///
    /// Flat rather than graded, deliberately: it carries the close button, and a gradient behind
    /// a rounded control puts dark notches in its corners unless the control samples it. Two
    /// distinct flat tones separate the chrome from the content just as well.
    /// </summary>
    public static readonly Color TitleBar = Color.FromArgb(16, 18, 24);

    /// <summary>The brand block at the top of the rail, lifted slightly away from the nav.</summary>
    public static readonly Color Brand = Color.FromArgb(28, 32, 41);
    public static readonly Color Surface = Color.FromArgb(30, 34, 43);
    public static readonly Color SurfaceHi = Color.FromArgb(42, 48, 60);
    public static readonly Color Line = Color.FromArgb(58, 66, 82);

    /// <summary>Inputs sit lighter than their card so they read as editable, not decorative.</summary>
    public static readonly Color Input = Color.FromArgb(48, 55, 69);
    public static readonly Color InputLine = Color.FromArgb(78, 88, 108);

    /// <summary>Divider between rows inside a card. Deliberately barely there.</summary>
    public static readonly Color Hairline = Color.FromArgb(45, 51, 63);

    /// <summary>Divider between rows of a dense list, where a faint line is not enough.</summary>
    public static readonly Color RowLine = Color.FromArgb(60, 68, 84);

    // ---- text ----
    public static readonly Color Text = Color.FromArgb(233, 236, 242);
    public static readonly Color TextDim = Color.FromArgb(172, 181, 197);
    public static readonly Color TextFaint = Color.FromArgb(126, 135, 152);

    // ---- accents ----
    public static readonly Color Accent = Color.FromArgb(124, 106, 247);   // violet
    public static readonly Color AccentHi = Color.FromArgb(142, 126, 255);
    public static readonly Color AccentDim = Color.FromArgb(58, 52, 104);
    public static readonly Color Good = Color.FromArgb(62, 191, 122);

    /// <summary>For notes that explain a limit rather than report a problem.</summary>
    // Violet rather than blue.
    //
    // Every note, hint and "worth knowing" mark in the app comes from here, and blue was the one
    // colour on screen with nothing to do with the app's own — it read as a link, or as another
    // program's palette. This is the accent lightened for text: same family as the brand, legible
    // at small sizes on a dark surface, and still clearly not the green or the red that mean
    // something specific.
    public static readonly Color Info = Color.FromArgb(178, 160, 255);
    public static readonly Color Warn = Color.FromArgb(240, 168, 72);
    public static readonly Color Bad = Color.FromArgb(238, 96, 96);
    public static readonly Color Donate = Color.FromArgb(240, 98, 98);
    public static readonly Color DonateHi = Color.FromArgb(228, 82, 82);

    // ---- type ----
    public static Font Title => new("Segoe UI Semibold", 19F);
    public static Font Heading => new("Segoe UI Semibold", 13F);
    public static Font Body => new("Segoe UI", 11F);
    public static Font BodySemi => new("Segoe UI Semibold", 11F);

    /// <summary>Setting descriptions. Kept a step below the title it belongs to, never smaller.</summary>
    public static Font Small => new("Segoe UI", 10F);

    /// <summary>Bold form of <see cref="Small"/>, for the emphasised run inside a description.</summary>
    public static Font SmallBold => new("Segoe UI Semibold", 10F);

    /// <summary>
    /// Emphasis inside a setting title. Bold rather than semibold, so it separates.
    ///
    /// Asked for as a style of the Segoe UI family, not as a family called "Segoe UI Bold".
    /// That family does not exist — bold is a style within Segoe UI, whereas Semibold genuinely
    /// is its own family, which is what made the mistake plausible. GDI+ does not complain about
    /// a missing family; it quietly substitutes a default face. So the one run meant to stand
    /// out was rendered in fallback text at regular weight, sitting inside a Semibold heading —
    /// lighter than the words around it, which is why "not recommended" never looked bold.
    /// </summary>
    public static Font BodyBold => new("Segoe UI", 11F, FontStyle.Bold);

    public static Font Caption => new("Segoe UI Semibold", 9F);

    /// <summary>Rounded rectangle path, used by every card, pill and button.</summary>
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
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

    /// <summary>
    /// Smoothing for a rounded shape.
    ///
    /// AntiAlias alone is not enough: GDI+ samples path edges on pixel boundaries by default,
    /// so a curve comes out visibly stepped even with smoothing on. HighQuality pixel offset is
    /// what actually puts the samples at pixel centres. Set here rather than at each call site
    /// so no control can be drawn without it — which is exactly how the dropdowns ended up with
    /// hard corners while everything else looked smooth.
    /// </summary>
    private static void Smooth(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    }

    public static void FillRounded(Graphics g, RectangleF bounds, float radius, Color fill)
    {
        Smooth(g);
        using var path = Rounded(bounds, radius);
        using var brush = new SolidBrush(fill);
        g.FillPath(brush, path);
    }

    /// <summary>
    /// A rounded shape filled with a vertical gradient.
    ///
    /// Kept shallow on purpose. A few percent of lift at the top reads as a lit surface and
    /// gives the panels some depth; anything stronger turns into a visible band and starts
    /// competing with the content, which on a dark theme looks cheap rather than modern.
    /// </summary>
    public static void FillRoundedGradient(Graphics g, RectangleF bounds, float radius,
                                           Color top, Color bottom)
    {
        Smooth(g);

        // A zero-height gradient brush throws, and one-pixel controls do occur mid-layout.
        if (bounds.Height < 1f) { FillRounded(g, bounds, radius, top); return; }

        using var path = Rounded(bounds, radius);
        using var brush = new LinearGradientBrush(
            new RectangleF(bounds.X, bounds.Y - 0.5f, bounds.Width, bounds.Height + 1f),
            top, bottom, LinearGradientMode.Vertical);

        g.FillPath(brush, path);
    }

    /// <summary>The same shade lifted and dropped a little, for a subtle gradient.</summary>
    public static (Color Top, Color Bottom) Sheen(Color baseColour, float strength = 0.05f) =>
        (Mix(baseColour, Color.White, strength), Mix(baseColour, Color.Black, strength * 0.6f));

    /// <summary>
    /// Fill a plain rectangle with a vertical gradient.
    ///
    /// Not for container backgrounds. Every rounded control here fills the area outside its
    /// corners with its parent's flat colour, so a gradient behind one shows up as a box around
    /// it. Gradients belong on the shapes themselves — see FillRoundedGradient.
    /// </summary>
    public static void FillGradient(Rectangle bounds, Graphics g, Color top, Color bottom)
    {
        if (bounds.Width < 1 || bounds.Height < 1) return;

        using var brush = new LinearGradientBrush(
            new Rectangle(bounds.X, bounds.Y - 1, bounds.Width, bounds.Height + 2),
            top, bottom, LinearGradientMode.Vertical);

        g.FillRectangle(brush, bounds);
    }

    /// <summary>The page backdrop: the window colour, lifted slightly at the top.</summary>
    public static void FillGradient(Rectangle bounds, Graphics g) =>
        FillGradient(bounds, g, Mix(Window, Color.White, 0.035f),
                                Mix(Window, Color.Black, 0.20f));

    public static void DrawRounded(Graphics g, RectangleF bounds, float radius, Color line, float width = 1f)
    {
        Smooth(g);
        using var path = Rounded(bounds, radius);
        using var pen = new Pen(line, width);
        g.DrawPath(pen, path);
    }

    /// <summary>
    /// Paint whatever is actually behind a control, into that control's own coordinates.
    ///
    /// Rounded controls have to fill the area outside their corners with the background, and a
    /// single flat colour is not good enough once that background is a gradient: the card fades
    /// downward while the corner fill stays at the card's base colour, so every button picks up
    /// four dark notches. That is the "black parts on the edges".
    ///
    /// Painting the matching slice of the card's own gradient makes the corners disappear
    /// regardless of where the control sits on the card.
    /// </summary>
    public static void PaintBackdrop(Graphics g, Control control)
    {
        var card = FindCard(control, out int offsetX, out int offsetY);

        if (card is null)
        {
            g.Clear(BackdropOf(control));
            return;
        }

        var (top, bottom) = Sheen(card.Fill, CardSheen);

        // The brush is laid out over the whole card, then read through the control's window on
        // to it — which is what keeps the slice aligned with what the card itself drew.
        using var brush = new LinearGradientBrush(
            new Rectangle(0, -offsetY, Math.Max(1, control.Width), Math.Max(1, card.Height)),
            top, bottom, LinearGradientMode.Vertical);

        g.FillRectangle(brush, control.ClientRectangle);

        // The card's highlighted row, drawn again here.
        //
        // This is the whole reason a highlight cannot simply be a BackColor on the row: every custom
        // control on these pages calls this method to paint its own background, and what it paints is
        // the *card's* surface — so anything set behind them was covered by each one in turn, leaving a
        // tint visible only in the gaps between controls. Drawing it here means the row's contents put
        // it back exactly where the card drew it.
        // The hovered row, on the same terms as the marked one: it sits behind the row's contents,
        // and its contents are what paint over it, so they have to put it back.
        if (!card.Hover.IsEmpty && card.Hover != card.Highlight)
        {
            var hovered = card.Hover;
            hovered.Offset(-offsetX, -offsetY);

            Card.PaintHover(g, hovered);
        }

        if (!card.Highlight.IsEmpty)
        {
            var here = card.Highlight;
            here.Offset(-offsetX, -offsetY);

            Card.PaintHighlight(g, here);
        }
    }

    /// <summary>How much lift a card's gradient carries. Shared so corners can match it.</summary>
    public const float CardSheen = 0.045f;

    /// <summary>The nearest Card ancestor, and how far down it this control sits.</summary>
    private static Card? FindCard(Control control, out int offsetX, out int offsetY)
    {
        offsetX = 0;
        offsetY = 0;

        for (var c = control; c.Parent is not null; c = c.Parent)
        {
            offsetX += c.Left;
            offsetY += c.Top;

            if (c.Parent is Card card) return card;
        }

        return null;
    }

    /// <summary>
    /// The colour actually behind a control, used by every owner-drawn control to fill the area
    /// outside its rounded corners.
    ///
    /// A Card is the case that matters. Its BackColor is Transparent — it paints its own rounded
    /// surface — so walking up by BackColor alone skips straight past it to the page background
    /// and returns a colour several shades too dark. Anything rounded sitting on a card then
    /// painted that darker colour into its corners, which is what drew a visible box around every
    /// button and switch. A Card is asked for the colour it actually paints.
    /// </summary>
    public static Color BackdropOf(Control control)
    {
        for (var parent = control.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is Card card) return card.Fill;
            if (parent.BackColor.A == 255) return parent.BackColor;
        }

        return Window;
    }

    /// <summary>Blend two colours; used for hover states so they stay in the palette.</summary>
    public static Color Mix(Color a, Color b, float amount)
    {
        amount = Math.Clamp(amount, 0f, 1f);
        return Color.FromArgb(
            (int)(a.R + (b.R - a.R) * amount),
            (int)(a.G + (b.G - a.G) * amount),
            (int)(a.B + (b.B - a.B) * amount));
    }
}
