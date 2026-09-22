using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Shotwin.Overlay;

/// <summary>
/// Text drawing for the Skia canvases, with shaping and font fallback.
///
/// <see cref="SKCanvas.DrawText(string, float, float, SKTextAlign, SKFont, SKPaint)"/>
/// maps characters to glyphs one at a time and emits them left to right. That is fine
/// for Latin and wrong for most of the world: Arabic letters join and change shape
/// according to their neighbours, and both Arabic and Hebrew read right to left, so the
/// overlay hint came out as unjoined letters in reverse. HarfBuzz does the shaping and
/// the bidi reordering that turns a string into the glyphs it is actually meant to be.
///
/// The second half of the problem is the font. Consolas carries no Arabic, Hebrew,
/// Devanagari, Thai or CJK glyphs at all, so even perfectly shaped text would have
/// drawn as rows of boxes; the typeface is chosen per string from what it contains.
/// </summary>
internal static class ShapedText
{
    /// <summary>
    /// The overlay redraws its labels on every paint, and building a shaper means reading
    /// and parsing the font, so one is kept per typeface. Keeping only the last one was
    /// not enough: a window title with a character Consolas lacks (the en dash in an
    /// IDE's title bar will do) sits next to a coordinate chip in Consolas, and the two
    /// evicted each other twice per paint.
    /// </summary>
    private static readonly Dictionary<SKTypeface, SKShaper> Shapers =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>More than the overlay ever shows at once; past it the cache just starts over.</summary>
    private const int MaxShapers = 8;

    private static readonly SKTypeface Consolas =
        SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;

    /// <summary>
    /// Latin, its accented range and the general punctuation Consolas covers. Anything
    /// above this is worth asking the system about.
    /// </summary>
    private const char SimpleLimit = (char)0x0370;

    public static SKFont Font(string text, float size = 12.5f) => new(TypefaceFor(text), size);

    /// <summary>
    /// A typeface that can actually render this string: Consolas while it is plain Latin,
    /// and whatever Windows offers for the first character it cannot.
    /// </summary>
    private static SKTypeface TypefaceFor(string text)
    {
        foreach (char c in text)
        {
            if (c < SimpleLimit) continue;

            var match = SKFontManager.Default.MatchCharacter(c);
            if (match is not null) return match;
        }

        return Consolas;
    }

    /// <summary>
    /// How wide the shaped run is. Measured through the shaper rather than
    /// <see cref="SKFont.MeasureText(string)"/>, which sums each character's own advance:
    /// for a script whose letters join, the shaped run is narrower than that sum, and a
    /// chip sized from it would be visibly too wide.
    /// </summary>
    public static float Measure(string text, SKFont font)
    {
        if (text.Length == 0) return 0;

        try
        {
            var shaper = ShaperFor(font.Typeface);
            var result = shaper.Shape(text, font);

            if (result.Points.Length == 0) return font.MeasureText(text);

            // The shaper reports each glyph's origin, not a total, so the width is the
            // last origin plus what that glyph itself advances.
            float last = result.Points[^1].X;
            return last + font.MeasureText(text[^1..]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            // A font the shaper cannot read is not worth losing the overlay over.
            return font.MeasureText(text);
        }
    }

    /// <summary>Draws the run with its glyphs joined and in reading order.</summary>
    public static void Draw(SKCanvas canvas, string text, float x, float y, SKFont font, SKPaint paint)
    {
        if (text.Length == 0) return;

        try
        {
            canvas.DrawShapedText(ShaperFor(font.Typeface), text, x, y, font, paint);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);
        }
    }

    private static SKShaper ShaperFor(SKTypeface typeface)
    {
        if (Shapers.TryGetValue(typeface, out var shaper))
            return shaper;

        if (Shapers.Count >= MaxShapers)
        {
            foreach (var old in Shapers.Values) old.Dispose();
            Shapers.Clear();
        }

        shaper = new SKShaper(typeface);
        Shapers[typeface] = shaper;
        return shaper;
    }
}
