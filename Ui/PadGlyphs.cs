using System.Drawing.Drawing2D;
using CouchMode.Input;

namespace CouchMode.Ui;

/// <summary>
/// Controller buttons drawn as the symbols printed on the pad, rather than spelled out.
///
/// "Triangle + D-pad Right" is a sentence about a shortcut. The shapes are the shortcut — they are
/// what is on the controller in front of you, and they are read at a glance rather than parsed.
/// It also solves a width problem: a three-button combination written out did not fit the box and
/// was cut off mid-word, which is the one thing a shortcut display must never do.
///
/// Everything is drawn rather than typed. Controller glyphs do exist in some fonts, but none that
/// ships with Windows reliably, and a missing glyph renders as a hollow rectangle — a shortcut
/// display showing three empty boxes is worse than the words it replaced. Paths and letters in a
/// font that is definitely present cannot fail that way.
/// </summary>
internal static class PadGlyphs
{
    /// <summary>Height of one glyph. Sized to sit comfortably on a 34px row.</summary>
    private const int Size = 21;

    /// <summary>
    /// The enlargement applied to every glyph drawn from here on.
    ///
    /// These are drawn from fixed pixel sizes rather than from a font, so unlike text they did not grow
    /// with anything: on a 4K television the button hints stayed at their desk-monitor size while the
    /// window around them doubled. Callers that scale their own layout set this to match.
    /// </summary>
    private static float _scale = 1f;

    /// <summary>Draw at this multiple of the design size until told otherwise.</summary>
    public static void UseScale(float scale) => _scale = scale <= 0f ? 1f : scale;

    private static int Scaled(int v) => (int)Math.Round(v * _scale);

    /// <summary>
    /// The fractional counterpart of Scaled, for the shapes drawn inside a glyph.
    ///
    /// The outer button scaled but its contents did not: the triangle, circle, cross and square, the
    /// D-pad arrows and every pen width were fixed numbers, so on a 4K television they stayed tiny
    /// inside a button twice the size. These are the numbers that were missed.
    /// </summary>
    private static float Sf(float v) => v * _scale;
    private static int Glyph => Scaled(Size);
    private static int Gap => Scaled(Join);

    /// <summary>Gap either side of the "+" between two glyphs.</summary>
    private const int Join = 14;

    /// <summary>Face button colours, as the pads themselves use them.</summary>
    private static readonly Color PsGreen = Color.FromArgb(90, 208, 168);
    private static readonly Color PsRed = Color.FromArgb(238, 104, 118);
    private static readonly Color PsBlue = Color.FromArgb(122, 165, 255);
    private static readonly Color PsPink = Color.FromArgb(226, 122, 196);

    private static readonly Color XboxGreen = Color.FromArgb(108, 192, 74);
    private static readonly Color XboxRed = Color.FromArgb(224, 74, 74);
    private static readonly Color XboxBlue = Color.FromArgb(74, 127, 224);
    private static readonly Color XboxYellow = Color.FromArgb(226, 182, 60);

    // Rebuilt whenever the scale changes, so the lettering inside a glyph grows with the glyph.
    private static Font CapFont => FontAt(ref _capFont, 8.5f);
    private static Font FaceFont => FontAt(ref _faceFont, 9.5f);

    private static Font? _capFont, _faceFont;
    private static float _fontScale = -1f;

    private static Font FontAt(ref Font? cached, float points)
    {
        if (cached is null || _fontScale != _scale)
        {
            _capFont = null;
            _faceFont = null;
            _fontScale = _scale;
            cached = null;
        }

        return cached ??= new Font("Segoe UI", points * _scale, FontStyle.Bold);
    }

    /// <summary>How wide a combination will draw, so the caller can centre it.</summary>
    public static int Measure(Graphics g, PadFamily family, IReadOnlyList<PadPress> presses)
    {
        if (presses.Count == 0) return 0;

        int width = 0;

        for (int i = 0; i < presses.Count; i++)
        {
            if (i > 0) width += Gap;
            width += WidthOf(g, family, presses[i]);
        }

        return width;
    }

