using SkiaSharp;

namespace Shotwin.Editor;

public enum ToolKind
{
    Select,
    Arrow,
    Rectangle,
    Ellipse,
    Line,
    Pen,
    Highlight,
    Text,
    Step,
    Pixelate,
    Blur,

    /// <summary>Keeps the dragged region and discards the rest.</summary>
    Crop,

    /// <summary>Removes the dragged band and closes the gap.</summary>
    Cut,
}

/// <summary>
/// One editable mark on the shot. Coordinates are in image pixels, never screen or
/// DIP units, so annotations survive zooming and window moves untouched.
/// </summary>
public abstract class Annotation
{
    public SKColor Color { get; set; } = new(0xFF, 0x3B, 0x30);
    public float StrokeWidth { get; set; } = 4f;

    public abstract SKRect Bounds { get; }

    /// <param name="source">The flattened image underneath, for effects that resample it.</param>
    public abstract void Draw(SKCanvas canvas, SKImage source);

    public abstract Annotation Clone();

    public abstract void Move(float dx, float dy);

    /// <summary>Called while dragging to define the shape. p2 is the live cursor.</summary>
    public abstract void UpdateDrag(SKPoint p1, SKPoint p2);

    public virtual bool HitTest(SKPoint p)
    {
        var b = Bounds;
        b.Inflate(StrokeWidth + 4, StrokeWidth + 4);
        return b.Contains(p);
    }

    protected SKPaint StrokePaint() => new()
    {
        Color = Color,
        StrokeWidth = StrokeWidth,
        IsAntialias = true,
        Style = SKPaintStyle.Stroke,
        StrokeCap = SKStrokeCap.Round,
        StrokeJoin = SKStrokeJoin.Round,
    };
}

/// <summary>Which corner or edge of a placed image a drag is pulling.</summary>
public enum ResizeHandle
{
    TopLeft, Top, TopRight, Right, BottomRight, Bottom, BottomLeft, Left,
}

/// <summary>
/// Another picture dropped onto the canvas, for collages. It is an annotation rather
/// than pixels painted into the base so it stays draggable: baking it in at paste time
/// is what made a pasted image impossible to reposition.
/// </summary>
public sealed class ImageAnnotation : Annotation
{
    /// <summary>
    /// Shared, never disposed by this class. SKImage is immutable, so undo snapshots
    /// can clone the annotation and point at the same pixels — copying the bitmap per
    /// snapshot would cost megabytes a step.
    /// </summary>
    public required SKImage Image { get; init; }

    /// <summary>
    /// Where it sits and how big it is drawn, independent of the source resolution, so
    /// the handles can scale it without touching the pixels. Resampling once at paint
    /// time keeps a shrunk image sharp instead of degrading it on every drag.
    /// </summary>
    public SKRect Rect { get; set; }

    /// <summary>Nothing useful survives below about this, and a zero-size rect cannot be grabbed back.</summary>
    public const float MinimumSize = 16f;

    public override SKRect Bounds => Rect;

    public override void Draw(SKCanvas canvas, SKImage source) =>
        canvas.DrawImage(Image, Rect, Sampling);

    /// <summary>
    /// Linear with mipmaps, not a cubic resampler. Mitchell looks marginally better on
    /// a big downscale and costs several times as much per frame, which is the whole
    /// difference between a smooth drag and a slideshow.
    /// </summary>
    private static readonly SKSamplingOptions Sampling =
        new(SKFilterMode.Linear, SKMipmapMode.Linear);

    public override Annotation Clone() =>
        new ImageAnnotation { Image = Image, Rect = Rect, Color = Color, StrokeWidth = StrokeWidth };

    public override void Move(float dx, float dy) =>
        Rect = SKRect.Create(Rect.Left + dx, Rect.Top + dy, Rect.Width, Rect.Height);

    /// <summary>Placed, not dragged out: paste puts it down at a computed spot.</summary>
    public override void UpdateDrag(SKPoint p1, SKPoint p2) { }

    /// <summary>The picture itself is the target — no stroke to forgive around it.</summary>
    public override bool HitTest(SKPoint p) => Bounds.Contains(p);
}

/// <summary>Base for anything defined by two dragged corners.</summary>
public abstract class TwoPointAnnotation : Annotation
{
    public SKPoint Start { get; set; }
    public SKPoint End { get; set; }

    public override SKRect Bounds => SKRect.Create(
        Math.Min(Start.X, End.X), Math.Min(Start.Y, End.Y),
        Math.Abs(End.X - Start.X), Math.Abs(End.Y - Start.Y));

