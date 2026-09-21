using SkiaSharp;

namespace Shotwin.Capture;

/// <summary>What a frame turned out to be worth.</summary>
public enum AppendResult
{
    First,
    Extended,

    /// <summary>The frame repeats what is already there: the page has bottomed out.</summary>
    NoNewContent,

    /// <summary>Nothing lines up, so the content changed under us rather than scrolled.</summary>
    Mismatch,
}

/// <summary>
/// Grows one tall image out of a stream of overlapping frames of the same region.
///
/// Pure and synchronous — no UI, no P/Invoke, no timing — so the awkward part of a
/// scrolling capture can be reasoned about on its own.
/// <see cref="ScrollingCaptureSession"/> does the wheeling and feeds frames in.
///
/// Frames are aligned on row signatures rather than pixels: one luminance sum per row,
/// sampled every eighth column. Full-pixel matching across every candidate offset is
/// O(h^2*w) and would stall the capture on every single frame; a signature reduces it
/// to O(h^2) additions. Signatures collide readily though — a page of flat background
/// gives every row the same number — so the winning offset is confirmed against real
/// pixels before anything is spliced.
///
/// Only the scrolling viewport is aligned and stitched. A region dragged tightly over the
/// content is all viewport, but a whole window brings its chrome along — title bar,
/// toolbar, sidebar, scrollbar, status bar — and that chrome is identical in every frame.
/// Correlating across it puts the best score at a shift of zero, where the chrome agrees
/// with itself perfectly, which reads as a page that has bottomed out on frame two. So the
/// viewport is measured once from the first pair of frames, everything after that happens
/// inside it, and the chrome is wrapped back around the stitched strip at the end.
/// </summary>
public sealed class ScrollStitcher : IDisposable
{
    /// <summary>Only vertical scrolling: this is the shape a long screenshot has.</summary>
    private const int MaxHeight = 32000;

    private const int ColumnStep = 8;

    /// <summary>Rows of real pixels compared before an offset is believed.</summary>
    private const int VerifyRows = 32;

    /// <summary>Mean per-channel difference, on 0-255, still counted as the same pixels.</summary>
    private const double VerifyTolerance = 12;

    /// <summary>Every fourth row and column is plenty to tell chrome from content.</summary>
    private const int ChromeStep = 4;

    /// <summary>
    /// Mean per-channel difference, on 0-255, under which a row or column counts as
    /// unchanged. Tighter than <see cref="VerifyTolerance"/>, because here the question is
    /// whether anything moved at all, but not exact equality: a live grab of a window
    /// carries caret blink and subpixel repaint noise through its chrome.
    /// </summary>
    private const double ChromeTolerance = 6;

    /// <summary>
    /// A viewport shorter than this, or smaller than a quarter of the region, is not
    /// believed. Content that genuinely sits still — a short page, a list that fits —
    /// looks exactly like chrome, and stitching it through a keyhole would be worse than
    /// stitching the whole frame.
    /// </summary>
    private const int MinViewportHeight = 80;

    private SKBitmap? _accumulated;

    /// <summary>The first frame, kept for the header and the viewport's own sides.</summary>
    private SKBitmap? _first;

    /// <summary>The newest frame that contributed content, kept for the footer.</summary>
    private SKBitmap? _last;

    private SKBitmap? _composed;

    /// <summary>The part of the region that scrolls, in region coordinates.</summary>
    private SKRectI _viewport;

    // Reused across calls: a frame arrives every ~130ms and these are the only
    // allocations the matching would otherwise make.
    private long[] _tailRows = [];
    private long[] _frameRows = [];

    /// <summary>
    /// The finished image, null until the first frame: the chrome the first and last
    /// frames came with, wrapped back around the stitched viewport.
    ///
    /// Composed on demand rather than kept current frame by frame — the footer only means
    /// anything once the capture has stopped, and rebuilding it four hundred times would
    /// be four hundred copies of an image that grows all the way through. Valid until the
    /// next <see cref="Append"/>; after <see cref="Dispose"/> it belongs to the caller.
    /// </summary>
    public SKBitmap? Result => _composed ??= Compose();

    /// <summary>Where the finished image stands now, which is what the progress window shows.</summary>
    public int Height => _accumulated is null
        ? _first?.Height ?? 0
        : HeaderHeight + _accumulated.Height + FooterHeight;

    private int HeaderHeight => _viewport.Top;

    private int FooterHeight => _last!.Height - _viewport.Bottom;

