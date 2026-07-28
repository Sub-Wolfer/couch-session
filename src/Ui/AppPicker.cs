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

    /// <summary>The width a row actually gets: the dialog, less its padding and the scrollbar.</summary>
    private const int RowW = Wide - Pad * 2 - 26;

    // [BUG] These were fixed pixel offsets that added up to more than a row is wide, so STATUS
    // drew as "STATU" and every protected row said "Prote". Laid out from the right edge
    // backwards now, which is the only way a last column can be guaranteed to fit.
    private const int TickX = 10;
    private const int NameX = 42;
    private const int StatusW = 78;
    private const int MemW = 84;
    private const int ProcW = 150;

    private const int StatusX = RowW - StatusW - 10;
    private const int MemX = StatusX - MemW - 10;
    private const int ProcX = MemX - ProcW - 10;

    /// <summary>The executables chosen. Empty if the dialog was dismissed.</summary>
    public IReadOnlyList<string> Chosen { get; private set; } = [];

    private readonly List<RunningApp> _all;
    private readonly HashSet<string> _ticked = new(StringComparer.OrdinalIgnoreCase);
    private readonly FlowLayoutPanel _rows;
    private readonly SearchBox _search;
    private readonly ToggleSwitch _background = new();
    private readonly FlatButton _add;
    private readonly Dictionary<string, InlineCheck> _ticks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Control> _boxes = new(StringComparer.OrdinalIgnoreCase);

    private Control? _hovering;

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
            BackColor = Theme.Surface,
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
        _hovering = null;

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

    /// <summary>
    /// Tick or untick one app from anywhere: the box, the row, or a label on it.
    ///
    /// One place rather than three, so the box and the row's shading can never disagree about
    /// what is chosen.
    /// </summary>
    private void Set(RunningApp app, bool on)
    {
        if (on) _ticked.Add(app.Path);
        else _ticked.Remove(app.Path);

        if (_ticks.TryGetValue(app.Path, out var box) && box.Checked != on) box.SetQuietly(on);
        if (_boxes.TryGetValue(app.Path, out var row)) Shade(row, app);

        RefreshAddButton();
    }

    /// <summary>
    /// The background one row should be showing, in priority order.
    ///
    /// Ticked beats hovered beats banded. Kept as one function because three separate handlers
    /// setting BackColor is how a row ends up stuck in its hover colour after being ticked.
    /// </summary>
    private void Shade(Control row, RunningApp app)
    {
        // Lifted from GameListView, deliberately the same three values: selected is the strongest,
        // hover sits between it and a plain row so it reads as "you are here" rather than "this is
        // chosen". Two lists in one app that shade rows differently is two lists to learn.
        bool ticked = _ticked.Contains(app.Path);
        bool hover = _hovering == row && !ticked;

        row.BackColor = ticked ? Theme.SurfaceHi
                      : hover  ? Theme.Mix(Theme.Surface, Theme.SurfaceHi, 0.45f)
                               : Theme.Surface;

        row.Invalidate();
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
            Size = new Size(RowW, note.Length > 0 ? 40 : 28),
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
            Size = new Size(RowW, 20),
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
            Size = new Size(RowW, 36),
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 0),
            Cursor = app.Protectable ? Cursors.Hand : Cursors.Default,
        };

        // Brighter than the dividers between settings rows, for the reason the games list gives:
        // those sit in a card with generous spacing, these are tight and evenly sized, and the eye
        // needs a firmer edge to track along a long list.
        row.Paint += (_, e) =>
        {
            using var rule = new SolidBrush(Theme.RowLine);
            e.Graphics.FillRectangle(rule, 10, row.Height - 1, row.Width - 20, 1);
        };

        void Cell(string text, int x, Font font, Color ink, int width = 0,
                  ContentAlignment align = ContentAlignment.MiddleLeft) =>
            row.Controls.Add(new Label
            {
                Text = text,
                Font = font,
                ForeColor = ink,
                AutoSize = false,
                Size = new Size(width == 0 ? 120 : width, 22),
                AutoEllipsis = true,
                TextAlign = align,
                Location = new Point(x, 7),
                BackColor = Color.Transparent,
            });

        if (app.Protectable)
        {
            var tick = new InlineCheck
            {
                Text = "",
                Location = new Point(TickX, 5),
                Size = new Size(22, 24),
            };

            tick.SetQuietly(_ticked.Contains(app.Path));
            tick.CheckedChanged += (_, _) => Set(app, tick.Checked);

            row.Controls.Add(tick);
            _ticks[app.Path] = tick;
        }

        var ink = app.Protectable ? Theme.Text : Theme.TextFaint;

        Cell(app.Name, NameX, Theme.BodySemi, ink, ProcX - NameX - 12);
        Cell(app.Process, ProcX, Theme.Small, Theme.TextDim, ProcW);
        Cell(Memory(app.WorkingSet), MemX, Theme.Small, Theme.TextDim, MemW, ContentAlignment.MiddleRight);
        Cell(app.Protectable ? "" : Words.AppPickerProtected, StatusX, Theme.Small, Theme.Warn, StatusW);

        // The whole row is the target, not the tick box.
        //
        // A 22-pixel square at the far left of a 620-pixel row is a small thing to hit for a list
        // people go down ticking several of. Every label is given the same handler rather than
        // disabled, because a disabled label greys its text and these have to stay readable.
        if (app.Protectable)
        {
            void Toggle(object? sender, EventArgs e) => Set(app, !_ticked.Contains(app.Path));

            row.Click += Toggle;
            foreach (Control child in row.Controls)
                if (child is Label) child.Click += Toggle;
        }

        _boxes[app.Path] = row;

        // Hover has to be hung off the labels too. A Label sitting on a Panel takes the mouse
        // with it, so a row wired only on itself lights up in the gaps between its own text.
        void Enter(object? sender, EventArgs e) { _hovering = row; Shade(row, app); }
        void Leave(object? sender, EventArgs e)
        {
            if (_hovering != row) return;
            _hovering = null;
            Shade(row, app);
        }

        row.MouseEnter += Enter;
        row.MouseLeave += Leave;

        foreach (Control child in row.Controls)
        {
            child.MouseEnter += Enter;
            child.MouseLeave += Leave;
        }

        Shade(row, app);

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

                byPath[path] = new RunningApp(DisplayName(path), name, path, set, !windowed,
                                              Protectable: !Session.ResourceControl.IsProtected(name));
            }
        }

        return byPath.Values
                     .OrderByDescending(a => a.WorkingSet)
                     .ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    /// <summary>
    /// What the app calls itself, taken from the executable's own version information.
    ///
    /// [BUG] This used to capitalise the process name, which produced "Msedge", "EXCEL" and
    /// "Applicationframehost" — names no one has ever seen on their taskbar. The description a
    /// publisher compiles into the file is where "Microsoft Edge" and "Microsoft Excel" come from,
    /// and it is what every task manager shows.
    ///
    /// Falls back to the tidied process name, because plenty of executables carry no description
    /// and a blank row would be worse than an ugly one.
    /// </summary>
    public static string DisplayName(string path)
    {
        string fallback = Tidy(Path.GetFileNameWithoutExtension(path));

        try
        {
            var described = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
            return string.IsNullOrEmpty(described) ? fallback : described;
        }
        catch { return fallback; }
    }

    private static string Tidy(string name) =>
        name.Length == 0 ? name : char.ToUpper(name[0]) + name[1..];
}
