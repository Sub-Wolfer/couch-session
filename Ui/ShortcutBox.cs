using System.Drawing.Drawing2D;
using CouchMode.Input;

namespace CouchMode.Ui;

/// <summary>
/// A box that shows a shortcut and records a new one when you press it.
///
/// One control for both the keyboard and the controller, because from the user's side they are
/// the same interaction: click here, do the thing, see it written down. Only the listening
/// differs — a key press arrives as a message, a pad combination has to be polled for — so that
/// is the only part that branches.
///
/// Deliberately not a text box. What goes in here is not typed, and a caret sitting in a field
/// invites someone to try. This paints as a button that changes what it says.
/// </summary>
internal sealed class ShortcutBox : Control
{
    /// <summary>What kind of shortcut this box records.</summary>
    public enum Source { Keyboard, Controller }

    private readonly Source _source;
    private readonly System.Windows.Forms.Timer _padPoll;

    private bool _listening;
    private bool _hover;
    private int _value;

    // How many boxes are recording right now, across the whole form. The controller-to-mouse driver
    // reads this to stand the pointer down only while a shortcut is actually being captured — so the
    // pad steers the pointer everywhere else in the app, including the rest of the Controller and
    // Hotkeys pages, rather than being blocked for the whole page.
    private static int _listeningCount;

    /// <summary>
    /// Whether any box is recording a *controller* combination.
    ///
    /// Only controller boxes are counted, and the distinction matters: this is what makes the pad
    /// stop steering the pointer, and a controller box genuinely needs that — the buttons being
    /// recorded would otherwise be eaten by the mouse it drives. A keyboard box needs no such thing.
    /// It used to count both, so opening the keyboard box switched the pad-mouse off for no reason
    /// and left no pad-reachable way to close the box again.
    /// </summary>
    public static bool AnyListening => System.Threading.Volatile.Read(ref _listeningCount) > 0;

    /// <summary>Raised when the recorded shortcut changes, including when it is cleared.</summary>
    public event EventHandler? ShortcutChanged;

    /// <summary>Called while listening starts and stops, so the owner can pause its own watcher.</summary>
    public event EventHandler<bool>? ListeningChanged;

    public ShortcutBox(Source source)
    {
        _source = source;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw
               | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Font = Theme.Body;
        Cursor = Cursors.Hand;
        TabStop = true;
        Size = new Size(260, 34);

        _padPoll = new System.Windows.Forms.Timer { Interval = 70 };
        _padPoll.Tick += (_, _) => PollPad();
    }