    public AppendResult Append(SKBitmap frame)
    {
        // Anything composed earlier described a shorter capture, and only the finished one
        // is ever handed out, so the half-built image goes rather than lingering.
        _composed?.Dispose();
        _composed = null;

        if (_first is null)
        {
            _first = frame.Copy();
            _last = frame.Copy();
            return AppendResult.First;
        }

        // The viewport is held in region coordinates and indexed into every frame
        // directly, so a frame of another size is not something to work around.
        if (frame.Width != _first.Width || frame.Height != _first.Height) return AppendResult.Mismatch;

        if (_accumulated is null)
        {
            // Measured once and then kept: chrome only gives itself away against a frame
            // that has moved, and re-measuring per frame would let the viewport drift as
            // different content passed through it.
            _viewport = DetectViewport(_first, frame);
            _accumulated = Crop(_first, _viewport);
        }

        if (_accumulated.Height >= MaxHeight) return AppendResult.NoNewContent;

        // Only the last frame's worth of rows can possibly overlap, and comparing
        // against the whole accumulated image would get slower with every frame.
        int tailHeight = Math.Min(_viewport.Height, _accumulated.Height);
        int tailTop = _accumulated.Height - tailHeight;

        Signature(_accumulated, tailTop, tailHeight, 0, _accumulated.Width, ref _tailRows);
        Signature(frame, _viewport.Top, _viewport.Height, _viewport.Left, _viewport.Width, ref _frameRows);

        int minOverlap = Math.Max(24, _viewport.Height / 8);
        int shift = BestShift(tailHeight, minOverlap);
        if (shift == 0) return AppendResult.NoNewContent;

        int newRows = _viewport.Height - (tailHeight - shift);
        if (newRows <= 0) return AppendResult.NoNewContent;

        if (!Verify(frame, tailTop, tailHeight, shift)) return AppendResult.Mismatch;

        Grow(frame, Math.Min(newRows, MaxHeight - _accumulated.Height));

        // The footer is whatever sat under the viewport when the capture stopped, so it
        // follows the newest frame that actually brought content with it.
        _last?.Dispose();
        _last = frame.Copy();

        return AppendResult.Extended;
    }

    /// <summary>
    /// The part of the region that moved between two frames, compared at zero shift.
    ///
    /// Rows and columns are judged independently and the winners intersected, which is
    /// what a window's chrome actually looks like: a band across the top and bottom, a
    /// band down one or both sides, and content in the middle.
    /// </summary>
    private static SKRectI DetectViewport(SKBitmap previous, SKBitmap frame)
    {
        int width = frame.Width;
        int height = frame.Height;

        var staticRows = new bool[height];
        var staticColumns = new bool[width];
        var columnTotals = new long[width];

        var previousPixels = previous.GetPixelSpan();
        var framePixels = frame.GetPixelSpan();
        int previousPixelBytes = previous.BytesPerPixel;
        int framePixelBytes = frame.BytesPerPixel;

        int rowSamples = (width + ChromeStep - 1) / ChromeStep;
        int columnSamples = 0;

        for (int y = 0; y < height; y++)
        {
            var a = previousPixels.Slice(y * previous.RowBytes, previous.RowBytes);
            var b = framePixels.Slice(y * frame.RowBytes, frame.RowBytes);

            long rowTotal = 0;
            for (int x = 0; x < width; x += ChromeStep)
            {
                int ai = x * previousPixelBytes, bi = x * framePixelBytes;
                rowTotal += Math.Abs(a[ai] - b[bi])
                          + Math.Abs(a[ai + 1] - b[bi + 1])
                          + Math.Abs(a[ai + 2] - b[bi + 2]);
            }

            staticRows[y] = rowTotal / (double)(rowSamples * 3) < ChromeTolerance;

            // The column test wants every column but only every fourth row, and walking
            // the image column by column would miss the cache on every single pixel.
            if (y % ChromeStep != 0) continue;
            columnSamples++;

            for (int x = 0; x < width; x++)
            {
                int ai = x * previousPixelBytes, bi = x * framePixelBytes;
                columnTotals[x] += Math.Abs(a[ai] - b[bi])
                                 + Math.Abs(a[ai + 1] - b[bi + 1])
                                 + Math.Abs(a[ai + 2] - b[bi + 2]);
            }
        }

        for (int x = 0; x < width; x++)
            staticColumns[x] = columnTotals[x] / (double)(columnSamples * 3) < ChromeTolerance;

        // Judged separately, so a text page can keep all its rows while still having a
        // genuine sidebar trimmed off its columns.
        var rows = EdgeBand(staticRows);
        var columns = EdgeBand(staticColumns);

        var viewport = new SKRectI(
            columns.Start, rows.Start,
            columns.Start + columns.Length, rows.Start + rows.Length);

        bool tooSmall = viewport.Height < MinViewportHeight
            || (long)viewport.Width * viewport.Height * 4 < (long)width * height;

        return tooSmall ? new SKRectI(0, 0, width, height) : viewport;
    }

