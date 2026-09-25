using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Shotwin.Capture;
using Shotwin.Services;
using SkiaSharp;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Overlay;

/// <summary>
/// Full-virtual-desktop selection overlay drawn over a frozen screenshot.
///
/// Coordinate rule for this whole file: the Skia canvas is in PHYSICAL PIXELS and its
/// origin is the top-left of the frozen bitmap. WPF hands us mouse positions in DIPs,
/// so every input point goes through <see cref="ToImage"/> first. Absolute virtual-desktop
/// coordinates (what Win32 reports for windows and the cursor) are image coordinates
/// plus <see cref="_originX"/>/<see cref="_originY"/>, which are negative when a
/// secondary monitor sits left of or above the primary.
/// </summary>
public partial class OverlayWindow : Window, Services.IFixedFlowDirection
{
    private enum OverlayState { Idle, Dragging }

    /// <summary>
    /// What this overlay is for. Area is the ordinary drag-a-region capture, and
    /// ScrollingArea and RecordArea pick their region exactly the same way — the
    /// difference is only in what happens after the drag ends.
    /// </summary>
    public enum OverlayMode { Area, ColourPick, ScrollingArea, RecordArea }

    private readonly SKBitmap _frozen;
    private readonly int _originX;
    private readonly int _originY;
    private readonly List<CapturableWindow> _windows;
    private readonly OverlayMode _mode;

    /// <summary>The frozen desktop as an image. It shares the bitmap's pixels, not a copy.</summary>
    private readonly SKImage _frozenImage;

    /// <summary>
    /// What is on screen, at the frozen desktop's size. Only the parts that changed are
    /// redrawn and marked dirty, so WPF uploads those rectangles and nothing else.
    /// </summary>
    private readonly WriteableBitmap _buffer;

    /// <summary>Everything the last paint drew on top of the desktop, to be cleared by the next.</summary>
    private readonly SKRegion _chrome = new();

    /// <summary>The undimmed rect the buffer currently shows, if any.</summary>
    private SKRectI? _paintedHole;

    private bool _paintQueued;

    private double _scale = 1.0;
    private OverlayState _state = OverlayState.Idle;

    private SKPoint _dragStart;
    private SKPoint _dragCurrent;
    private SKPoint _cursor;
    private bool _cursorInside;

    private CapturableWindow? _hoveredWindow;
    private SKRectI? _hoveredBounds;

    /// <summary>Set while Space is held so the whole selection moves instead of resizing.</summary>
    private bool _panning;
    private SKPoint _panAnchor;

    private bool _completed;
    private bool _everActivated;

    /// <summary>Fires with the cropped image and its absolute virtual-desktop rect.</summary>
    public event Action<SKBitmap, SKRectI>? Captured;

    /// <summary>Fires with the colour under the cursor when picking.</summary>
    public event Action<SKColor>? ColourPicked;

    /// <summary>
    /// Fires with the chosen rect in absolute virtual-desktop pixels when scrolling.
    /// No image comes with it: the frozen desktop is a still of the page before it
    /// scrolled, so the session has to go and grab the region live.
    /// </summary>
    public event Action<SKRectI>? ScrollingAreaChosen;

    /// <summary>
    /// Fires with the chosen rect in absolute virtual-desktop pixels when recording.
    /// Like <see cref="ScrollingAreaChosen"/> it carries no image: what the recorder
    /// wants is the live screen from this moment on, not the still behind the overlay.
    /// </summary>
    public event Action<SKRectI>? RecordAreaChosen;

    public OverlayWindow(SKBitmap frozen, int originX, int originY, OverlayMode mode)
    {
        InitializeComponent();

        _frozen = frozen;
        _originX = originX;
        _originY = originY;
        _mode = mode;

        // Nothing writes to the frozen desktop again. Saying so lets Skia draw it as it
        // is; a mutable bitmap gets copied, whole, every time it is drawn.
        _frozen.SetImmutable();
        _frozenImage = SKImage.FromBitmap(frozen);
        _windows = WindowEnumerator.Enumerate(IntPtr.Zero);

        _buffer = BufferFor(frozen.Width, frozen.Height);
        Surface.Source = _buffer;
        Paint(everything: true);

        Loaded += OnLoaded;
        MouseMove += OnMouseMove;
        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseLeftButtonUp += OnMouseLeftButtonUp;
        MouseRightButtonDown += (_, _) => Cancel();
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;
        KeyUp += OnKeyUp;

        // Losing focus means the user went elsewhere, so get out of their way — but only
        // once the overlay has actually held focus. Treating the never-activated state as
        // "deactivated" would close it the instant it opened.
        Activated += (_, _) => _everActivated = true;
        Deactivated += (_, _) => { if (_everActivated && !_completed) Cancel(); };
    }

