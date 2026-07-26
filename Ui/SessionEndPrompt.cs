using System.Drawing.Drawing2D;
using System.Linq;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using CouchMode.Input;

namespace CouchMode.Ui;

/// <summary>
/// The "a game is still running" confirmation shown when a session ends with a game up and closing it
/// is gated on. Styled like the app's notification badges — the CS mark and name up top — so it reads
/// as ours at a glance.
///
/// It is driven by the app's own controller reading (not Steam Input, which is routing the pad to the
/// game): the D-pad moves, the face buttons choose. It also takes the foreground so, while it is up,
/// the pad can no longer reach the game behind it. The stick is deliberately NOT used for navigation,
/// so it stays free to drive the mouse. Mouse and keyboard work too, for when the controller has died
/// — which is exactly the case this protects against. The default is "Keep playing", and nothing
/// happens without an explicit pick, so a dead or idle controller never closes a game.
/// </summary>
internal sealed class SessionEndPrompt : Form
{
    public enum Choice
    {
        Stay, Close, Minimize, CloseToBigPicture,

        /// <summary>
        /// Go back into a session that is still running behind the desktop.
        ///
        /// Only ever offered by the prompt that lands on the desk after a controller disconnects. The
        /// other prompts are shown while the session is live, where there is nothing to go back to.
        /// </summary>
        Resume,

        /// <summary>Turn the controller-as-mouse on or off, then get out of the way.</summary>
        ToggleMouse,

        /// <summary>Bring up an on-screen keyboard.</summary>
        Keyboard,
    }

    /// <summary>
    /// One answer. <paramref name="Text"/> is the action; <paramref name="Detail"/> is what it does.
    ///
    /// The second line exists because the first cannot carry the weight on its own. "Close the game and
    /// end the session" names an action but says nothing about what the user loses by taking it, and a
    /// row of near-identical sentences all beginning with a verb is read as a shape rather than as
    /// words — which is exactly how someone ends up closing a game they meant to leave running. The
    /// action stays short so it can be scanned; the consequence goes underneath in plain language.
    ///
    /// Detail is optional. The two navigation rows at the foot of the prompt do not need one, and
    /// giving them a second line would make them look as consequential as the answers above them.
    /// </summary>
    public readonly record struct Option(string Text, Choice Choice, Color Accent, string Detail = "");

    /// <summary>Raised once with the chosen action. Not raised if the window is closed another way.</summary>
    public event Action<Choice>? Chosen;

    /// <summary>
    /// What B / Circle does, in the hint row.
    ///
    /// Backing out is not one of the listed options: it is the button, and only the button. Listing it
    /// as well would have been the same action written twice — one of them selected by default, so the
    /// safe choice was also the one an accidental A press landed on, which made the list read as if
    /// doing nothing were a decision. The hint carries it instead, and this is what it says.
    /// </summary>
    public string KeepHint { get; init; } = "Keep playing";

    /// <summary>
    /// A choice that Square / X makes directly, without selecting it first.
    ///
    /// For the one answer worth reaching in a single press. Everything else is a deliberate walk down
    /// the list, which is right for choices you should look at before making — but ending the session
    /// is the thing people came here to do, and making them scroll past two other options to reach it
    /// is a tax on the common case.
    ///
    /// Null leaves the button doing nothing, and the hint row says nothing about it.
    /// </summary>
    public Choice? Shortcut { get; init; }

    /// <summary>What the Square / X hint reads, when <see cref="Shortcut"/> is set.</summary>
    public string ShortcutHint { get; init; } = "";

    /// <summary>
    /// This prompt is on a desk monitor with a mouse, not on a television with a controller.
    ///
    /// Everything else this class does assumes the opposite, and rightly: it exists to be answered
    /// with a pad over a fullscreen game that will fight it for the foreground. One prompt is not
    /// like that — the one that appears at the desk after a controller has disconnected — and every
    /// one of those assumptions is actively wrong there:
    ///
    ///  • The hint row names pad buttons. The event that raised this prompt is the pad ceasing to
    ///    exist, so those hints would be telling somebody to press A on a controller that is flat.
    ///  • The foreground is re-taken four times a second for up to three minutes. On a television
    ///    that is how the prompt survives a game grabbing focus back; on a desktop it means the
    ///    machine cannot be used for anything else until the prompt is answered.
    ///  • The cursor is hidden. This is the one prompt that has to be answered with a mouse.
    ///  • Backing out hands the foreground back to whatever was in front, and restores it if it has
    ///    been minimized. What was in front here is the game, which was deliberately minimized a
    ///    moment ago — so the restore would drag it back onto the desk three minutes later.
    /// </summary>
    public bool AtTheDesk { get; init; }

    private bool PadHints => !AtTheDesk;

    private readonly string _title;
    private readonly string _body;

    // Top to bottom. The first is selected by default, so callers should put the safe "cancel" choice
    // first — an accidental confirm then does nothing irreversible.
    private readonly Option[] _options;

    private int _selected;
    private int _hover = -1;
    private bool _decided;

    // Rising-edge state for the pad, so a held button fires once.
    private bool _pUp, _pDown, _pA, _pB, _pSquare;

    // Latch for the left stick: set once a firm push has fired a step, cleared only when the stick
    // returns near centre, so a held lean is one move rather than a stream.
    private bool _pStickUp, _pStickDown;

    /// <summary>A B / Circle press seen while live, waiting for its release to back out.</summary>
    private bool _bArmed;

    // 25ms, not 70. A quick tap of the D-pad can be over inside 70ms, and a poll that slow simply never
    // saw it happen — the press went down and back up between two looks. Fast scrolling was made of
    // exactly those taps, which is why some of them did nothing.
    private readonly System.Windows.Forms.Timer _poll = new() { Interval = 25 };

    /// <summary>
    /// Drives the lights behind the panel. Separate from the input poll on purpose: input wants to be
    /// as prompt as possible, and a decoration wants to be as cheap as possible, so tying the two
    /// together would mean paying for one at the rate of the other.
    /// </summary>
    private readonly System.Windows.Forms.Timer _shimmer = new() { Interval = 33 };

    /// <summary>Where the animation is, 0 to 1 and wrapping. One number drives every part of it.</summary>
    private float _phase;

    // The border, flattened to points once per size, with the distance along it at each one. Walking a
    // curve is the expensive half of running a light around it, and the curve only changes when the
    // window does — so it is walked once and the light just looks up where it has got to.
    private PointF[] _edge = [];
    private float[] _edgeAt = [];
    private float _edgeLength;
    private Size _edgeFor;
    private readonly DateTime _openedAt = DateTime.UtcNow;

    // The window that had the foreground when we appeared (the game). Restored on "Keep playing" so the
    // player drops straight back in; left alone for Close/Minimize, which tear the session down anyway.
    private IntPtr _previousForeground;

    /// <summary>
    /// The running game's window, if there is one.
    ///
    /// Nothing acts on it any more. It fed a fallback that minimized a game refusing to give up the
    /// foreground, which turned out to fire on ordinary fullscreen games and throw the player out of the
    /// one they were being asked about. Kept as a hook because knowing which window is the game is
    /// genuinely useful here, and it costs nothing to leave connected.
    /// </summary>
    public Func<IntPtr>? GameWindow { get; set; }

    /// <summary>
    /// How to hand the couch back when the user keeps what they had. Supplied by the caller because
    /// only it knows what "back" means — Big Picture brought forward, or the game refocused. Falls
    /// back to the window that had the foreground when we appeared.
    /// </summary>
    public Action? RestoreFocus { get; set; }

    // Set once we have had to minimize the game to get the pad away from it, so it can be put back.
    private IntPtr _minimizedGame;

    // Consecutive checks where something else held the foreground. One is ordinary (the grab has not
    // landed yet); a run of them means a window is fighting us for it.
    private int _lostForeground;
    private bool _reportedTheft;
    private bool _reportedYield;

    private DateTime _lastHold = DateTime.MinValue;

