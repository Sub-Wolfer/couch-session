namespace CouchMode.Ui;

/// <summary>
/// A small modal that asks for one line of text.
///
/// Written rather than borrowed because .NET has no text-input dialog of its own — the usual
/// answer is Visual Basic's InputBox, which drags in a whole assembly and renders in the system
/// theme, which against this window's dark chrome looks like a different program.
/// </summary>
internal static class Prompt
{
    /// <param name="reset">
    /// Label for an optional third button that answers with an empty string.
    ///
    /// For "use the original name" and its like, where the way to undo a choice is to give no
    /// answer at all. Offering it here rather than as another button on the page puts it where the
    /// decision is actually made — looking at the name you gave something — instead of taking a
    /// permanent place in a row of settings for something used once.
    /// </param>
    /// <returns>The text entered, or null if the user backed out.</returns>
    public static string? Ask(IWin32Window owner, string title, string message,
                              string initial = "", string? reset = null)
    {
        using var box = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(420, 200),
            BackColor = Theme.Window,
            ForeColor = Theme.Text,
            Font = Theme.Body,
            ShowInTaskbar = false,
        };

        WindowCorners.Round(box);

        var heading = new Label
        {
            Text = title,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(24, 22),
            BackColor = Color.Transparent,
        };

        var note = new RichNote(message, 372)
        {
            Location = new Point(24, 56),
        };

        var input = new TextBox
        {
            Text = initial,
            Location = new Point(24, 56 + note.Height + 10),
            Width = 372,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = Theme.Input,
            ForeColor = Theme.Text,
            Font = Theme.Body,
        };

        input.SelectAll();

        var ok = new FlatButton
        {
            Text = "Save",
            Size = new Size(96, 34),
            Fill = Theme.Good,
            Line = Color.Empty,
        };

        var cancel = new FlatButton { Text = "Cancel", Size = new Size(96, 34) };

        FlatButton? clear = reset is null
            ? null
            : new FlatButton { Text = reset, Size = new Size(150, 34) };

        // Sized to the content rather than fixed: the message wraps to however many lines it
        // needs, and a fixed height would either clip it or leave a gap under a short one.
        int bottom = input.Bottom + 18;

        ok.Location = new Point(box.ClientSize.Width - ok.Width - 24, bottom);
        cancel.Location = new Point(ok.Left - cancel.Width - 8, bottom);

        // Far left, away from Save and Cancel. It is a different kind of action from either, and
        // sitting beside them invites the wrong one being pressed.
        if (clear is not null) clear.Location = new Point(24, bottom);

        box.ClientSize = new Size(box.ClientSize.Width, bottom + ok.Height + 20);

        string? answer = null;

        ok.Click += (_, _) => { answer = input.Text; box.Close(); };
        cancel.Click += (_, _) => box.Close();

        // Empty is already how the caller is told to drop the name, so this answers with that
        // rather than inventing a second way of saying the same thing.
        if (clear is not null) clear.Click += (_, _) => { answer = ""; box.Close(); };

        // Enter saves and Escape backs out, which is what every other dialog on the machine does
        // and what fingers will try before reaching for the mouse.
        box.KeyPreview = true;
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { answer = input.Text; box.Close(); }
            else if (e.KeyCode == Keys.Escape) box.Close();
        };

        box.Controls.AddRange([heading, note, input, ok, cancel]);
        if (clear is not null) box.Controls.Add(clear);

        // After the controls are in, so the walk sees them. The heading and the background move the
        // window; the text box and the buttons keep their own clicks.
        WindowDrag.Enable(box);

        box.ShowDialog(owner);

        return answer;
    }
}