    // ---- Window placement -------------------------------------------------------

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var dpi = VisualTreeHelper.GetDpi(this);
        _scale = dpi.DpiScaleX;

        // WPF sizes windows in DIPs, which cannot express a mixed-DPI virtual desktop
        // exactly. Place it with Win32 in raw pixels instead and let WPF follow.
        var handle = new WindowInteropHelper(this).Handle;
        SetWindowPos(handle, HWND_TOPMOST, _originX, _originY, _frozen.Width, _frozen.Height,
            SWP_SHOWWINDOW);

        // It covers every pixel of the desktop; rounded corners would leak the
        // wallpaper through and lose four bits of the shot.
        Interop.WindowCorners.ApplySquare(this);

        // The overlay is opened from a tray process that Windows considers a background
        // app, so it does not get the foreground for free. Without this it paints on top
        // but never receives the keyboard, and Esc goes to whatever was focused before.
        SetForegroundWindow(handle);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Activate();
        Focus();
        Keyboard.Focus(this);
        Mouse.Capture(this);

        if (GetCursorPos(out POINT p))
        {
            _cursor = new SKPoint(p.X - _originX, p.Y - _originY);
            _cursorInside = true;
            UpdateHover();
        }
        RequestPaint();
    }

    // ---- Input ------------------------------------------------------------------

    private SKPoint ToImage(Point dip) => new((float)(dip.X * _scale), (float)(dip.Y * _scale));

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = ToImage(e.GetPosition(Surface));
        _cursorInside = true;

        if (_state == OverlayState.Dragging)
        {
            if (_panning)
            {
                float dx = p.X - _panAnchor.X, dy = p.Y - _panAnchor.Y;
                _dragStart = new SKPoint(_dragStart.X + dx, _dragStart.Y + dy);
                _dragCurrent = new SKPoint(_dragCurrent.X + dx, _dragCurrent.Y + dy);
                _panAnchor = p;
            }
            else
            {
                _dragCurrent = ApplyModifiers(p);
            }
        }
        else
        {
            _cursor = p;
            UpdateHover();
        }

        if (_state == OverlayState.Dragging) _cursor = p;
        RequestPaint();
    }

    /// <summary>Shift constrains to a square; Alt grows the selection from its centre.</summary>
    private SKPoint ApplyModifiers(SKPoint p)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        if (!shift) return p;

        float dx = p.X - _dragStart.X, dy = p.Y - _dragStart.Y;
        float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new SKPoint(_dragStart.X + Math.Sign(dx) * side, _dragStart.Y + Math.Sign(dy) * side);
    }

    /// <summary>Copies the sampled colour and closes. The shot itself is never taken.</summary>
    private void PickColourAt(SKPoint p)
    {
        int x = (int)p.X, y = (int)p.Y;
        if (x < 0 || y < 0 || x >= _frozen.Width || y >= _frozen.Height) return;

        var colour = _frozen.GetPixel(x, y);

        _completed = true;
        Mouse.Capture(null);
        Close();

        ColourPicked?.Invoke(colour);
    }

    private void UpdateHover()
    {
        if (_mode == OverlayMode.ColourPick) { _hoveredWindow = null; _hoveredBounds = null; return; }
        if (!SettingsService.Current.SnapToWindows)
        {
            _hoveredWindow = null;
            _hoveredBounds = null;
            return;
        }

        int ax = (int)_cursor.X + _originX;
        int ay = (int)_cursor.Y + _originY;

        var hit = WindowEnumerator.HitTest(_windows, ax, ay);
        if (hit is null)
        {
            _hoveredWindow = null;
            _hoveredBounds = null;
            return;
        }

        _hoveredWindow = hit;
        _hoveredBounds = ClampToImage(new SKRectI(
            hit.Bounds.Left - _originX, hit.Bounds.Top - _originY,
            hit.Bounds.Right - _originX, hit.Bounds.Bottom - _originY));
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var p = ToImage(e.GetPosition(Surface));

        if (_mode == OverlayMode.ColourPick)
        {
            PickColourAt(p);
            return;
        }

        _state = OverlayState.Dragging;
        _dragStart = p;
        _dragCurrent = p;
        _cursor = p;
        RequestPaint();
    }

    private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_state != OverlayState.Dragging) return;
        _state = OverlayState.Idle;

        var rect = CurrentSelection();

        // A click rather than a drag: fall back to the window under the cursor,
        // which is how you grab a dialog without tracing its edges.
        if (rect.Width < 4 || rect.Height < 4)
        {
            if (_hoveredBounds is { } bounds && SettingsService.Current.SnapToWindows)
            {
                Complete(bounds);
                return;
            }
            RequestPaint();
            return;
        }

        Complete(rect);
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Reserved for magnifier zoom; consumed so the frozen desktop never scrolls.
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;

            case Key.Space when _state == OverlayState.Dragging && !_panning:
                _panning = true;
                _panAnchor = _cursor;
                e.Handled = true;
                break;

            case Key.Enter when _state == OverlayState.Idle && _hoveredBounds is { } hovered:
                Complete(hovered);
                e.Handled = true;
                break;

            case Key.A when (Keyboard.Modifiers & ModifierKeys.Control) != 0:
                // Ctrl+A grabs the monitor the cursor is on, not the whole virtual desktop.
                // Deliberately not limited to the capture modes: Complete routes the rect
                // by mode, and in RecordArea this is the only way to record a whole screen.
                var monitor = Monitors.FromPoint((int)_cursor.X + _originX, (int)_cursor.Y + _originY);
                Complete(ClampToImage(new SKRectI(
                    monitor.Left - _originX, monitor.Top - _originY,
                    monitor.Bounds.Right - _originX, monitor.Bounds.Bottom - _originY)));
                e.Handled = true;
                break;
        }
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space)
        {
            _panning = false;
            e.Handled = true;
        }
    }

    // ---- Result -----------------------------------------------------------------

    private SKRectI CurrentSelection()
    {
        var r = new SKRectI(
            (int)Math.Round(Math.Min(_dragStart.X, _dragCurrent.X)),
            (int)Math.Round(Math.Min(_dragStart.Y, _dragCurrent.Y)),
            (int)Math.Round(Math.Max(_dragStart.X, _dragCurrent.X)),
            (int)Math.Round(Math.Max(_dragStart.Y, _dragCurrent.Y)));
        return ClampToImage(r);
    }

    private SKRectI ClampToImage(SKRectI r)
    {
        var clamped = r;
        clamped.Intersect(new SKRectI(0, 0, _frozen.Width, _frozen.Height));
        return clamped;
    }

    private void Complete(SKRectI imageRect)
    {
        if (imageRect.Width < 1 || imageRect.Height < 1)
        {
            Cancel();
            return;
        }

        _completed = true;

        var absolute = new SKRectI(
            imageRect.Left + _originX, imageRect.Top + _originY,
            imageRect.Right + _originX, imageRect.Bottom + _originY);

        // Both of these want the rect and nothing else, so neither crops the frozen
        // bitmap: by the time they run, the screen has moved on from it.
        if (_mode is OverlayMode.ScrollingArea or OverlayMode.RecordArea)
        {
            Mouse.Capture(null);
            Close();

            if (_mode == OverlayMode.ScrollingArea) ScrollingAreaChosen?.Invoke(absolute);
            else RecordAreaChosen?.Invoke(absolute);
            return;
        }

        var crop = new SKBitmap(new SKImageInfo(imageRect.Width, imageRect.Height,
            SKColorType.Bgra8888, SKAlphaType.Opaque));
        _frozen.ExtractSubset(crop, imageRect);
        var copy = crop.Copy();   // detach from the frozen bitmap before it is disposed
        crop.Dispose();

        Mouse.Capture(null);
        Close();
        Captured?.Invoke(copy, absolute);
    }

    private void Cancel()
    {
        if (_completed) return;
        _completed = true;
        Mouse.Capture(null);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        CompositionTarget.Rendering -= OnRendering;
        _paintQueued = true;   // nothing paints into a closed window
        _chrome.Dispose();
        _frozenImage.Dispose();
        _frozen.Dispose();
    }

    // ---- Warm-up ----------------------------------------------------------------

    /// <summary>
    /// The buffer every overlay paints into, kept between captures. A new one each time
    /// is a desktop-sized allocation on the path between the shortcut and the overlay,
    /// and only one overlay is ever open, so there is never a second one to want.
    /// </summary>
    private static WriteableBitmap? _sharedBuffer;

    private static WriteableBitmap BufferFor(int width, int height)
    {
        if (_sharedBuffer is { } buffer && buffer.PixelWidth == width && buffer.PixelHeight == height)
            return buffer;

        _sharedBuffer = new WriteableBitmap(width, height, 96, 96, PixelFormats.Pbgra32, null);
        return _sharedBuffer;
    }

    /// <summary>
    /// Does once, at startup, what the first capture would otherwise do while the user
    /// waits on it: load Skia and HarfBuzz, read the label fonts and build their shapers,
    /// and set aside the buffer. Without it the first shortcut after launch took three
    /// times as long to put the overlay up as every one after it.
    /// </summary>
    public static void Warm()
    {
        var info = new SKImageInfo(256, 64, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var canvas = surface.Canvas;

        using (var desktop = new SKBitmap(info.WithAlphaType(SKAlphaType.Opaque)))
        {
            desktop.SetImmutable();
            using var image = SKImage.FromBitmap(desktop);
            canvas.DrawImage(image, 0, 0);
        }

        DrawDimming(canvas, info, new SKRectI(16, 16, 48, 48));

        // The coordinate chip is plain Latin; a window title rarely is. The en dash in an
        // app's title bar is enough to send it to a fallback font with its own shaper.
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (string text in new[] { "1920, 1080", "Shotwin – Overlay" })
        {
            using var font = LabelFont(text);
            canvas.DrawRoundRect(new SKRect(0, 0, ShapedText.Measure(text, font), LabelHeight), 4, 4, paint);
            ShapedText.Draw(canvas, text, 0, 14.5f, font, paint);
        }

        _ = WindowEnumerator.Enumerate(IntPtr.Zero);

        var (_, _, width, height) = Monitors.VirtualDesktop;
        if (width > 0 && height > 0) BufferFor(width, height);
    }

    // ---- Painting ---------------------------------------------------------------

    private static readonly SKColor Accent = new(0x3D, 0x8B, 0xFD);

    /// <summary>
    /// Paints at most once per frame, however many mouse moves arrive in between. A
    /// gaming mouse reports far more often than any screen refreshes.
    /// </summary>
    private void RequestPaint()
    {
        if (_paintQueued) return;
        _paintQueued = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        CompositionTarget.Rendering -= OnRendering;
        _paintQueued = false;
        Paint();
    }

    /// <summary>
    /// Brings the buffer up to date by redrawing only what changed: the desktop comes
    /// back wherever the last paint drew chrome or the dimming moved, then the chrome
    /// is drawn again where it now belongs. Moving the cursor touches a few thousand
    /// pixels instead of the whole desktop, and dragging touches the strips the
    /// selection's edges passed over.
    /// </summary>
    private void Paint(bool everything = false)
    {
        var bounds = new SKRectI(0, 0, _frozen.Width, _frozen.Height);
        var hole = Hole();

        using var stale = new SKRegion();
        if (everything)
        {
            stale.SetRect(bounds);
        }
        else
        {
            stale.SetRegion(_chrome);

            // The dimming changes only inside one hole or the other, not inside both.
            using var moved = new SKRegion();
            if (_paintedHole is { } was) moved.Op(was, SKRegionOperation.XOR);
            if (hole is { } now) moved.Op(now, SKRegionOperation.XOR);
            stale.Op(moved, SKRegionOperation.Union);
        }

        var info = new SKImageInfo(_buffer.PixelWidth, _buffer.PixelHeight,
            SKColorType.Bgra8888, SKAlphaType.Premul);

        _buffer.Lock();
        try
        {
            using (var surface = SKSurface.Create(info, _buffer.BackBuffer, _buffer.BackBufferStride))
            {
                var canvas = surface.Canvas;

                canvas.Save();
                canvas.ClipRegion(stale);
                canvas.DrawImage(_frozenImage, 0, 0);

                // No dimming when picking a colour: the point is to read the real colours.
                if (_mode != OverlayMode.ColourPick) DrawDimming(canvas, info, hole);
                canvas.Restore();

                _chrome.SetRect(SKRectI.Empty);
                DrawChrome(canvas, info);
                _chrome.Op(bounds, SKRegionOperation.Intersect);
            }

            stale.Op(_chrome, SKRegionOperation.Union);
            stale.Op(bounds, SKRegionOperation.Intersect);

            using var rects = stale.CreateRectIterator();
            while (rects.Next(out var r))
                _buffer.AddDirtyRect(new Int32Rect(r.Left, r.Top, r.Width, r.Height));
        }
        finally
        {
            _buffer.Unlock();
        }

        _paintedHole = hole;
    }

    /// <summary>
    /// The part left undimmed: the selection being dragged, or else the window under
    /// the cursor. Null when there is none, or it has no area yet.
    /// </summary>
    private SKRectI? Hole()
    {
        if (_mode == OverlayMode.ColourPick) return null;

        SKRectI? r = _state == OverlayState.Dragging ? CurrentSelection() : _hoveredBounds;
        return r is { Width: > 0, Height: > 0 } ? r : null;
    }

    /// <summary>
    /// Everything drawn over the desktop. Each piece reports where it lands through
    /// <see cref="Mark"/>, so the next paint knows what to clear.
    /// </summary>
    private void DrawChrome(SKCanvas canvas, SKImageInfo info)
    {
        if (_mode == OverlayMode.ColourPick)
        {
            // The lens is the pointer in this mode, so nothing else is drawn at the
            // cursor: no crosshair, no chip beside it.
            if (_cursorInside) DrawLoupe(canvas, info);
            return;
        }

        SKRectI? selection = _state == OverlayState.Dragging ? CurrentSelection() : null;

        if (selection is { } sel)
        {
            DrawSelectionChrome(canvas, sel);
        }
        else if (_hoveredBounds is { } hover)
        {
            DrawSelectionChrome(canvas, hover);
            DrawWindowLabel(canvas, hover);
        }

        if (HintKey() is { } hint)
            DrawHint(canvas, hint);

        if (_cursorInside)
            DrawCursor(canvas, info, selection);
    }

    /// <summary>
    /// Records that something was drawn inside this rect. The margin covers the
    /// antialiased fringe, which lands outside the geometry it was asked for.
    /// </summary>
    private void Mark(SKRect r, float margin = 2)
    {
        _chrome.Op(new SKRectI(
            (int)MathF.Floor(r.Left - margin), (int)MathF.Floor(r.Top - margin),
            (int)MathF.Ceiling(r.Right + margin), (int)MathF.Ceiling(r.Bottom + margin)),
            SKRegionOperation.Union);
    }

    /// <summary>
    /// The modes that have to say something, and nothing for the ones that do not.
    /// Every other overlay ends when the drag does; these two carry on working on the
    /// region afterwards, and nothing on screen would otherwise explain why the page is
    /// moving on its own or why a bar has appeared in the corner.
    /// </summary>
    private string? HintKey() => _mode switch
    {
        OverlayMode.ScrollingArea => "OverlayScrollingHint",
        OverlayMode.RecordArea => "RecordOverlayHint",
        _ => null,
    };

    private void DrawHint(SKCanvas canvas, string key)
    {
        // Drawn with Skia, not laid out by Windows, so this one is neither mirrored nor
        // shaped for a right-to-left script: see the note on the resource entry.
        string Text = Services.Localisation.Get(key);

        // On the monitor being used, not the primary one, and near its top edge so the
        // chip is nowhere near a region someone is about to drag out.
        var monitor = Monitors.FromPoint((int)_cursor.X + _originX, (int)_cursor.Y + _originY);
        float left = monitor.Left - _originX + (monitor.Width - LabelWidth(Text)) / 2f;

        DrawLabel(canvas, Text, new SKPoint(left, monitor.Top - _originY + 40));
    }

    private static void DrawDimming(SKCanvas canvas, SKImageInfo info, SKRectI? hole)
    {
        using var dim = new SKPaint { Color = new SKColor(0, 0, 0, 0x7A) };

        if (hole is not { } r || r.Width <= 0 || r.Height <= 0)
        {
            canvas.DrawRect(new SKRect(0, 0, info.Width, info.Height), dim);
            return;
        }

        // Four bands around the hole; cheaper and crisper than a clipped path.
        canvas.DrawRect(new SKRect(0, 0, info.Width, r.Top), dim);
        canvas.DrawRect(new SKRect(0, r.Bottom, info.Width, info.Height), dim);
        canvas.DrawRect(new SKRect(0, r.Top, r.Left, r.Bottom), dim);
        canvas.DrawRect(new SKRect(r.Right, r.Top, info.Width, r.Bottom), dim);
    }

    /// <summary>
    /// A plain border and nothing else. Resize handles belong on a selection you can
    /// come back and adjust; here the drag ends and the shot is taken, so eight dots
    /// are just decoration sitting on top of the pixels you are trying to frame.
    /// </summary>
    private void DrawSelectionChrome(SKCanvas canvas, SKRectI r)
    {
        using var border = new SKPaint
        {
            Color = Accent,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = false,
        };
        var edge = new SKRect(r.Left - 1, r.Top - 1, r.Right + 1, r.Bottom + 1);
        canvas.DrawRect(edge, border);

        // The four sides, not the rect: marking the whole selection would redraw all
        // of it on every move of a drag, which is the cost this is here to avoid.
        Mark(new SKRect(edge.Left, edge.Top, edge.Right, edge.Top));
        Mark(new SKRect(edge.Left, edge.Bottom, edge.Right, edge.Bottom));
        Mark(new SKRect(edge.Left, edge.Top, edge.Left, edge.Bottom));
        Mark(new SKRect(edge.Right, edge.Top, edge.Right, edge.Bottom));
    }

    /// <summary>
    /// A small crosshair with a coordinate chip beside it, and nothing else.
    ///
    /// Full-width guide lines and a zoom loupe both sound helpful and are not: on a
    /// 3440x1440 desktop the lines cut the screen into quarters and the loupe covers
    /// the very thing you are trying to frame. A cursor and a number are enough,
    /// so that is what this shows.
    /// </summary>
    private void DrawCursor(SKCanvas canvas, SKImageInfo info, SKRectI? selection, bool withChip = true)
    {
        const float Arm = 9f;
        const float Gap = 3.5f;

        // Half-pixel offset so the 1px strokes land on pixel centres instead of blurring.
        float x = MathF.Round(_cursor.X) + 0.5f;
        float y = MathF.Round(_cursor.Y) + 0.5f;

        // Dark pass first, white on top: legible over a white page and a black terminal alike.
        using var halo = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 0xB4), StrokeWidth = 3f,
            IsAntialias = true, StrokeCap = SKStrokeCap.Round,
        };
        using var line = new SKPaint
        {
            Color = SKColors.White, StrokeWidth = 1.3f,
            IsAntialias = true, StrokeCap = SKStrokeCap.Round,
        };

        DrawArms(halo);
        DrawArms(line);
        Mark(new SKRect(x - Gap - Arm, y - Gap - Arm, x + Gap + Arm, y + Gap + Arm), margin: 3);

        void DrawArms(SKPaint paint)
        {
            canvas.DrawLine(x - Gap - Arm, y, x - Gap, y, paint);
            canvas.DrawLine(x + Gap, y, x + Gap + Arm, y, paint);
            canvas.DrawLine(x, y - Gap - Arm, x, y - Gap, paint);
            canvas.DrawLine(x, y + Gap, x, y + Gap + Arm, paint);
        }

        if (!withChip || !SettingsService.Current.ShowCoordinates) return;

        string text = selection is { } sel && (sel.Width > 0 || sel.Height > 0)
            ? $"{sel.Width} x {sel.Height}"
            : $"{(int)_cursor.X + _originX}, {(int)_cursor.Y + _originY}";

        DrawLabel(canvas, text, ChipSpot(info, text));
    }

    /// <summary>Keeps the chip beside the cursor and on screen near the edges.</summary>
    private SKPoint ChipSpot(SKImageInfo info, string text)
    {
        const float Offset = 15f;

        float width = LabelWidth(text);
        float left = _cursor.X + Offset;
        float top = _cursor.Y + Offset;

        if (left + width > info.Width) left = _cursor.X - Offset - width;
        if (top + LabelHeight > info.Height) top = _cursor.Y - Offset - LabelHeight;

        return new SKPoint(Math.Max(2, left), Math.Max(2, top));
    }

    /// <summary>
    /// Zoomed pixel grid with the exact value under the cursor. This is the one place a
    /// loupe belongs: when the whole task is reading a single pixel, magnifying it is
    /// the feature rather than something covering the shot.
    /// </summary>
    private void DrawLoupe(SKCanvas canvas, SKImageInfo info)
    {
        const int Zoom = 10;
        const int Box = 150;
        const int Samples = Box / Zoom;

        int cx = (int)_cursor.X, cy = (int)_cursor.Y;
        if (cx < 0 || cy < 0 || cx >= _frozen.Width || cy >= _frozen.Height) return;

        // Centred on the cursor: the lens replaces the pointer rather than trailing it,
        // so the pixel inside the centre ring is literally the one being pointed at.
        // Deliberately not clamped to the screen — nudging it back from an edge would
        // break that correspondence, and a half-clipped lens is the honest result.
        float left = _cursor.X - Box / 2f;
        float top = _cursor.Y - Box / 2f;

        var box = new SKRect(left, top, left + Box, top + Box);
        var src = new SKRect(cx - Samples / 2f, cy - Samples / 2f,
                             cx + Samples / 2f, cy + Samples / 2f);

        var colour = _frozen.GetPixel(cx, cy);

        float radius = Box / 2f;
        float centreX = left + radius, centreY = top + radius;

        // Round, not square: a loupe is a lens, and the circle also leaves the corners
        // of the screen visible instead of blanking four bits of what you are sampling.
        canvas.Save();
        using (var lens = new SKPath())
        {
            lens.AddCircle(centreX, centreY, radius);
            canvas.ClipPath(lens, antialias: true);
        }

        canvas.DrawImage(_frozenImage, src, box, new SKSamplingOptions(SKFilterMode.Nearest), null);

        using var grid = new SKPaint { Color = new SKColor(0, 0, 0, 0x28), StrokeWidth = 1 };
        for (int i = 1; i < Samples; i++)
        {
            float o = i * Zoom;
            canvas.DrawLine(left + o, top, left + o, top + Box, grid);
            canvas.DrawLine(left, top + o, left + Box, top + o, grid);
        }

        // Ring the exact pixel that will be taken. Square, because a pixel is.
        using var centre = new SKPaint
        {
            Color = SKColors.White, Style = SKPaintStyle.Stroke, StrokeWidth = 1.6f,
        };
        canvas.DrawRect(new SKRect(
            centreX - Zoom / 2f, centreY - Zoom / 2f,
            centreX + Zoom / 2f, centreY + Zoom / 2f), centre);

        canvas.Restore();

        // The rim carries the sampled colour, so the answer is readable without
        // going to the label at all.
        using var rim = new SKPaint
        {
            Color = colour, Style = SKPaintStyle.Stroke, StrokeWidth = 5, IsAntialias = true,
        };
        canvas.DrawCircle(centreX, centreY, radius + 2.5f, rim);

        using var edge = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 0x90),
            Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true,
        };
        canvas.DrawCircle(centreX, centreY, radius + 5.5f, edge);
        canvas.DrawCircle(centreX, centreY, radius, edge);
        Mark(box, margin: 8);   // the outer ring reaches 6px past the lens

        // Centred under the lens and pill-shaped, so the chip belongs to the circle
        // rather than sitting beside it like a tooltip that lost its anchor.
        // Below the lens, or above it when the cursor is near the bottom of the screen.
        const float PillHeight = 26f;
        float pillTop = top + Box + 13;
        if (pillTop + PillHeight > info.Height) pillTop = top - 13 - PillHeight;

        DrawSwatchPill(canvas, info,
            ColourText(colour, SettingsService.Current.ColourFormat),
            $"{cx + _originX}, {cy + _originY}",
            centreX, pillTop, colour);
    }

    /// <summary>The value in the configured notation, which is what lands on the clipboard.</summary>
    public static string ColourText(SKColor c, string format) => format.ToUpperInvariant() switch
    {
        "RGB" => $"rgb({c.Red}, {c.Green}, {c.Blue})",
        "HSL" => HslText(c),
        _ => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}",
    };

    private static string HslText(SKColor c)
    {
        c.ToHsl(out float h, out float s, out float l);
        return $"hsl({h:0}, {s:0}%, {l:0}%)";
    }

    /// <summary>
    /// The value chip: a pill centred on the lens, carrying a round swatch that echoes
    /// the circle above it. Every radius here is derived from the height, so the shape
    /// stays a true pill at any font size.
    /// </summary>
    private void DrawSwatchPill(
        SKCanvas canvas, SKImageInfo info, string text, string coordinates,
        float centreX, float top, SKColor swatch)
    {
        const float Height = 26f;
        const float SwatchDiameter = 13f;

        // Both runs share one font, so it is chosen from them together: a hex value is
        // Latin but the coordinates beside it follow the UI language's digits.
        using var font = LabelFont(text + coordinates);

        float gap = 8f;
        float sidePadding = Height / 2f - SwatchDiameter / 2f + 1;

        // The coordinates ride in the same pill, dimmed. They are context for the value,
        // not a second thing to read, and a separate chip beside the cursor only ends up
        // overlapping the lens.
        float valueWidth = font.MeasureText(text);
        float coordsWidth = coordinates.Length > 0 ? font.MeasureText(coordinates) + 12 : 0;

        float width = sidePadding + SwatchDiameter + gap + valueWidth + coordsWidth + Height / 2f;

        // Keep the whole pill on screen even when the cursor is near an edge.
        float left = Math.Clamp(centreX - width / 2f, 2, Math.Max(2, info.Width - width - 2));
        var box = new SKRect(left, top, left + width, top + Height);

        using var shadow = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 0x70),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 2, 4, 4, new SKColor(0, 0, 0, 0x90)),
        };
        canvas.DrawRoundRect(box, Height / 2f, Height / 2f, shadow);
        Mark(box, margin: 16);   // the blur spreads about three sigmas, plus the 2px drop

        using var bg = new SKPaint { Color = new SKColor(0x18, 0x18, 0x1B, 0xF0), IsAntialias = true };
        canvas.DrawRoundRect(box, Height / 2f, Height / 2f, bg);

        float swatchCentreX = left + sidePadding + SwatchDiameter / 2f;
        float swatchCentreY = top + Height / 2f;

        using var chip = new SKPaint { Color = swatch, IsAntialias = true };
        canvas.DrawCircle(swatchCentreX, swatchCentreY, SwatchDiameter / 2f, chip);

        using var edge = new SKPaint
        {
            Color = new SKColor(0xFF, 0xFF, 0xFF, 0x60),
            Style = SKPaintStyle.Stroke, StrokeWidth = 1, IsAntialias = true,
        };
        canvas.DrawCircle(swatchCentreX, swatchCentreY, SwatchDiameter / 2f, edge);

        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        float baseline = swatchCentreY - (font.Metrics.Ascent + font.Metrics.Descent) / 2f;
        float textX = swatchCentreX + SwatchDiameter / 2f + gap;

        ShapedText.Draw(canvas, text, textX, baseline, font, paint);

        if (coordinates.Length == 0) return;

        using var muted = new SKPaint { Color = new SKColor(0xFF, 0xFF, 0xFF, 0x8C), IsAntialias = true };
        ShapedText.Draw(canvas, coordinates, textX + valueWidth + 12, baseline, font, muted);
    }

    private void DrawWindowLabel(SKCanvas canvas, SKRectI r)
    {
        if (_hoveredWindow is null || string.IsNullOrWhiteSpace(_hoveredWindow.Title)) return;

        string title = _hoveredWindow.Title.Length > 60
            ? _hoveredWindow.Title[..60] + "..."
            : _hoveredWindow.Title;

        float y = r.Top > 30 ? r.Top - 26 : r.Top + 6;
        DrawLabel(canvas, $"{title}   {r.Width} x {r.Height}", new SKPoint(r.Left, y));
    }

    // ---- Chip ------------------------------------------------------------------

    private const float LabelHeight = 21f;
    private const float LabelPadding = 8f;

    /// <summary>
    /// Consolas for the coordinate readouts it was chosen for, and whatever the system
    /// has when the string is in a script it does not carry. A hint translated into
    /// Arabic or Thai drew as a row of boxes otherwise.
    /// </summary>
    private static SKFont LabelFont(string text) => ShapedText.Font(text);

    private static float LabelWidth(string text)
    {
        using var font = LabelFont(text);
        return ShapedText.Measure(text, font) + LabelPadding * 2;
    }

    private void DrawLabel(SKCanvas canvas, string text, SKPoint at)
    {
        using var font = LabelFont(text);
        var box = new SKRect(at.X, at.Y,
            at.X + ShapedText.Measure(text, font) + LabelPadding * 2, at.Y + LabelHeight);

        using var bg = new SKPaint { Color = new SKColor(0x18, 0x18, 0x1B, 0xE0), IsAntialias = true };
        canvas.DrawRoundRect(box, 4, 4, bg);

        // Wide enough for glyphs that overhang the chip, which a fallback font's can.
        Mark(box, margin: 6);

        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        ShapedText.Draw(canvas, text, at.X + LabelPadding, at.Y + 14.5f, font, paint);
    }
}
