using SkiaSharp;

namespace Shotwin.Editor;

/// <summary>
/// The image plus its annotations, with undo/redo.
///
/// Rendering keeps a cached composite of the base image and every committed
/// annotation. The annotation currently being dragged is drawn on top of that cache
/// each frame, so a live drag costs one blit plus one shape instead of re-flattening
/// a 4K image on every mouse-move.
/// </summary>
public sealed class ShotDocument : IDisposable
{
    private readonly List<Annotation> _items = [];
    private readonly Stack<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    /// <summary>
    /// One undo step. Base is null for the common case of an annotation edit, and a
    /// copy of the pixels only when an operation replaces the image itself: cropping,
    /// cutting a band out, or adding another shot to the canvas.
    /// </summary>
    private sealed record Snapshot(SKBitmap? Base, List<Annotation> Items);

    private SKImage? _composite;
    private bool _compositeDirty = true;

    public SKBitmap Base { get; private set; }
    public IReadOnlyList<Annotation> Items => _items;

    /// <summary>The annotation being dragged right now. Not yet in <see cref="Items"/>.</summary>
    public Annotation? Live { get; set; }

    public Annotation? Selected { get; set; }

    public int Width => Base.Width;
    public int Height => Base.Height;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public bool IsDirty { get; private set; }

    public event Action? Changed;

    public ShotDocument(SKBitmap baseBitmap)
    {
        Base = baseBitmap;
    }

    // ---- Mutation ---------------------------------------------------------------

    public void Add(Annotation annotation)
    {
        PushUndo();
        _items.Add(annotation);
        Invalidate();
    }

    public void Remove(Annotation annotation)
    {
        if (!_items.Contains(annotation)) return;
        PushUndo();
        _items.Remove(annotation);
        if (ReferenceEquals(Selected, annotation)) Selected = null;
        Invalidate();
    }

    public void Clear()
    {
        if (_items.Count == 0) return;
        PushUndo();
        _items.Clear();
        Selected = null;
        Invalidate();
    }

    /// <summary>Call before mutating an existing annotation in place (move, restyle).</summary>
    /// <param name="mayResizeCanvas">
    /// True for edits that can push content past the edge, so the snapshot keeps the
    /// base bitmap too and undo restores the old canvas size along with the position.
    /// </param>
    public void BeginEdit(bool mayResizeCanvas = false) => PushUndo(includeBase: mayResizeCanvas);

    public void EndEdit() => Invalidate();

    /// <summary>Where the live annotation came from, so it goes back at the same depth.</summary>
    private int _liveIndex = -1;

    /// <summary>
    /// Lifts an annotation out of the cached composite for the duration of a drag, so
    /// each frame costs one blit of the cache plus that one shape. Without this, moving
    /// anything re-flattened the entire canvas on every mouse-move — unnoticeable for a
    /// small arrow, and unusable once the thing being dragged is a whole screenshot.
    /// </summary>
    public void BeginLive(Annotation annotation)
    {
        _liveIndex = _items.IndexOf(annotation);
        if (_liveIndex < 0) return;

        _items.RemoveAt(_liveIndex);
        Live = annotation;
        Invalidate();
    }

    /// <summary>Puts it back and rebuilds the composite once.</summary>
    /// <param name="raise">
    /// True when it was actually dragged, which puts it on top. A live annotation is
    /// drawn over everything while you hold it, so dropping it back at its old depth
    /// made it duck behind whatever it had just been pulled in front of. A plain click
    /// that selects without moving leaves the stacking alone.
    /// </param>
    public void EndLive(bool raise = false)
    {
        if (Live is null) return;

        if (raise) _items.Add(Live);
        else _items.Insert(Math.Clamp(_liveIndex, 0, _items.Count), Live);

        Live = null;
        _liveIndex = -1;
        Invalidate();
    }