    /// <summary>
    /// The band that scrolls: the longest unbroken stretch of rows or columns that changed,
    /// as long as little else changed outside it.
    ///
    /// Chrome is contiguous and sits at an edge. A page of text also holds still in its
    /// middle — the blank bands between lines land on each other whenever the scroll step
    /// is not a multiple of the line pitch — and so does a flat margin, but those are gaps
    /// in the content rather than chrome. Trimming to them would hand the correlation a
    /// fraction of the rows it could have had, so stillness scattered through the frame
    /// means the whole extent is content.
    /// </summary>
    /// <summary>
    /// Trims the unchanged runs at each END, and nothing else.
    ///
    /// Chrome is by definition at an edge: a toolbar above the content, a status bar
    /// below it, a sidebar beside it. Stillness in the middle is not chrome — it is the
    /// blank band between two paragraphs, or a heading that happens to land on the same
    /// row twice — and cutting the viewport down to the longest moving run because of it
    /// throws away most of the page.
    ///
    /// Picking the longest run instead was worse than either mistake it was meant to
    /// avoid: a browser window's toolbars are contiguous stillness at the top, so any
    /// interior blankness in the page below made the whole lot fall back to the full
    /// height, chrome included. The alignment then matched on rows that never move and
    /// every single frame failed verification.
    /// </summary>
    private static (int Start, int Length) EdgeBand(bool[] isStatic)
    {
        int start = 0;
        while (start < isStatic.Length && isStatic[start]) start++;

        int end = isStatic.Length;
        while (end > start && isStatic[end - 1]) end--;

        return (start, end - start);
    }

    /// <summary>
    /// The viewport's own pixels, lifted out once so that the matching, the verification
    /// and the splicing never have to step over the chrome's columns.
    /// </summary>
    private static SKBitmap Crop(SKBitmap source, SKRectI rect)
    {
        using var subset = new SKBitmap();
        source.ExtractSubset(subset, rect);
        return subset.Copy();
    }

    /// <summary>
    /// How far the content moved between the tail and this frame, in rows.
    ///
    /// Zero is a candidate on purpose. A page that has hit its end sends back the frame
    /// it sent last time, and calling that a failed match would report content changing
    /// under us when the truth is the opposite.
    /// </summary>
    private int BestShift(int tailHeight, int minOverlap)
    {
        int best = 0;
        double bestScore = double.MaxValue;

        for (int d = 0; d <= tailHeight - minOverlap; d++)
        {
            int overlap = tailHeight - d;
            long total = 0;
            for (int i = 0; i < overlap; i++)
                total += Math.Abs(_tailRows[d + i] - _frameRows[i]);

            double score = (double)total / overlap;
            if (score >= bestScore) continue;

            bestScore = score;
            best = d;
        }

        return best;
    }

    /// <summary>
    /// One number per row: luminance summed across every eighth column of the viewport.
    /// Weights are the usual 0.299/0.587/0.114 scaled by a thousand, which keeps the whole
    /// thing in integer arithmetic — only the ordering of the differences matters.
    /// </summary>
    private static void Signature(SKBitmap bitmap, int top, int rows, int left, int width, ref long[] into)
    {
        if (into.Length < rows) into = new long[rows];

        var pixels = bitmap.GetPixelSpan();
        int rowBytes = bitmap.RowBytes;
        int pixelBytes = bitmap.BytesPerPixel;
        int start = left * pixelBytes;
        int limit = (left + width) * pixelBytes;
        int step = pixelBytes * ColumnStep;

        for (int y = 0; y < rows; y++)
        {
            var row = pixels.Slice((top + y) * rowBytes, rowBytes);

            long sum = 0;
            for (int x = start; x < limit; x += step)
                sum += row[x + 2] * 299L + row[x + 1] * 587L + row[x] * 114L;

            into[y] = sum;
        }
    }

