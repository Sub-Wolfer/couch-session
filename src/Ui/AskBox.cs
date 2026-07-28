namespace CouchMode.Ui;

/// <summary>
/// The app's own yes/no and acknowledge dialogs, replacing MessageBox.
///
/// MessageBox renders in the system theme, so against this window's dark chrome it arrives looking
/// like a different program interrupting — the same objection <see cref="Prompt"/> and
/// <see cref="UpdateDialog"/> were written for. It also gives no control over the two things that
/// matter most in a destructive confirmation: which answer the buttons are *named* after, and which
/// one is easiest to hit.
///
/// "Yes" and "No" are the worst possible labels for a question about deleting something. They force
/// the reader back up to the title to work out what they are agreeing to, and the title is the part
/// people skip. Naming the buttons after their actions — "Reset everything", "Keep my settings" —
/// means the buttons alone are enough, which is what someone skimming will read either way.
///
/// The safe answer is the wide one, sits on the right where the eye finishes, and owns Escape. The
/// destructive one is coloured and deliberately not the default: nothing here responds to Enter.
/// </summary>
internal static class AskBox
{
    private const int Pad = 24;
    private const int Wide = 460;

    /// <summary>
    /// Ask a destructive question. Returns true only if the user picked the destructive answer.
    /// </summary>
    /// <param name="confirm">Names the destructive action, not "Yes".</param>
    /// <param name="cancel">Names what backing out preserves, not "No".</param>
    public static bool Confirm(IWin32Window owner, string title, string message,
                               string confirm, string cancel)
        => Show(owner, title, message, confirm, cancel) == DialogResult.Yes;

    /// <summary>Say something the reader only has to acknowledge.</summary>
    public static void Tell(IWin32Window owner, string title, string message)
        => Show(owner, title, message, confirm: null, cancel: Words.AskClose);

    private static DialogResult Show(IWin32Window owner, string title, string message,
                                     string? confirm, string cancel)
    {
        using var box = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterParent,
            ShowInTaskbar = false,
            BackColor = Theme.Surface,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            Width = Wide,
            KeyPreview = true,
        };

        WindowCorners.Round(box, 14);

        var badge = new PictureBox
        {
            Size = new Size(36, 36),
            Location = new Point(Pad, Pad),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try { badge.Image = AppIcon.Monogram(72); } catch { /* decorative */ }
        box.Controls.Add(badge);

        box.Controls.Add(new Label
        {
            Text = title,
            Font = Theme.Heading,

            // Red only when something is about to be destroyed. A heading that shouts on every
            // dialog teaches people to stop reading headings.
            ForeColor = confirm is null ? Theme.Text : Theme.Bad,
            AutoSize = true,
            MaximumSize = new Size(Wide - Pad * 2 - 48, 0),
            Location = new Point(Pad + 48, Pad + 4),
            BackColor = Color.Transparent,
        });

        // RichNote rather than a Label: these messages carry **bold** and real line breaks, and it is
        // what the rest of the app uses for prose.
        var body = new RichNote(message, Wide - Pad * 2, Theme.Body, Theme.BodySemi, Theme.TextDim)
        {
            Location = new Point(Pad, Pad + 52),
        };
        box.Controls.Add(body);

        int y = body.Bottom + 20;

        // Backing out is the wide button on the right, where the eye finishes and the hand goes.
        var safe = new FlatButton
        {
            Text = cancel,
            Size = new Size(Math.Max(120, TextRenderer.MeasureText(cancel, Theme.BodySemi).Width + 40), 40),
            Fill = Theme.SurfaceHi,
            Line = Theme.Line,
        };
        safe.Location = new Point(Wide - Pad - safe.Width, y);
        safe.Click += (_, _) => box.DialogResult = DialogResult.No;
        box.Controls.Add(safe);

        if (confirm is not null)
        {
            var danger = new FlatButton
            {
                Text = confirm,
                Size = new Size(Math.Max(130, TextRenderer.MeasureText(confirm, Theme.BodySemi).Width + 40), 40),
                Fill = Theme.Bad,
                ForeColor = Color.White,
                Line = Color.Empty,
            };
            danger.Location = new Point(safe.Left - 12 - danger.Width, y);
            danger.Click += (_, _) => box.DialogResult = DialogResult.Yes;
            box.Controls.Add(danger);
        }

        box.Height = y + 40 + Pad;

        // Escape backs out. Enter does nothing on purpose: there is no AcceptButton, so a stray
        // keypress cannot confirm something irreversible.
        box.KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) box.DialogResult = DialogResult.No; };

        box.Paint += (_, e) =>
        {
            using var pen = new Pen(Theme.Line);
            e.Graphics.DrawRectangle(pen, 0, 0, box.Width - 1, box.Height - 1);
        };

        // Last, so every control is in place for it to walk. Borderless means no title bar to drag,
        // and the window underneath is disabled while this is up.
        WindowDrag.Enable(box);

        return box.ShowDialog(owner);
    }
}