    /// <summary>Next unused badge number, so deleting step 2 renumbers nothing but reuses the gap.</summary>
    public int NextStepNumber()
    {
        int max = 0;
        foreach (var a in _items)
            if (a is StepAnnotation s) max = Math.Max(max, s.Number);
        return max + 1;
    }

    public Annotation? HitTest(SKPoint imagePoint)
    {
        // Topmost first: later annotations are drawn over earlier ones.
        for (int i = _items.Count - 1; i >= 0; i--)
            if (_items[i].HitTest(imagePoint)) return _items[i];
        return null;
    }

    // ---- Undo / redo ------------------------------------------------------------

    private void PushUndo(bool includeBase = false)
    {
        _undo.Push(Capture(includeBase));
        DisposeAll(_redo);
        _redo.Clear();

        // Pixel snapshots are heavy, so the history is shorter than it would be for
        // annotations alone.
        if (_undo.Count > 40) TrimUndo();
    }

    private void TrimUndo()
    {
        var all = _undo.ToArray();
        foreach (var dropped in all.Skip(40)) dropped.Base?.Dispose();

        _undo.Clear();
        foreach (var s in all.Take(40).Reverse()) _undo.Push(s);
    }

    private Snapshot Capture(bool includeBase) =>
        new(includeBase ? Base.Copy() : null, _items.Select(a => a.Clone()).ToList());

    private void Restore(Snapshot snapshot)
    {
        if (snapshot.Base is not null)
        {
            Base.Dispose();
            Base = snapshot.Base;
        }

        _items.Clear();
        _items.AddRange(snapshot.Items);
        Selected = null;
        Invalidate();
    }

    private static void DisposeAll(Stack<Snapshot> stack)
    {
        foreach (var s in stack) s.Base?.Dispose();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;

        var step = _undo.Pop();
        _redo.Push(Capture(step.Base is not null));
        Restore(step);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;

        var step = _redo.Pop();
        _undo.Push(Capture(step.Base is not null));
        Restore(step);
    }

    // ---- Operations that replace the image --------------------------------------

    /// <summary>
    /// Keeps only the given region. Annotations move with the pixels rather than being
    /// discarded, so an arrow drawn before cropping still points at the same thing.
    /// </summary>
    public bool Crop(SKRectI region)
    {
        region.Intersect(new SKRectI(0, 0, Base.Width, Base.Height));
        if (region.Width < 2 || region.Height < 2) return false;
        if (region.Width == Base.Width && region.Height == Base.Height) return false;

        PushUndo(includeBase: true);

        var cropped = new SKBitmap(new SKImageInfo(
            region.Width, region.Height, Base.ColorType, Base.AlphaType));

        using (var canvas = new SKCanvas(cropped))
        using (var source = SKImage.FromBitmap(Base))
        {
            canvas.DrawImage(source,
                new SKRect(region.Left, region.Top, region.Right, region.Bottom),
                new SKRect(0, 0, region.Width, region.Height));
        }

        Base.Dispose();
        Base = cropped;

        foreach (var a in _items) a.Move(-region.Left, -region.Top);

        Invalidate();
        return true;
    }

