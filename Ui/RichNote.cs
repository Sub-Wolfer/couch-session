namespace CouchMode.Ui;

/// <summary>
/// The grey explanation under a setting, with one kind of emphasis.
///
/// A plain Label draws in a single font and colour, so there was no way to make part of a
/// sentence stand out — and some of them need it. "Not recommended" carries more weight than
/// the sentence around it and should not read at the same level as everything else.
///
/// Wrapping <c>**like this**</c> in a description makes that run bold and blue. The markers are
/// chosen to be typeable and obvious in Words.cs, so the wording and its emphasis can both be
/// changed there without touching any layout code.
///
/// Used for setting titles as well, which is why the fonts are passed in rather than fixed: a
/// caveat belongs wherever it reads best, and that is sometimes the title.
/// </summary>
internal sealed class RichNote : Control
{
    private readonly List<Run> _runs = [];
    private int _wrapWidth;

    private readonly record struct Run(string Text, bool Strong);

    /// <summary>A word, with the formatting it inherited, already placed.</summary>
    private readonly record struct Placed(string Text, bool Strong, Point At);

    private readonly List<Placed> _laid = [];

    private readonly Font _base;
    private readonly Font _strong;

    /// <summary>
    /// Colour for **emphasised** runs. Blue by default, matching every other note in the window.
    ///
    /// Was red, on the reasoning that most emphasis is a caution. In practice almost none of it
    /// is: it marks the part of a sentence worth reading first — which setting is already on,
    /// where a file was saved, what a page is for. Red says "something is wrong" whether or not
    /// that is meant, and a window where most of the emphasis is red teaches people to read
    /// warnings as decoration. Actual failures still have their own red mark, which is the one
    /// place it should carry weight.
    /// </summary>
    private readonly Color _emphasis;

    public RichNote(string markup, int maxWidth, Font? font = null, Font? strong = null,
                    Color? colour = null, Color? emphasis = null)
    {
        _emphasis = emphasis ?? Theme.Info;

        _base = font ?? Theme.Small;
        _strong = strong ?? Theme.SmallBold;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
               | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);

        BackColor = Color.Transparent;
        Font = _base;
        ForeColor = colour ?? Theme.TextDim;
        Margin = new Padding(0, 2, 0, 0);

        _wrapWidth = maxWidth;
        Parse(markup);

