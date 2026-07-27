using System.Drawing.Drawing2D;

namespace CouchMode.Ui;

/// <summary>Which column the list is ordered by.</summary>
internal enum GameSort { Enabled, Name, LastPlayed, Installed, Hdr }

/// <summary>
/// Column titles above the games list, which are also the sort controls.
///
/// A separate control rather than a fixed first row of the list itself: a row would scroll away
/// with everything else, and ListBox has no concept of a header that stays put.
///
/// It takes its column positions from the list rather than repeating them, so the two cannot
/// drift apart when the window is resized or the badge column is remeasured.
/// </summary>
internal sealed class GameListHeader : Control
{
    private readonly GameListView _list;
    private GameSort _hovered = (GameSort)(-1);

    /// <summary>The column being sorted by, and which way.</summary>
    public GameSort Sort { get; private set; } = GameSort.Enabled;

    public bool Descending { get; private set; } = true;

    /// <summary>Raised when the user picks a different column or flips the direction.</summary>
    public event Action? SortChanged;

    public GameListHeader(GameListView list)
    {
        _list = list;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);

        BackColor = Theme.Surface;
        Height = 30;
        Cursor = Cursors.Hand;
    }


    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);

        if (e.Button != MouseButtons.Left || ColumnAt(e.X) is not { } column) return;

        // Clicking the active column reverses it; a new column starts at its natural direction.
        //
        // Natural means most-useful-first rather than ascending: newest date, ticked games and
        // native HDR at the top, but names from A. Making everything ascending by default meant
        // every date column opened on the games you had not played in years.
        if (column == Sort) Descending = !Descending;
        else
        {
            Sort = column;
            Descending = column != GameSort.Name;
        }

        Invalidate();
        SortChanged?.Invoke();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);

        var column = ColumnAt(e.X) ?? (GameSort)(-1);
        if (column == _hovered) return;

        _hovered = column;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);

        if ((int)_hovered < 0) return;

        _hovered = (GameSort)(-1);
        Invalidate();
    }

    private GameSort? ColumnAt(int x)
    {
        var (played, installed, badge, right) = _list.Columns();

        if (x < CheckWidth) return GameSort.Enabled;
        if (x < played - 10) return GameSort.Name;
        if (x < installed - 10) return GameSort.LastPlayed;
        if (x < badge - 10) return GameSort.Installed;
        if (x <= right) return GameSort.Hdr;

        return null;
    }

    /// <summary>
    /// Width of the tick column.
    ///
    /// Wider than the checkbox itself, because the title has to hold a word and a sort arrow —
    /// at the checkbox's own width the label was clipped to "O".
    /// </summary>
    private const int CheckWidth = 56;

    /// <summary>
    /// Left edge of the name column, matching where a row draws its title.
    ///
    /// The row lays out checkbox (10..26), icon (36..54), then text at 62. Hard-coding the same
    /// number here is the one piece of geometry the header cannot ask the list for, since the
    /// list derives it while painting a row.
    /// </summary>
    private const int NameLeft = 62;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Theme.Surface);

        var (played, installed, badge, right) = _list.Columns();

        Cell(g, GameSort.Enabled, Words.ColumnOn, 10, CheckWidth - 16, TextFormatFlags.Left);
        Cell(g, GameSort.Name, Words.ColumnGame, NameLeft, played - 16 - NameLeft, TextFormatFlags.Left);
        Cell(g, GameSort.LastPlayed, Words.ColumnLastPlayed, played, GameListView.DateColumn - 12, TextFormatFlags.Right);
        Cell(g, GameSort.Installed, Words.ColumnInstalled, installed, GameListView.DateColumn - 12, TextFormatFlags.Right);
        Cell(g, GameSort.Hdr, Words.ColumnHdr, badge, right - badge, TextFormatFlags.Right);

        Divider(g, CheckWidth - 6);
        Divider(g, played - 10);
        Divider(g, installed - 10);
        Divider(g, badge - 10);

        using var underline = new SolidBrush(Theme.Line);
        g.FillRectangle(underline, 10, Height - 1, right - 10, 1);
    }

    private void Cell(Graphics g, GameSort column, string text, int left, int width,
                      TextFormatFlags align)
    {
        width = Math.Max(0, width);

        bool active = column == Sort;
        var ink = active ? Theme.Text : column == _hovered ? Theme.TextDim : Theme.TextFaint;

        // The arrow is drawn beside the title, so the title's box is shortened to make room
        // rather than the two being allowed to overlap.
        int arrow = active ? 14 : 0;

        var rect = new Rectangle(left + (align == TextFormatFlags.Right ? 0 : 0), 0,
                                 width - arrow, Height - 1);

        if (align == TextFormatFlags.Left) rect.X += 0;

        TextRenderer.DrawText(g, text.ToUpperInvariant(), Theme.Caption, rect, ink,
                              align | TextFormatFlags.VerticalCenter
                            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

        if (!active) return;

        int measured = TextRenderer.MeasureText(g, text.ToUpperInvariant(), Theme.Caption,
                                                Size.Empty, TextFormatFlags.NoPadding).Width;

        float arrowX = align == TextFormatFlags.Left
            ? left + Math.Min(measured, width - arrow) + 7
            : left + width - 5;

        DrawArrow(g, new PointF(arrowX, Height / 2f - 1), ink, Descending);
    }

    private static void DrawArrow(Graphics g, PointF centre, Color colour, bool down)
    {
        float tip = down ? 2.5f : -2.5f;

        var points = new[]
        {
            new PointF(centre.X - 3.5f, centre.Y - tip),
            new PointF(centre.X + 3.5f, centre.Y - tip),
            new PointF(centre.X, centre.Y + tip),
        };

        using var brush = new SolidBrush(colour);
        g.FillPolygon(brush, points);
    }

    /// <summary>A short vertical rule, inset top and bottom so it reads as a separator.</summary>
    private void Divider(Graphics g, int x)
    {
        using var brush = new SolidBrush(Theme.RowLine);
        g.FillRectangle(brush, x, 8, 1, Height - 17);
    }
}