    /// <summary>Draw a combination centred in the given area.</summary>
    public static void Draw(Graphics g, Rectangle bounds, PadFamily family,
                            IReadOnlyList<PadPress> presses, Color ink)
    {
        if (presses.Count == 0) return;

        int total = Measure(g, family, presses);
        int x = bounds.Left + (bounds.Width - total) / 2;
        int y = bounds.Top + (bounds.Height - Glyph) / 2;

        var old = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        for (int i = 0; i < presses.Count; i++)
        {
            if (i > 0)
            {
                // The joiner is dimmed on purpose. It is punctuation between the buttons, and at
                // full strength it competed with them for attention.
                var plus = new Rectangle(x, y, Gap, Glyph);
                TextRenderer.DrawText(g, "+", CapFont, plus, Theme.Mix(ink, Theme.Input, 0.45f),
                                      TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                    | TextFormatFlags.NoPrefix);
                x += Gap;
            }

            int w = WidthOf(g, family, presses[i]);
            DrawOne(g, new Rectangle(x, y, w, Glyph), family, presses[i], ink);
            x += w;
        }

        g.SmoothingMode = old;
    }

    private static int WidthOf(Graphics g, PadFamily family, PadPress press)
    {
        // Round buttons are as wide as they are tall. Everything else is a lozenge sized to its
        // text, with a floor so a single character does not draw as a circle by accident.
        if (IsFace(press.Control) || IsDpad(press.Control)) return Glyph;

        string text = CapText(family, press);
        int measured = TextRenderer.MeasureText(g, text, CapFont, new Size(Scaled(200), Glyph),
                                                TextFormatFlags.NoPrefix).Width;

        return Math.Max(Glyph, measured + Scaled(10));
    }

    private static void DrawOne(Graphics g, Rectangle box, PadFamily family, PadPress press, Color ink)
    {
        if (IsFace(press.Control))
        {
            DrawFace(g, box, family, press.Control);
            return;
        }

        if (IsDpad(press.Control))
        {
            DrawDpad(g, box, press.Control, ink);
            return;
        }

        DrawCap(g, box, CapText(family, press), ink);
    }

    /// <summary>
    /// A face button: a coloured ring with its symbol inside.
    ///
    /// A ring rather than a filled disc, because these sit on a dark input background and a solid
    /// block of colour at this size reads as a status dot rather than a button.
    /// </summary>
    private static void DrawFace(Graphics g, Rectangle box, PadFamily family, PadControl control)
    {
        var colour = FaceColour(family, control);
        var circle = new RectangleF(box.X + Sf(0.75f), box.Y + Sf(0.75f), Glyph - Sf(1.5f), Glyph - Sf(1.5f));

        using (var fill = new SolidBrush(Color.FromArgb(38, colour)))
            g.FillEllipse(fill, circle);

        using (var pen = new Pen(colour, Sf(1.4f)))
            g.DrawEllipse(pen, circle);

        if (family == PadFamily.Xbox)
        {
            // The letter is what is printed on an Xbox pad, so that is what goes inside.
            TextRenderer.DrawText(g, XboxLetter(control), FaceFont, box, colour,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.NoPrefix);
            return;
        }

        DrawPlayStationShape(g, box, control, colour);
    }

    /// <summary>The four shapes, drawn to the same visual weight rather than the same box.</summary>
    private static void DrawPlayStationShape(Graphics g, Rectangle box, PadControl control, Color colour)
    {
        float cx = box.X + Glyph / 2f;
        float cy = box.Y + Glyph / 2f;

        using var pen = new Pen(colour, Sf(1.5f)) { LineJoin = LineJoin.Round };

        switch (control)
        {
            case PadControl.FaceUp:      // Triangle
            {
                float r = Sf(5.4f);

                // Nudged down a touch: a triangle centred on its bounding box looks high, because
                // its visual mass sits below the apex.
                var points = new[]
                {
                    new PointF(cx, cy - r + Sf(0.6f)),
                    new PointF(cx + r * 0.92f, cy + r * 0.72f + Sf(0.6f)),
                    new PointF(cx - r * 0.92f, cy + r * 0.72f + Sf(0.6f)),
                };

                g.DrawPolygon(pen, points);
                break;
            }

            case PadControl.FaceRight:   // Circle
                g.DrawEllipse(pen, cx - Sf(4.6f), cy - Sf(4.6f), Sf(9.2f), Sf(9.2f));
                break;

            case PadControl.FaceDown:    // Cross
            {
                float r = Sf(4.2f);
                g.DrawLine(pen, cx - r, cy - r, cx + r, cy + r);
                g.DrawLine(pen, cx + r, cy - r, cx - r, cy + r);
                break;
            }

            case PadControl.FaceLeft:    // Square
                g.DrawRectangle(pen, cx - Sf(4.4f), cy - Sf(4.4f), Sf(8.8f), Sf(8.8f));
                break;
        }
    }

