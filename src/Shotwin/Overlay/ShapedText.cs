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
    /// The overlay repaints on every mouse move, and building a shaper means parsing the
    /// font's tables, so the last one is kept. One entry is enough: the strings on screen
    /// at any moment are the hint and a window title, in the same language.
    /// </summary>
    private static SKTypeface? _cachedTypeface;
    private static SKShaper? _cachedShaper;

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

        return SKTypeface.FromFamilyName("Consolas") ?? SKTypeface.Default;
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
        if (ReferenceEquals(typeface, _cachedTypeface) && _cachedShaper is not null)
            return _cachedShaper;

        _cachedShaper?.Dispose();
        _cachedShaper = new SKShaper(typeface);
        _cachedTypeface = typeface;

        return _cachedShaper;
    }
}
