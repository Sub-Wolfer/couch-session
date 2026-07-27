using System.Runtime.InteropServices;

namespace CouchMode.Input;

/// <summary>
/// A controller button combination, and the polling that notices it being held.
///
/// Two ways of reading a pad, because no single one covers everything.
///
/// XInput is tried first. It normalises every Xbox-style controller to the same button layout,
/// which is why the names it gives can be trusted. But it only sees devices presented as Xbox
/// pads, so a DualSense — or a DualSense through a passthrough dongle — is invisible to it.
///
/// HidPad covers the rest by reading the HID report directly. Buttons there are numbered rather
/// than named, and the numbering is the device's own, so a combination recorded through one
/// backend is meaningless to the other. That is why the backend is stored alongside the mask
/// rather than inferred: the same bits mean different buttons depending on where they came from,
/// and quietly reading them with the wrong one would bind a shortcut to the wrong buttons.
///
/// Combinations, not single buttons, on purpose. A single button would fire in the middle of a
/// game — this is meant to be pressed deliberately, so it wants two or more.
/// </summary>
public sealed class PadShortcut : IDisposable
{
    private readonly Action _pressed;
    private readonly System.Windows.Forms.Timer _poll;

    private int _combo;
    private bool _wasDown;

    /// <summary>
    /// Marks a combination as having come from HID rather than XInput.
    ///
    /// Kept in the stored value itself so an old config cannot be misread: anything saved before
    /// this existed has the bit clear and is XInput, which is what it was.
    /// </summary>
    public const int HidFlag = 1 << 30;

    public static bool IsHid(int combo) => (combo & HidFlag) != 0;
    public static uint MaskOf(int combo) => (uint)(combo & ~HidFlag);

    /// <summary>How often the pads are read. Fast enough to feel instant, slow enough to be free.</summary>
    private const int PollMs = 90;

    public PadShortcut(Action pressed)
    {
        _pressed = pressed;

        _poll = new System.Windows.Forms.Timer { Interval = PollMs };
        _poll.Tick += (_, _) => Check();
    }

    /// <summary>
    /// Buttons held right now on whichever pad has the most of them, with the backend they came
    /// from, or 0 if nothing is pressed anywhere.
    ///
    /// For recording a shortcut, where one answer is wanted and it should be the pad the person
    /// is actually holding. Matching an existing shortcut uses <see cref="HeldAll"/> instead,
    /// because there any pad may be the right one.
    /// </summary>
    public static PadReading Held()
    {
        var all = HeldAll();
        if (all.Count == 0) return default;

        // The fullest reading, not the first. With two pads connected the first is whichever
        // Windows happened to enumerate first, and picking it meant a combination pressed on the
        // other one was thrown away — see HeldAll.
        var best = all[0];

        foreach (var held in all)
            if (System.Numerics.BitOperations.PopCount(MaskOf(held.Combo)) >
                System.Numerics.BitOperations.PopCount(MaskOf(best.Combo)))
                best = held;

        return best;
    }

    /// <summary>
    /// Every pad with something pressed, one entry each.
    ///
    /// This exists because returning a single reading was wrong in two ways at once, and both
    /// were reported as separate faults.
    ///
    /// The loop used to stop at the first pad with any button down. With two controllers
    /// connected that is whichever Windows enumerated first, so the second pad could not work a
    /// shortcut while the first had anything held — and a pad resting with a trigger a fraction
    /// off zero counts as held. The same stop also hid an Xbox pad behind a DualSense: the
    /// PlayStation pad is the one that reports constantly, having a gyroscope and a touchpad that
    /// are never quite still, so it answered first every time and the Xbox pad was never read.
    ///
    /// Asking every device and letting the caller decide costs one more pass over at most four
    /// XInput slots and a short dictionary, which is nothing at a 70ms poll.
    /// </summary>
    public static IReadOnlyList<PadReading> HeldAll()
    {
        var all = new List<PadReading>();

        for (uint pad = 0; pad < 4; pad++)
        {
            if (!TryGetState(pad, out var state)) continue;

            int held = state.Gamepad.Buttons | TriggersOf(state.Gamepad);

            // Vendor and product are left at zero: XInput normalises every pad to an Xbox one and
            // does not say what the hardware really is, which is exactly why its readings are
            // interpreted as Xbox regardless.
            if (held != 0) all.Add(new PadReading(held, 0, 0));
        }

        foreach (var hid in HidPad.HeldAll())
            if (hid.Buttons != 0)
                all.Add(new PadReading((int)hid.Buttons | HidFlag, hid.Vendor, hid.Product));

        return all;
    }

