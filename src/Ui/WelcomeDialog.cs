namespace CouchMode.Ui;

/// <summary>
/// The one-time welcome, shown the first time settings ever opens.
///
/// Deliberately not a tour. The research on this is unambiguous: auto-launched multi-step tours are
/// skipped by most people and finished by fewer than a quarter, while letting someone work at their
/// own pace is the pattern that actually lands. So this window has one job: say what the app does in
/// two sentences, and point at the one page that has to be filled in. Both buttons retire it forever.
/// The obvious way out is on purpose, since a visible skip measurably increases how many people read
/// the thing they are skipping.
///
/// Laid out from measured heights rather than fixed coordinates. The old version placed each
/// paragraph at a hard-coded Y and the buttons at a hard-coded offset from a hard-coded window size,
/// so a sentence wrapping to one line more than expected crept toward the buttons with nothing to
/// stop it.
/// </summary>
internal sealed class WelcomeDialog : Form
{
    private const int Pad = 24;
    private const int Wide = 470;

    public WelcomeDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        Width = Wide;
        DoubleBuffered = true;

        WindowCorners.Round(this, 14);

        var badge = new PictureBox
        {
            Size = new Size(42, 42),
            Location = new Point(Pad, Pad),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try { badge.Image = AppIcon.Monogram(84); } catch { /* decorative */ }
        Controls.Add(badge);

        Controls.Add(new Label
        {
            Text = Words.WelcomeTitle,
            Font = Theme.Title,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(Pad + 56, Pad + 8),
            BackColor = Color.Transparent,

            // WinForms reads "&" in a Label as a keyboard mnemonic and swallows it. Not a problem in
            // this string today, but it is one line to make the window immune to it.
            UseMnemonic = false,
        });

        int y = Pad + 62;
        int wide = Wide - Pad * 2;

        // RichNote, not Label.
        //
        // [BUG] These were plain Labels, and a Label treats "&" as a mnemonic marker: "Display &
        // Audio" rendered as "Display  Audio", the ampersand eaten and a double space left behind.
        // The rest of the app already solved this — SettingsForm.NewLabel exists specifically because
        // "Display & Audio" needs it — but this window predated that and never got it.
        //
        // RichNote is the better answer anyway: it is what the rest of the app uses for prose, it
        // wraps to a measured height so the layout below can depend on it, and it supports the
        // **bold** that lets the one page worth naming actually stand out.
        var body = new RichNote(Words.WelcomeBody, wide, Theme.Body, Theme.BodySemi, Theme.TextDim)
        {
            Location = new Point(Pad, y),
        };
        Controls.Add(body);

        y = body.Bottom + 18;

        var choose = new RichNote(Words.WelcomeChoose, wide, Theme.Body, Theme.BodySemi, Theme.Text)
        {
            Location = new Point(Pad, y),
        };
        Controls.Add(choose);

        y = choose.Bottom + 10;

        // Two answers, each with its own line under it, stacked rather than side by side.
        //
        // Side by side would make them look like OK and Cancel, where one is the action and the
        // other is backing out. These are two equal readings of what the app is for, and the one on
        // the right is not a lesser version of the one on the left.
        y = AddChoice(Words.WelcomeCouch, Words.WelcomeCouchWhy, Theme.Accent, y, wide, () =>
        {
            DeskOnly = false;
            DialogResult = DialogResult.OK;
        });

        y = AddChoice(Words.WelcomeDesk, Words.WelcomeDeskWhy, Theme.SurfaceHi, y + 6, wide, () =>
        {
            DeskOnly = true;
            DialogResult = DialogResult.OK;
        });

        var either = new RichNote(Words.WelcomeEither, wide, Theme.Small, Theme.SmallBold, Theme.TextFaint)
        {
            Location = new Point(Pad, y + 8),
        };
        Controls.Add(either);

        Height = either.Bottom + Pad;

        // Escape leaves too. A welcome that traps anyone has failed at being welcoming.
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
    }


    /// <summary>True when the reader said they have no television. Read after the dialog closes.</summary>
    public bool DeskOnly { get; private set; }

    /// <summary>
    /// One answer: a full-width button and the sentence explaining it. Returns the next free y.
    /// </summary>
    private int AddChoice(string label, string why, Color fill, int y, int wide, Action chosen)
    {
        var button = new FlatButton
        {
            Text = label,
            Size = new Size(wide, 42),
            Location = new Point(Pad, y),
            Fill = fill,
            ForeColor = fill == Theme.SurfaceHi ? Theme.Text : Color.White,
            Line = fill == Theme.SurfaceHi ? Theme.Line : Color.Empty,
        };
        button.Click += (_, _) => chosen();
        Controls.Add(button);

        var note = new RichNote(why, wide, Theme.Small, Theme.SmallBold, Theme.TextDim)
        {
            Location = new Point(Pad, button.Bottom + 5),
        };
        Controls.Add(note);

        return note.Bottom + 12;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
