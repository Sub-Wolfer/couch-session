namespace CouchMode.Ui;

/// <summary>
/// The offer to install a new version, in the app's own chrome.
///
/// Replaces a MessageBox. That box rendered in the system theme against this app's dark window, which
/// is the same objection <see cref="Prompt"/> was written for — but it had a worse problem than looking
/// borrowed: it printed GitHub's release notes raw. Those notes are markdown written for a web page, so
/// the one thing a person actually wants from this dialog — what changed — arrived as
/// "## What's Changed", a list of "* thing by @someone in https://github.com/…/pull/12", and a
/// "**Full Changelog**:" line ending in a URL nobody can click. Three lines of punctuation for every
/// line of meaning.
///
/// So the notes are tidied into a plain list and given room to scroll, and the two answers are ordinary
/// app buttons. Nothing here is decoration for its own sake: the version numbers say what you are
/// trading, the list says what you get, and the warning about a running session is the one consequence
/// that is not obvious.
/// </summary>
internal sealed class UpdateDialog : Form
{
    private const int Pad = 24;
    private const int Wide = 520;

    public UpdateDialog(UpdateInfo found, bool sessionRunning)
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;
        Width = Wide;

        WindowCorners.Round(this, 14);

        var badge = new PictureBox
        {
            Size = new Size(40, 40),
            Location = new Point(Pad, Pad),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        try { badge.Image = AppIcon.Monogram(80); } catch { /* decorative */ }
        Controls.Add(badge);

        Controls.Add(new Label
        {
            Text = string.Format(Words.UpdateCardTitle, found.Version),
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(Pad + 54, Pad + 2),
            BackColor = Color.Transparent,
        });

        Controls.Add(new Label
        {
            Text = string.Format(Words.UpdateFromTo, AppInfo.Version, found.Version),
            Font = Theme.Small,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Location = new Point(Pad + 54, Pad + 24),
            BackColor = Color.Transparent,
        });

        int y = Pad + 62;

        Controls.Add(new Label
        {
            Text = Words.UpdateWhatChanged,
            Font = Theme.SmallBold,
            ForeColor = Theme.TextFaint,
            AutoSize = true,
            Location = new Point(Pad, y),
            BackColor = Color.Transparent,
        });

        y += 22;

        // The notes, in a box that scrolls rather than a window that grows without limit. A release with
        // forty commits in it would otherwise produce a dialog taller than the screen.
        var changes = Summarise(found.Notes);

        // RichNote rather than a Label, so the ** that Summarise leaves in comes out bold instead of
        // as two asterisks. A Label renders markup as the characters it is made of, which is fine for
        // GitHub's generated list of commit subjects and wrong for notes somebody wrote.
        var list = new RichNote(changes, Wide - Pad * 2 - 24, Theme.Body, Theme.BodySemi, Theme.Text)
        {
            Location = new Point(0, 0),
        };

        var host = new ScrollHost
        {
            Location = new Point(Pad, y),
            Width = Wide - Pad * 2,
            BackColor = Theme.Input,
            Padding = new Padding(12, 10, 12, 10),
        };

        var holder = new Panel { AutoSize = true, BackColor = Color.Transparent };
        holder.Controls.Add(list);
        host.SetContent(holder);

        // Tall enough for the notes, capped so the dialog stays a dialog.
        host.Height = Math.Clamp(list.PreferredSize.Height + 22, 70, 240);
        Controls.Add(host);

        y += host.Height + 14;

        if (sessionRunning)
        {
            var warn = new RichNote(Words.UpdateEndsSession, Wide - Pad * 2, Theme.Small, Theme.SmallBold,
                                    Theme.Warn)
            {
                Location = new Point(Pad, y),
            };
            Controls.Add(warn);
            y += warn.Height + 12;
        }

        var install = new FlatButton
        {
            Text = Words.UpdateCardButton,
            Size = new Size(150, 40),
            Fill = Theme.Accent,
            ForeColor = Color.White,
            Line = Color.Empty,
            Location = new Point(Wide - Pad - 150, y),
        };
        install.Click += (_, _) => DialogResult = DialogResult.Yes;
        Controls.Add(install);

        var later = new FlatButton
        {
            Text = Words.UpdateLater,
            Size = new Size(110, 40),
            Fill = Theme.SurfaceHi,
            Line = Theme.Line,
            Location = new Point(Wide - Pad - 150 - 120, y),
        };
        later.Click += (_, _) => DialogResult = DialogResult.No;
        Controls.Add(later);

        Height = y + 40 + Pad;

        // Escape declines. A dialog offering an optional thing must be trivially dismissable.
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.No; };