    private static readonly TimeSpan HoldEvery = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long after opening the foreground is defended on every poll — see HoldForeground.
    ///
    /// Measured from when this window opened, and deliberately short.
    ///
    /// [BUG] This was briefly tied to the *last theft* instead, so the defence lasted as long as the
    /// fight did. That is the wrong instinct here and it broke two things at once. Steam Input picks
    /// its controller configuration from the foreground window, and it re-reads that on every change —
    /// so grabbing the foreground forty times a second for as long as Big Picture keeps grabbing it
    /// back does not win the argument, it hands Steam Input a queue of configuration changes to work
    /// through. That queue is why the pad stopped driving Big Picture at all after closing a game, and
    /// why answering a prompt took seconds to hand control back.
    ///
    /// The contest during a Big Picture launch is not winnable by being louder. It is handled at the
    /// other end instead: while something else holds the foreground, this window does not act on the
    /// pad, so the two never both respond. See the input gate in Poll.
    /// </summary>
    private static readonly TimeSpan OverlaySettling = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long the pad is ignored while something else holds the foreground.
    ///
    /// Bounded, because not every window that keeps the foreground is going to give it up. A game in
    /// exclusive fullscreen may hold it for as long as it likes, and a prompt that answers to nothing
    /// because of that is worse than one that shares the pad with the thing behind it — at least the
    /// second can be answered. So this covers the launch scramble and then gets out of the way.
    /// </summary>
    private static readonly TimeSpan ContestGrace = TimeSpan.FromSeconds(2);

    private PadFamily _family = PadFamily.Xbox;

    private static readonly TimeSpan InputGrace = TimeSpan.FromMilliseconds(450);
    private static readonly TimeSpan AutoStayAfter = TimeSpan.FromMinutes(3);

    /// <summary>The monitor this prompt will appear on — decided before anything is sized for it.</summary>
    private readonly Screen? _target;

    private readonly float _scale;
    private int S(int v) => (int)Math.Round(v * _scale);

    private readonly Font _nameFont, _titleFont, _bodyFont, _optionFont, _hintFont, _detailFont;
    private readonly Bitmap _logo;
    private int _pad, _logoSize, _headerBottom, _optionH, _optionGap, _radius;

    /// <summary>
    /// The window's own corner radius — the outline, the light clipping and the shape all use this one.
    ///
    /// It was written out as <c>S(14)</c> in four places, which is how they came to disagree with the
    /// shape the window was actually being clipped to. See <see cref="CarveCorners"/>.
    /// </summary>
    private int _corner;

    /// <summary>The two ends of the backdrop gradient. Named because the shape, the fill and the
    /// window's own BackColor all have to agree on them.</summary>
    private static readonly Color BackdropFrom = Color.FromArgb(38, 42, 54);
    private static readonly Color BackdropTo = Color.FromArgb(22, 25, 33);