    public int Value
    {
        get => _value;
        set
        {
            if (_value == value) return;

            _value = value;
            Redraw();
            ShortcutChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stop listening and keep whatever was already recorded.</summary>
    public void Cancel()
    {
        if (!_listening) return;

        _listening = false;
        if (_source == Source.Controller) System.Threading.Interlocked.Decrement(ref _listeningCount);
        _padPoll.Stop();

        Shortcuts.EndCapture();
        ListeningChanged?.Invoke(this, false);

        Redraw();
    }

    /// <summary>
    /// Repaint, unless this control is on its way out.
    ///
    /// Every path here can be reached during teardown — losing focus as the form closes, a
    /// timer tick that was already queued — and Invalidate on a disposed control throws.
    /// </summary>
    private void Redraw()
    {
        if (!IsDisposed && IsHandleCreated) Invalidate();
    }

    public void Clear()
    {
        Cancel();
        Value = 0;
    }

    private void Listen()
    {
        if (_listening) return;

        _listening = true;
        if (_source == Source.Controller) System.Threading.Interlocked.Increment(ref _listeningCount);
        _held = 0;
        _rejected = "";

        // Ask whatever holds the live shortcuts to let go, or the combination being recorded
        // would be swallowed on its way here — see Shortcuts.
        Shortcuts.BeginCapture();
        ListeningChanged?.Invoke(this, true);

        if (_source == Source.Controller) _padPoll.Start();

        Focus();
        Redraw();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (_listening) Cancel(); else Listen();
        base.OnMouseDown(e);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Redraw(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Redraw(); base.OnMouseLeave(e); }
    protected override void OnLostFocus(EventArgs e) { Cancel(); base.OnLostFocus(e); }

    /// <summary>
    /// Every key, including the ones the dialog would otherwise eat.
    ///
    /// Tab, the arrows, Enter and Escape never reach OnKeyDown for a control inside a form —
    /// the dialog takes them first for navigation. While listening, this box wants them, since
    /// people reasonably bind things like Ctrl+Alt+Left. Escape is the exception, kept as the
    /// way out rather than something bindable.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (!_listening || _source != Source.Keyboard) return base.ProcessCmdKey(ref msg, keyData);

        if ((keyData & Keys.KeyCode) == Keys.Escape) { Cancel(); return true; }

        Record(keyData);
        return true;
    }

    private void Record(Keys keyData)
    {
        // Ignore the modifiers on their own. Holding Ctrl on the way to Ctrl+Alt+C arrives here
        // first, and taking it would end the capture before the real key was pressed.
        var key = keyData & Keys.KeyCode;

        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
                or Keys.LWin or Keys.RWin or Keys.None)
            return;

        if (!KeyShortcut.IsUsable((int)keyData))
        {
            // Said in the box rather than in a dialog. The rule is easier to show than explain.
            _rejected = Words.ShortcutNeedsModifier;
            Redraw();
            return;
        }

        _rejected = "";
        Cancel();
        Value = (int)keyData;
    }

    // ---- controller ----

    /// <summary>
    /// An int, not a ushort. The value carries a backend flag in bit 30 as well as the button
    /// mask, and a ushort would silently drop it — turning every HID combination into an XInput
    /// one at the moment it was recorded.
    /// </summary>
    private int _held;

    /// <summary>
    /// The controller the value in this box was recorded on.
    ///
    /// Kept alongside the mask because a HID mask is that device's own numbering and cannot be
    /// read without it. It is what lets the buttons still be drawn with the pad unplugged, and
    /// what lets the same combination be recognised on a different controller later.
    /// </summary>
    public ushort Vendor { get; set; }
    public ushort Product { get; set; }

    private PadReading _heldFrom;

    /// <summary>
    /// Records the largest combination held, and commits it once the buttons come back up.
    ///
    /// Waiting for the release is what makes a combination possible at all: pressing three
    /// buttons is never simultaneous, so taking the first reading would record whichever one
    /// happened to land first.
    /// </summary>
    private void PollPad()
    {
        var now = PadShortcut.Held();

        if (now.Combo != 0)
        {
            // Compared on the buttons alone; the backend flag is not a button and would make
            // any HID reading look like it had one more.
            if (System.Numerics.BitOperations.PopCount(PadShortcut.MaskOf(now.Combo)) >=
                System.Numerics.BitOperations.PopCount(PadShortcut.MaskOf(_held)))
            {
                _held = now.Combo;
                _heldFrom = now;
            }

            _rejected = "";
            Redraw();
            return;
        }

        if (_held == 0) return;   // nothing pressed yet, keep waiting

        if (!PadShortcut.IsUsable(_held))
        {
            _rejected = Words.ShortcutNeedsTwoButtons;
            _held = 0;
            Redraw();
            return;
        }

        int captured = _held;
        var from = _heldFrom;

        _held = 0;
        _heldFrom = default;

        Cancel();

        // Recorded together, so the mask never outlives the knowledge of what it means.
        Vendor = from.Vendor;
        Product = from.Product;
        Value = captured;
    }

    private string _rejected = "";

    // ---- painting ----

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        var box = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        var edge = _listening ? Theme.Accent : _hover ? Theme.InputLine : Theme.Line;

        Theme.FillRounded(g, box, 8, Theme.Input);
        Theme.DrawRounded(g, box, 8, edge);

        var (text, ink) = Label();

        // A controller combination is drawn as the buttons themselves; everything else — the
        // prompts, the refusals, an empty box, and every keyboard shortcut — is words.
        if (PadCombo() is { } combo && combo != 0)
        {
            var (vendor, product) = ShownFrom();
            var (family, presses) = PadShortcut.Decode(new PadReading(combo, vendor, product));

            // Drawn in the symbols of whatever is plugged in now, when that differs from the pad
            // it was recorded on. The combination is stored as positions, so a shortcut set on a
            // DualSense really does fire from Y and D-pad on an Xbox controller — and showing it
            // as Triangle while an Xbox pad sits on the desk would be describing a pad that is not
            // there.
            family = PadShortcut.FamilyInUse(family);

            if (presses.Count > 0)
            {
                // Back to design size: this box lives in the settings window, which Windows scales for
                // DPI itself. The prompt sets its own scale before drawing, and that setting is shared.
                PadGlyphs.UseScale(1f);
                PadGlyphs.Draw(g, Rectangle.Round(box), family, presses, ink);
                return;
            }
        }

        TextRenderer.DrawText(g, text, Font, Rectangle.Round(box), ink,
                              TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }

    /// <summary>
    /// The controller combination this box is currently showing, or null if it is showing words.
    ///
    /// Both states matter and they are different values: while listening it is whatever is held
    /// down right now, so the shapes appear as the buttons go down, and afterwards it is what was
    /// recorded. A rejection takes priority over both — that is a message, not a combination.
    /// </summary>
    private int? PadCombo()
    {
        if (_source != Source.Controller || _rejected.Length > 0) return null;

        return _listening ? _held : _value;
    }

    /// <summary>
    /// The controller to read the shown combination against.
    ///
    /// While listening that is the pad being pressed right now; otherwise it is the pad the value
    /// was recorded on, which is remembered precisely so the buttons can still be named once that
    /// controller has been switched off.
    /// </summary>
    private (ushort Vendor, ushort Product) ShownFrom()
        => _listening && _heldFrom.Combo != 0 ? (_heldFrom.Vendor, _heldFrom.Product)
                                              : (Vendor, Product);

    private (string Text, Color Ink) Label()
    {
        if (_rejected.Length > 0) return (_rejected, Theme.Bad);

        if (_listening)
        {
            if (_source == Source.Keyboard) return (Words.ShortcutListenKeyboard, Theme.Info);

            if (_held != 0) return (PadShortcut.Describe(_held, _heldFrom.Vendor, _heldFrom.Product),
                                    Theme.Info);

            // Says which of the two silences this is. Waiting for a button and waiting for a
            // controller that will never appear look the same otherwise.
            return PadShortcut.AnyConnected()
                 ? (Words.ShortcutListenController, Theme.Info)
                 : (Words.ShortcutNoPad, Theme.Bad);
        }

        if (_value == 0) return (Words.ShortcutNone, Theme.TextFaint);

        return (_source == Source.Keyboard
                    ? KeyShortcut.Describe(_value)
                    : PadShortcut.Describe(_value, Vendor, Product),
                Theme.Text);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Give the capture back before going away.
            //
            // Shortcuts counts captures rather than flagging them, so a box disposed mid-listen
            // would leave the count above zero for the life of the process — and the shortcuts
            // it asked to be released would never be taken back up. Silent, permanent, and
            // impossible to guess at from the outside.
            if (_listening)
            {
                _listening = false;
                if (_source == Source.Controller) System.Threading.Interlocked.Decrement(ref _listeningCount);
                Shortcuts.EndCapture();
            }

            _padPoll.Stop();
            _padPoll.Dispose();
        }

        base.Dispose(disposing);
    }
}
