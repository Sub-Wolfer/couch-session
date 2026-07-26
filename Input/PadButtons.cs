namespace CouchMode.Input;

/// <summary>
/// Which analog stick moves the cursor. Exactly one, because the other stick is then free to
/// scroll — a choice, not a set.
/// </summary>
public enum CursorStick
{
    Left = 0,
    Right = 1,
}

/// <summary>
/// Which family of controller a combination was recorded on, and so which symbols to draw for it.
///
/// The distinction is presentational, not mechanical. The same physical button is a cross on a
/// DualSense and an A on an Xbox pad, and someone looking at their shortcut wants to see the one
/// printed on the controller in their hands.
/// </summary>
public enum PadFamily
{
    /// <summary>Xbox pads, and anything unrecognised. ABXY, LB/RB, LT/RT.</summary>
    Xbox,

    /// <summary>DualSense and DualShock. Shapes, L1/R1, L2/R2.</summary>
    PlayStation,
}

/// <summary>
/// A control on a pad, named by where it is rather than by what is printed on it.
///
/// Position is the only thing the two families agree on: the button under your thumb on the right
/// hand side is cross on a PlayStation pad and B on an Xbox pad, and calling it FaceRight lets one
/// recording be drawn correctly for either. Naming it "Cross" here would mean storing the family
/// in the shortcut itself and re-recording if you changed controller.
/// </summary>
public enum PadControl
{
    /// <summary>Cross / A.</summary>
    FaceDown,

    /// <summary>Circle / B.</summary>
    FaceRight,

    /// <summary>Square / X.</summary>
    FaceLeft,

    /// <summary>Triangle / Y.</summary>
    FaceUp,

    /// <summary>L1 / LB.</summary>
    L1,

    /// <summary>R1 / RB.</summary>
    R1,

    /// <summary>L2 / LT.</summary>
    L2,

    /// <summary>R2 / RT.</summary>
    R2,

    /// <summary>Left stick click.</summary>
    L3,

    /// <summary>Right stick click.</summary>
    R3,

    /// <summary>Create / Share / Back / View.</summary>
    Select,

    /// <summary>Options / Start / Menu.</summary>
    Start,

    /// <summary>PS / Guide.</summary>
    Home,

    /// <summary>DualSense and DualShock only.</summary>
    Touchpad,

    /// <summary>DualSense only.</summary>
    Mute,

    DpadUp,
    DpadDown,
    DpadLeft,
    DpadRight,

    /// <summary>A button on a pad with no known layout. <see cref="PadPress.Number"/> says which.</summary>
    Unknown,
}

/// <param name="Number">
/// The 1-based button number, for <see cref="PadControl.Unknown"/> only. HID hands out numbers
/// rather than names, and for a pad nobody has mapped the number is the honest answer — better a
/// box reading "7" than a guess at what the seventh button might be called.
/// </param>
public readonly record struct PadPress(PadControl Control, int Number = 0);

/// <param name="Combo">The mask, with PadShortcut.HidFlag set if it came from a HID pad.</param>
/// <param name="Vendor">USB vendor id, or 0 for an XInput reading.</param>
/// <param name="Product">USB product id, or 0 for an XInput reading.</param>
/// <remarks>
/// A mask on its own is not enough to know what was pressed. HID numbers buttons per device, so
/// button 3 is Circle on a DualSense and something else entirely on the next pad along. Carrying
/// the device with the mask is what makes a reading interpretable at all — and what lets a
/// shortcut recorded on one controller be recognised on another.
/// </remarks>
public readonly record struct PadReading(int Combo, ushort Vendor, ushort Product);

/// <param name="Touching">Whether a finger is on the pad right now.</param>
/// <param name="X">Horizontal position, 0 to <see cref="HidPad.TrackpadWidth"/>.</param>
/// <param name="Y">Vertical position, 0 to <see cref="HidPad.TrackpadHeight"/>.</param>
/// <remarks>The default value — not touching, no coordinates — is the "no finger" reading.</remarks>
public readonly record struct PadTouch(bool Touching, int X, int Y);

/// <summary>
/// Reads one analogue stick as a pointer or scroll vector.
///
/// Axes are up-positive and right-positive, the XInput convention, so a HID pad has to flip its Y
/// before calling in.
/// </summary>
public static class PadSticks
{
    /// <summary>
    /// One stick past a round dead zone, as a 0..1 vector.
    ///
    /// <paramref name="dead"/> is the dead-zone radius as a fraction of full deflection: anything
    /// closer to centre than this reads as no movement. It is a setting because a worn stick that
    /// drifts off-centre needs a wider one to sit still, while a good stick wants it small so a
    /// light push already moves the pointer.
    /// </summary>
    public static (float X, float Y) Apply(float x, float y, float dead)
    {
        float m = MathF.Sqrt(x * x + y * y);

        dead = Math.Clamp(dead, 0f, 0.9f);
        if (m < dead || m <= 0f) return (0f, 0f);

        // Direction kept, magnitude rescaled so the first responsive movement past the dead zone
        // is gentle rather than a jump.
        float scaled = Math.Clamp((m - dead) / (1f - dead), 0f, 1f);
        return (x / m * scaled, y / m * scaled);
    }
}