        AcceptButton = null;
    }

    /// <summary>
    /// Turn GitHub's generated release notes into a plain list of what changed.
    ///
    /// The generated format is a "## What's Changed" heading, one "* message by @user in &lt;url&gt;"
    /// per merged change, sometimes a "## New Contributors" block, and a trailing
    /// "**Full Changelog**: &lt;url&gt;". Only the middle part means anything to someone deciding
    /// whether to press Update, so the rest goes — along with the attribution tail on each line, which
    /// is a repository's business rather than a reader's.
    ///
    /// Deliberately forgiving: notes written by hand instead of generated come through as their own
    /// text with the markdown bullets normalised, which is the right outcome for a release where
    /// somebody bothered to explain themselves.
    /// </summary>
    private static string Summarise(string notes)
    {
        if (string.IsNullOrWhiteSpace(notes)) return Words.UpdateNoNotes;

        var lines = new List<string>();
        bool blankPending = false;
        int shown = 0;

        foreach (var raw in notes.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();

            // "> [!NOTE]" and its four siblings. GitHub draws these as a coloured panel; here the
            // marker is the panel's name and carries nothing a reader needs, so it goes and the text
            // it introduces stays.
            if (line.StartsWith("> [!") && line.EndsWith("]")) continue;

            // The rest of a quoted block is ordinary text once its marker is gone.
            if (line.StartsWith(">")) line = line[1..].Trim();

            if (line.Length == 0)
            {
                blankPending = lines.Count > 0;
                continue;
            }

            if (line.StartsWith("**Full Changelog**")) continue;         // the trailing link
            if (line.StartsWith("https://")) continue;                   // a bare url on its own line

            // "* thing by @user in https://…" — the tail is bookkeeping, not news.
            int by = line.LastIndexOf(" by @", StringComparison.Ordinal);
            if (by > 0) line = line[..by];

            bool heading = line.StartsWith("#");
            if (heading) line = line.TrimStart('#').Trim();

            bool bullet = line.Length > 1
                       && (line[0] == '-' || line[0] == '*' || line[0] == '+')
                       && line[1] == ' ';

            if (bullet) line = line[2..].Trim();

            line = Plain(line);
            if (line.Length == 0) continue;

            // Air before a heading, and between blocks that had it in the source. Without this every
            // section runs into the one above it and the whole thing reads as one paragraph.
            if ((blankPending || heading) && lines.Count > 0 && lines[^1].Length > 0) lines.Add("");

            blankPending = false;

            lines.Add(heading ? $"**{line}**" : bullet ? $"•  {line}" : line);

            // A ceiling, because a release that merged eighty branches is not a list anyone reads.
            // Counted on content rather than on lines, so the spacing above does not spend it.
            if (++shown == 40) { lines.Add(""); lines.Add(Words.UpdateMoreChanges); break; }
        }

        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        return lines.Count > 0 ? string.Join("\n", lines) : Words.UpdateNoNotes;
    }

    /// <summary>
    /// Markdown this window cannot draw, reduced to the words it was wrapping.
    ///
    /// Bold is left alone: RichNote understands ** and it is the one piece of emphasis worth keeping.
    /// Everything else here would otherwise be read out as punctuation — a link as its own bracketed
    /// url, a keyboard key as an HTML tag, inline code as backticks.
    /// </summary>
    private static string Plain(string line)
    {
        // [text](url) — the text is the point; the address is not clickable here anyway.
        line = System.Text.RegularExpressions.Regex.Replace(line, @"\[([^\]]+)\]\([^)]+\)", "$1");

        // <kbd>Alt</kbd> and any other simple tag.
        line = System.Text.RegularExpressions.Regex.Replace(line, @"</?[A-Za-z][^>]*>", "");

        line = line.Replace("`", "").Replace("~~", "");

        // Single-asterisk italics, without disturbing the double-asterisk bold around them.
        const string keep = "";
        line = line.Replace("**", keep);
        line = line.Replace("*", "");
        line = line.Replace(keep, "**");

        return line.Trim();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }
}