    /// <summary>
    /// Confirms an offset on actual pixels. A band is enough: signatures agreeing over
    /// hundreds of rows and pixels disagreeing over thirty does not happen, and reading
    /// the whole overlap would cost what the signatures were there to avoid.
    /// </summary>
    private bool Verify(SKBitmap frame, int tailTop, int tailHeight, int shift)
    {
        var accumulated = _accumulated!;

        int rows = Math.Min(VerifyRows, tailHeight - shift);
        if (rows <= 0) return false;

        var accPixels = accumulated.GetPixelSpan();
        var framePixels = frame.GetPixelSpan();
        int accPixelBytes = accumulated.BytesPerPixel;
        int framePixelBytes = frame.BytesPerPixel;
        int width = _viewport.Width;

        long total = 0;
        for (int y = 0; y < rows; y++)
        {
            var a = accPixels.Slice((tailTop + shift + y) * accumulated.RowBytes, accumulated.RowBytes);
            var b = framePixels.Slice((_viewport.Top + y) * frame.RowBytes, frame.RowBytes);

            for (int x = 0; x < width; x++)
            {
                int ai = x * accPixelBytes, bi = (_viewport.Left + x) * framePixelBytes;
                total += Math.Abs(a[ai] - b[bi])
                       + Math.Abs(a[ai + 1] - b[bi + 1])
                       + Math.Abs(a[ai + 2] - b[bi + 2]);
            }
        }

        return total / (double)(rows * width * 3) < VerifyTolerance;
    }

    /// <summary>
    /// Reallocates once per frame and blits the band of viewport that is new. Growing a
    /// row at a time would copy the whole image again for every row it gained.
    /// </summary>
    private void Grow(SKBitmap frame, int newRows)
    {
        var old = _accumulated!;
        var grown = new SKBitmap(new SKImageInfo(
            old.Width, old.Height + newRows, old.ColorType, old.AlphaType));

        using (var canvas = new SKCanvas(grown))
        using (var source = SKImage.FromBitmap(frame))
        {
            canvas.DrawBitmap(old, 0, 0);
            canvas.DrawImage(source,
                new SKRect(_viewport.Left, _viewport.Bottom - newRows, _viewport.Right, _viewport.Bottom),
                new SKRect(0, old.Height, old.Width, old.Height + newRows));
        }

        old.Dispose();
        _accumulated = grown;
    }

    /// <summary>
    /// Puts the chrome back: the first frame's header on top, the last frame's footer
    /// underneath, and the viewport's own sides run down the stitched strip so the result
    /// is the full width of the region the user selected.
    /// </summary>
    private SKBitmap? Compose()
    {
        if (_first is null) return null;

        // One frame, nothing ever aligned against it: the frame is the capture.
        if (_accumulated is null) return _first.Copy();

        int width = _first.Width;
        int header = HeaderHeight;
        int body = _accumulated.Height;
        int footer = FooterHeight;

        var composed = new SKBitmap(new SKImageInfo(
            width, header + body + footer, _first.ColorType, _first.AlphaType));

        using (var canvas = new SKCanvas(composed))
        using (var first = SKImage.FromBitmap(_first))
        using (var last = SKImage.FromBitmap(_last!))
        using (var stitched = SKImage.FromBitmap(_accumulated))
        {
            if (header > 0)
                canvas.DrawImage(first,
                    new SKRect(0, 0, width, header),
                    new SKRect(0, 0, width, header));

            // The sides never changed — that is how they were found — so one frame's worth
            // of them is all there is to draw.
            if (_viewport.Left > 0) DrawSide(canvas, first, 0, _viewport.Left, header, body);
            if (_viewport.Right < width) DrawSide(canvas, first, _viewport.Right, width, header, body);

            canvas.DrawImage(stitched, _viewport.Left, header);

            if (footer > 0)
                canvas.DrawImage(last,
                    new SKRect(0, _viewport.Bottom, width, _last!.Height),
                    new SKRect(0, header + body, width, header + body + footer));
        }

        return composed;
    }

    /// <summary>
    /// One of the viewport's sides, run down the stitched strip: the first frame's band at
    /// its own height, then the last row of that band repeated to fill what is left.
    ///
    /// Stretching the band to fit would smear whatever detail it has — a logo in a nav
    /// rail, a scrollbar thumb, a section heading — over the whole capture, and tiling it
    /// would seam that detail back in every viewport height. Repeating one row keeps the
    /// detail at its real size and carries the colour the sidebar ends on to the bottom.
    /// </summary>
    private void DrawSide(SKCanvas canvas, SKImage first, int left, int right, int top, int height)
    {
        int natural = Math.Min(_viewport.Height, height);

        canvas.DrawImage(first,
            new SKRect(left, _viewport.Top, right, _viewport.Top + natural),
            new SKRect(left, top, right, top + natural));

        if (height <= natural) return;

        canvas.DrawImage(first,
            new SKRect(left, _viewport.Bottom - 1, right, _viewport.Bottom),
            new SKRect(left, top + natural, right, top + height));
    }

    public void Dispose()
    {
        _accumulated?.Dispose();
        _accumulated = null;

        _first?.Dispose();
        _first = null;

        _last?.Dispose();
        _last = null;

        // Deliberately not disposed: Result handed this one to the caller.
        _composed = null;
    }
}
