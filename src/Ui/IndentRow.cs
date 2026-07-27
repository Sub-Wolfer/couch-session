using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>
/// A nested setting, with an elbow joining it to the one it belongs to.
///
/// Indenting alone left the relationship implied rather than shown — a reader had to notice a
/// few pixels of offset and infer that the second setting depends on the first. The elbow says
/// it outright: the line comes down from the setting above and turns into this one.
///
/// It hosts the row rather than sitting beside it, because the space to the left of an indented
/// row is margin, and nothing can be drawn in another control's margin.
/// </summary>
internal sealed class IndentRow : Panel
{
    private readonly int _indent;

    /// <summary>Where the elbow turns, measured to line up with the middle of the title.</summary>
    private const int TurnY = 13;

    public IndentRow(Control child, int indent, int width)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);

        _indent = indent;
        BackColor = Color.Transparent;

        child.Location = new Point(indent, 0);
        Controls.Add(child);

        Width = width;
        Height = child.Height;

        // The row grows when a description wraps to another line, so the container follows it.
        child.SizeChanged += (_, _) =>
        {
            Height = child.Height;
            child.Left = _indent;
        };
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Theme.PaintBackdrop(g, this);

        // Up out of the top of this control, because the line belongs to the pair rather than
        // to either row — it starts in the gap above, where the divider would otherwise sit.
        //
        // The vertical sits a third of the way in rather than halfway: at halfway the corner
        // radius consumed the entire horizontal run and the elbow drew as a plain curve with
        // nothing after it.
        int x = 9;
        int radius = 5;
        int end = _indent - 4;

        using var pen = new Pen(Theme.RowLine, 1.4f);
        using var path = new GraphicsPath();

        path.AddLine(x, -14, x, TurnY - radius);
        path.AddArc(x, TurnY - radius * 2, radius * 2, radius * 2, 180, -90);
        path.AddLine(x + radius, TurnY, Math.Max(x + radius + 4, end), TurnY);

        g.DrawPath(pen, path);
    }
}
