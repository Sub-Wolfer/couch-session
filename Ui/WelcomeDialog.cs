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

    /// <summary>True when the user asked to be taken to the page that needs filling in.</summary>
    public bool GoToSetup { get; private set; }

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

        y = body.Bottom + 14;

        var steps = new RichNote(Words.WelcomeSteps, wide, Theme.Body, Theme.BodySemi, Theme.TextDim)
        {
            Location = new Point(Pad, y),
        };
        Controls.Add(steps);

        y = steps.Bottom + 24;

        // The action that goes somewhere is the accented one, on the right.
        //
        // It was green, which in this app means "good" and is used for confirmations and healthy
        // state. A primary action is not a confirmation, and the accent is what the rest of the app
        // uses for the button it would like pressed.
        var go = new FlatButton
        {
            Text = Words.WelcomeGo,
            Size = new Size(Math.Max(150, TextRenderer.MeasureText(Words.WelcomeGo, Theme.BodySemi).Width + 44), 40),
            Fill = Theme.Accent,
            ForeColor = Color.White,
            Line = Color.Empty,
        };
        go.Location = new Point(Wide - Pad - go.Width, y);
        go.Click += (_, _) => { GoToSetup = true; DialogResult = DialogResult.OK; };
        Controls.Add(go);

        var skip = new FlatButton
        {
            Text = Words.WelcomeSkip,
            Size = new Size(Math.Max(150, TextRenderer.MeasureText(Words.WelcomeSkip, Theme.BodySemi).Width + 40), 40),
            Fill = Theme.SurfaceHi,
            Line = Theme.Line,
        };
        skip.Location = new Point(go.Left - 12 - skip.Width, y);
        skip.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(skip);

        Height = y + 40 + Pad;

        // Escape leaves too. A welcome that traps anyone has failed at being welcoming.
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