    /// <summary>
    /// Removes a band and closes the gap, the way you would cut a strip out of a printed
    /// page and tape the halves together. It is how you delete the dead space in the
    /// middle of a long screenshot without losing either end.
    /// </summary>
    public bool CutOut(SKRectI band, bool horizontal)
    {
        band.Intersect(new SKRectI(0, 0, Base.Width, Base.Height));

        int removed = horizontal ? band.Height : band.Width;
        if (removed < 2) return false;

        int width = horizontal ? Base.Width : Base.Width - removed;
        int height = horizontal ? Base.Height - removed : Base.Height;
        if (width < 2 || height < 2) return false;

        PushUndo(includeBase: true);

        var result = new SKBitmap(new SKImageInfo(width, height, Base.ColorType, Base.AlphaType));
        using (var canvas = new SKCanvas(result))
        using (var source = SKImage.FromBitmap(Base))
        {
            canvas.Clear(SKColors.Black);

            if (horizontal)
            {
                // Everything above the band, then everything below it pulled up.
                canvas.DrawImage(source,
                    new SKRect(0, 0, Base.Width, band.Top),
                    new SKRect(0, 0, Base.Width, band.Top));
                canvas.DrawImage(source,
                    new SKRect(0, band.Bottom, Base.Width, Base.Height),
                    new SKRect(0, band.Top, Base.Width, height));
            }
            else
            {
                canvas.DrawImage(source,
                    new SKRect(0, 0, band.Left, Base.Height),
                    new SKRect(0, 0, band.Left, Base.Height));
                canvas.DrawImage(source,
                    new SKRect(band.Right, 0, Base.Width, Base.Height),
                    new SKRect(band.Left, 0, width, Base.Height));
            }
        }

        Base.Dispose();
        Base = result;

        // Annotations past the cut slide back by the width of what was removed.
        foreach (var a in _items)
        {
            var bounds = a.Bounds;
            if (horizontal && bounds.Top >= band.Bottom) a.Move(0, -removed);
            else if (!horizontal && bounds.Left >= band.Right) a.Move(-removed, 0);
        }

        Invalidate();
        return true;
    }

    /// <summary>
    /// Grows the canvas and drops another image alongside, for stitching two shots into
    /// one. Existing annotations keep their coordinates, so nothing shifts under them.
    /// </summary>
    public bool AddImage(SKBitmap other, bool toTheRight, int gap = 12)
    {
        if (other.Width < 1 || other.Height < 1) return false;

        PushUndo(includeBase: true);

        // The shot already on the canvas becomes a movable object too, the moment there
        // is anything to arrange it against. A collage of one fixed backdrop and one
        // movable guest is not a collage.
        if (!_items.Any(i => i is ImageAnnotation)) LiftBaseIntoAnnotation();

        int width = toTheRight ? Base.Width + gap + other.Width : Math.Max(Base.Width, other.Width);
        int height = toTheRight ? Math.Max(Base.Height, other.Height) : Base.Height + gap + other.Height;

        // Centre the shorter one across the shared axis.
        float x = toTheRight ? Base.Width + gap : (width - other.Width) / 2f;
        float y = toTheRight ? (height - other.Height) / 2f : Base.Height + gap;

        // The canvas grows to make room, but only the original is painted into it. The
        // new picture goes on as an annotation, which is what keeps it draggable — it
        // used to be drawn in here and was then unreachable pixels.
        var result = new SKBitmap(new SKImageInfo(width, height, Base.ColorType, Base.AlphaType));
        using (var canvas = new SKCanvas(result))
        {
            // The gap reads as a seam rather than as part of either shot.
            canvas.Clear(new SKColor(0x14, 0x14, 0x17));
            canvas.DrawBitmap(Base, 0, 0);
        }

        Base.Dispose();
        Base = result;

        var placed = new ImageAnnotation
        {
            Image = SKImage.FromBitmap(other.Copy()),
            Rect = SKRect.Create(x, y, other.Width, other.Height),
        };

        _items.Add(placed);
        Selected = placed;

        Invalidate();
        return true;
    }

    /// <summary>
    /// Turns the backdrop into an ImageAnnotation at the origin and leaves the base
    /// bitmap as empty canvas. Inserted first so everything already drawn on the shot
    /// still composites over it, and placed at 0,0 so nothing appears to move.
    /// </summary>
    private void LiftBaseIntoAnnotation()
    {
        _items.Insert(0, new ImageAnnotation
        {
            Image = SKImage.FromBitmap(Base.Copy()),
            Rect = SKRect.Create(0, 0, Base.Width, Base.Height),
        });

        using var canvas = new SKCanvas(Base);
        canvas.Clear(new SKColor(0x14, 0x14, 0x17));
    }