    public override void UpdateDrag(SKPoint p1, SKPoint p2)
    {
        Start = p1;
        End = p2;
    }

    public override void Move(float dx, float dy)
    {
        Start = new SKPoint(Start.X + dx, Start.Y + dy);
        End = new SKPoint(End.X + dx, End.Y + dy);
    }
}

/// <summary>
/// Tapered arrow: the shaft widens toward the head instead of being a uniform stroke.
/// That taper is most of why the arrow reads as deliberate rather than as MS Paint.
/// </summary>
public sealed class ArrowAnnotation : TwoPointAnnotation
{
    public override void Draw(SKCanvas canvas, SKImage source)
    {
        float dx = End.X - Start.X, dy = End.Y - Start.Y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        if (len < 1f) return;

        float ux = dx / len, uy = dy / len;
        float px = -uy, py = ux;                    // unit normal

        float headLen = Math.Min(len * 0.38f, StrokeWidth * 5.2f);
        float headHalf = headLen * 0.52f;
        float tailHalf = StrokeWidth * 0.18f;
        float shaftHalf = StrokeWidth * 0.62f;

        float nx = End.X - ux * headLen, ny = End.Y - uy * headLen;  // where the head meets the shaft

        using var path = new SKPath();
        path.MoveTo(Start.X + px * tailHalf, Start.Y + py * tailHalf);
        path.LineTo(nx + px * shaftHalf, ny + py * shaftHalf);
        path.LineTo(nx + px * headHalf, ny + py * headHalf);
        path.LineTo(End.X, End.Y);
        path.LineTo(nx - px * headHalf, ny - py * headHalf);
        path.LineTo(nx - px * shaftHalf, ny - py * shaftHalf);
        path.LineTo(Start.X - px * tailHalf, Start.Y - py * tailHalf);
        path.Close();

        using var paint = new SKPaint { Color = Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawPath(path, paint);
    }

    public override Annotation Clone() =>
        new ArrowAnnotation { Start = Start, End = End, Color = Color, StrokeWidth = StrokeWidth };

    public override bool HitTest(SKPoint p)
    {
        // Distance to the segment, so the thin tail is still grabbable.
        float dx = End.X - Start.X, dy = End.Y - Start.Y;
        float lenSq = dx * dx + dy * dy;
        if (lenSq < 1e-3f) return SKPoint.Distance(p, Start) < StrokeWidth * 3;
        float t = Math.Clamp(((p.X - Start.X) * dx + (p.Y - Start.Y) * dy) / lenSq, 0f, 1f);
        var proj = new SKPoint(Start.X + t * dx, Start.Y + t * dy);
        return SKPoint.Distance(p, proj) < StrokeWidth * 3;
    }
}

public sealed class RectangleAnnotation : TwoPointAnnotation
{
    public float CornerRadius { get; set; } = 3f;
    public bool Filled { get; set; }

    public override void Draw(SKCanvas canvas, SKImage source)
    {
        using var paint = Filled
            ? new SKPaint { Color = Color, IsAntialias = true, Style = SKPaintStyle.Fill }
            : StrokePaint();
        canvas.DrawRoundRect(Bounds, CornerRadius, CornerRadius, paint);
    }

    public override bool HitTest(SKPoint p)
    {
        if (Filled) return Bounds.Contains(p);
        var outer = Bounds; outer.Inflate(StrokeWidth + 4, StrokeWidth + 4);
        var inner = Bounds; inner.Inflate(-(StrokeWidth + 4), -(StrokeWidth + 4));
        return outer.Contains(p) && !inner.Contains(p);
    }

    public override Annotation Clone() => new RectangleAnnotation
    {
        Start = Start, End = End, Color = Color, StrokeWidth = StrokeWidth,
        CornerRadius = CornerRadius, Filled = Filled,
    };
}

public sealed class EllipseAnnotation : TwoPointAnnotation
{
    public override void Draw(SKCanvas canvas, SKImage source)
    {
        using var paint = StrokePaint();
        canvas.DrawOval(Bounds, paint);
    }

    public override Annotation Clone() =>
        new EllipseAnnotation { Start = Start, End = End, Color = Color, StrokeWidth = StrokeWidth };
}

public sealed class LineAnnotation : TwoPointAnnotation
{
    public override void Draw(SKCanvas canvas, SKImage source)
    {
        using var paint = StrokePaint();
        canvas.DrawLine(Start, End, paint);
    }