    /// <summary>
    /// Compose the whole window and hand it to Windows with per-pixel alpha.
    ///
    /// This window is layered — WS_EX_LAYERED, see CreateParams — and its contents come from here
    /// rather than from a paint handler, which is what makes genuinely antialiased corners possible.
    ///
    /// [BUG] The corners were previously a <c>Region</c>. A region is a binary mask with no
    /// antialiasing — a pixel is either in the window or out of it — so the curve came out visibly
    /// stepped, which is the whole objection <see cref="WindowCorners"/> raises against regions.
    /// Alpha has no such limit: a pixel half-covered by the curve is half-transparent, and the corner
    /// is as smooth as the maths.
    ///
    /// The order matters and is not the obvious one. Everything is painted solid first — including the
    /// text, which goes through GDI's TextRenderer and writes no alpha whatsoever — and only then is
    /// the corner shape carved out of the alpha channel. Doing it the other way round, filling a
    /// rounded path into a transparent bitmap, leaves GDI free to stamp opaque text over transparent
    /// pixels and transparent text over opaque ones, with no way to tell afterwards which was which.
    /// </summary>
    private void Redraw()
    {
        if (_decided || !IsHandleCreated || Width <= 0 || Height <= 0) return;

        try
        {
            using var frame = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);

            using (var g = Graphics.FromImage(frame))
            {
                // Opaque to start with, so every pixel has a defined alpha before GDI text lands on it.
                g.Clear(BackdropTo);
                PaintFrame(g);
            }

            CarveCorners(frame);
            Present(frame);
        }
        catch (Exception ex)
        {
            // Never fatal. A prompt that fails to repaint is bad; one that throws out of a timer tick
            // and takes the session with it is worse.
            Log.Warn($"The session prompt could not be drawn: {ex.Message}");
        }
    }

    /// <summary>
    /// Replace the alpha channel at the four corners with the rounded shape's coverage.
    ///
    /// Only the corners are touched: the shape's alpha is 255 everywhere else, since its straight edges
    /// run along whole pixels and have nothing to contribute, and walking every pixel of a
    /// television-sized window thirty times a second to write the value it already has is a cost with
    /// nothing on the other side of it.
    ///
    /// The colours are premultiplied by the new alpha as they are written, because that is the form
    /// UpdateLayeredWindow expects. Straight alpha shows up as a corner lighter than the edge it joins,
    /// which reads as a halo rather than as a mistake and is easy to chase for a long time.
    /// </summary>
    private void CarveCorners(Bitmap frame)
    {
        BuildCoverage();
        if (_coverage.Length == 0) return;

        int side = _coverageSide;

        var corners = new[]
        {
            new Rectangle(0, 0, side, side),
            new Rectangle(Width - side, 0, side, side),
            new Rectangle(0, Height - side, side, side),
            new Rectangle(Width - side, Height - side, side, side),
        };

        for (int c = 0; c < corners.Length; c++)
        {
            var shape = _coverage[c];
            var into = frame.LockBits(corners[c], ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);

            try
            {
                var row = new int[side];

                for (int y = 0; y < side; y++)
                {
                    Marshal.Copy(into.Scan0 + y * into.Stride, row, 0, side);

                    for (int x = 0; x < side; x++)
                    {
                        int alpha = shape[y * side + x];

                        if (alpha == 255) { row[x] |= unchecked((int)0xFF000000); continue; }
                        if (alpha == 0) { row[x] = 0; continue; }

                        int colour = row[x];
                        int r = ((colour >> 16) & 0xFF) * alpha / 255;
                        int g = ((colour >> 8) & 0xFF) * alpha / 255;
                        int b = (colour & 0xFF) * alpha / 255;

                        row[x] = (alpha << 24) | (r << 16) | (g << 8) | b;
                    }

                    Marshal.Copy(row, 0, into.Scan0 + y * into.Stride, side);
                }
            }
            finally
            {
                frame.UnlockBits(into);
            }
        }
    }

    /// <summary>How many samples across each pixel the corner coverage is measured with.</summary>
    private const int Oversample = 4;

    private byte[][] _coverage = [];
    private int _coverageSide;
    private Size _coverageFor;

    /// <summary>
    /// Work out, once per size, how much of each corner pixel the rounded shape actually covers.
    ///
    /// [BUG] This used to be GDI+'s own antialiasing, read straight off a filled path — and that is
    /// what the stepped corner was. GDI+ antialiases a path edge over roughly a single pixel and
    /// quantises it coarsely, so on a television, where this window is scaled two or three times up and
    /// the corner radius with it, one pixel of transition across a long curve is not a gradient. It is
    /// a staircase with the edges softened, which is exactly what it looked like.
    ///
    /// So the shape is rendered at <see cref="Oversample"/> times the size and each output pixel is the
    /// average of the samples inside it. Sixteen samples per pixel gives seventeen levels of coverage
    /// instead of GDI+'s handful, and it does not depend on how well GDI+ happens to antialias at the
    /// radius in play. Measured once per window size and cached, so the per-frame cost is a lookup.
    /// </summary>
    private void BuildCoverage()
    {
        if (_coverageFor == Size && _coverage.Length == 4) return;

        int side = Math.Min(_corner + 2, Math.Min(Width, Height) / 2);
        if (side <= 0) { _coverage = []; return; }

        var corners = new[]
        {
            new Point(0, 0),
            new Point(Width - side, 0),
            new Point(0, Height - side),
            new Point(Width - side, Height - side),
        };

        var built = new byte[4][];
        int big = side * Oversample;

        for (int c = 0; c < corners.Length; c++)
        {
            var samples = new byte[side * side];

            using (var sheet = new Bitmap(big, big, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(sheet))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Color.Transparent);

                    // The whole window's shape, drawn magnified, with this corner brought to the origin.
                    g.ScaleTransform(Oversample, Oversample);
                    g.TranslateTransform(-corners[c].X, -corners[c].Y);

                    using var shape = Theme.Rounded(new RectangleF(0, 0, Width, Height), _corner);
                    using var solid = new SolidBrush(Color.White);
                    g.FillPath(solid, shape);
                }

                var all = sheet.LockBits(new Rectangle(0, 0, big, big), ImageLockMode.ReadOnly,
                                         PixelFormat.Format32bppArgb);

                try
                {
                    var row = new int[big];

                    // Accumulated a magnified row at a time, so only one row of the large sheet is ever
                    // copied into managed memory.
                    var totals = new int[side];

                    for (int y = 0; y < big; y++)
                    {
                        Marshal.Copy(all.Scan0 + y * all.Stride, row, 0, big);

                        for (int x = 0; x < big; x++)
                            totals[x / Oversample] += (row[x] >> 24) & 0xFF;

                        if ((y + 1) % Oversample != 0) continue;

                        int line = y / Oversample;
                        for (int x = 0; x < side; x++)
                        {
                            samples[line * side + x] = (byte)(totals[x] / (Oversample * Oversample));
                            totals[x] = 0;
                        }
                    }
                }
                finally
                {
                    sheet.UnlockBits(all);
                }
            }

            built[c] = samples;
        }

        _coverage = built;
        _coverageSide = side;
        _coverageFor = Size;
    }

    /// <summary>
    /// Hand the composed frame to Windows as this window's contents.
    ///
    /// The bits go through a DIB section this method owns rather than through Bitmap.GetHbitmap.
    /// GetHbitmap composites the image against the colour it is given and hands back a
    /// device-dependent bitmap, and how much of the alpha channel survives that trip is not something
    /// worth depending on when the alpha channel is the entire point. A 32-bit top-down DIB is what
    /// UpdateLayeredWindow wants, so it is what it gets.
    /// </summary>
    private void Present(Bitmap frame)
    {
        if (!EnsureSurface()) return;

        var whole = new Rectangle(0, 0, Width, Height);
        var from = frame.LockBits(whole, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        try
        {
            int stride = Width * 4;
            var row = new int[Width];

            for (int y = 0; y < Height; y++)
            {
                Marshal.Copy(from.Scan0 + y * from.Stride, row, 0, Width);
                Marshal.Copy(row, 0, _bits + y * stride, Width);
            }
        }
        finally
        {
            frame.UnlockBits(from);
        }

        var size = new SIZE { cx = Width, cy = Height };
        var origin = new POINT { x = 0, y = 0 };
        var where = new POINT { x = Left, y = Top };

        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };

        IntPtr screen = GetDC(IntPtr.Zero);

        try
        {
            UpdateLayeredWindow(Handle, screen, ref where, ref size, _surface, ref origin, 0,
                                ref blend, ULW_ALPHA);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
    }

    private IntPtr _surface, _section, _previousSection, _bits;
    private Size _surfaceFor;

    /// <summary>Make sure there is a DIB of the right size to hand over. Kept between frames.</summary>
    private bool EnsureSurface()
    {
        if (_surface != IntPtr.Zero && _surfaceFor == Size) return true;

        ReleaseSurface();

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = Width,

            // Negative: top-down, so row 0 is the top one and the copy above needs no flipping.
            biHeight = -Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,   // BI_RGB
        };

        _section = CreateDIBSection(IntPtr.Zero, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_section == IntPtr.Zero) { Log.Warn("The session prompt could not create its drawing surface."); return false; }

        _surface = CreateCompatibleDC(IntPtr.Zero);
        _previousSection = SelectObject(_surface, _section);
        _surfaceFor = Size;
        return true;
    }

    private void ReleaseSurface()
    {
        if (_surface != IntPtr.Zero)
        {
            SelectObject(_surface, _previousSection);
            DeleteDC(_surface);
            _surface = IntPtr.Zero;
        }

        if (_section != IntPtr.Zero) { DeleteObject(_section); _section = IntPtr.Zero; }
        _bits = IntPtr.Zero;
        _surfaceFor = Size.Empty;
    }

    private Rectangle[] _optionRects = [];
    private Rectangle _hintRect;

    /// <param name="on">
    /// The monitor to appear on. Null works it out from whatever has the foreground, which is right
    /// for every prompt raised over a game or Big Picture. The desk prompt names its screen instead:
    /// it is raised straight after the game and Big Picture have been minimized, and a minimized
    /// window's rectangle is off in the far corner of the desktop, so inferring from the foreground
    /// there is a coin toss.
    /// </param>
    public SessionEndPrompt(string title, string body, Option[] options, Screen? on = null)
    {
        _title = title;
        _body = body;
        _options = options;

        // Deliberately NOT inside a per-monitor DPI scope, though it was for one revision and that made
        // this worse. A scope only lasts as long as the constructor: the position worked out under it
        // is in real pixels, but the window's handle is not created until Show(), by which time the
        // thread is back to its ordinary awareness and reads those same numbers as virtualised ones.
        // The window then landed off-centre every time, on 4K at 150% most visibly of all. One set of
        // units throughout — whatever WinForms is already using — is what keeps the sizing and the
        // placement agreeing with each other.

        // Which screen first, then how big — in that order, and never the other way round. Sizing
        // against the primary display and then showing the window on the television is what made this
        // come back small and off-centre: measured for one monitor, placed on another, and rescaled
        // again by Windows on arrival for the DPI difference between them.
        _target = on ?? ScreenForPrompt();
        _scale = UiScale.ForNotifications(_target);

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;

        // Fully opaque, on purpose.
        //
        // This was briefly translucent so what is behind still read as being there. It was the wrong
        // trade: whatever is behind is a game, and therefore arbitrarily bright and busy, so even a
        // mild amount of it showing through competes with the text of the very question being asked.
        // The background lights already keep the window from looking like a flat slab.
        Opacity = 1.0;
        StartPosition = FormStartPosition.Manual;

        // The dark end of the backdrop gradient, not Theme.Surface.
        //
        // This colour is only ever seen in the sliver between the painted corner curve and the clipped
        // window edge — a pixel or two at each corner. Theme.Surface is lighter than the gradient, so
        // that sliver read as a bright fleck sitting outside the outline. Matching the gradient's
        // darkest colour makes the same pixels invisible whether or not they end up covered.
        BackColor = BackdropTo;
        DoubleBuffered = true;

        // Windows must not resize this window, because this window has already been sized on purpose.
        //
        // A form defaults to rescaling itself whenever it lands on a monitor with a different DPI, and
        // that is exactly what happens here: it is built while the thread is looking at one screen and
        // then shown on the television. WinForms then "helpfully" applied the DPI ratio between the two
        // on arrival — shrinking a window already scaled by hand, and moving it off the centre it had
        // just been placed at. Every dimension here comes from _scale, which was measured against the
        // screen it is going to, so there is nothing left for automatic scaling to add.
        AutoScaleMode = AutoScaleMode.None;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        _nameFont = Scaled(Theme.BodySemi);
        _titleFont = Scaled(Theme.Heading);
        _bodyFont = Scaled(Theme.Body);
        _optionFont = Scaled(Theme.BodySemi);
        _hintFont = Scaled(Theme.Small);
        _detailFont = Scaled(Theme.Small);

        _pad = S(20);
        _logoSize = S(30);

        // Two lines of room when any answer explains itself, one when none does.
        //
        // Measured off the row rather than fixed, so a prompt made only of plain answers — the Big
        // Picture one, before it gained consequences — does not grow a band of empty space under every
        // row for the sake of a line that is not there.
        _optionH = options.Any(o => o.Detail.Length > 0) ? S(64) : S(46);
        _optionGap = S(10);
        _radius = S(12);
        _corner = S(14);
        _logo = AppIcon.Monogram(_logoSize);

        _family = PadShortcut.FamilyInUse(PadFamily.Xbox);

        LayoutContent();

        _poll.Tick += (_, _) => Poll();
        _poll.Start();

        _shimmer.Tick += (_, _) =>
        {
            // A full turn every twelve seconds or so: slow enough to read as ambient rather than as
            // something demanding an answer, which is the opposite of what this window wants.
            _phase += 1f / 360f;
            if (_phase >= 1f) _phase -= 1f;

            if (!IsDisposed && IsHandleCreated) Redraw();
        };
        _shimmer.Start();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);

        // Take the foreground off the game while we are deciding, so the pad drives this and not the
        // game behind it. Steam Input follows focus, so this is what actually blocks it.
        _previousForeground = GetForegroundWindow();
        // No Alt-Tab emulation, even once. It is the one call here that actively asks Windows to put
        // the window behind us away, and against an exclusive-fullscreen game that is the last thing
        // wanted. The ordinary foreground grab is enough.
        ForceForeground(Handle);

        // No pointer on a couch. This is answered with a controller, and a mouse cursor stranded in the
        // middle of it — wherever it happened to be left — is just clutter on a television. It comes
        // back the moment the mouse is actually moved, so it is hidden rather than disabled: anyone who
        // reaches for a mouse still gets one.
        _cursorAtShow = MousePosition;

        if (!AtTheDesk)
        {
            Cursor.Hide();
            _cursorHidden = true;
        }

        // A layered window has nothing on it until UpdateLayeredWindow is called, and showing it does
        // not call one. Without this the prompt appears as an empty rectangle until the first shimmer
        // tick a thirtieth of a second later — brief, but it is the first frame anyone sees.
        Redraw();
    }

    /// <summary>
    /// The app's font at this window's scale, rasterised without ClearType.
    ///
    /// [BUG] The small text on this window fringed — colour edges on the letters, worst on the thin
    /// strokes, which is what made it hard to read rather than merely small. That is ClearType, and it
    /// is not a rendering bug so much as ClearType being asked to do something it cannot.
    ///
    /// Subpixel antialiasing works by treating one pixel as three coloured stripes and lighting them
    /// separately, which only produces grey-looking text if the pixel is actually opaque and actually
    /// over the background it was blended against. This window is layered, per-pixel alpha, composited
    /// by the desktop over whatever game is behind it — so neither is true, and the three stripes stay
    /// visibly three colours.
    ///
    /// ANTIALIASED_QUALITY asks GDI for plain grayscale antialiasing instead. Slightly softer, and
    /// correct at any opacity over any background. Done through the LOGFONT because that is where the
    /// quality lives; Font has no property for it, and TextRenderer draws through GDI so it honours it.
    /// </summary>
    private Font Scaled(Font source)
    {
        var scaled = new Font(source.Name, source.SizeInPoints * _scale, source.Style, GraphicsUnit.Point);
        source.Dispose();

        try
        {
            var description = new LOGFONT();
            scaled.ToLogFont(description);
            description.lfQuality = AntialiasedQuality;

            var grey = Font.FromLogFont(description);
            scaled.Dispose();
            return grey;
        }
        catch (Exception ex)
        {
            // Worth having, not worth failing over: the window is perfectly usable with fringed text.
            Log.Warn($"Could not switch the prompt to grayscale text: {ex.Message}");
            return scaled;
        }
    }

    private const byte AntialiasedQuality = 4;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class LOGFONT
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet;
        public byte lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string lfFaceName = "";
    }

    private void LayoutContent()
    {
        // Wide enough for the longest line on it, within reason.
        //
        // [BUG] This was a flat S(470) while the answers gained a second line, and the second lines are
        // longer than the first — so they were quietly ellipsised, which is the one thing a sentence
        // explaining a consequence cannot survive. "Everything moves back to your…" tells you nothing.
        //
        // Measured rather than widened by a guess: brief wording keeps the window narrow, and only text
        // that genuinely needs the room takes it. The ceiling stops one careless sentence stretching the
        // prompt across a television.
        int longest = 0;

        foreach (var option in _options)
        {
            longest = Math.Max(longest, TextRenderer.MeasureText(option.Text, _optionFont).Width);

            if (option.Detail.Length > 0)
                longest = Math.Max(longest, TextRenderer.MeasureText(option.Detail, _detailFont).Width);
        }

        // The text inside a row starts S(34) in and stops S(10) short of the far edge; the row itself is
        // inset by _pad on both sides. Those are the same numbers PaintFrame lays the text out with.
        int width = Math.Clamp(longest + _pad * 2 + S(44) + S(6), S(470), S(640));

        _headerBottom = _pad + _logoSize;

        int titleH = TextRenderer.MeasureText(_title, _titleFont).Height;
        int bodyH = TextRenderer.MeasureText(_body, _bodyFont).Height;

        int top = _headerBottom + S(16) + titleH + S(6) + bodyH + S(16);
        _optionRects = new Rectangle[_options.Length];
        for (int i = 0; i < _options.Length; i++)
        {
            _optionRects[i] = new Rectangle(_pad, top, width - _pad * 2, _optionH);
            top += _optionH + _optionGap;
        }

        int hintWidth = width - _pad * 2;
        int hintRows = PadHints ? HintRows(hintWidth) : 0;

        _hintRect = new Rectangle(_pad, top + S(4), hintWidth, S(24) * hintRows);
        int height = _hintRect.Bottom + _pad;
        Size = new Size(width, height);

        // Placed on the screen the session is actually on, not on whichever Windows calls primary.
        //
        // Primary used to be the couch display for the whole of a session, so centring on it was the
        // same thing. It is not any more: keeping the couch display attached when dropping to the
        // desktop means primary can be the desk monitor while the game — and the person — are on the
        // television. The prompt was still appearing, just on a screen nobody was looking at.
        //
        // The window that had the foreground when this opened is the game or Big Picture, so its
        // monitor is the right one by definition.
        var area = _target?.WorkingArea ?? Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1920, 1080);
        Location = new Point(area.Left + (area.Width - width) / 2, area.Top + (area.Height - height) / 2);

        Log.Info($"Session prompt placed on the screen at {area.Left},{area.Top} ({area.Width}x{area.Height}).");
    }

    /// <summary>The monitor holding the window we are appearing over — the game, or Big Picture.</summary>
    private static Screen? ScreenForPrompt()
    {
        try
        {
            var over = GetForegroundWindow();
            if (over != IntPtr.Zero) return Screen.FromHandle(over);
        }
        catch { /* fall back to primary */ }

        return Screen.PrimaryScreen;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;

            // Layered, so the window has a real alpha channel and its corners can be antialiased
            // rather than stepped. Set here rather than through Form.Opacity: that route calls
            // SetLayeredWindowAttributes, which is the other, incompatible way of using a layered
            // window — one whole-window alpha instead of one per pixel.
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_LAYERED;
            return cp;
        }
    }

    // ---- input ----

    private void Poll()
    {
        if (_decided) return;

        // Not at the desk. See AtTheDesk: holding the foreground against a fullscreen game is this
        // window's job on a television and an act of vandalism on a desktop.
        if (!AtTheDesk) HoldForeground();

        if (DateTime.UtcNow - _openedAt > AutoStayAfter) { Decide(Choice.Stay); return; }

        // Keep the glyphs matching whatever pad is answering right now.
        var fam = PadShortcut.FamilyInUse(_family);
        if (fam != _family) { _family = fam; Redraw(); }

        // Not just "has enough time passed" — "is this window the one the pad is talking to".
        //
        // [BUG] Opening this over a Big Picture that was still launching left both responding to the
        // same presses: Steam Input was still feeding Big Picture, because Big Picture still had the
        // foreground, while this window read the pad directly and acted on it too. Nothing can stop
        // Steam feeding the window it thinks is in front, so the fix is not to be the second thing
        // reacting. While the foreground belongs to something else, this ignores the pad entirely.
        //
        // Time-limited so it cannot strand the user — see ContestGrace.
        var since = DateTime.UtcNow - _openedAt;
        bool ours = GetForegroundWindow() == Handle;

        if (!ours && since <= ContestGrace)
        {
            if (!_reportedYield)
            {
                _reportedYield = true;
                Log.Info("Session prompt is not the foreground window yet; ignoring the pad so it is not "
                       + "answering the same presses as whatever is.");
            }

            return;
        }

        bool live = since > InputGrace;

        bool up = PadShortcut.IsControlHeld(PadControl.DpadUp);
        bool down = PadShortcut.IsControlHeld(PadControl.DpadDown);
        bool a = PadShortcut.IsControlHeld(PadControl.FaceDown);    // A / Cross
        bool b = PadShortcut.IsControlHeld(PadControl.FaceRight);   // B / Circle
        bool square = PadShortcut.IsControlHeld(PadControl.FaceLeft);  // Square / X

        // The left stick moves the selection too. A Schmitt trigger — fire past a firm push, re-arm only
        // once it falls back near centre — makes one flick one step and a held lean not a stream.
        //
        // The re-arm threshold is deliberately close to centre. Flicking quickly does not return the
        // stick all the way home between flicks, so a high one left it latched and swallowed the second
        // and third movements of a fast scroll.
        float sy = LeftStickY();
        if (sy < 0.20f) _pStickUp = false;
        if (sy > -0.20f) _pStickDown = false;

        bool stickUp = live && sy > 0.50f && !_pStickUp;
        bool stickDown = live && sy < -0.50f && !_pStickDown;
        if (stickUp) _pStickUp = true;
        if (stickDown) _pStickDown = true;

        // Held directions repeat, so scrolling a long way is a hold rather than a burst of taps that
        // the poll has to be lucky enough to catch.
        bool repeatUp = live && (up || sy > 0.50f) && DueForRepeat(_upSince);
        bool repeatDown = live && (down || sy < -0.50f) && DueForRepeat(_downSince);

        Track(up || sy > 0.50f, ref _upSince);
        Track(down || sy < -0.50f, ref _downSince);

        if ((live && up && !_pUp) || stickUp || repeatUp) MoveSelection(-1);
        if ((live && down && !_pDown) || stickDown || repeatDown) MoveSelection(+1);
        if (live && a && !_pA) Decide(_options[_selected].Choice);

        // Acted on the press, like A and unlike B. B waits for the release because its press has to
        // not fall through to whatever is behind this window; Square is not a button any game menu
        // behind here treats as "back", so there is nothing to wait out.
        if (live && square && !_pSquare && Shortcut is { } quick) Decide(quick);

        // Backing out happens when B / Circle comes back *up*, not when it goes down — matching the PS
        // button, which is handled the same way one level up.
        //
        // Acting on the press left the release to land on whatever was behind this window: the game, or
        // Big Picture's own menu, which took it as a press of its own. Waiting for the release means the
        // whole button, down and up, belongs to the prompt.
        //
        // Armed only by a press seen while the prompt is live, so a button already held as this opened —
        // someone mashing B in a game — cannot dismiss it the moment they let go.
        if (live && b && !_pB) _bArmed = true;

        if (_bArmed && !b)
        {
            _bArmed = false;
            Decide(Choice.Stay);
        }

        _pUp = up; _pDown = down; _pA = a; _pB = b; _pSquare = square;
    }

    // When each direction went down, for the auto-repeat below. Null means it is not being held.
    private DateTime? _upSince, _downSince;
    private DateTime _lastRepeat = DateTime.MinValue;

    private static readonly TimeSpan RepeatAfter = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan RepeatEvery = TimeSpan.FromMilliseconds(110);

    private static void Track(bool held, ref DateTime? since)
    {
        if (held) since ??= DateTime.UtcNow;
        else since = null;
    }

    /// <summary>
    /// Whether a held direction should step again: once it has been down long enough to be a hold
    /// rather than a tap, and then at a steady rate.
    /// </summary>
    private bool DueForRepeat(DateTime? since)
    {
        if (since is not { } start) return false;

        var now = DateTime.UtcNow;
        if (now - start < RepeatAfter) return false;
        if (now - _lastRepeat < RepeatEvery) return false;

        _lastRepeat = now;
        return true;
    }

    /// <summary>
    /// Keep the foreground for as long as this window is up — the only lever there is for taking the
    /// pad off the game.
    ///
    /// Both Steam Input and Windows route a controller by the foreground window: Steam swaps to its
    /// desktop configuration when a non-game window is in front, and XInput on Windows 10+ is gated
    /// on focus. So holding the foreground is what actually stops the buttons reaching the game — and
    /// it is done with plain window calls, nothing injected or hooked, so there is nothing here for
    /// an anti-cheat to object to.
    ///
    /// A fullscreen game often grabs the foreground straight back, and while it has it the pad is its
    /// again. When that keeps happening we minimize it, which it cannot argue with, and put it back if
    /// the user chooses to keep playing.
    /// </summary>
    private void HoldForeground()
    {
        // Fast while something is still fighting for the foreground, then four times a second.
        //
        // Steam opens its overlay from the same button press that opens this, and it does not always
        // finish first: the prompt is shown after a fixed wait, and when the overlay lands *after* that
        // it takes the foreground from us — and with it the controller, which is why the overlay was
        // sometimes the thing being driven. A quarter-second re-assert leaves a quarter-second window
        // for exactly that, so while thefts are still happening this runs on every poll and takes the
        // foreground straight back.
        //
        // After that it settles down to four times a second. Brief and aggressive, then quiet — see
        // the note on OverlaySettling for what happens when it is not quiet.
        var now = DateTime.UtcNow;

        bool settling = now - _openedAt < OverlaySettling;

        if (!settling && now - _lastHold < HoldEvery) return;
        _lastHold = now;

        // Z-order is not the same thing as focus, and only focus was being asserted.
        //
        // Big Picture is a fullscreen topmost window. Taking the foreground from it moves input here,
        // but does nothing about what is drawn in front — so the prompt could hold focus and still sit
        // behind Big Picture, invisible, being answered blind. Re-stating topmost each poll puts it
        // above another topmost window that was raised after us. SWP_NOACTIVATE, because focus is the
        // job of the code below and doing both here would fight it.
        SetWindowPos(Handle, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

        var fg = GetForegroundWindow();
        if (fg == Handle) { _lostForeground = 0; return; }

        // Named once per prompt. "Something else has the foreground" was all the log could say, which
        // is why this took a round of guessing: knowing whether it is Steam's overlay, the game, or
        // Big Picture is the whole diagnosis.
        if (!_reportedTheft)
        {
            _reportedTheft = true;
            var cls = new System.Text.StringBuilder(96);
            GetClassName(fg, cls, cls.Capacity);
            Log.Info($"Session prompt lost the foreground to a '{cls}' window; taking it back.");
        }

        // Nothing is minimized here any more.
        //
        // There used to be a last resort: a game that kept taking the foreground back was minimized so
        // the prompt could be answered. It fired on ordinary fullscreen games that were doing nothing
        // wrong, throwing the player out of the very game they were being asked about — a worse outcome
        // than the problem it guarded against. Being topmost is what makes the prompt readable and
        // clickable; the foreground grab below is what takes the pad off the game. Where a game will
        // not give the foreground up, the honest answer is to leave it be.
        _lostForeground++;

        // allowSwitch stays false here. This runs a dozen times a second, and the Alt-Tab emulation it
        // would otherwise reach for asks Windows to minimize whatever it switches away from — which,
        // against a fullscreen Big Picture, is how resuming ended up minimizing it. The one-shot grab
        // in OnShown is allowed to use it; this loop is not.
        ForceForeground(Handle);
    }

    /// <summary>The left stick's vertical position, up-positive, from whichever pad is connected.</summary>
    private static float LeftStickY()
    {
        float y = 0f;
        if (PadShortcut.XInputStick(true, 0.15f) is { } xi) y = xi.Y;

        var (_, sonyY) = HidPad.SonyStick(true, 0.15f);
        if (MathF.Abs(sonyY) > MathF.Abs(y)) y = sonyY;

        return y;
    }

    private void MoveSelection(int delta)
    {
        _selected = (_selected + delta + _options.Length) % _options.Length;
        Redraw();
    }

    private void Decide(Choice choice)
    {
        if (_decided) return;
        _decided = true;
        _poll.Stop();

        // Off the screen first, so the pad is not left waiting on the slower work below.
        Hide();

        // Put back anything we minimized to get the pad's attention.
        // Put back whatever was in front, restoring it if it has been minimized in the meantime.
        //
        // A game in exclusive fullscreen is minimized by Windows itself the moment it loses the
        // foreground — that is DXGI's behaviour, not something this app does, and it cannot be avoided
        // while also taking the foreground away from the game, which is the whole point of this window.
        // Cyberpunk in fullscreen does exactly this. So rather than pretend it can be prevented, keeping
        // playing puts the game back: restored, then handed the foreground.
        // Never at the desk. The window "behind the prompt" there is a game that was minimized on
        // purpose seconds earlier, and restoring it is the opposite of what backing out means.
        if (choice == Choice.Stay && !AtTheDesk)
        {
            var back = _minimizedGame != IntPtr.Zero ? _minimizedGame : _previousForeground;

            if (back != IntPtr.Zero && IsIconic(back))
            {
                Log.Info("The window behind the prompt was minimized while it was up — most likely an "
                       + "exclusive-fullscreen game losing the foreground; restoring it.");
                ShowWindow(back, SW_RESTORE);
            }

            _minimizedGame = IntPtr.Zero;
        }

        Log.Info($"Session-end prompt: chose {choice}.");

        // Gone completely before the foreground is handed back.
        //
        // Order matters here and getting it wrong is what kept minimizing Big Picture. Closing a
        // topmost window makes Windows pick a new foreground and reshuffle the z-order, and doing that
        // *after* restoring Big Picture undid the restore — sometimes leaving a fullscreen shell that
        // had just been asked for the foreground minimized instead. So the window is destroyed first,
        // and only then is anything told to come forward.
        Close();

        if (choice == Choice.Stay && !AtTheDesk)
        {
            if (RestoreFocus is not null) RestoreFocus();
            else if (_previousForeground != IntPtr.Zero) ForceForeground(_previousForeground);
        }

        Chosen?.Invoke(choice);
    }

    /// <summary>Whether we are currently holding the cursor hidden, so Show is balanced against Hide.</summary>
    private bool _cursorHidden;

    /// <summary>Where the pointer sat when this opened, to tell a real move from the arrival event.</summary>
    private Point _cursorAtShow;

    /// <summary>Give the cursor back. Safe to call more than once; only ever undoes our own Hide.</summary>
    private void RevealCursor()
    {
        if (!_cursorHidden) return;

        _cursorHidden = false;
        Cursor.Show();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Showing the window under a stationary pointer produces a mouse-move of its own, so the
        // pointer only counts as used once it has actually gone somewhere.
        if (_cursorHidden && MousePosition != _cursorAtShow) RevealCursor();

        int over = -1;
        for (int i = 0; i < _optionRects.Length; i++)
            if (_optionRects[i].Contains(e.Location)) { over = i; break; }

        if (over != _hover) { _hover = over; Cursor = over >= 0 ? Cursors.Hand : Cursors.Default; Redraw(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        for (int i = 0; i < _optionRects.Length; i++)
            if (_optionRects[i].Contains(e.Location)) { Decide(_options[i].Choice); return; }
        base.OnMouseDown(e);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Up: MoveSelection(-1); return true;
            case Keys.Down: MoveSelection(+1); return true;
            case Keys.Enter: Decide(_options[_selected].Choice); return true;
            case Keys.Escape: Decide(Choice.Stay); return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _poll.Stop();
        _shimmer.Stop();
        RevealCursor();   // never leave the machine without a pointer

        // The egg cannot outlive the window that summoned it. It has its own timer and its own backstop,
        // but this is the one that matters: whatever else goes wrong, closing the prompt closes it.
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            RevealCursor();   // belt and braces: a hidden cursor outliving this window is unrecoverable
            _poll.Dispose();
            _shimmer.Dispose();
            _logo?.Dispose();
            _nameFont?.Dispose();
            _titleFont?.Dispose();
            _bodyFont?.Dispose();
            _optionFont?.Dispose();
            _hintFont?.Dispose();
            _detailFont?.Dispose();
            ReleaseSurface();
        }
        base.Dispose(disposing);
    }

    // ---- paint ----

    /// <summary>
    /// Soft coloured lights drifting behind the panel.
    ///
    /// Drawn straight after the backdrop and before anything that has to be read, so they are scenery
    /// rather than something competing with the words. That is also why the alpha is as low as it is:
    /// this window exists to be answered, and a background that draws the eye is a background that
    /// slows the answer down.
    ///
    /// Clipped to the rounded panel, or the corners square off — a radial glow does not know the
    /// window has rounded corners, and a bright quarter-circle sitting outside the border is the first
    /// thing anyone would notice.
    /// </summary>
    private void PaintLights(Graphics g)
    {
        // Where each light sits, how far it wanders, how fast, and its colour. Fractions of the window
        // rather than pixels, so the arrangement survives every scale this window is built at.
        (float X, float Y, float Drift, float Speed, float Turn, Color Tint)[] lights =
        [
            (0.14f, 0.22f, 0.10f, 1.00f, 0.00f, Theme.Accent),
            (0.86f, 0.16f, 0.08f, 0.70f, 0.35f, Theme.Info),
            (0.22f, 0.84f, 0.09f, 0.85f, 0.60f, Theme.AccentHi),
            (0.82f, 0.80f, 0.11f, 0.55f, 0.85f, Theme.Good),
        ];

        float radius = Math.Max(Width, Height) * 0.42f;

        // No clip. The window's own alpha does the shaping now — see CarveCorners.
        //
        // [BUG] These were clipped to a rounded path, and a GDI+ path clip is a region: hard-edged, one
        // bit per pixel, no antialiasing whatever the SmoothingMode says. So every light that reached a
        // corner was cut off with a visible staircase, right on the border where the eye already is.
        // That is the jaggedness — not the corner itself, which is smooth, but the bright thing being
        // sliced by a mask that is not. Letting them run past the edge and carving the corner out of the
        // alpha channel afterwards cuts them with coverage instead of with a yes-or-no.
        foreach (var (fx, fy, drift, speed, turn, tint) in lights)
        {
            double angle = (_phase * speed + turn) * Math.PI * 2;

            float cx = (fx + (float)Math.Cos(angle) * drift) * Width;
            float cy = (fy + (float)Math.Sin(angle * 0.8) * drift) * Height;

            var area = new RectangleF(cx - radius, cy - radius, radius * 2, radius * 2);

            using var blob = new GraphicsPath();
            blob.AddEllipse(area);

            using var brush = new PathGradientBrush(blob)
            {
                CenterPoint = new PointF(cx, cy),
                CenterColor = Color.FromArgb(46, tint),
                SurroundColors = [Color.FromArgb(0, tint)],
            };

            g.FillPath(brush, blob);
        }
    }

    /// <summary>
    /// Soft lights travelling around the border.
    ///
    /// Drawn as glows placed on the path, not as a stroke along it. Stroking was the first attempt and
    /// it looked wrong for a reason worth keeping: a rounded rectangle is mostly four long straight
    /// runs, so a lit stretch of it is a bright bar down one side rather than a light going past. A
    /// glow has no direction and no ends, so it reads the same at a corner as it does mid-edge.
    /// </summary>
    private void PaintEdgeLights(Graphics g)
    {
        BuildEdge();
        if (_edge.Length < 2 || _edgeLength <= 0) return;

        // Big on purpose. A small glow sitting on a line reads as a bead threaded onto it; a large
        // one reads as light coming from somewhere, which is the thing being asked for.
        float radius = Math.Max(Width, Height) * 0.30f;

        (float Lead, Color Tint, int Alpha)[] lights =
        [
            (0.00f, Theme.AccentHi, 85),
            (0.34f, Theme.Info, 65),
            (0.67f, Theme.Accent, 75),
        ];

        var mode = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Not clipped either, for the reason given in PaintLights: a path clip is a hard-edged region,
        // and these glows sit *on* the border, so they were the worst affected by it. Anything that runs
        // past the edge is removed by the corner alpha, smoothly.

        foreach (var (lead, tint, alpha) in lights)
        {
            var at = PointAt(_phase + lead);
            var area = new RectangleF(at.X - radius, at.Y - radius, radius * 2, radius * 2);

            using var blob = new GraphicsPath();
            blob.AddEllipse(area);

            using var brush = new PathGradientBrush(blob)
            {
                CenterPoint = at,
                CenterColor = Color.FromArgb(alpha, tint),
                SurroundColors = [Color.FromArgb(0, tint)],
            };

            g.FillPath(brush, blob);
        }

        g.SmoothingMode = mode;
    }

    /// <summary>The point a given fraction of the way around the border. Wraps.</summary>
    private PointF PointAt(float t)
    {
        t -= (float)Math.Floor(t);

        float want = t * _edgeLength;

        // Binary search the cumulative lengths: the border flattens to several hundred points and this
        // is asked roughly ninety times a frame.
        int lo = 0, hi = _edgeAt.Length - 1;

        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (_edgeAt[mid] < want) lo = mid + 1; else hi = mid;
        }

        int at = Math.Clamp(lo, 1, _edge.Length - 1);

        float spanStart = _edgeAt[at - 1];
        float span = Math.Max(0.0001f, _edgeAt[at] - spanStart);
        float within = Math.Clamp((want - spanStart) / span, 0f, 1f);

        var a = _edge[at - 1];
        var b = _edge[at];

        return new PointF(a.X + (b.X - a.X) * within, a.Y + (b.Y - a.Y) * within);
    }

    /// <summary>Flatten and measure the border, once per window size.</summary>
    private void BuildEdge()
    {
        if (_edgeFor == Size && _edge.Length > 0) return;
        _edgeFor = Size;

        try
        {
            float inset = Math.Max(1f, S(2) / 2f);

            using var path = Theme.Rounded(
                new RectangleF(inset, inset, Width - inset * 2, Height - inset * 2), _corner);

            path.Flatten(null, 0.4f);

            var points = path.PathPoints;
            if (points.Length < 2) { _edge = []; return; }

            // Closed explicitly: Flatten does not repeat the first point, and without it the light
            // jumps the gap between the last corner and the start instead of rounding it.
            _edge = [.. points, points[0]];
            _edgeAt = new float[_edge.Length];

            float run = 0;
            for (int i = 1; i < _edge.Length; i++)
            {
                float dx = _edge[i].X - _edge[i - 1].X;
                float dy = _edge[i].Y - _edge[i - 1].Y;

                run += (float)Math.Sqrt(dx * dx + dy * dy);
                _edgeAt[i] = run;
            }

            _edgeLength = run;
        }
        catch (Exception ex)
        {
            // A decoration must never be the reason this window fails to draw.
            Log.Warn($"Prompt edge lights unavailable: {ex.Message}");
            _edge = [];
            _edgeLength = 0;
        }
    }

    /// <summary>
    /// Nothing is painted through WM_PAINT. See <see cref="Redraw"/>.
    ///
    /// A layered window's contents come from UpdateLayeredWindow, not from a paint handler — anything
    /// drawn into e.Graphics here is simply discarded. Redraw is called instead from the places that
    /// used to invalidate.
    /// </summary>
    protected override void OnPaint(PaintEventArgs e) => Redraw();

    /// <summary>Suppressed: the backdrop is part of the frame Redraw composes.</summary>
    protected override void OnPaintBackground(PaintEventArgs e) { }

    /// <summary>Draw the whole window into <paramref name="g"/>, opaquely. Corners are shaped later.</summary>
    private void PaintFrame(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // Filled as a rectangle, deliberately, even though the window is not one.
        //
        // The corner curve is applied afterwards, as alpha, by CarveCorners. Filling a rounded path
        // here instead would put the antialiased edge into the colour channels — and the text on top of
        // it is drawn with GDI, which writes no alpha at all, so the two could not be reconciled
        // afterwards. Paint everything solid, then cut the shape out of it.
        using (var backdrop = new LinearGradientBrush(
                   new Rectangle(-1, -1, Width + 2, Height + 2),
                   BackdropFrom, BackdropTo,
                   LinearGradientMode.ForwardDiagonal))
            g.FillRectangle(backdrop, 0, 0, Width, Height);

        PaintLights(g);

        // Radius reduced by the inset, so this curve and the alpha mask's are concentric.
        //
        // The mask is drawn at (0,0,W,H) with radius _corner; this was drawn half a pixel inside it at
        // the *same* radius, which is a different curve — the two drifted apart around each corner, so
        // the mask ate into the outline by a varying amount and left it looking thin and broken exactly
        // where it turns. Insetting a rounded rectangle by d means dropping the radius by d.
        Theme.DrawRounded(g, new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), _corner - 0.5f, Theme.Line);

        PaintEdgeLights(g);

        const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.NoPrefix | TextFormatFlags.WordEllipsis;
        const TextFormatFlags LeftMid = Left | TextFormatFlags.VerticalCenter;

        // Header: the CS badge and the app name, like a notification.
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_logo, new Rectangle(_pad, _pad, _logoSize, _logoSize));
        TextRenderer.DrawText(g, AppInfo.Name, _nameFont,
                              new Rectangle(_pad + _logoSize + S(10), _pad, Width - _pad * 2 - _logoSize - S(10), _logoSize),
                              Theme.Text, LeftMid);

        int titleY = _headerBottom + S(16);
        int titleH = TextRenderer.MeasureText(g, _title, _titleFont).Height;
        TextRenderer.DrawText(g, _title, _titleFont,
                              new Rectangle(_pad, titleY, Width - _pad * 2, titleH), Theme.Text, Left);

        int bodyY = titleY + titleH + S(6);
        int bodyH = TextRenderer.MeasureText(g, _body, _bodyFont).Height;
        TextRenderer.DrawText(g, _body, _bodyFont,
                              new Rectangle(_pad, bodyY, Width - _pad * 2, bodyH), Theme.TextDim, Left);

        for (int i = 0; i < _options.Length; i++)
        {
            var r = _optionRects[i];
            bool selected = i == _selected;
            bool hover = i == _hover;

            var accent = _options[i].Accent;
            var fill = selected ? Theme.Mix(accent, Theme.Surface, 0.78f)
                     : hover ? Theme.SurfaceHi
                     : Theme.Input;
            Theme.FillRounded(g, r, _radius, fill);
            Theme.DrawRounded(g, r, _radius, selected ? accent : Theme.InputLine, selected ? 2f : 1f);

            if (selected)
            {
                int d = S(9);
                using var dot = new SolidBrush(accent);
                g.FillEllipse(dot, r.Left + S(14), r.Top + (r.Height - d) / 2, d, d);
            }

            int left = r.Left + S(34);
            int wide = r.Width - S(44);
            string detail = _options[i].Detail;

            if (detail.Length == 0)
            {
                TextRenderer.DrawText(g, _options[i].Text, _optionFont,
                                      new Rectangle(left, r.Top, wide, r.Height), Theme.Text, LeftMid);
            }
            else
            {
                // Both lines measured and centred as a block, so the pair sits on the row's middle the
                // way a single line does. Stacking from the top instead leaves the group riding high in
                // rows whose consequence happens to be short.
                int actionH = TextRenderer.MeasureText(g, _options[i].Text, _optionFont).Height;
                int detailH = TextRenderer.MeasureText(g, detail, _detailFont).Height;

                int top = r.Top + (r.Height - (actionH + S(3) + detailH)) / 2;

                TextRenderer.DrawText(g, _options[i].Text, _optionFont,
                                      new Rectangle(left, top, wide, actionH), Theme.Text, Left);

                // One step down from the action, and no further.
                //
                // This was TextFaint on the unselected rows, which is about 2.6:1 against the row behind
                // it — under the 4.5:1 body text is expected to reach, and it showed: the line explaining
                // what an answer costs was the hardest thing on the prompt to read. The hierarchy is
                // carried by the smaller font, which is enough; it does not also need to be carried by
                // making the words hard to make out.
                TextRenderer.DrawText(g, detail, _detailFont,
                                      new Rectangle(left, top + actionH + S(3), wide, detailH),
                                      Theme.TextDim, Left);
            }
        }

        DrawHint(g);
    }

    /// <summary>Each hint: the glyphs to draw side by side, then its label.</summary>
    private (PadControl[] Glyphs, string Label)[] HintParts()
    {
        if (!PadHints) return [];

        // No "Move" hint.
        //
        // A vertical list with one row visibly selected already says which way the stick goes; nobody
        // holding a pad needs to be told that up and down move a highlight. It was also the widest of
        // the hints — two glyphs to everyone else's one — so dropping it is what lets the rest sit on
        // a single line instead of wrapping onto a second.
        //
        // The hints that remain each name an action that is *not* guessable from looking at the list:
        // which button commits, which one backs out, and what the extra shortcut does.
        var listed = new List<(PadControl[] Glyphs, string Label)>
        {
            (new[] { PadControl.FaceDown }, "Choose"),
            (new[] { PadControl.FaceRight }, KeepHint),
        };

        if (Shortcut is not null && ShortcutHint.Length > 0)
            listed.Add((new[] { PadControl.FaceLeft }, ShortcutHint));

        return [.. listed];
    }

    /// <summary>
    /// How many rows the hints need at the given width.
    ///
    /// Worked out during layout, before there is a window to measure against, so the measuring is done
    /// on a screen device context. Without this the row was laid out as one line whatever it contained
    /// and simply ran off the right-hand edge once there were four hints instead of three — the width
    /// is fixed, so nothing pushed back.
    /// </summary>
    private int HintRows(int available)
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);

            PadGlyphs.UseScale(_scale);

            const int GlyphGap = 3;
            int gap = S(16);

            // Measured for whichever pad family needs the most room, not for the one connected now.
            //
            // The family is not settled at layout time — it starts as Xbox and switches the moment a
            // DualSense answers — and PlayStation glyphs are the wider of the two. Measuring the
            // current family meant the window was sized for three hints on one line and then painted
            // four on two, into a strip only tall enough for one. That is the overlap.
            int worst = 1;

            foreach (var family in new[] { PadFamily.Xbox, PadFamily.PlayStation })
            {
                int rows = 1, line = 0;

                foreach (var (glyphs, label) in HintParts())
                {
                    int w = 0;
                    foreach (var gl in glyphs) w += PadGlyphs.Measure(g, family, [new PadPress(gl)]) + GlyphGap;
                    w += TextRenderer.MeasureText(g, label, _hintFont, Size.Empty, TextFormatFlags.NoPrefix).Width + S(4);

                    int next = line == 0 ? w : line + gap + w;

                    if (next > available && line > 0) { rows++; line = w; }
                    else line = next;
                }

                worst = Math.Max(worst, rows);
            }

            return worst;
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>The button hints, drawn in the symbols of the pad in the user's hands.</summary>
    private void DrawHint(Graphics g)
    {
        if (!PadHints) return;

        // Raised from TextFaint. That colour is for something you are meant to skim past, and this row
        // is the only place the window says which button does what — the one line a controller user has
        // to be able to read from the sofa.
        var ink = Theme.TextDim;

        // The glyphs are drawn from fixed pixel sizes, not from a font, so they are the one part of
        // this window that does not grow with everything else unless told to. On a 4K television that
        // left the button hints at their desk-monitor size inside a window twice as large.
        PadGlyphs.UseScale(_scale);

        var parts = HintParts();

        const int GlyphGap = 3;
        int gap = S(16);

        int Width(PadControl[] glyphs, string label)
        {
            int w = 0;
            foreach (var gl in glyphs) w += PadGlyphs.Measure(g, _family, new[] { new PadPress(gl) }) + GlyphGap;
            w += TextRenderer.MeasureText(g, label, _hintFont, Size, TextFormatFlags.NoPrefix).Width + S(4);
            return w;
        }

        // Packed into rows first, then drawn, so each row can be centred on its own.
        //
        // It used to lay every hint on one line and centre that, with the overflow clamped to zero —
        // which does not mean "it fits", it means the excess runs off the right-hand edge. The window
        // is a fixed width, so nothing was ever going to push back; a fourth hint simply vanished
        // halfway through its own label.
var rows = new List<List<(PadControl[] Glyphs, string Label, int Span)>>();
        rows.Add([]);

        int used = 0;

        foreach (var (glyphs, label) in parts)
        {
            int w = Width(glyphs, label);
            int next = used == 0 ? w : used + gap + w;

            if (next > _hintRect.Width && used > 0)
            {
                rows.Add([]);
                used = w;
            }
            else used = next;

            rows[^1].Add((glyphs, label, w));
        }

        // Fixed, not the strip divided by the row count. Dividing means an unexpected extra row
        // silently halves the spacing and the two draw through each other, which hides the fault
        // instead of showing it; a fixed height keeps every row legible whatever happens above.
        int rowHeight = S(24);

        for (int r = 0; r < rows.Count; r++)
        {
            int total = 0;
            foreach (var part in rows[r]) total += part.Span;
            total += gap * (rows[r].Count - 1);

            int x = _hintRect.Left + Math.Max(0, (_hintRect.Width - total) / 2);
            int cy = _hintRect.Top + rowHeight * r + rowHeight / 2;

            foreach (var (glyphs, label, _) in rows[r])
            {
                foreach (var gl in glyphs)
                {
                    int gw = PadGlyphs.Measure(g, _family, [new PadPress(gl)]);
                    PadGlyphs.Draw(g, new Rectangle(x, cy - S(11), gw, S(22)), _family, [new PadPress(gl)], ink);
                    x += gw + GlyphGap;
                }

                int lw = TextRenderer.MeasureText(g, label, _hintFont, Size, TextFormatFlags.NoPrefix).Width;

                TextRenderer.DrawText(g, label, _hintFont,
                                      new Rectangle(x + S(2), cy - rowHeight / 2, lw + S(4), rowHeight),
                                      ink, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);

                x += lw + S(4) + gap;
            }
        }
    }

    // ---- foreground + DPI ----

    /// <summary>
    /// Put a window in front, working around the foreground lock.
    ///
    /// Windows refuses SetForegroundWindow from a process that does not already own the foreground —
    /// a tray app never does — so on its own it silently fails and only flashes the taskbar. Attaching
    /// to the current foreground thread makes Windows treat the two as one input queue and allows it,
    /// and SwitchToThisWindow is the backstop for the cases where it still will not (a game running at
    /// a different elevation, where AttachThreadInput is refused).
    ///
    /// All ordinary window management. Nothing is injected, hooked or written into another process.
    /// </summary>
    /// <param name="allowSwitch">
    /// Whether the SwitchToThisWindow backstop may be used. Off by default because it emulates Alt-Tab,
    /// and Alt-Tabbing away from a fullscreen window asks Windows to minimize it — fine once, on the
    /// way in, and actively destructive from a loop.
    /// </param>
    private static void ForceForeground(IntPtr hwnd, bool allowSwitch = false)
    {
        try
        {
            AllowSetForegroundWindow(ASFW_ANY);

            uint fgThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
            uint us = GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != us && AttachThreadInput(us, fgThread, true);

            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetActiveWindow(hwnd);
            SetFocus(hwnd);

            if (attached) AttachThreadInput(us, fgThread, false);

            // Still not ours: the last resort, which Windows honours where the above is refused.
            if (allowSwitch && GetForegroundWindow() != hwnd) SwitchToThisWindow(hwnd, false);
        }
        catch { /* best effort */ }
    }

    /// <summary>
    /// How much to enlarge the prompt: the monitor's own scaling, or an enlargement for panels wider
    /// than 2K, whichever asks for more. Unchanged at 2K and below. See <see cref="UiScale"/>.
    /// </summary>
    private const int WS_EX_TOPMOST = 0x00000008;
    private const uint ASFW_ANY = 0xFFFFFFFF;
    private const int SW_MINIMIZE = 6;
    private const int SW_RESTORE = 9;

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const int WS_EX_LAYERED = 0x00080000;
    private const byte AC_SRC_OVER = 0x00, AC_SRC_ALPHA = 0x01;
    private const uint ULW_ALPHA = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr screen, ref POINT position,
                                                   ref SIZE size, IntPtr source, ref POINT origin,
                                                   int key, ref BLENDFUNCTION blend, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize, biWidth, biHeight;
        public short biPlanes, biBitCount;
        public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
    }

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER header, uint usage,
                                                  out IntPtr bits, IntPtr section, uint offset);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] private static extern bool AllowSetForegroundWindow(uint pid);
    [DllImport("user32.dll")] private static extern void SwitchToThisWindow(IntPtr hwnd, bool altTab);
    [DllImport("user32.dll")] private static extern IntPtr SetActiveWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr SetFocus(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder text, int count);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