    /// <summary>
    /// Grows the canvas so nothing placed on it hangs over an edge. The composite is
    /// rendered at exactly the base bitmap's size, so an image dragged past the border
    /// was simply clipped away — the canvas has to follow the content.
    ///
    /// Expanding upward or to the left moves the origin, so everything already on the
    /// canvas shifts by the same amount to stay where it looks like it is.
    /// </summary>
    /// <returns>True if the canvas changed size.</returns>
    public bool FitCanvasToContent()
    {
        // Until the shot has been lifted into an object the base bitmap *is* the
        // content, and the canvas must not shrink away from it.
        if (!_items.Any(a => a is ImageAnnotation)) return false;

        // Past that point the base is blank filler, so it is deliberately left out of
        // the union: including it would pin the canvas at its largest size ever and
        // leave dead margins behind whenever an image is moved back inwards.
        SKRect? content = null;
        foreach (var a in _items)
            content = content is { } so_far ? SKRect.Union(so_far, a.Bounds) : a.Bounds;

        if (content is not { } union || union.Width < 1 || union.Height < 1) return false;

        int left = (int)Math.Floor(union.Left);
        int top = (int)Math.Floor(union.Top);
        int right = (int)Math.Ceiling(union.Right);
        int bottom = (int)Math.Ceiling(union.Bottom);

        int width = right - left, height = bottom - top;
        if (left == 0 && top == 0 && width == Base.Width && height == Base.Height) return false;
        if (width < 1 || height < 1) return false;

        var grown = new SKBitmap(new SKImageInfo(width, height, Base.ColorType, Base.AlphaType));
        using (var canvas = new SKCanvas(grown))
        {
            // Same seam colour the collage gap uses, so new space matches the old.
            canvas.Clear(new SKColor(0x14, 0x14, 0x17));
            canvas.DrawBitmap(Base, -left, -top);
        }

        Base.Dispose();
        Base = grown;

        if (left != 0 || top != 0)
            foreach (var a in _items) a.Move(-left, -top);

        Invalidate();
        return true;
    }

    // ---- Rendering --------------------------------------------------------------

    public void Invalidate()
    {
        _compositeDirty = true;
        IsDirty = true;
        Changed?.Invoke();
    }

    /// <summary>Base image plus every committed annotation, cached until the document changes.</summary>
    public SKImage Composite()
    {
        if (!_compositeDirty && _composite is not null) return _composite;

        _composite?.Dispose();

        var info = new SKImageInfo(Base.Width, Base.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(Base, 0, 0);

        using var baseImage = SKImage.FromBitmap(Base);
        SKImage? below = null;
        foreach (var a in _items)
        {
            // Only redaction needs to see what it is covering; snapshotting is the
            // expensive part, so we pay for it exactly once per obscure annotation.
            if (a is ObscureAnnotation)
            {
                below?.Dispose();
                below = surface.Snapshot();
                a.Draw(canvas, below);
            }
            else
            {
                a.Draw(canvas, below ?? baseImage);
            }
        }
        below?.Dispose();

        _composite = surface.Snapshot();
        _compositeDirty = false;
        return _composite;
    }

    /// <summary>Draws the document into an image-space canvas (1 unit = 1 image pixel).</summary>
    public void Render(SKCanvas canvas)
    {
        var composite = Composite();
        canvas.DrawImage(composite, 0, 0);
        Live?.Draw(canvas, composite);
    }

    /// <summary>
    /// Everything flattened, ready to encode or put on the clipboard.
    ///
    /// Always a fresh image the caller owns and disposes. Handing back the cached
    /// composite instead would let a single `using` on a save or copy dispose the
    /// cache out from under the next repaint.
    /// </summary>
    public SKImage Flatten()
    {
        var info = new SKImageInfo(Base.Width, Base.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        Render(surface.Canvas);
        return surface.Snapshot();
    }

    public void MarkSaved() => IsDirty = false;

    public void Dispose()
    {
        _composite?.Dispose();
        DisposeAll(_undo);
        DisposeAll(_redo);
        Base.Dispose();
    }
}
