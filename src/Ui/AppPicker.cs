using System.Diagnostics;

namespace CouchMode.Ui;

/// <summary>
/// Pick the open applications to close, from the ones running right now.
///
/// The alternative is Browse, and Browse is a poor way to answer "close Discord for me": it wants a
/// path most people could not name, for an app installed somewhere they never chose, and half the
/// things worth closing live under AppData behind a version-numbered folder. Everything on this list
/// is something the user can see on their own taskbar.
///
/// Three things make it a table rather than a list of names:
///
///   - **Memory.** It is the entire reason for the feature. A browser holding six gigabytes and a
///     chat client holding two hundred megabytes are not the same decision, and without the number
///     the user is guessing at which of their apps is the expensive one.
///   - **Process name.** Two apps can present the same friendly name, and the process is what the
///     app actually matches on.
///   - **Ticks rather than one press each.** Somebody setting this up is choosing four or five
///     things, and a dialog that closes after every one is four or five trips.
///
/// Deliberately no CPU column, unlike the app this borrows its shape from. A meaningful CPU figure
/// needs two samples a second or so apart, so it would mean holding this dialog open sampling every
/// process before it could draw — and the number would then move under the pointer while being read.
/// Memory is a single honest read.
/// </summary>
internal sealed class AppPicker : Form
{
    private const int Pad = 24;
    private const int Wide = 700;

    private const int TickX = 10;
    private const int NameX = 40;
    private const int ProcX = 330;
    private const int MemX = 500;
    private const int StatusX = 590;

    /// <summary>The executables chosen. Empty if the dialog was dismissed.</summary>
    public IReadOnlyList<string> Chosen { get; private set; } = [];