    /// <summary>
    /// The pointer vector from the first XInput pad's sticks, honouring which sticks to use.
    ///
    /// Up-positive and right-positive, dead-zoned, from the larger of the enabled sticks — see
    /// PadSticks. Null when no XInput pad is present, so the caller can try a DualSense over HID
    /// instead. A disabled stick is passed in as zero and so cannot win.
    /// </summary>
    public static (float X, float Y)? XInputStick(bool leftStick, float dead)
    {
        bool any = false;
        (float X, float Y) best = (0f, 0f);
        float bestMagnitude = -1f;

        // Every pad, not the first one. With two controllers attached the first slot is very often the
        // one sitting on the table, and taking its answer meant the stick actually being pushed was
        // read as centred on alternate polls — which is what made the pointer stutter rather than move.
        // The pad being pushed hardest is the one in someone's hands.
        for (uint pad = 0; pad < 4; pad++)
        {
            if (!TryGetState(pad, out var g)) continue;

            any = true;

            float x = (leftStick ? g.Gamepad.ThumbLX : g.Gamepad.ThumbRX) / 32767f;
            float y = (leftStick ? g.Gamepad.ThumbLY : g.Gamepad.ThumbRY) / 32767f;

            var applied = PadSticks.Apply(x, y, dead);
            float magnitude = applied.X * applied.X + applied.Y * applied.Y;

            if (magnitude > bestMagnitude) { bestMagnitude = magnitude; best = applied; }
        }

        return any ? best : null;
    }

    /// <summary>
    /// Whether a given control is held right now on any connected pad.
    ///
    /// Position-based, like the shortcut matching: an L1 chosen as the mouse modifier is held when
    /// L1 is down on a DualSense or LB is down on an Xbox pad, since both decode to the same
    /// PadControl. That is what lets one saved choice work across controller types.
    /// </summary>
    public static bool IsControlHeld(PadControl control)
    {
        foreach (var reading in HeldAll())
            if (ControlsOf(reading).Contains(control))
                return true;

        return false;
    }

    /// <summary>
    /// The symbols to draw in, given a controller is actually connected.
    ///
    /// A shortcut is stored as positions on a pad, so it is equally valid on either family — but
    /// it should be shown in the symbols of the controller in the user's hands, not the one it
    /// happened to be recorded on. With nothing connected the recorded family stands, since that
    /// is the last thing known to be true.
    /// </summary>
    public static PadFamily FamilyInUse(PadFamily recorded)
    {
        var (vendor, product) = HidPad.FirstIds();
        bool hid = vendor != 0;

        bool xinput = false;
        for (uint pad = 0; pad < 4; pad++)
            if (TryGetState(pad, out _)) { xinput = true; break; }

        // Both kinds plugged in at once: answer with whichever was touched last, so picking up the
        // other controller redraws the buttons as that pad's symbols. HID used to win unconditionally,
        // which left an Xbox pad in your hands being described in circles and triangles.
        if (hid && xinput)
            return HidPad.LastActivityUtc >= LastXInputActivityUtc
                 ? HidPad.FamilyOf(vendor, product)
                 : PadFamily.Xbox;

        if (hid) return HidPad.FamilyOf(vendor, product);

        // XInput seeing a pad means an Xbox-style one, which is what it presents everything as.
        if (xinput) return PadFamily.Xbox;

        return recorded;
    }

    /// <summary>
    /// When an XInput pad was last actually used, as opposed to merely present. The HID side keeps the
    /// same clock, and comparing the two is what lets the UI follow whichever controller was picked up.
    /// </summary>
    public static DateTime LastXInputActivityUtc { get; private set; } = DateTime.MinValue;