    public override Annotation Clone() =>
        new LineAnnotation { Start = Start, End = End, Color = Color, StrokeWidth = StrokeWidth };
}

public sealed class PenAnnotation : Annotation
{
    public List<SKPoint> Points { get; set; } = [];

    public override SKRect Bounds
    {
        get
        {
            if (Points.Count == 0) return SKRect.Empty;
            float minX = Points[0].X, maxX = minX, minY = Points[0].Y, maxY = minY;
            foreach (var p in Points)
            {
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }
            return new SKRect(minX, minY, maxX, maxY);
        }
    }

    public override void UpdateDrag(SKPoint p1, SKPoint p2)
    {
        // Skip sub-pixel jitter so the smoothed path does not get lumpy.
        if (Points.Count == 0 || SKPoint.Distance(Points[^1], p2) > 1.2f)
            Points.Add(p2);
    }

    public override void Draw(SKCanvas canvas, SKImage source)
    {
        if (Points.Count < 2)
        {
            if (Points.Count == 1)
            {
                using var dot = new SKPaint { Color = Color, IsAntialias = true, Style = SKPaintStyle.Fill };
                canvas.DrawCircle(Points[0], StrokeWidth / 2, dot);
            }
            return;
        }

        using var path = new SKPath();
        path.MoveTo(Points[0]);
        // Quadratic midpoint smoothing — cheap, and kills the polyline faceting.
        for (int i = 1; i < Points.Count - 1; i++)
        {
            var mid = new SKPoint((Points[i].X + Points[i + 1].X) / 2, (Points[i].Y + Points[i + 1].Y) / 2);
            path.QuadTo(Points[i], mid);
        }
        path.LineTo(Points[^1]);

        using var paint = StrokePaint();
        canvas.DrawPath(path, paint);
    }

    public override void Move(float dx, float dy)
    {
        for (int i = 0; i < Points.Count; i++)
            Points[i] = new SKPoint(Points[i].X + dx, Points[i].Y + dy);
    }

    public override bool HitTest(SKPoint p)
    {
        foreach (var q in Points)
            if (SKPoint.Distance(p, q) < StrokeWidth + 5) return true;
        return false;
    }

    public override Annotation Clone() =>
        new PenAnnotation { Points = [.. Points], Color = Color, StrokeWidth = StrokeWidth };
}

/// <summary>Marker-pen highlight: multiply blend so the text underneath stays readable.</summary>
public sealed class HighlightAnnotation : TwoPointAnnotation
{
    public override void Draw(SKCanvas canvas, SKImage source)
    {
        using var paint = new SKPaint
        {
            Color = Color.WithAlpha(0xA0),
            IsAntialias = true,
            Style = SKPaintStyle.Fill,
            BlendMode = SKBlendMode.Multiply,
        };
        canvas.DrawRect(Bounds, paint);
    }

    public override Annotation Clone() =>
        new HighlightAnnotation { Start = Start, End = End, Color = Color, StrokeWidth = StrokeWidth };

    public override bool HitTest(SKPoint p) => Bounds.Contains(p);
}

/// <summary>
/// Redaction. Pixelate downsamples with nearest-neighbour so the blocks are hard-edged
/// and obviously deliberate; Blur is a real gaussian for a softer look. Both resample
/// the flattened image under the mark, so stacking a blur over an arrow hides the arrow too.
/// </summary>
public sealed class ObscureAnnotation : TwoPointAnnotation
{
    public bool Pixelate { get; set; } = true;
    public float Strength { get; set; } = 12f;

    public override void Draw(SKCanvas canvas, SKImage source)
    {
        var rect = Bounds;
        if (rect.Width < 2 || rect.Height < 2) return;

        var src = SKRectI.Round(rect);
        src.Intersect(new SKRectI(0, 0, source.Width, source.Height));
        if (src.Width < 2 || src.Height < 2) return;

        canvas.Save();
        canvas.ClipRect(rect);

        if (Pixelate)
        {
            // Downsample the crop, then blow it back up with nearest-neighbour so the
            // blocks are hard-edged and obviously deliberate.
            int bw = Math.Max(1, (int)(src.Width / Strength));
            int bh = Math.Max(1, (int)(src.Height / Strength));

            using var surface = SKSurface.Create(new SKImageInfo(bw, bh, SKColorType.Bgra8888, SKAlphaType.Premul));
            if (surface is not null)
            {
                surface.Canvas.DrawImage(source, src, new SKRect(0, 0, bw, bh),
                    new SKSamplingOptions(SKFilterMode.Linear), null);
                using var small = surface.Snapshot();
                canvas.DrawImage(small, new SKRect(0, 0, bw, bh), rect,
                    new SKSamplingOptions(SKFilterMode.Nearest), null);
            }
        }
        else
        {
            using var paint = new SKPaint
            {
                ImageFilter = SKImageFilter.CreateBlur(Strength, Strength, SKShaderTileMode.Clamp),
            };
            canvas.DrawImage(source, src, rect, new SKSamplingOptions(SKFilterMode.Linear), paint);
        }

        canvas.Restore();
    }