    /// <summary>A D-pad direction: an arrow in a lozenge, so it reads as one key rather than four.</summary>
    private static void DrawDpad(Graphics g, Rectangle box, PadControl control, Color ink)
    {
        DrawCapBackground(g, box, ink);

        float cx = box.X + box.Width / 2f;
        float cy = box.Y + Glyph / 2f;
        float r = Sf(4.1f);

        var points = control switch
        {
            PadControl.DpadUp => new[]
            {
                new PointF(cx, cy - r), new PointF(cx + r, cy + r * 0.8f), new PointF(cx - r, cy + r * 0.8f),
            },
            PadControl.DpadDown => new[]
            {
                new PointF(cx, cy + r), new PointF(cx + r, cy - r * 0.8f), new PointF(cx - r, cy - r * 0.8f),
            },
            PadControl.DpadLeft => new[]
            {
                new PointF(cx - r, cy), new PointF(cx + r * 0.8f, cy - r), new PointF(cx + r * 0.8f, cy + r),
            },
            _ => new[]
            {
                new PointF(cx + r, cy), new PointF(cx - r * 0.8f, cy - r), new PointF(cx - r * 0.8f, cy + r),
            },
        };

        using var brush = new SolidBrush(ink);
        g.FillPolygon(brush, points);
    }

    /// <summary>A lozenge with text on it, for shoulders, triggers, sticks and menu buttons.</summary>
    private static void DrawCap(Graphics g, Rectangle box, string text, Color ink)
    {
        DrawCapBackground(g, box, ink);

        TextRenderer.DrawText(g, text, CapFont, box, ink,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.NoPrefix);
    }

    private static void DrawCapBackground(Graphics g, Rectangle box, Color ink)
    {
        var shape = new RectangleF(box.X + Sf(0.5f), box.Y + Sf(0.5f), box.Width - Sf(1f), Glyph - Sf(1f));

        Theme.FillRounded(g, shape, 6, Color.FromArgb(26, ink));
        Theme.DrawRounded(g, shape, 6, Color.FromArgb(110, ink));
    }

    private static Color FaceColour(PadFamily family, PadControl control) => family switch
    {
        PadFamily.PlayStation => control switch
        {
            PadControl.FaceUp => PsGreen,
            PadControl.FaceRight => PsRed,
            PadControl.FaceDown => PsBlue,
            _ => PsPink,
        },
        _ => control switch
        {
            PadControl.FaceUp => XboxYellow,
            PadControl.FaceRight => XboxRed,
            PadControl.FaceDown => XboxGreen,
            _ => XboxBlue,
        },
    };

    private static string XboxLetter(PadControl control) => control switch
    {
        PadControl.FaceUp => "Y",
        PadControl.FaceRight => "B",
        PadControl.FaceDown => "A",
        _ => "X",
    };

    /// <summary>What is printed on the button, for the family it is being drawn as.</summary>
    private static string CapText(PadFamily family, PadPress press)
    {
        bool ps = family == PadFamily.PlayStation;

        return press.Control switch
        {
            PadControl.L1 => ps ? "L1" : "LB",
            PadControl.R1 => ps ? "R1" : "RB",
            PadControl.L2 => ps ? "L2" : "LT",
            PadControl.R2 => ps ? "R2" : "RT",
            PadControl.L3 => ps ? "L3" : "LS",
            PadControl.R3 => ps ? "R3" : "RS",
            PadControl.Select => ps ? "Create" : "View",
            PadControl.Start => ps ? "Options" : "Menu",
            PadControl.Home => ps ? "PS" : "Guide",
            PadControl.Touchpad => "Pad",
            PadControl.Mute => "Mute",
            PadControl.Unknown => press.Number.ToString(),
            _ => "",
        };
    }

    private static bool IsFace(PadControl c)
        => c is PadControl.FaceDown or PadControl.FaceRight
             or PadControl.FaceLeft or PadControl.FaceUp;

    private static bool IsDpad(PadControl c)
        => c is PadControl.DpadUp or PadControl.DpadDown
             or PadControl.DpadLeft or PadControl.DpadRight;
}