    private readonly List<RunningApp> _all;
    private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);
    private readonly FlowLayoutPanel _rows;
    private readonly SearchBox _search;
    private readonly ToggleSwitch _background = new();
    private readonly FlatButton _add;

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

        _all = Running(already).ToList();

        Controls.Add(new Label
        {
            Text = Words.AppPickerTitle,
            Font = Theme.Heading,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(Pad, Pad),
            BackColor = Color.Transparent,
        });

        var why = new RichNote(Words.AppPickerWhy, Wide - Pad * 2, Theme.Small, Theme.SmallBold,
                               Theme.TextDim)
        {
            Location = new Point(Pad, Pad + 30),
        };
        Controls.Add(why);

        int y = why.Bottom + 14;

        _search = new SearchBox(Words.AppPickerSearch)
        {
            Location = new Point(Pad, y),
            Width = 320,
        };
        _search.QueryChanged += Refill;
        Controls.Add(_search);

        var bgLabel = new Label
        {
            Text = Words.AppPickerShowBackground,
            Font = Theme.Small,
            ForeColor = Theme.TextDim,
            AutoSize = true,
            Location = new Point(Wide - Pad - 46 - 210, y + 8),
            BackColor = Color.Transparent,
        };
        Controls.Add(bgLabel);

        _background.Location = new Point(Wide - Pad - 46, y + 4);
        _background.CheckedChanged += (_, _) => Refill();
        Controls.Add(_background);

        y = _search.Bottom + 12;

        _rows = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Color.Transparent,
        };

        var host = new ScrollHost
        {
            Location = new Point(Pad, y),
            Width = Wide - Pad * 2,
            Height = 360,
            BackColor = Theme.Input,
            Padding = new Padding(6, 6, 6, 6),
        };

        host.SetContent(_rows);
        Controls.Add(host);

        y += host.Height + 16;

        var cancel = new FlatButton
        {
            Text = Words.AppPickerCancel,
            Size = new Size(110, 38),
            Location = new Point(Wide - Pad - 110 - 170, y),
        };
        cancel.Click += (_, _) => DialogResult = DialogResult.Cancel;
        Controls.Add(cancel);

        _add = new FlatButton
        {
            Size = new Size(160, 38),
            Fill = Theme.Mix(Theme.Accent, Color.Black, 0.12f),
            ForeColor = Color.White,
            Line = Color.Empty,
            Location = new Point(Wide - Pad - 160, y),
        };

        _add.Click += (_, _) =>
        {
            if (_ticked.Count == 0) return;

            Chosen = [.. _ticked];
            DialogResult = DialogResult.OK;
        };

        Controls.Add(_add);

        Height = y + 38 + Pad;

        Refill();

        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) DialogResult = DialogResult.Cancel; };
        WindowDrag.Enable(this);
    }

    private void Refill()
    {
        string query = _search.Query;

        var shown = _all.Where(a => _background.Checked || !a.Background)
                        .Where(a => query.Length == 0
                                 || a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                 || a.Process.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                        .ToList();

        SuspendLayout();
        _rows.SuspendLayout();

        foreach (Control old in _rows.Controls) old.Dispose();
        _rows.Controls.Clear();

        var apps = shown.Where(a => !a.Background).ToList();
        var back = shown.Where(a => a.Background).ToList();

        if (apps.Count > 0)
        {
            _rows.Controls.Add(GroupHeader(Words.AppPickerGroupApps, ""));
            _rows.Controls.Add(ColumnHeader());
            foreach (var app in apps) _rows.Controls.Add(Row(app));
        }

        if (back.Count > 0)
        {
            _rows.Controls.Add(GroupHeader(Words.AppPickerGroupBackground,
                                           Words.AppPickerGroupBackgroundWhy));
            _rows.Controls.Add(ColumnHeader());
            foreach (var app in back) _rows.Controls.Add(Row(app));
        }

        if (shown.Count == 0)
        {
            _rows.Controls.Add(new Label
            {
                Text = query.Length > 0 ? Words.AppPickerNoMatch : Words.AppPickerNothing,
                ForeColor = Theme.TextFaint,
                AutoSize = true,
                Margin = new Padding(8, 10, 8, 10),
                BackColor = Color.Transparent,
            });
        }

        _rows.ResumeLayout();
        ResumeLayout();

        RefreshAddButton();
    }

    private void RefreshAddButton()
    {
        _add.Text = string.Format(Words.AppPickerAdd, _ticked.Count);
        _add.Enabled = _ticked.Count > 0;
        _add.Invalidate();
    }

    private Control GroupHeader(string title, string note)
    {
        var row = new BufferedPanel
        {
            Size = new Size(Wide - Pad * 2 - 24, note.Length > 0 ? 40 : 28),
            BackColor = Theme.Surface,
            Margin = new Padding(0, 6, 0, 2),
        };

        row.Controls.Add(new Label
        {
            Text = title,
            Font = Theme.BodySemi,
            ForeColor = Theme.Text,
            AutoSize = true,
            Location = new Point(TickX, 5),
            BackColor = Color.Transparent,
        });

        if (note.Length > 0)
        {
            row.Controls.Add(new Label
            {
                Text = note,
                Font = Theme.Small,
                ForeColor = Theme.TextFaint,
                AutoSize = true,
                Location = new Point(TickX, 22),
                BackColor = Color.Transparent,
            });
        }

        return row;
    }

    private Control ColumnHeader()
    {
        var row = new BufferedPanel
        {
            Size = new Size(Wide - Pad * 2 - 24, 20),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 2),
        };

        void Head(string text, int x) => row.Controls.Add(new Label
        {
            Text = text,
            Font = Theme.Caption,
            ForeColor = Theme.TextFaint,
            AutoSize = true,
            Location = new Point(x, 3),
            BackColor = Color.Transparent,
        });

        Head(Words.AppPickerColApp, NameX);
        Head(Words.AppPickerColProcess, ProcX);
        Head(Words.AppPickerColMemory, MemX);
        Head(Words.AppPickerColStatus, StatusX);

        return row;
    }

    private Control Row(RunningApp app)
    {
        var row = new BufferedPanel
        {
            Size = new Size(Wide - Pad * 2 - 24, 32),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 1),
        };

        void Cell(string text, int x, Font font, Color ink, int width = 0) =>
            row.Controls.Add(new Label
            {
                Text = text,
                Font = font,
                ForeColor = ink,
                AutoSize = width == 0,
                Size = width == 0 ? Size.Empty : new Size(width, 18),
                AutoEllipsis = width > 0,
                Location = new Point(x, 7),
                BackColor = Color.Transparent,
            });

        if (app.Protectable)
        {
            var tick = new InlineCheck
            {
                Text = "",
                Location = new Point(TickX, 4),
                Size = new Size(22, 24),
            };

            tick.SetQuietly(_ticked.Contains(app.Path));

            tick.CheckedChanged += (_, _) =>
            {
                if (tick.Checked) _ticked.Add(app.Path);
                else _ticked.Remove(app.Path);

                RefreshAddButton();
            };

            row.Controls.Add(tick);
        }

        var ink = app.Protectable ? Theme.Text : Theme.TextFaint;

        Cell(app.Name, NameX, Theme.BodySemi, ink, ProcX - NameX - 12);
        Cell(app.Process, ProcX, Theme.Small, Theme.TextDim, MemX - ProcX - 12);
        Cell(Memory(app.WorkingSet), MemX, Theme.Small, Theme.TextDim);
        Cell(app.Protectable ? "" : Words.AppPickerProtected, StatusX, Theme.Small, Theme.Warn);

        return row;
    }

    private static string Memory(long bytes) =>
        bytes <= 0 ? "" : $"{bytes / 1024 / 1024:N0} MB";

    private sealed record RunningApp(string Name, string Process, string Path, long WorkingSet,
                                     bool Background, bool Protectable);

    /// <summary>
    /// Everything running that this could be asked about, minus what is already on the list.
    ///
    /// One entry per executable, not per process: Chrome is a dozen processes and one application,
    /// and a list saying so twelve times would be useless. The memory shown is the total across all
    /// of them, which is the number that answers "what would closing this give me back".
    /// </summary>
    private static IEnumerable<RunningApp> Running(IEnumerable<string> already)
    {
        var skip = new HashSet<string>(already, StringComparer.OrdinalIgnoreCase);
        var byPath = new Dictionary<string, RunningApp>(StringComparer.OrdinalIgnoreCase);

        Process[] all;
        try { all = Process.GetProcesses(); }
        catch (Exception ex) { Log.Warn($"Could not list running apps: {ex.Message}"); return []; }

        foreach (var proc in all)
        {
            using (proc)
            {
                string path, name;
                long set;
                bool windowed;

                try
                {
                    if (proc.Id == Environment.ProcessId) continue;

                    path = proc.MainModule?.FileName ?? "";
                    if (path.Length == 0 || skip.Contains(path)) continue;

                    name = proc.ProcessName;
                    set = proc.WorkingSet64;
                    windowed = proc.MainWindowHandle != IntPtr.Zero && proc.MainWindowTitle.Length > 0;
                }
                catch
                {
                    // Elevated or protected by Windows. This app runs unelevated and could not close
                    // it, so it is not a choice worth offering.
                    continue;
                }

                if (byPath.TryGetValue(path, out var seen))
                {
                    byPath[path] = seen with
                    {
                        WorkingSet = seen.WorkingSet + set,
                        Background = seen.Background && !windowed,
                    };
                    continue;
                }

                byPath[path] = new RunningApp(Nice(name), name, path, set, !windowed,
                                              Protectable: !Session.ResourceControl.IsProtected(name));
            }
        }

        return byPath.Values
                     .OrderByDescending(a => a.WorkingSet)
                     .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>"chrome" reads as a process; "Chrome" reads as the thing on the taskbar.</summary>
    private static string Nice(string processName) =>
        processName.Length == 0 ? processName : char.ToUpper(processName[0]) + processName[1..];
}
