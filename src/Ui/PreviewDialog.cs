using CouchMode.Session;

namespace CouchMode.Ui;

/// <summary>
/// The session preview, drawn.
///
/// A list, not a paragraph. Each line is one thing that will happen and one word for whether it is
/// put back, because "will this leave my machine like this?" is the question people actually have
/// about an app that changes system settings, and it is the one a settings page answers worst.
///
/// Problems come first and in warning colour. A session with no display chosen is not a session with
/// one missing step, it is a session that cannot happen, and burying that under six things that would
/// have worked would be describing the wrong thing.
/// </summary>
internal sealed class PreviewDialog : Form
{
    private const int Pad = 24;
    private const int Wide = 600;

    /// <summary>The width a row really gets: the dialog, less its padding and the scrollbar.</summary>
    private const int RowW = Wide - Pad * 2 - 26;

    /// <summary>True if the user pressed Start rather than closing the window.</summary>
    public bool StartNow { get; private set; }

    public PreviewDialog(SessionPreview.Result preview, bool canStart)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;
        Width = Wide;
        KeyPreview = true;

        WindowCorners.Round(this, 14);

        Controls.Add(new Label
        {
            Text = Words.PreviewTitle,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(Pad, Pad),
            BackColor = Color.Transparent,
        });

        var why = new RichNote(Words.PreviewWhy, Wide - Pad * 2, Theme.Small, Theme.SmallBold,
                               Theme.TextDim)
        {
            Location = new Point(Pad, Pad + 32),
        };
        Controls.Add(why);

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };

        foreach (var problem in preview.Problems)
            stack.Controls.Add(Line(problem.What, problem.Detail, "", Theme.Warn));

        foreach (var step in preview.Steps)
        {
            string tail = step.Restores switch
            {
                SessionPreview.Undo.Restored => Words.PreviewRestored,
                SessionPreview.Undo.Kept => Words.PreviewKept,
                _ => "",
            };

            stack.Controls.Add(Line(step.What, step.Detail, tail, Theme.Text));
        }

        if (preview.Steps.Count == 0 && preview.Problems.Count == 0)
        {
            stack.Controls.Add(new RichNote(Words.PreviewNothing, Wide - Pad * 2 - 40,
                                            Theme.Body, Theme.BodySemi, Theme.TextDim)
            {
                Margin = new Padding(6, 8, 6, 8),
            });
        }

        int y = why.Bottom + 14;

        var host = new ScrollHost
        {
            Location = new Point(Pad, y),
            Width = Wide - Pad * 2,
            BackColor = Theme.Surface,
            Padding = new Padding(10, 10, 10, 10),
        };

        host.SetContent(stack);

        int room = Screen.FromPoint(Cursor.Position).WorkingArea.Height - 320;
        host.Height = Math.Clamp(stack.PreferredSize.Height + 20, 120, Math.Clamp(room, 200, 520));
        Controls.Add(host);

        y += host.Height + 16;

        var close = new FlatButton
        {
            Text = Words.PreviewClose,
            Size = new Size(110, 38),
            Location = new Point(Wide - Pad - 110, y),
        };
        close.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(close);

        // Offered only when a session could actually begin. A Start button on a preview whose first
        // line is "no couch display chosen" would be a button that fails on purpose.
        if (canStart && preview.Problems.Count == 0)
        {
            var start = new FlatButton
            {
                Text = Words.PreviewStart,
                Size = new Size(150, 38),
                Fill = Theme.Mix(Theme.Accent, Color.Black, 0.12f),
                ForeColor = Color.White,
                Line = Color.Empty,
                Location = new Point(Wide - Pad - 110 - 160, y),
            };

            start.Click += (_, _) => { StartNow = true; DialogResult = DialogResult.OK; };
            Controls.Add(start);
        }

        Height = y + 38 + Pad;

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        WindowDrag.Enable(this);
    }

    /// <summary>
    /// One row: what happens, any qualifier, and whether it is undone.
    ///
    /// Laid out as the games list and the app picker are, so a third list in this app is not a
    /// third thing to learn: rows on the card colour, a rule between them, and the column that
    /// answers the question aligned down the right instead of trailing each sentence wherever it
    /// happens to end. Ragged was the problem — the whole value of this dialog is being able to
    /// read one column and know what the machine keeps.
    /// </summary>
    private static Control Line(string what, string detail, string tail, Color ink)
    {
        const int TagW = 116;
        const int Pad0 = 12;

        bool two = detail.Length > 0;

        var row = new BufferedPanel
        {
            Size = new Size(RowW, two ? 46 : 32),
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };

        row.Paint += (_, e) =>
        {
            using var rule = new SolidBrush(Theme.RowLine);
            e.Graphics.FillRectangle(rule, 10, row.Height - 1, row.Width - 20, 1);
        };

        row.Controls.Add(new Label
        {
            Text = what,
            Font = Theme.BodySemi,
            ForeColor = ink,
            AutoSize = false,
            Size = new Size(RowW - TagW - Pad0 * 3, 20),
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Location = new Point(Pad0, two ? 5 : 6),
            BackColor = Color.Transparent,
        });

        if (two)
        {
            row.Controls.Add(new Label
            {
                Text = detail,
                Font = Theme.Small,
                ForeColor = Theme.TextFaint,
                AutoSize = false,
                Size = new Size(RowW - TagW - Pad0 * 3, 18),
                AutoEllipsis = true,
                TextAlign = ContentAlignment.MiddleLeft,
                Location = new Point(Pad0, 24),
                BackColor = Color.Transparent,
            });
        }

        if (tail.Length > 0)
        {
            row.Controls.Add(new Label
            {
                Text = tail,
                Font = Theme.Small,
                ForeColor = tail == Words.PreviewKept ? Theme.Warn : Theme.Good,
                AutoSize = false,
                Size = new Size(TagW, 20),
                TextAlign = ContentAlignment.MiddleRight,
                Location = new Point(RowW - TagW - Pad0, two ? 5 : 6),
                BackColor = Color.Transparent,
            });
        }

        return row;
    }
}
