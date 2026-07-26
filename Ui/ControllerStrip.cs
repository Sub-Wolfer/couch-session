using System.Drawing.Drawing2D;
using CouchMode.Input;

namespace CouchMode.Ui;

/// <summary>
/// Which controllers are connected, in the title bar.
///
/// In the chrome rather than on a page because it answers the question you have before you sit
/// down — is the pad on, and does the app see it — without navigating anywhere to find out. That
/// second half matters more than it sounds: controller detection is the part of this app most
/// likely to be the thing that is wrong, and having it visible turns "it did not start" into
/// "the app cannot see my controller".
///
/// A cable mark, and no charge level. See PadPresence for why the level is not shown: a
/// percentage means trusting an unpublished byte offset, and a wrong one is indistinguishable
/// from a right one. Whether a pad is on power is a different question with a dependable answer.
///
/// The whole strip hides when nothing is connected, and reappears on its own.
/// </summary>
internal sealed class ControllerStrip : Control
{
    private readonly System.Windows.Forms.Timer _poll;
    private IReadOnlyList<PadStatus> _pads = [];

    /// <summary>
    /// How often the pads are asked. Slow on purpose: this enumerates devices, and the settings
    /// page already pushes a refresh the moment one comes or goes.
    /// </summary>
    private const int PollMs = 5000;

    private const int BoltWidth = 10;
    private const int MarkWidth = 18;   // the PS / Xbox mark before each name
    private const int Gap = 5;        // between a name and its cable mark
    private const int Spacing = 16;   // between one pad and the next

    /// <summary>Matches SettingsForm.TitleBarHeight; the strip fills it and centres inside.</summary>
    private const int TitleBarHeight = 42;

    public ControllerStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Font = Theme.Caption;
        Height = TitleBarHeight;
        TabStop = false;

        _poll = new System.Windows.Forms.Timer { Interval = PollMs };
        _poll.Tick += (_, _) => Refresh(force: false);
    }

    /// <summary>Names the user has given their controllers, keyed by device id.</summary>
    public IReadOnlyDictionary<string, string>? Names { get; set; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Refresh(force: true);
        _poll.Start();
    }

    /// <summary>Re-read the pads and resize to whatever is connected now.</summary>
    public void Refresh(bool force)
    {
        if (IsDisposed || !IsHandleCreated) return;

        IReadOnlyList<PadStatus> now;

        try { now = PadPresence.All(Names); }
        catch (Exception ex)
        {
            // Never worth taking the window down over. A missing strip is a missing convenience.
            Log.Warn($"Could not read connected controllers: {ex.Message}");
            now = [];
        }

        if (!force && Same(now, _pads)) return;

        _pads = now;

        int width = Measure();

        Visible = width > 0;
        Width = Math.Max(width, 1);

        // The form right-aligns this against the close button, and it can only do that once it
        // knows how wide the content made it.
        ContentResized?.Invoke(this, EventArgs.Empty);
        Invalidate();
    }

    /// <summary>
    /// Raised when the pads have changed the strip's width, so the owner can reposition it.
    ///
    /// Its own event rather than Control.SizeChanged, which would have to be shadowed to be
    /// useful here and leaves the base one unreachable — a trap for whoever reads this next.
    /// </summary>
    public event EventHandler? ContentResized;

    private static bool Same(IReadOnlyList<PadStatus> a, IReadOnlyList<PadStatus> b)
    {
        if (a.Count != b.Count) return false;

        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;

        return true;
    }

    private int Measure()
    {
        if (_pads.Count == 0) return 0;

        int total = 0;

        using var g = CreateGraphics();

        for (int i = 0; i < _pads.Count; i++)
        {
            if (i > 0) total += Spacing;

            // The console mark is drawn before every name, so it has to be measured with them or the
            // strip sizes itself too narrow and clips the last pad.
            total += MarkWidth + Gap;

            total += TextRenderer.MeasureText(g, Label(i), Font, new Size(240, Height),
                                              TextFormatFlags.NoPrefix).Width;

            if (_pads[i].Power == PadPower.Charging) total += Gap + BoltWidth;
        }

        return total;
    }

    /// <summary>
    /// The model, numbered only when it would otherwise be ambiguous.
    ///
    /// Numbering a lone controller reads as though a second one is missing, so the number only
    /// appears when there is genuinely more than one of that name.
    /// </summary>
    private string Label(int index)
    {
        string name = _pads[index].Name;

        int sameName = 0, position = 0;

        for (int i = 0; i < _pads.Count; i++)
        {
            if (!string.Equals(_pads[i].Name, name, StringComparison.Ordinal)) continue;

            sameName++;
            if (i <= index) position = sameName;
        }

        // "Connected" after the name, so the strip states a fact rather than just naming a device — the
        // question being answered up here is whether the app can see the pad, not what it is called.
        // Numbered only when two of the same model are attached, or a lone pad reads as if one is
        // missing; the word then follows the number, so it is said once per pad rather than twice.
        return sameName > 1 ? $"{name} {position} — Connected" : $"{name} — Connected";
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        if (_pads.Count == 0) return;

        int x = 0;
        int mid = Height / 2;

        for (int i = 0; i < _pads.Count; i++)
        {
            if (i > 0) x += Spacing;

            var pad = _pads[i];
            string label = Label(i);

            // The console's own mark, then the name in green.
            //
            // This replaces the "Connected: …" line that used to sit on the home page: same meaning,
            // same colour, but in the title bar where it is true of every page rather than one. The mark
            // is what makes it readable at a glance — the shape says which family of pad before the
            // words are read at all.
            // The family comes from the device's vendor id, carried on PadStatus. It used to be read
            // out of the name, which broke the moment anyone renamed their pad: "PS5 Wireless" contains
            // none of the words that test looked for, so a DualSense was drawn with an Xbox mark.
            ConsoleMarks.Draw(g, new Rectangle(x, mid - 9, MarkWidth, MarkWidth), pad.PlayStation);
            x += MarkWidth + Gap;

            int textWidth = TextRenderer.MeasureText(g, label, Font, new Size(240, Height),
                                                     TextFormatFlags.NoPrefix).Width;

            TextRenderer.DrawText(g, label, Font,
                                  new Rectangle(x, mid - 8, textWidth, 16), Theme.Good,
                                  TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                                | TextFormatFlags.NoPrefix);

            x += textWidth;

            // Only ever drawn for a pad known to be on a cable. Running on its own battery is the
            // ordinary state and needs no mark, and an unknown state gets nothing rather than a
            // symbol that would have to be interpreted.
            if (pad.Power == PadPower.Charging)
            {
                x += Gap;
                DrawBolt(g, new Rectangle(x, mid - 6, BoltWidth, 12), Theme.Good);
                x += BoltWidth;
            }
        }
    }


    private static void DrawBolt(Graphics g, Rectangle body, Color ink)
    {
        float cx = body.X + body.Width / 2f;
        float cy = body.Y + body.Height / 2f;

        var bolt = new[]
        {
            new PointF(cx + 1.5f, cy - 5f),
            new PointF(cx - 2.6f, cy + 0.5f),
            new PointF(cx - 0.2f, cy + 0.5f),
            new PointF(cx - 1.5f, cy + 5f),
            new PointF(cx + 2.6f, cy - 0.5f),
            new PointF(cx + 0.2f, cy - 0.5f),
        };

        using var brush = new SolidBrush(ink);
        g.FillPolygon(brush, bolt);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _poll.Stop();
            _poll.Dispose();
        }

        base.Dispose(disposing);
    }
}
