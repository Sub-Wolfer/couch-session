namespace CouchMode.Ui;

/// <summary>
/// The one-time welcome, shown the first time settings ever opens.
///
/// Deliberately not a tour. The research on this is unambiguous: auto-launched multi-step tours are
/// skipped by most people and finished by fewer than a quarter, while a checklist people work through at
/// their own pace is the pattern that actually lands — so the app's onboarding *is* the Home page's
/// setup list, and this window's only job is to say so. One screen, two buttons, both of which close it
/// forever. The obvious way out is on purpose: a visible skip measurably increases how many people read
/// the thing they are skipping.
/// </summary>
internal sealed class WelcomeDialog : Form
{
    /// <summary>True when the user asked to be shown the setup list rather than dismissing.</summary>
    public bool GoToSetup { get; private set; }

    public WelcomeDialog()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        Size = new Size(460, 268);
        DoubleBuffered = true;

        WindowCorners.Round(this, 14);

        var badge = new PictureBox
        {
            Size = new Size(44, 44),
            Location = new Point(24, 24),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try { badge.Image = AppIcon.Monogram(88); } catch { /* decorative */ }
        Controls.Add(badge);

        Controls.Add(new Label
        {
            Text = Words.WelcomeTitle,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(80, 32),
            BackColor = Color.Transparent,
        });

        Controls.Add(new Label
        {
            Text = Words.WelcomeBody,
            Font = Theme.Body,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            MaximumSize = new Size(Width - 48, 0),
            Location = new Point(24, 88),
            BackColor = Color.Transparent,
        });

        Controls.Add(new Label
        {
            Text = Words.WelcomeSteps,
            Font = Theme.Body,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            MaximumSize = new Size(Width - 48, 0),
            Location = new Point(24, 140),
            BackColor = Color.Transparent,
        });

        var go = new FlatButton
        {
            Text = Words.WelcomeGo,
            Size = new Size(130, 40),
            Fill = Theme.Good,
            Line = Color.Empty,
            Location = new Point(Width - 154, Height - 64),
        };
        go.Click += (_, _) => { GoToSetup = true; DialogResult = DialogResult.OK; };
        Controls.Add(go);

        var skip = new FlatButton
        {
            Text = Words.WelcomeSkip,
            Size = new Size(190, 40),
            Fill = Theme.SurfaceHi,
            Line = Theme.Line,
            Location = new Point(Width - 354, Height - 64),
        };
        skip.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(skip);

        // Escape leaves too — a welcome that traps anyone has failed at being welcoming.
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
