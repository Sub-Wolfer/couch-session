using System.Diagnostics;

namespace CouchMode.Ui;

/// <summary>
/// Pick an open application from the ones running right now.
///
/// The alternative is Browse, and Browse is a poor way to answer "close Discord for me": it wants a
/// path most people could not name, for an app installed somewhere they never chose, and half the
/// things worth closing live under AppData behind a version-numbered folder. Everything on this list
/// is something the user can see on their own taskbar.
///
/// Only processes with a window are offered, for the same reason <see cref="Session.ResourceControl"/>
/// only asks those to close: a background service has nothing to ask, and listing one would offer a
/// choice that quietly does nothing.
/// </summary>
internal sealed class AppPicker : Form
{
    private const int Pad = 24;
    private const int Wide = 560;

    /// <summary>The executable path chosen, or null if the dialog was dismissed.</summary>
    public string? Chosen { get; private set; }

    public AppPicker(IEnumerable<string> already)
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
            Text = Words.AppPickerTitle,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(Pad, Pad),
            BackColor = Color.Transparent,
        });

        var note = new RichNote(Words.AppPickerWhy, Wide - Pad * 2, Theme.Small, Theme.SmallBold,
                                Theme.TextDim)
        {
            Location = new Point(Pad, Pad + 30),
        };
        Controls.Add(note);

        int y = note.Bottom + 14;

        var running = Windowed(already).ToList();

        var host = new ScrollHost
        {
            Location = new Point(Pad, y),
            Width = Wide - Pad * 2,
            BackColor = Theme.Input,
            Padding = new Padding(8, 8, 8, 8),
        };

        var stack = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };

        if (running.Count == 0)
        {
            stack.Controls.Add(new Label
            {
                Text = Words.AppPickerNothing,
                ForeColor = Theme.TextFaint,
                AutoSize = true,
                Margin = new Padding(6, 8, 6, 8),
                BackColor = Color.Transparent,
            });
        }

        foreach (var app in running) stack.Controls.Add(Row(app));

        host.SetContent(stack);
        host.Height = Math.Clamp(stack.PreferredSize.Height + 16, 120, 340);
        Controls.Add(host);

        y += host.Height + 16;

        var cancel = new FlatButton
        {
            Text = Words.AppPickerCancel,
            Size = new Size(110, 38),
            Location = new Point(Wide - Pad - 110, y),
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);

        Height = y + 38 + Pad;

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        WindowDrag.Enable(this);
    }

    /// <summary>One clickable line: what it is called, and where it actually lives.</summary>
    private Control Row(RunningApp app)
    {
        var row = new FlatButton
        {
            Text = "",
            Size = new Size(Wide - Pad * 2 - 24, 46),
            Fill = Theme.Surface,
            Line = Theme.Line,
            Margin = new Padding(0, 0, 0, 6),
        };

        // Drawn on top of the button rather than as its Text, because two lines at two sizes is not
        // something FlatButton draws — and the path matters. Two apps can be called the same thing.
        var name = new Label
        {
            Text = app.Name,
            Font = Theme.BodySemi,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(12, 6),
            BackColor = Color.Transparent,
            Enabled = false,
        };

        var path = new Label
        {
            Text = Shorten(app.Path, 72),
            Font = Theme.Small,
            ForeColor = Theme.TextFaint,
            AutoSize = true,
            Location = new Point(12, 25),
            BackColor = Color.Transparent,
            Enabled = false,
        };

        row.Controls.Add(name);
        row.Controls.Add(path);

        row.Click += (_, _) => { Chosen = app.Path; DialogResult = DialogResult.OK; };
        return row;
    }

    /// <summary>Middle of a long path replaced with an ellipsis, so both ends stay readable.</summary>
    private static string Shorten(string path, int max)
    {
        if (path.Length <= max) return path;

        int tail = max / 2 - 2;
        return path[..(max - tail - 3)] + "..." + path[^tail..];
    }

    private sealed record RunningApp(string Name, string Path);

    /// <summary>
    /// Everything on screen right now that could be asked to close, minus what is already listed.
    ///
    /// One entry per executable, not per process: Chrome is a dozen processes and one app, and a
    /// list that said so twelve times would be useless.
    /// </summary>
    private static IEnumerable<RunningApp> Windowed(IEnumerable<string> already)
    {
        var seen = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
        var found = new List<RunningApp>();

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { Log.Warn($"Could not list running apps: {ex.Message}"); yield break; }

        foreach (var proc in all)
        {
            using (proc)
            {
                string path;
                string title;

                try
                {
                    if (proc.Id == Environment.ProcessId) continue;
                    if (proc.MainWindowHandle == IntPtr.Zero) continue;

                    title = proc.MainWindowTitle;
                    if (title.Length == 0) continue;

                    // Throws for anything elevated or protected, which is the right filter anyway:
                    // this app runs unelevated and could not close those either.
                    path = proc.MainModule?.FileName ?? "";
                }
                catch { continue; }

                if (path.Length == 0 || !seen.Add(path)) continue;

                found.Add(new RunningApp(Nice(proc.ProcessName), path));
            }
        }

        foreach (var app in found.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase))
            yield return app;
    }

    /// <summary>"chrome" reads as a process; "Chrome" reads as the thing on the taskbar.</summary>
    private static string Nice(string processName) =>
        processName.Length == 0 ? processName : char.ToUpper(processName[0]) + processName[1..];
}