    /// <summary>
    /// Note real use of an XInput pad.
    ///
    /// Deliberately not the packet number, which ticks over on stick drift alone: a pad left on the
    /// table would then look permanently "in use" and the glyphs would sit on it forever. Buttons, a
    /// stick pushed well past any plausible drift, or a trigger meaningfully pulled — those are people.
    /// </summary>
    private static void NoteXInputUse(in XInputState state)
    {
        const short Pushed = 12000;   // ~37% deflection; far beyond drift, well short of a full push
        const byte Pulled = 60;

        var g = state.Gamepad;

        bool used = g.Buttons != 0
                 || Math.Abs((int)g.ThumbLX) > Pushed || Math.Abs((int)g.ThumbLY) > Pushed
                 || Math.Abs((int)g.ThumbRX) > Pushed || Math.Abs((int)g.ThumbRY) > Pushed
                 || g.LeftTrigger > Pulled || g.RightTrigger > Pulled;

        if (used) LastXInputActivityUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Whether XInput can see any controller at all.
    ///
    /// Worth asking separately from Held, because "no buttons pressed" and "no pad XInput can
    /// see" look identical from a poll and mean completely different things to the user. A
    /// DualSense on a passthrough dongle is a HID device, not an XInput one, so it reports
    /// nothing here however many buttons are held — and without this the box sits saying "hold
    /// the buttons" forever while the user does exactly that.
    /// </summary>
    public static bool AnyConnected()
    {
        for (uint pad = 0; pad < 4; pad++)
            if (TryGetState(pad, out _)) return true;

        return HidPad.AnyConnected();
    }

    /// <summary>
    /// The triggers, as two more buttons.
    ///
    /// XInput does not report LT and RT in its button mask at all — they are separate analogue
    /// bytes, because on a pad they are analogue. So a shortcut using a trigger could be pressed
    /// but never recorded: the button mask stayed at zero however hard the trigger was pulled,
    /// and Xbox triggers simply would not map. PlayStation pads were unaffected only because HID
    /// reports L2 and R2 as ordinary buttons alongside their analogue axes, so they arrived
    /// through the button path by chance rather than by design.
    ///
    /// Folded into the mask above the sixteen XInput uses, in the same spirit as the D-pad hat:
    /// to the person pulling it, a trigger is a button.
    /// </summary>
    private static int TriggersOf(XInputGamepad pad)
    {
        int held = 0;

        if (pad.LeftTrigger >= TriggerPull) held |= TriggerLeft;
        if (pad.RightTrigger >= TriggerPull) held |= TriggerRight;

        return held;
    }

    /// <summary>
    /// Bits 16 and 17, clear of XInput's own sixteen and clear of the HID flag at bit 30.
    ///
    /// Deliberately not 0x0400 or 0x0800, XInput's two unused button bits: 0x0400 is the Guide
    /// button on every pad that reports one, documented or not.
    /// </summary>
    public const int TriggerLeft = 1 << 16;
    public const int TriggerRight = 1 << 17;

    /// <summary>
    /// How far a trigger comes in as pressed. A quarter of its travel.
    ///
    /// Higher than the 30 of 255 Windows suggests, on purpose. A trigger resting fractionally off
    /// zero — which is ordinary wear, not a fault — would read as permanently held, and a control
    /// that is always down does not merely add itself to a combination: it stops the combination
    /// ever being released, and a shortcut is only recorded once everything comes back up. That
    /// is exactly how a mis-read D-pad made Xbox pads unmappable, and it is not worth risking
    /// twice for the sake of registering a lighter pull.
    /// </summary>
    private const byte TriggerPull = 64;

    /// <summary>
    /// Watch for this combination, as recorded on the given controller. Zero stops watching.
    ///
    /// The vendor and product matter because a HID mask means nothing without them: button 3 is
    /// whatever the third button happens to be on that particular pad. They are what let the
    /// combination be understood as *which buttons* rather than which bits, and so be matched on a
    /// completely different controller later.
    /// </summary>
    public void Apply(int combo, ushort vendor = 0, ushort product = 0)
    {
        _combo = combo;
        _wasDown = false;

        // Worked out once here rather than on every poll, ninety milliseconds apart.
        _wanted = combo == 0 ? [] : ControlsOf(new PadReading(combo, vendor, product));

        if (_combo == 0) _poll.Stop();
        else _poll.Start();
    }

    /// <summary>What the watched combination means, as positions on a pad.</summary>
    private IReadOnlyList<PadControl> _wanted = [];

    /// <summary>Stop watching without forgetting the combination — used while it is being changed.</summary>
    public void Pause() => _poll.Stop();

    public void Resume()
    {
        _wasDown = false;
        if (_combo != 0) _poll.Start();
    }

    private void Check()
    {
        // Compared as positions on a pad, not as raw bits.
        //
        // The bits are meaningless across controllers — a HID mask is that device's own numbering,
        // and an XInput mask is a different scheme again — so a shortcut used to work only on the
        // exact controller it was recorded on. Decoding both sides to the control each bit stands
        // for makes the comparison portable: Triangle and Y are both FaceUp, so a combination set
        // on a DualSense fires on an Xbox pad from the buttons in the same places.
        //
        // Any pad may satisfy it, and each is judged on its own. Merging the pads into one set
        // would be worse than the single reading this replaced: two controllers each holding half
        // of the combination would fire it between them, which is not something anybody pressed.
        bool down = false;

        if (_wanted.Count > 0)
        {
            foreach (var reading in HeldAll())
            {
                var got = ControlsOf(reading);

                bool all = true;

                foreach (var control in _wanted)
                {
                    if (got.Contains(control)) continue;
                    all = false;
                    break;
                }

                if (!all) continue;

                down = true;
                break;
            }
        }

        // Fired on the press, not held. Without this it would repeat every poll for as long as
        // the buttons are down, which for a session toggle means flipping in and out of couch
        // mode ten times a second.
        if (down && !_wasDown)
        {
            try { _pressed(); }
            catch (Exception ex) { Log.Error("The controller shortcut action failed", ex); }
        }

        _wasDown = down;
    }

    /// <summary>
    /// The controls a reading stands for, in a fixed order.
    ///
    /// The single place a mask is turned into meaning, used for matching and for drawing alike so
    /// the two can never disagree about what a shortcut is.
    /// </summary>
    public static IReadOnlyList<PadControl> ControlsOf(PadReading reading)
    {
        var presses = Decode(reading).Presses;
        var controls = new List<PadControl>(presses.Count);

        foreach (var press in presses)
        {
            // A button on a pad nobody has mapped is only ever itself. Folding several of those
            // into one control would have unrelated buttons matching each other.
            controls.Add(press.Control == PadControl.Unknown
                         ? (PadControl)(1000 + press.Number)
                         : press.Control);
        }

        return controls;
    }

    /// <summary>Human form, in a fixed order so the same combination always reads the same way.</summary>
    public static string Describe(int combo, ushort vendor = 0, ushort product = 0)
    {
        if (combo == 0) return "";

        if (IsHid(combo))
            return HidPad.Describe(MaskOf(combo), vendor, product);

        var names = new List<string>();

        foreach (var (bit, name) in Names)
            if ((combo & bit) != 0) names.Add(name);

        return names.Count == 0 ? "" : string.Join(" + ", names);
    }

    /// <summary>
    /// A combination broken into the controls it names, with the symbols to draw them in.
    ///
    /// The family follows the backend. XInput normalises everything to an Xbox pad and reports
    /// Xbox buttons, so an XInput combination is an Xbox one whatever is physically plugged in —
    /// drawing PlayStation shapes for it would be inventing detail the reading does not carry.
    /// A HID combination is asked of the pad it came from.
    /// </summary>
    public static (PadFamily Family, IReadOnlyList<PadPress> Presses) Decode(PadReading reading)
    {
        if (reading.Combo == 0) return (PadFamily.Xbox, []);

        if (IsHid(reading.Combo))
        {
            return (HidPad.FamilyOf(reading.Vendor, reading.Product),
                    HidPad.Decode(MaskOf(reading.Combo), reading.Vendor, reading.Product));
        }

        var presses = new List<PadPress>();

        foreach (var (bit, control) in Controls)
            if ((reading.Combo & bit) != 0) presses.Add(new PadPress(control));

        return (PadFamily.Xbox, presses);
    }

    /// <summary>Where each XInput bit sits on the pad. Same order as the names below.</summary>
    private static readonly (int Bit, PadControl Control)[] Controls =
    [
        (TriggerLeft, PadControl.L2),
        (TriggerRight, PadControl.R2),
        (0x0020, PadControl.Select),
        (0x0010, PadControl.Start),
        (0x0100, PadControl.L1),
        (0x0200, PadControl.R1),
        (0x0040, PadControl.L3),
        (0x0080, PadControl.R3),
        (0x0001, PadControl.DpadUp),
        (0x0002, PadControl.DpadDown),
        (0x0004, PadControl.DpadLeft),
        (0x0008, PadControl.DpadRight),
        (0x1000, PadControl.FaceDown),
        (0x2000, PadControl.FaceRight),
        (0x4000, PadControl.FaceLeft),
        (0x8000, PadControl.FaceUp),
        (GuideBit, PadControl.Home),
    ];

    /// <summary>
    /// The Guide / Xbox button. Absent from the public XInput headers — Microsoft reserves it for the
    /// shell — and only reported by the undocumented entry point below. A DualSense puts its PS button
    /// in the ordinary HID report, so that side needs none of this.
    /// </summary>
    private const int GuideBit = 0x0400;

    /// <summary>
    /// Xbox names, because XInput is what is being read and those are the names on the buttons
    /// it reports. A PlayStation pad seen through XInput genuinely is reporting A, B, X and Y.
    /// </summary>
    private static readonly (int Bit, string Name)[] Names =
    [
        (TriggerLeft, "LT"),
        (TriggerRight, "RT"),
        (0x0020, "Back"),
        (0x0010, "Start"),
        (0x0100, "LB"),
        (0x0200, "RB"),
        (0x0040, "Left stick"),
        (0x0080, "Right stick"),
        (0x0001, "D-pad Up"),
        (0x0002, "D-pad Down"),
        (0x0004, "D-pad Left"),
        (0x0008, "D-pad Right"),
        (0x1000, "A"),
        (0x2000, "B"),
        (0x4000, "X"),
        (0x8000, "Y"),
        (GuideBit, "Guide"),
    ];

    /// <summary>
    /// At least two controls, so it cannot fire during ordinary play.
    ///
    /// A diagonal on the D-pad counts as one, not two. The hat expands north-east into up *and*
    /// right, which is right for matching — a shortcut on "up" should fire on north-east — but
    /// it would let a single diagonal press satisfy the two-button rule on its own, which is
    /// exactly the accidental trigger the rule exists to prevent.
    /// </summary>
    public static bool IsUsable(int combo)
    {
        uint mask = MaskOf(combo);
        uint hat = mask & HidPad.HatBits;

        int count = System.Numerics.BitOperations.PopCount(mask & ~HidPad.HatBits);
        if (hat != 0) count++;

        return count >= 2;
    }

    public void Dispose() => _poll.Dispose();

    // ---- interop ----

    /// <summary>
    /// XInput 1.4 ships with Windows 8 and later; 9.1.0 is the one present on everything older.
    /// Tried in that order once, and the answer remembered, so a missing DLL is not a thrown
    /// exception on every poll.
    /// </summary>
    private static bool TryGetState(uint pad, out XInputState state)
    {
        state = default;

        // The Ex variant first, purely so the Guide button is visible. It is the same call with one
        // extra bit — Microsoft simply never published it, because the shell wants that button — and
        // it is exported by ordinal only, with no name to bind to. Steam reads it exactly this way.
        //
        // Everything below is a fallback chain rather than a requirement: if the ordinal is missing on
        // this machine the public entry point answers instead, and the only thing lost is the Guide
        // button on Xbox pads. Nothing else changes, so this can never be the reason a pad stops
        // working.
        if (_guide != false)
        {
            try
            {
                uint rc = XInputGetStateEx(pad, out state);
                _guide = true;
                if (rc == 0) { NoteXInputUse(state); return true; }
                return false;
            }
            catch (DllNotFoundException) { _guide = false; }
            catch (EntryPointNotFoundException) { _guide = false; }
        }

        try
        {
            if (_modern != false)
            {
                uint rc = XInputGetState14(pad, out state);
                _modern = true;
                if (rc == 0) { NoteXInputUse(state); return true; }
                return false;
            }
        }
        catch (DllNotFoundException) { _modern = false; }
        catch (EntryPointNotFoundException) { _modern = false; }

        try
        {
            if (XInputGetState910(pad, out state) != 0) return false;
            NoteXInputUse(state);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private static bool? _modern;
    private static bool? _guide;

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger, RightTrigger;
        public short ThumbLX, ThumbLY, ThumbRX, ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    /// <summary>
    /// XInputGetStateEx, bound by ordinal because it has no exported name. Identical to XInputGetState
    /// apart from reporting the Guide button in bit 0x0400.
    /// </summary>
    [DllImport("xinput1_4.dll", EntryPoint = "#100")]
    private static extern uint XInputGetStateEx(uint index, out XInputState state);

    [DllImport("xinput1_4.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState14(uint index, out XInputState state);

    [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetState")]
    private static extern uint XInputGetState910(uint index, out XInputState state);
}