        Width = maxWidth;
        Height = Reflow();
    }

    /// <summary>Replace the wording and re-lay it out. Height follows the new text.</summary>
    public void SetMarkup(string markup)
    {
        Parse(markup);
        Height = Reflow();
        Invalidate();
    }

    /// <summary>Split the text into plain and emphasised runs on the ** markers.</summary>
    private void Parse(string markup)
    {
        _runs.Clear();

        bool strong = false;

        var piece = new System.Text.StringBuilder();

        for (int i = 0; i < markup.Length; i++)
        {
            // An escaped asterisk is content and can never be part of a marker.
            //
            // This has to be decided here, one character at a time, rather than by splitting on
            // "**" and unescaping afterwards. Splitting first gets \*\*** wrong: scanning for
            // two adjacent asterisks finds the pair straddling the escape — the * that closes
            // the second \* and the * that opens the real marker — and cuts the string in the
            // middle of the escape, leaving a stray asterisk behind. Which is exactly what
            // "**not recommended*\*" on screen was.
            if (markup[i] == '\\' && i + 1 < markup.Length && markup[i + 1] == '*')
            {
                piece.Append('*');
                i++;
                continue;
            }

            if (markup[i] == '*' && i + 1 < markup.Length && markup[i + 1] == '*')
            {
                if (piece.Length > 0)
                {
                    _runs.Add(new Run(piece.ToString(), strong));
                    piece.Clear();
                }

                strong = !strong;
                i++;
                continue;
            }

            piece.Append(markup[i]);
        }

        if (piece.Length > 0) _runs.Add(new Run(piece.ToString(), strong));
    }

    /// <summary>
    /// Place every word, and return the height needed.
    ///
    /// Named Reflow rather than Layout because Control already has a Layout event, and a method
    /// of the same name hides it.
    ///
    /// Word by word rather than run by run, because a wrap can fall in the middle of a run and
    /// the bold text has to break across lines like anything else.
    /// </summary>
    /// <summary>
    /// Measured widths, kept for the life of the process.
    ///
    /// Measuring is the expensive part of laying this out, and it now happens once per distinct
    /// piece of text rather than once per word, so the cache is smaller and the hit rate higher.
    /// </summary>
    private static readonly Dictionary<(string Text, string Font), int> Widths = [];

    private static readonly Dictionary<string, int> LineHeights = [];

    /// <summary>
    /// A cache key that actually identifies the font.
    ///
    /// [BUG] The cache used to be keyed on the text and a <c>bool strong</c> flag, on the assumption
    /// that "strong" meant one specific font. It does not: this control takes its fonts as
    /// constructor arguments, so the notes under a setting use Small/SmallBold while the welcome
    /// window uses Body/BodySemi. The cache is static and lives for the process, so whichever window
    /// measured a given word first won, and every later window at a different size was laid out
    /// against those wrong numbers. A single space measured at Small and reused at Body is the
    /// visible version of that: the gap before an emphasised phrase collapsed to almost nothing.
    /// </summary>
    private static string Key(Font font)
        => $"{font.Name}|{font.SizeInPoints}|{(int)font.Style}|{(int)font.Unit}";

    private static int WidthOf(string text, Font font)
    {
        if (text.Length == 0) return 0;

        var key = (text, Key(font));
        if (Widths.TryGetValue(key, out int cached)) return cached;

        // Measured with a trailing marker and then subtracted.
        //
        // TextRenderer.MeasureText discards trailing whitespace — measuring "word " gives the
        // width of "word", and measuring " " on its own gives nothing at all. That is what
        // broke the spacing: every gap between words was laid out as zero pixels wide, so
        // "Start with Windows" drew as "Start withWindows", while words whose own glyph
        // advances happened to overhang produced accidental double gaps elsewhere.
        //
        // Appending a character that cannot be trimmed, measuring, then removing that
        // character's own width gives the real figure including any trailing space.
        int withMark = TextRenderer.MeasureText(text + Mark, font, Size.Empty, Flags).Width;
        int markOnly = TextRenderer.MeasureText(Mark, font, Size.Empty, Flags).Width;

        int measured = Math.Max(0, withMark - markOnly);

        Widths[key] = measured;
        return measured;
    }

    /// <summary>A narrow, unambiguous character used only to defeat whitespace trimming.</summary>
    private const string Mark = "|";

    private const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    /// <summary>
    /// Lay the text out as runs of words, not as individual words.
    ///
    /// The previous version placed each word separately and added a measured space between
    /// them. That cannot be made accurate: a word's measured width and its actual advance
    /// differ by a fraction of a pixel, and those errors accumulate across a line, so gaps
    /// drifted wider or narrower the further along the line they were.
    ///
    /// Now the words that share a line and a style are drawn as one string and the font decides
    /// the spacing inside it, which is what fonts are for. Only a style change or a line break
    /// starts a new piece.
    ///
    /// The gap either side of an emphasised phrase is carried *inside* the next piece rather than
    /// added to x as a separate measurement.
    ///
    /// [BUG] It used to be added on its own: <c>x += WidthOf(" ")</c>. Two things go wrong with
    /// that. A lone space is the one string a text measurement is least reliable about, since the
    /// whole point of DT_CALCRECT is to report ink rather than advance — so the figure it produced
    /// was small and, once the stale cache above is in play, sometimes near zero. And whatever
    /// number it did produce was never checked against what actually gets drawn, because nothing
    /// draws a bare space. "Start on **Display &amp; Audio**" rendered as "Start onDisplay &amp; Audio".
    ///
    /// Folding the space into the piece removes the guesswork: the exact string that is measured is
    /// the string that is drawn, so the gap on screen is the gap the layout was told about.
    /// </summary>
    private int Reflow()
    {
        _laid.Clear();

        var lineKey = Key(_base);

        if (!LineHeights.TryGetValue(lineKey, out int lineHeight))
        {
            lineHeight = TextRenderer.MeasureText("Agy", _base, Size.Empty, Flags).Height + 2;
            LineHeights[lineKey] = lineHeight;
        }

        int x = 0, y = 0;

        var piece = new System.Text.StringBuilder();
        int words = 0;          // words in the piece; a piece holding only a leading space has none
        string lead = "";       // a space owed to the front of the next piece

        void Flush(bool strong)
        {
            if (piece.Length == 0) return;

            var text = piece.ToString();
            _laid.Add(new Placed(text, strong, new Point(x, y)));

            x += WidthOf(text, strong ? _strong : _base);

            piece.Clear();
            words = 0;
        }

        foreach (var run in _runs)
        {
            var font = run.Strong ? _strong : _base;

            // A run that *starts* on a space keeps that gap — the mirror of the trailing case at
            // the bottom of the loop. Emphasis written as "**...closed.** Couch Mode" puts the
            // space at the front of the plain run, and splitting on spaces discards a leading
            // empty piece just as readily as a trailing one. The result was "closed.Couch".
            if (x > 0 && run.Text.StartsWith(' ')) lead = " ";

            foreach (var word in Tokens(run.Text))
            {
                // A deliberate line break, rather than a word.
                //
                // Without this a \n in the wording did nothing visible at all: it is not a
                // space, so the splitter kept it inside whatever word it touched, and the two
                // sentences either side ran together — "then let go" straight into the next
                // sentence, with the second paragraph landing on top of the first.
                if (word == "\n")
                {
                    Flush(run.Strong);
                    x = 0;
                    y += lineHeight;
                    lead = "";
                    continue;
                }

                if (word.Length == 0) continue;   // collapses any accidental double space

                // What this line would measure with the word appended. A gap owed from the
                // previous run rides along at the front, so it is measured and drawn as one
                // string with the word rather than estimated separately.
                var candidate = words == 0 ? lead + word : piece.ToString() + " " + word;

                if (x + WidthOf(candidate, font) > _wrapWidth && (x > 0 || words > 0))
                {
                    Flush(run.Strong);
                    x = 0;
                    y += lineHeight;

                    // A gap owed at the end of a line is not owed at the start of the next one.
                    candidate = word;
                }

                lead = "";

                piece.Clear();
                piece.Append(candidate);
                words++;
            }

            Flush(run.Strong);

            // A run ending on a space keeps that gap. Splitting on spaces throws the trailing one
            // away, and the next run is a different style so it cannot share a piece with this
            // one — without this the emphasised text would butt straight against the word before.
            if (x > 0 && run.Text.EndsWith(' ')) lead = " ";
        }

        return y + lineHeight;
    }

    /// <summary>
    /// Split into words, with line breaks kept as tokens of their own.
    ///
    /// Splitting on spaces alone loses newlines entirely. Splitting on both and discarding them
    /// loses the break. Handing the break through as a token lets the layout act on it in the
    /// one place that knows where the current line is.
    /// </summary>
    private static IEnumerable<string> Tokens(string text)
    {
        var lines = text.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            // Between lines, not before the first and not after the last — a break belongs
            // between two things or it is not a break.
            if (i > 0) yield return "\n";

            foreach (var word in lines[i].Split(' ')) yield return word;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Theme.PaintBackdrop(g, this);

        foreach (var word in _laid)
        {
            TextRenderer.DrawText(g, word.Text,
                                  word.Strong ? _strong : _base,
                                  word.At,
                                  word.Strong ? _emphasis : ForeColor,
                                  TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }
    }
}