    public override bool HitTest(SKPoint p) => Bounds.Contains(p);

    public override Annotation Clone() => new ObscureAnnotation
    {
        Start = Start, End = End, Color = Color, StrokeWidth = StrokeWidth,
        Pixelate = Pixelate, Strength = Strength,
    };
}

public sealed class TextAnnotation : Annotation
{
    public SKPoint Position { get; set; }
    public string Text { get; set; } = string.Empty;
    public float FontSize { get; set; } = 24f;
    public bool HasBackdrop { get; set; }

    private SKRect MeasuredBounds()
    {
        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), FontSize);
        var lines = Text.Split('\n');
        float w = 0, lineHeight = font.Spacing;
        foreach (var line in lines)
            w = Math.Max(w, font.MeasureText(line));
        return SKRect.Create(Position.X, Position.Y, w, lineHeight * lines.Length);
    }

    public override SKRect Bounds => MeasuredBounds();

    public override void UpdateDrag(SKPoint p1, SKPoint p2) => Position = p1;

    public override void Draw(SKCanvas canvas, SKImage source)
    {
        if (string.IsNullOrEmpty(Text)) return;

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), FontSize);
        using var paint = new SKPaint { Color = Color, IsAntialias = true };

        if (HasBackdrop)
        {
            var b = Bounds;
            b.Inflate(8, 6);
            using var bg = new SKPaint { Color = new SKColor(0, 0, 0, 0xB0), IsAntialias = true };
            canvas.DrawRoundRect(b, 6, 6, bg);
        }

        var lines = Text.Split('\n');
        float y = Position.Y - font.Metrics.Ascent;
        foreach (var line in lines)
        {
            canvas.DrawText(line, Position.X, y, SKTextAlign.Left, font, paint);
            y += font.Spacing;
        }
    }

    public override void Move(float dx, float dy) =>
        Position = new SKPoint(Position.X + dx, Position.Y + dy);

    public override bool HitTest(SKPoint p)
    {
        var b = Bounds; b.Inflate(8, 6);
        return b.Contains(p);
    }

    public override Annotation Clone() => new TextAnnotation
    {
        Position = Position, Text = Text, FontSize = FontSize,
        Color = Color, StrokeWidth = StrokeWidth, HasBackdrop = HasBackdrop,
    };
}

/// <summary>Numbered badge for walkthroughs. The number is assigned by the document, not the tool.</summary>
public sealed class StepAnnotation : Annotation
{
    public SKPoint Center { get; set; }
    public int Number { get; set; } = 1;
    public float Radius { get; set; } = 18f;

    public override SKRect Bounds =>
        new(Center.X - Radius, Center.Y - Radius, Center.X + Radius, Center.Y + Radius);

    public override void UpdateDrag(SKPoint p1, SKPoint p2) => Center = p2;

    public override void Draw(SKCanvas canvas, SKImage source)
    {
        using var fill = new SKPaint { Color = Color, IsAntialias = true, Style = SKPaintStyle.Fill };
        canvas.DrawCircle(Center, Radius, fill);

        using var ring = new SKPaint
        {
            Color = SKColors.White, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = Math.Max(2f, Radius * 0.12f),
        };
        canvas.DrawCircle(Center, Radius, ring);

        using var font = new SKFont(SKTypeface.FromFamilyName("Segoe UI", SKFontStyle.Bold), Radius * 1.15f);
        using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
        string s = Number.ToString();
        float y = Center.Y - (font.Metrics.Ascent + font.Metrics.Descent) / 2;
        canvas.DrawText(s, Center.X, y, SKTextAlign.Center, font, text);
    }

    public override void Move(float dx, float dy) =>
        Center = new SKPoint(Center.X + dx, Center.Y + dy);

    public override bool HitTest(SKPoint p) => SKPoint.Distance(p, Center) <= Radius + 4;

    public override Annotation Clone() => new StepAnnotation
    {
        Center = Center, Number = Number, Radius = Radius,
        Color = Color, StrokeWidth = StrokeWidth,
    };
}
