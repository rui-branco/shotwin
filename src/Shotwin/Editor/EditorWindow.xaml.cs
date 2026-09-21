using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using Shotwin.Pin;
using Shotwin.Services;
using SkiaSharp;
using SkiaSharp.Views.Desktop;

namespace Shotwin.Editor;

/// <summary>
/// Post-capture annotation window.
///
/// Like the overlay, the Skia canvas works in device pixels. On top of that there is
/// a view transform (<see cref="_zoom"/>, <see cref="_offset"/>) mapping image pixels
/// to device pixels, so annotation coordinates stay in image space and survive
/// zooming, resizing and moving the window between monitors.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly SKColor[] Palette =
    [
        new(0xFF, 0x3B, 0x30), new(0xFF, 0x9F, 0x0A), new(0xFF, 0xD6, 0x0A),
        new(0x30, 0xD1, 0x58), new(0x3D, 0x8B, 0xFD), new(0xBF, 0x5A, 0xF2),
        new(0x1C, 0x1C, 0x1E), new(0xFF, 0xFF, 0xFF),
    ];

    private readonly ShotDocument _document;
    private readonly Dictionary<ToggleButton, ToolKind> _toolButtons = [];

    private ToolKind _tool = ToolKind.Arrow;
    private SKColor _color = Palette[0];
    private float _strokeWidth = 4f;

    private double _dpiScale = 1.0;
    private float _zoom = 1f;
    private SKPoint _offset;
    private bool _zoomPinned;   // true once the user zooms manually, so resize stops refitting

    /// <summary>
    /// Dragging the canvas itself. Zoom alone is useless without it: past fit-to-window
    /// the image is bigger than the viewport and everything off-screen is unreachable.
    /// Middle-drag anywhere, or hold Space and drag, which is what every editor does.
    /// </summary>
    private bool _panning;
    private Point _panLast;
    private bool _spaceHeld;

    private Annotation? _dragging;

    /// <summary>Which corner or edge of a placed image is being dragged, if any.</summary>
    private ResizeHandle? _resizing;

    /// <summary>Whether the live annotation actually moved, as opposed to being clicked.</summary>
    private bool _liveMoved;

    /// <summary>Live crop or cut rectangle. Not an annotation: it edits the image itself.</summary>
    private bool _regionActive;
    private SKPoint _regionStart;
    private SKPoint _regionCurrent;

    private SKPoint _dragOrigin;
    private SKPoint _moveLast;
    private bool _movingSelection;

    private int _lastWidth;
    private int _lastHeight;

    private TextAnnotation? _editingText;
    private bool _committingText;

    /// <summary>Collects a burst of slider changes into one write.</summary>
    private readonly System.Windows.Threading.DispatcherTimer _settingsWrite =
        new() { Interval = TimeSpan.FromMilliseconds(400) };

    public EditorWindow(ShotDocument document)
    {
        InitializeComponent();

        FlowDirection = Services.Localisation.FlowDirection;

        // The caption and the status bar are written here rather than bound, so they
        // are rebuilt when the language changes under an open editor.
        void OnLanguageChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RefreshCaption();
            UpdateStatus();
        }

        Services.Localisation.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => Services.Localisation.Instance.PropertyChanged -= OnLanguageChanged;

        _document = document;
        _document.Changed += OnDocumentChanged;

        BuildSwatches();
        WireTools();

        StrokeSlider.Value = SettingsService.Current.LastStrokeWidth;
        _strokeWidth = (float)StrokeSlider.Value;
        StrokeSlider.ValueChanged += (_, e) =>
        {
            _strokeWidth = (float)e.NewValue;

            // The slider reports NaN while it is being sized against a range it does not
            // have yet, and a stroke width of NaN draws nothing at all. Keeping it out of
            // the setting is what stops that one moment reaching the file and coming back
            // in every session after it.
            if (float.IsFinite(_strokeWidth))
                SettingsService.Current.LastStrokeWidth = _strokeWidth;

            // Dragging the slider raises this dozens of times a second, so the write is
            // held back until it settles rather than writing the file on every pixel.
            _settingsWrite.Stop();
            _settingsWrite.Start();

            ApplyStyleToSelection();
        };

        MinimiseButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximiseButton.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CaptionCloseButton.Click += (_, _) => Close();
        StateChanged += (_, _) => RefreshCaption();

        AddImageButton.Click += (_, _) => AddImageFromFile();
        OcrButton.Click += (_, _) => _ = CopyTextAsync();
        UndoButton.Click += (_, _) => { _document.Undo(); UpdateStatus(); };
        CopyButton.Click += (_, _) => CopyToClipboard();
        SaveButton.Click += (_, _) => Save();
        PinButton.Click += (_, _) => PinShot();

        Surface.MouseLeftButtonDown += OnCanvasMouseDown;
        Surface.MouseMove += OnCanvasMouseMove;
        Surface.MouseLeftButtonUp += OnCanvasMouseUp;
        Surface.MouseWheel += OnCanvasWheel;
        Surface.MouseDown += OnCanvasAnyButtonDown;
        Surface.MouseUp += OnCanvasAnyButtonUp;

        // Space is a modifier here, not a key press, so it is tracked on the window:
        // the canvas does not always hold focus when the hand is wanted.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Space && _editingText is null) { _spaceHeld = true; UpdateCanvasCursor(); } };
        PreviewKeyUp += (_, e) => { if (e.Key == Key.Space) { _spaceHeld = false; UpdateCanvasCursor(); } };
        Deactivated += (_, _) => { _spaceHeld = false; _panning = false; UpdateCanvasCursor(); };
        Surface.SizeChanged += (_, _) => { if (!_zoomPinned) FitToWindow(); };

        TextEntry.LostKeyboardFocus += (_, _) => CommitText();
        TextEntry.PreviewKeyDown += OnTextEntryKeyDown;

        _settingsWrite.Tick += (_, _) =>
        {
            _settingsWrite.Stop();
            SettingsService.Save();
        };

        KeyDown += OnWindowKeyDown;
        Loaded += OnLoaded;
        Closing += OnClosing;

        SelectTool(Enum.TryParse(SettingsService.Current.LastTool, out ToolKind saved)
            ? saved
            : ToolKind.Arrow);
        SizeToImage();
    }

    /// <summary>
    /// Undoing a crop, a cut or an added image changes the canvas size, and the view
    /// was still fitted to the old one — the restored pixels came back clipped, which
    /// reads as undo not having worked. Refit whenever the dimensions move.
    /// </summary>
    private void OnDocumentChanged()
    {
        if (_document.Width != _lastWidth || _document.Height != _lastHeight)
        {
            _lastWidth = _document.Width;
            _lastHeight = _document.Height;

            _zoomPinned = false;
            FitToWindow();
            RefreshCaption();
        }

        Surface.InvalidateVisual();
    }

    // ---- Setup ------------------------------------------------------------------

    private void BuildSwatches()
    {
        foreach (var colour in Palette)
        {
            var swatch = new RadioButton
            {
                Style = (Style)FindResource("Swatch"),
                Background = new SolidColorBrush(Color.FromRgb(colour.Red, colour.Green, colour.Blue)),
                GroupName = "Palette",
                Tag = colour,
                ToolTip = $"#{colour.Red:X2}{colour.Green:X2}{colour.Blue:X2}",
            };
            swatch.Checked += (s, _) =>
            {
                _color = (SKColor)((RadioButton)s).Tag;
                SettingsService.Current.LastColorHex = $"#{_color.Red:X2}{_color.Green:X2}{_color.Blue:X2}";

                // Written now rather than when the window closes: an editor that is
                // killed, or that crashes, would otherwise lose the choice silently.
                SettingsService.Save();

                ApplyStyleToSelection();
            };
            Swatches.Children.Add(swatch);
        }

        // The colour was being stored on every change and then ignored on the way back
        // in — the first swatch won every time, so the setting did nothing at all.
        int remembered = Array.FindIndex(Palette, c =>
            $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}"
                .Equals(SettingsService.Current.LastColorHex, StringComparison.OrdinalIgnoreCase));

        ((RadioButton)Swatches.Children[Math.Max(0, remembered)]).IsChecked = true;
    }

    private void WireTools()
    {
        _toolButtons[ToolSelect] = ToolKind.Select;
        _toolButtons[ToolArrow] = ToolKind.Arrow;
        _toolButtons[ToolRect] = ToolKind.Rectangle;
        _toolButtons[ToolEllipse] = ToolKind.Ellipse;
        _toolButtons[ToolLine] = ToolKind.Line;
        _toolButtons[ToolPen] = ToolKind.Pen;
        _toolButtons[ToolHighlight] = ToolKind.Highlight;
        _toolButtons[ToolText] = ToolKind.Text;
        _toolButtons[ToolStep] = ToolKind.Step;
        _toolButtons[ToolPixelate] = ToolKind.Pixelate;
        _toolButtons[ToolBlur] = ToolKind.Blur;
        _toolButtons[ToolCrop] = ToolKind.Crop;
        _toolButtons[ToolCut] = ToolKind.Cut;

        foreach (var (button, kind) in _toolButtons)
            button.Checked += (_, _) => SelectTool(kind);
    }

    private void SelectTool(ToolKind kind)
    {
        _tool = kind;
        foreach (var (button, buttonKind) in _toolButtons)
            button.IsChecked = buttonKind == kind;

        // Highlighter wants a translucent marker colour, not the arrow red.
        if (kind == ToolKind.Highlight && _color == Palette[0])
        {
            var yellow = Swatches.Children.OfType<RadioButton>()
                .FirstOrDefault(r => (SKColor)r.Tag == Palette[2]);
            if (yellow is not null) yellow.IsChecked = true;
        }

        if (kind is not (ToolKind.Crop or ToolKind.Cut))
        {
            SettingsService.Current.LastTool = kind.ToString();
            SettingsService.Save();
        }

        UpdateCanvasCursor();
        UpdateStatus();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _dpiScale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        FitToWindow();
        RefreshCaption();
        Activate();
        Focus();
    }

    /// <summary>Mirrors the main window: square to maximise, offset squares to restore.</summary>
    private void RefreshCaption()
    {
        bool maximised = WindowState == WindowState.Maximized;

        MaximiseButton.Tag = FindResource(maximised ? "GlyphRestore" : "GlyphMaximise");
        MaximiseButton.ToolTip = Localisation.Get(maximised ? "NavRestore" : "NavMaximise");

        CaptionTitle.Text = Localisation.Format("EditorCaption", _document.Width, _document.Height);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Shotwin.Interop.WindowCorners.ApplyNative(this);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _dpiScale = newDpi.DpiScaleX;
        if (!_zoomPinned) FitToWindow();
    }

    /// <summary>Open at 1:1 where it fits, capped to 80% of the work area.</summary>
    private void SizeToImage()
    {
        var area = SystemParameters.WorkArea;
        double maxW = area.Width * 0.85, maxH = area.Height * 0.85;

        double imageW = _document.Width / Math.Max(1.0, _dpiScale);
        double imageH = _document.Height / Math.Max(1.0, _dpiScale);

        Width = Math.Clamp(imageW + 40, MinWidth, maxW);
        Height = Math.Clamp(imageH + 100, MinHeight, maxH);
    }

    // ---- View transform ---------------------------------------------------------

    private void FitToWindow()
    {
        double viewW = Surface.ActualWidth * _dpiScale;
        double viewH = Surface.ActualHeight * _dpiScale;
        if (viewW < 1 || viewH < 1) return;

        const float Margin = 24f;
        float fit = (float)Math.Min(
            (viewW - Margin) / _document.Width,
            (viewH - Margin) / _document.Height);

        _zoom = Math.Min(1f, Math.Max(0.05f, fit));
        CentreView();
        Surface.InvalidateVisual();
        UpdateStatus();
    }

    private void CentreView()
    {
        double viewW = Surface.ActualWidth * _dpiScale;
        double viewH = Surface.ActualHeight * _dpiScale;
        _offset = new SKPoint(
            (float)(viewW - _document.Width * _zoom) / 2f,
            (float)(viewH - _document.Height * _zoom) / 2f);
    }

    private SKPoint ToImage(Point dip)
    {
        float dx = (float)(dip.X * _dpiScale);
        float dy = (float)(dip.Y * _dpiScale);
        return new SKPoint((dx - _offset.X) / _zoom, (dy - _offset.Y) / _zoom);
    }

    private Point ToDip(SKPoint image) => new(
        (image.X * _zoom + _offset.X) / _dpiScale,
        (image.Y * _zoom + _offset.Y) / _dpiScale);

    // ---- Drawing interaction ----------------------------------------------------

    private void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingText is not null) { CommitText(); return; }

        Surface.Focus();
        var p = ToImage(e.GetPosition(Surface));

        if (_tool == ToolKind.Select)
        {
            // A handle sits on the edge of the image, which is also inside its hit area,
            // so it has to be tested first or a resize would always start a move.
            if (_document.Selected is ImageAnnotation sized && HandleAt(sized.Rect, p) is { } grabbed)
            {
                _document.BeginEdit(mayResizeCanvas: true);
                _document.BeginLive(sized);
                _liveMoved = false;
                _resizing = grabbed;
                Surface.CaptureMouse();
                return;
            }

            var hit = _document.HitTest(p);
            _document.Selected = hit;
            if (hit is not null)
            {
                _document.BeginEdit(mayResizeCanvas: hit is ImageAnnotation);
                _document.BeginLive(hit);
                _liveMoved = false;
                _movingSelection = true;
                _moveLast = p;
                Surface.CaptureMouse();
            }
            _document.Invalidate();
            UpdateStatus();
            return;
        }

        if (_tool is ToolKind.Crop or ToolKind.Cut)
        {
            _regionActive = true;
            _regionStart = p;
            _regionCurrent = p;
            Surface.CaptureMouse();
            Surface.InvalidateVisual();
            return;
        }

        if (_tool == ToolKind.Text)
        {
            StartTextEntry(p);
            return;
        }

        if (_tool == ToolKind.Step)
        {
            var step = new StepAnnotation
            {
                Center = p,
                Number = _document.NextStepNumber(),
                Color = _color,
                Radius = Math.Max(12f, _strokeWidth * 4f),
            };
            _document.Add(step);
            UpdateStatus();
            return;
        }

        _dragging = CreateAnnotation(_tool);
        if (_dragging is null) return;

        _dragOrigin = p;
        _dragging.UpdateDrag(p, p);
        _document.Live = _dragging;
        Surface.CaptureMouse();
        Surface.InvalidateVisual();
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_panning)
        {
            // In device pixels, not image pixels: the offset is applied before the zoom
            // transform, so converting here would scale the drag by the zoom twice.
            var now = e.GetPosition(Surface);
            _offset = new SKPoint(
                _offset.X + (float)((now.X - _panLast.X) * _dpiScale),
                _offset.Y + (float)((now.Y - _panLast.Y) * _dpiScale));
            _panLast = now;
            _zoomPinned = true;   // a deliberate view change; resizing must not undo it
            _document.Invalidate();
            return;
        }

        var p = ToImage(e.GetPosition(Surface));

        if (_resizing is { } handle && _document.Selected is ImageAnnotation resizing)
        {
            resizing.Rect = ApplyHandle(resizing.Rect, handle, p,
                keepAspect: (Keyboard.Modifiers & ModifierKeys.Shift) != 0,
                aspect: resizing.Image.Width / (float)resizing.Image.Height);

            // The shape is live, so the cached composite is still valid: repaint the
            // surface without marking the document dirty.
            _liveMoved = true;
            Surface.InvalidateVisual();
            return;
        }

        if (_tool == ToolKind.Select && !_movingSelection && _dragging is null
            && _document.Selected is ImageAnnotation hover)
        {
            Surface.Cursor = HandleAt(hover.Rect, p) is { } over ? CursorFor(over) : Cursors.Arrow;
        }

        if (_movingSelection && _document.Selected is { } selected)
        {
            selected.Move(p.X - _moveLast.X, p.Y - _moveLast.Y);
            _moveLast = p;
            _liveMoved = true;
            Surface.InvalidateVisual();
            return;
        }

        if (_regionActive)
        {
            // Mouse-moves arrive far faster than the canvas can redraw; ignore the ones
            // that would not change a single pixel of the outline.
            if (Math.Abs(p.X - _regionCurrent.X) >= 1 || Math.Abs(p.Y - _regionCurrent.Y) >= 1)
            {
                _regionCurrent = p;
                Surface.InvalidateVisual();
            }
        }

        if (_dragging is not null)
        {
            _dragging.UpdateDrag(_dragOrigin, ConstrainIfShift(p));
            Surface.InvalidateVisual();
        }

        UpdateStatus(p);
    }

    private SKPoint ConstrainIfShift(SKPoint p)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Shift) == 0) return p;

        float dx = p.X - _dragOrigin.X, dy = p.Y - _dragOrigin.Y;

        // Lines and arrows snap to 45 degrees; boxes snap to square.
        if (_tool is ToolKind.Arrow or ToolKind.Line)
        {
            float angle = MathF.Atan2(dy, dx);
            float snapped = MathF.Round(angle / (MathF.PI / 4)) * (MathF.PI / 4);
            float len = MathF.Sqrt(dx * dx + dy * dy);
            return new SKPoint(_dragOrigin.X + MathF.Cos(snapped) * len,
                               _dragOrigin.Y + MathF.Sin(snapped) * len);
        }

        float side = Math.Max(Math.Abs(dx), Math.Abs(dy));
        return new SKPoint(_dragOrigin.X + Math.Sign(dx) * side, _dragOrigin.Y + Math.Sign(dy) * side);
    }

    private void OnCanvasMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizing is not null)
        {
            _resizing = null;
            Surface.ReleaseMouseCapture();
            _document.EndLive(raise: _liveMoved);
            GrowCanvasToFitContent();
            UpdateStatus();
            return;
        }

        Surface.ReleaseMouseCapture();

        if (_regionActive)
        {
            _regionActive = false;
            ApplyRegion();
            return;
        }

        if (_movingSelection)
        {
            _movingSelection = false;
            _document.EndLive(raise: _liveMoved);
            GrowCanvasToFitContent();
            _document.EndEdit();
            return;
        }

        if (_dragging is null) return;

        var finished = _dragging;
        _dragging = null;
        _document.Live = null;

        // Discard accidental micro-drags rather than littering 2px marks.
        var b = finished.Bounds;
        bool tooSmall = finished is not PenAnnotation && b.Width < 3 && b.Height < 3;
        if (tooSmall)
        {
            _document.Invalidate();
            return;
        }

        _document.Add(finished);
        UpdateStatus();
    }

    /// <summary>
    /// Starts a pan on the middle button, or on the left button while Space is held.
    /// Left-with-Space is checked here rather than in the left-button handler so the
    /// drawing tools never see the press at all.
    /// </summary>
    private void OnCanvasAnyButtonDown(object sender, MouseButtonEventArgs e)
    {
        bool wantsPan = e.ChangedButton == MouseButton.Middle
                     || (e.ChangedButton == MouseButton.Left && _spaceHeld);
        if (!wantsPan) return;

        _panning = true;
        _panLast = e.GetPosition(Surface);
        Surface.CaptureMouse();
        UpdateCanvasCursor();
        e.Handled = true;
    }

    private void OnCanvasAnyButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_panning) return;
        if (e.ChangedButton is not (MouseButton.Middle or MouseButton.Left)) return;

        _panning = false;
        Surface.ReleaseMouseCapture();
        UpdateCanvasCursor();
        e.Handled = true;
    }

    /// <summary>
    /// The hand appears whenever a drag would pan, so the mode is visible before the
    /// button goes down rather than being something you discover by trying it.
    /// </summary>
    private void UpdateCanvasCursor()
    {
        Surface.Cursor = _panning ? Cursors.ScrollAll
                       : _spaceHeld ? Cursors.Hand
                       : _tool == ToolKind.Select ? Cursors.Arrow
                       : Cursors.Cross;
    }

    private void OnCanvasWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;

        var before = ToImage(e.GetPosition(Surface));
        float factor = e.Delta > 0 ? 1.15f : 1 / 1.15f;
        _zoom = Math.Clamp(_zoom * factor, 0.05f, 12f);
        _zoomPinned = true;

        // Keep the pixel under the cursor pinned while zooming.
        var dip = e.GetPosition(Surface);
        _offset = new SKPoint(
            (float)(dip.X * _dpiScale) - before.X * _zoom,
            (float)(dip.Y * _dpiScale) - before.Y * _zoom);

        Surface.InvalidateVisual();
        UpdateStatus();
        e.Handled = true;
    }

    private Annotation? CreateAnnotation(ToolKind kind)
    {
        Annotation? a = kind switch
        {
            ToolKind.Arrow => new ArrowAnnotation(),
            ToolKind.Rectangle => new RectangleAnnotation(),
            ToolKind.Ellipse => new EllipseAnnotation(),
            ToolKind.Line => new LineAnnotation(),
            ToolKind.Pen => new PenAnnotation(),
            ToolKind.Highlight => new HighlightAnnotation(),
            ToolKind.Pixelate => new ObscureAnnotation { Pixelate = true, Strength = 12f },
            ToolKind.Blur => new ObscureAnnotation { Pixelate = false, Strength = 14f },
            _ => null,
        };

        if (a is not null)
        {
            a.Color = _color;
            a.StrokeWidth = _strokeWidth;
        }
        return a;
    }

    private void ApplyStyleToSelection()
    {
        if (_document.Selected is not { } selected) return;
        _document.BeginEdit();
        selected.Color = _color;
        selected.StrokeWidth = _strokeWidth;
        _document.EndEdit();
    }

    // ---- Text tool --------------------------------------------------------------

    private void StartTextEntry(SKPoint imagePoint)
    {
        _editingText = new TextAnnotation
        {
            Position = imagePoint,
            Color = _color,
            FontSize = Math.Max(14f, _strokeWidth * 5f),
            HasBackdrop = false,
        };

        var dip = ToDip(imagePoint);
        Canvas.SetLeft(TextEntry, dip.X);
        Canvas.SetTop(TextEntry, dip.Y);
        TextEntry.FontSize = _editingText.FontSize * _zoom / _dpiScale;
        TextEntry.Foreground = new SolidColorBrush(Color.FromRgb(_color.Red, _color.Green, _color.Blue));
        TextEntry.Text = string.Empty;
        TextEntry.Visibility = Visibility.Visible;
        EditLayer.IsHitTestVisible = true;

        TextEntry.Focus();
        Keyboard.Focus(TextEntry);
    }

    private void OnTextEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _editingText = null;
            HideTextEntry();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            CommitText();
            e.Handled = true;
        }
    }

    private void CommitText()
    {
        if (_editingText is null || _committingText) return;

        _committingText = true;
        try
        {
            string text = TextEntry.Text.TrimEnd();
            if (!string.IsNullOrWhiteSpace(text))
            {
                _editingText.Text = text;
                _document.Add(_editingText);
            }
            _editingText = null;
            HideTextEntry();
        }
        finally
        {
            _committingText = false;
        }
    }

    private void HideTextEntry()
    {
        TextEntry.Visibility = Visibility.Collapsed;
        TextEntry.Text = string.Empty;
        EditLayer.IsHitTestVisible = false;
        Surface.Focus();
    }

    // ---- Commands ---------------------------------------------------------------

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (_editingText is not null) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl)
        {
            switch (e.Key)
            {
                case Key.Z when !shift: _document.Undo(); UpdateStatus(); e.Handled = true; return;
                case Key.Y:
                case Key.Z when shift: _document.Redo(); UpdateStatus(); e.Handled = true; return;
                case Key.T when shift: _ = CopyTextAsync(); e.Handled = true; return;
                case Key.A when shift: AddImageFromFile(); e.Handled = true; return;
                case Key.V: AddImageFromClipboard(); e.Handled = true; return;
                case Key.C: CopyToClipboard(); e.Handled = true; return;
                case Key.S when shift: SaveAs(); e.Handled = true; return;
                case Key.S: Save(); e.Handled = true; return;
                case Key.P: PinShot(); e.Handled = true; return;
                case Key.D0: _zoomPinned = false; FitToWindow(); e.Handled = true; return;
            }
            return;
        }

        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Delete:
            case Key.Back:
                if (_document.Selected is { } selected)
                {
                    _document.Remove(selected);
                    UpdateStatus();
                    e.Handled = true;
                }
                break;

            case Key.V: SelectTool(ToolKind.Select); break;
            case Key.A: SelectTool(ToolKind.Arrow); break;
            case Key.R: SelectTool(ToolKind.Rectangle); break;
            case Key.O: SelectTool(ToolKind.Ellipse); break;
            case Key.L: SelectTool(ToolKind.Line); break;
            case Key.P: SelectTool(ToolKind.Pen); break;
            case Key.H: SelectTool(ToolKind.Highlight); break;
            case Key.T: SelectTool(ToolKind.Text); break;
            case Key.S: SelectTool(ToolKind.Step); break;
            case Key.B: SelectTool(shift ? ToolKind.Blur : ToolKind.Pixelate); break;
            case Key.C: SelectTool(ToolKind.Crop); break;
            case Key.K: SelectTool(ToolKind.Cut); break;
        }
    }

    /// <summary>
    /// Recognises the text in the shot as it stands, annotations included — so a
    /// pixelated block reads as noise rather than leaking the words underneath.
    /// </summary>
    private async Task CopyTextAsync()
    {
        OcrButton.IsEnabled = false;
        Flash(Localisation.Get("EditorReadingText"));
        try
        {
            using var flat = _document.Flatten();
            using var bitmap = SKBitmap.FromImage(flat);

            var result = await TextRecognition.RecognizeAsync(bitmap);

            if (!result.Ok) Flash(result.Problem!);
            else if (!result.HasText) Flash(Localisation.Get("EditorNoText"));
            else
            {
                ImageIO.CopyTextToClipboard(result.Text);
                Flash(Localisation.Plural("CaptureTextCopiedLines_One", "CaptureTextCopiedLines_Many",
                    result.LineCount, result.LineCount));
            }
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }
    }

    private void CopyToClipboard()
    {
        using var flat = _document.Flatten();
        ImageIO.CopyToClipboard(flat);
        Flash(Localisation.Get("EditorCopiedToClipboard"));
    }

    private void Save()
    {
        using var flat = _document.Flatten();
        var result = ImageIO.SaveToFolder(flat);
        if (!result.Ok)
        {
            Flash(Localisation.Format("EditorSaveFailed", result.Problem));
            return;
        }

        _document.MarkSaved();
        Flash(result.Problem is null
            ? Localisation.Format("EditorSavedTo", result.Path)
            : Localisation.Format("EditorSavedToWithProblem", result.Path, result.Problem));
    }

    private void SaveAs()
    {
        var dialog = new SaveFileDialog
        {
            Filter = Localisation.Get("EditorSaveFilter"),
            FileName = "Shot.png",
            InitialDirectory = SettingsService.Current.SaveFolder,
        };
        if (dialog.ShowDialog(this) != true) return;

        using var flat = _document.Flatten();
        if (ImageIO.SaveAs(flat, dialog.FileName))
        {
            _document.MarkSaved();
            Flash(Localisation.Format("EditorSavedTo", dialog.FileName));
        }
        else
        {
            Flash(Localisation.Get("EditorSaveFailedShort"));
        }
    }

    private void PinShot()
    {
        using var flat = _document.Flatten();
        var pin = new PinWindow(SKBitmap.FromImage(flat));
        pin.Show();
        Flash(Localisation.Get("EditorPinned"));
    }

    // ---- Status -----------------------------------------------------------------

    private void UpdateStatus(SKPoint? cursor = null)
    {
        string position = cursor is { } c && c.X >= 0 && c.Y >= 0 && c.X < _document.Width && c.Y < _document.Height
            ? Localisation.Format("EditorStatusPosition", (int)c.X, (int)c.Y)
            : string.Empty;

        StatusText.Text = Localisation.Plural("EditorStatus_One", "EditorStatus_Many",
            _document.Items.Count, _document.Width, _document.Height, _document.Items.Count) + position;
        ZoomText.Text = Localisation.Format("EditorZoom",
            (_zoom * 100).ToString("0", System.Globalization.CultureInfo.CurrentCulture));
        UndoButton.IsEnabled = _document.CanUndo;
    }

    private void Flash(string message)
    {
        StatusText.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            UpdateStatus();
        };
        timer.Start();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Everything here is already saved as it changes; this only catches a slider
        // move still sitting in the debounce when the window was closed.
        _settingsWrite.Stop();
        if (float.IsFinite(_strokeWidth))
            SettingsService.Current.LastStrokeWidth = _strokeWidth;
        SettingsService.Save();

        _shadowPaint?.Dispose();
        _dimPaint?.Dispose();
        _document.Dispose();
    }

    // ---- Paint ------------------------------------------------------------------

    private void OnPaintSurface(object? sender, SKPaintSurfaceEventArgs e)
    {
        var canvas = e.Surface.Canvas;
        canvas.Clear(new SKColor(0x14, 0x14, 0x17));

        canvas.Save();
        canvas.Translate(_offset.X, _offset.Y);
        canvas.Scale(_zoom);

        // The shadow is a gaussian blur the size of the whole image. Rebuilding and
        // rasterising it on every mouse-move made dragging a crop crawl, so it is
        // cached per zoom level and skipped outright while something is being dragged.
        if (!IsInteracting)
            canvas.DrawRect(new SKRect(0, 0, _document.Width, _document.Height), ShadowPaint());

        _document.Render(canvas);

        if (_document.Selected is { } selected)
            DrawSelectionMarker(canvas, selected.Bounds, selected is ImageAnnotation);

        if (_regionActive) DrawRegion(canvas);

        canvas.Restore();
    }

    /// <summary>
    /// Applies the dragged region. Crop keeps it, Cut removes it and closes the gap,
    /// choosing the axis from the shape of the drag: a wide flat drag takes out a
    /// horizontal band, a tall narrow one takes out a vertical one.
    /// </summary>
    private void ApplyRegion()
    {
        var region = new SKRectI(
            (int)MathF.Round(Math.Min(_regionStart.X, _regionCurrent.X)),
            (int)MathF.Round(Math.Min(_regionStart.Y, _regionCurrent.Y)),
            (int)MathF.Round(Math.Max(_regionStart.X, _regionCurrent.X)),
            (int)MathF.Round(Math.Max(_regionStart.Y, _regionCurrent.Y)));

        bool applied = _tool == ToolKind.Crop
            ? _document.Crop(region)
            : _document.CutOut(region, horizontal: region.Width >= region.Height);

        if (!applied)
        {
            Flash(Localisation.Get("EditorRegionTooSmall"));
            Surface.InvalidateVisual();
            return;
        }

        // The canvas just changed size, so the old zoom and offset mean nothing.
        _zoomPinned = false;
        FitToWindow();

        SelectTool(ToolKind.Select);
        Flash(Localisation.Get(_tool == ToolKind.Crop ? "EditorCropped" : "EditorCutOut"));
        UpdateStatus();
    }

    private void DrawRegion(SKCanvas canvas)
    {
        var rect = new SKRect(
            Math.Min(_regionStart.X, _regionCurrent.X),
            Math.Min(_regionStart.Y, _regionCurrent.Y),
            Math.Max(_regionStart.X, _regionCurrent.X),
            Math.Max(_regionStart.Y, _regionCurrent.Y));

        bool cutting = _tool == ToolKind.Cut;
        bool horizontal = rect.Width >= rect.Height;

        // Cut works on a full band, so show the whole strip that will disappear.
        if (cutting)
        {
            rect = horizontal
                ? new SKRect(0, rect.Top, _document.Width, rect.Bottom)
                : new SKRect(rect.Left, 0, rect.Right, _document.Height);
        }

        var dim = DimPaint();

        if (cutting)
        {
            // Dim what goes away.
            canvas.DrawRect(rect, dim);
        }
        else
        {
            // Dim what gets discarded: four bands around the keeper.
            canvas.DrawRect(new SKRect(0, 0, _document.Width, rect.Top), dim);
            canvas.DrawRect(new SKRect(0, rect.Bottom, _document.Width, _document.Height), dim);
            canvas.DrawRect(new SKRect(0, rect.Top, rect.Left, rect.Bottom), dim);
            canvas.DrawRect(new SKRect(rect.Right, rect.Top, _document.Width, rect.Bottom), dim);
        }

        using var border = new SKPaint
        {
            Color = cutting ? new SKColor(0xFF, 0x62, 0x5A) : new SKColor(0x3D, 0x8B, 0xFD),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.5f / _zoom,
            PathEffect = cutting ? SKPathEffect.CreateDash([6 / _zoom, 4 / _zoom], 0) : null,
            IsAntialias = true,
        };
        canvas.DrawRect(rect, border);
    }

    /// <summary>Adds another image beside this one. Hold Shift to place it to the right.</summary>
    private void AddImageFromFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = Localisation.Get("EditorAddImageTitle"),
            Filter = Localisation.Get("EditorOpenFilter"),
            InitialDirectory = Services.SettingsService.Current.SaveFolder,
        };

        if (dialog.ShowDialog(this) != true) return;

        var bitmap = SKBitmap.Decode(dialog.FileName);
        if (bitmap is null)
        {
            Flash(Localisation.Get("EditorFileUnreadable"));
            return;
        }

        using (bitmap) AddImage(bitmap);
    }

    private void AddImageFromClipboard()
    {
        if (!Clipboard.ContainsImage())
        {
            Flash(Localisation.Get("EditorNoClipboardImage"));
            return;
        }

        try
        {
            using var stream = new System.IO.MemoryStream();
            var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
            encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(Clipboard.GetImage()));
            encoder.Save(stream);
            stream.Position = 0;

            using var bitmap = SKBitmap.Decode(stream);
            if (bitmap is null)
            {
                Flash(Localisation.Get("EditorClipboardUnreadable"));
                return;
            }

            AddImage(bitmap);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Flash(Localisation.Get("EditorClipboardBusy"));
        }
    }

    private void AddImage(SKBitmap bitmap)
    {
        bool toTheRight = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (!_document.AddImage(bitmap, toTheRight))
        {
            Flash(Localisation.Get("EditorImageNotAdded"));
            return;
        }

        _zoomPinned = false;
        FitToWindow();

        // The pasted image lands selected, so switching to Select means it can be
        // dragged straight away rather than after hunting for the right tool.
        SelectTool(ToolKind.Select);

        Flash(Localisation.Get(toTheRight ? "EditorAddedRight" : "EditorAddedBelow"));
        UpdateStatus();
    }

    // ---- Cached paint objects ---------------------------------------------------
    //
    // Everything here used to be allocated inside OnPaintSurface, which runs on every
    // mouse-move. The image filter in particular is expensive to build and expensive
    // to apply, so it is kept until the zoom it was built for changes.

    private SKPaint? _shadowPaint;
    private float _shadowZoom = -1;

    private SKPaint? _dimPaint;

    /// <summary>True while a drag is in flight, when decoration can be skipped.</summary>
    /// <summary>
    /// Called when a placed image is let go. Growing the canvas mid-drag would move the
    /// image under the cursor on every frame, so it happens once, on release.
    /// </summary>
    private void GrowCanvasToFitContent()
    {
        if (!_document.FitCanvasToContent()) return;

        // The canvas is a different size now, so a view still fitted to the old one
        // would show the new space cropped off.
        if (!_zoomPinned) FitToWindow();
        RefreshCaption();
    }

    private bool IsInteracting =>
        _regionActive || _dragging is not null || _movingSelection || _resizing is not null;

    private SKPaint ShadowPaint()
    {
        if (_shadowPaint is not null && Math.Abs(_shadowZoom - _zoom) < 0.0001f)
            return _shadowPaint;

        _shadowPaint?.Dispose();
        _shadowPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 0x80),
            ImageFilter = SKImageFilter.CreateDropShadowOnly(
                0, 4 / _zoom, 10 / _zoom, 10 / _zoom, new SKColor(0, 0, 0, 0xA0)),
        };
        _shadowZoom = _zoom;
        return _shadowPaint;
    }

    private SKPaint DimPaint() =>
        _dimPaint ??= new SKPaint { Color = new SKColor(0, 0, 0, 0x88) };

    private void DrawSelectionMarker(SKCanvas canvas, SKRect bounds, bool withHandles)
    {
        // A dashed halo says "this is selected" for a mark that has no edge of its own.
        // An image does have one, and the handles sit on it, so it gets a single solid
        // outline instead — drawing both left two borders round the same picture.
        if (!withHandles)
        {
            var r = bounds;
            r.Inflate(6 / _zoom, 6 / _zoom);

            using var paint = new SKPaint
            {
                Color = new SKColor(0x3D, 0x8B, 0xFD),
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1.5f / _zoom,
                PathEffect = SKPathEffect.CreateDash([6 / _zoom, 4 / _zoom], 0),
                IsAntialias = true,
            };
            canvas.DrawRect(r, paint);
            return;
        }

        using var edge = new SKPaint
        {
            Color = new SKColor(0x3D, 0x8B, 0xFD),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.25f / _zoom,
            IsAntialias = true,
        };
        canvas.DrawRect(bounds, edge);

        using var fill = new SKPaint { Color = SKColors.White, IsAntialias = true };
        using var ring = new SKPaint
        {
            Color = new SKColor(0x3D, 0x8B, 0xFD),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 1.25f / _zoom,
            IsAntialias = true,
        };

        foreach (var handle in AllHandles)
        {
            var box = HandleRect(bounds, handle);
            canvas.DrawRect(box, fill);
            canvas.DrawRect(box, ring);
        }
    }

    // ---- Resize handles ---------------------------------------------------------

    private static readonly ResizeHandle[] AllHandles = Enum.GetValues<ResizeHandle>();

    /// <summary>Drawn at a fixed size on screen, so zooming out does not shrink them away.</summary>
    private const float HandleScreenSize = 8f;

    private SKRect HandleRect(SKRect b, ResizeHandle handle)
    {
        var (x, y) = HandleCentre(b, handle);
        float half = HandleScreenSize / 2 / _zoom;
        return new SKRect(x - half, y - half, x + half, y + half);
    }

    private static (float X, float Y) HandleCentre(SKRect b, ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft => (b.Left, b.Top),
        ResizeHandle.Top => (b.MidX, b.Top),
        ResizeHandle.TopRight => (b.Right, b.Top),
        ResizeHandle.Right => (b.Right, b.MidY),
        ResizeHandle.BottomRight => (b.Right, b.Bottom),
        ResizeHandle.Bottom => (b.MidX, b.Bottom),
        ResizeHandle.BottomLeft => (b.Left, b.Bottom),
        _ => (b.Left, b.MidY),
    };

    /// <summary>
    /// The handle under a point, with a grab area wider than the drawn square. Aiming
    /// at an eight pixel box with a mouse is not a reasonable thing to ask.
    /// </summary>
    private ResizeHandle? HandleAt(SKRect bounds, SKPoint p)
    {
        float grab = 11f / _zoom;

        foreach (var handle in AllHandles)
        {
            var (x, y) = HandleCentre(bounds, handle);
            if (Math.Abs(p.X - x) <= grab && Math.Abs(p.Y - y) <= grab) return handle;
        }
        return null;
    }

    private static Cursor CursorFor(ResizeHandle handle) => handle switch
    {
        ResizeHandle.TopLeft or ResizeHandle.BottomRight => Cursors.SizeNWSE,
        ResizeHandle.TopRight or ResizeHandle.BottomLeft => Cursors.SizeNESW,
        ResizeHandle.Top or ResizeHandle.Bottom => Cursors.SizeNS,
        _ => Cursors.SizeWE,
    };

    /// <summary>
    /// Moves the dragged edges to the cursor. The opposite edge is the anchor, so the
    /// image grows away from where you grabbed it, and a drag past that anchor stops
    /// at a minimum size rather than turning the picture inside out.
    /// </summary>
    private static SKRect ApplyHandle(SKRect r, ResizeHandle handle, SKPoint p, bool keepAspect, float aspect)
    {
        float left = r.Left, top = r.Top, right = r.Right, bottom = r.Bottom;
        const float Min = ImageAnnotation.MinimumSize;

        bool west = handle is ResizeHandle.TopLeft or ResizeHandle.Left or ResizeHandle.BottomLeft;
        bool east = handle is ResizeHandle.TopRight or ResizeHandle.Right or ResizeHandle.BottomRight;
        bool north = handle is ResizeHandle.TopLeft or ResizeHandle.Top or ResizeHandle.TopRight;
        bool south = handle is ResizeHandle.BottomLeft or ResizeHandle.Bottom or ResizeHandle.BottomRight;

        if (west) left = Math.Min(p.X, right - Min);
        if (east) right = Math.Max(p.X, left + Min);
        if (north) top = Math.Min(p.Y, bottom - Min);
        if (south) bottom = Math.Max(p.Y, top + Min);

        // Corners can hold the original proportions; an edge moves one axis only, so
        // there is nothing to hold it to.
        if (keepAspect && aspect > 0 && (west || east) && (north || south))
        {
            float width = right - left;
            float height = Math.Max(Min, width / aspect);

            if (north) top = bottom - height;
            else bottom = top + height;
        }

        return new SKRect(left, top, right, bottom);
    }
}
