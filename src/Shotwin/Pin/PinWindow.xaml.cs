using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using Shotwin.Services;
using SkiaSharp;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Pin;

/// <summary>
/// An always-on-top floating copy of a shot — the "keep this on screen while I work"
/// window. Drag to move, Ctrl+wheel to scale, Esc to dismiss.
/// </summary>
public partial class PinWindow : Window
{
    private readonly SKBitmap _bitmap;
    private double _scale = 1.0;

    public PinWindow(SKBitmap bitmap)
    {
        InitializeComponent();

        FlowDirection = Localisation.FlowDirection;

        _bitmap = bitmap;
        Shot.Source = ImageIO.ToBitmapSource(bitmap);

        // Written rather than bound, because the same item says the opposite once the
        // shot is faded.
        MenuOpacity.Header = Localisation.Get("PinFade");

        MouseLeftButtonDown += OnMouseLeftButtonDown;
        MouseDoubleClick += (_, _) => Close();
        MouseWheel += OnMouseWheel;
        KeyDown += OnKeyDown;

        MenuCopy.Click += (_, _) => Copy();
        MenuSave.Click += (_, _) => Save();
        MenuClose.Click += (_, _) => Close();
        MenuResetZoom.Click += (_, _) => SetScale(1.0);
        MenuOpacity.Click += (_, _) => ToggleFade();
        MenuClickThrough.Click += (_, _) => SetClickThrough(MenuClickThrough.IsChecked);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SetScale(1.0);
        PlaceNearCursor();
        Activate();
    }

    /// <summary>
    /// Image pixels are laid out in DIPs, so a 2x shot on a 200% display must be
    /// halved to appear at its true physical size rather than doubled.
    /// </summary>
    private void SetScale(double scale)
    {
        _scale = Math.Clamp(scale, 0.1, 6.0);
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;

        Shot.Width = _bitmap.Width * _scale / dpi;
        Shot.Height = _bitmap.Height * _scale / dpi;
    }

    private void PlaceNearCursor()
    {
        if (!GetCursorPos(out POINT p)) return;

        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var area = SystemParameters.WorkArea;

        double left = p.X / dpi + 16;
        double top = p.Y / dpi + 16;

        Left = Math.Min(left, area.Right - ActualWidth - 8);
        Top = Math.Min(top, area.Bottom - ActualHeight - 8);
    }

    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) return;
        try { DragMove(); }
        catch (InvalidOperationException) { /* mouse released mid-drag */ }
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        SetScale(_scale * (e.Delta > 0 ? 1.1 : 1 / 1.1));
        e.Handled = true;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Escape: Close(); break;
            case Key.C when ctrl: Copy(); break;
            case Key.S when ctrl: Save(); break;
            case Key.D0 when ctrl: SetScale(1.0); break;
        }
    }

    private void Copy()
    {
        using var image = SKImage.FromBitmap(_bitmap);
        ImageIO.CopyToClipboard(image);
    }

    private void Save()
    {
        var dialog = new SaveFileDialog
        {
            Filter = Localisation.Get("EditorSaveFilter"),
            FileName = "Shot.png",
            InitialDirectory = SettingsService.Current.SaveFolder,
        };
        if (dialog.ShowDialog(this) != true) return;

        using var image = SKImage.FromBitmap(_bitmap);
        ImageIO.SaveAs(image, dialog.FileName);
    }

    private void ToggleFade()
    {
        bool faded = Opacity < 0.99;
        Opacity = faded ? 1.0 : 0.5;
        MenuOpacity.Header = Localisation.Get(faded ? "PinFade" : "PinFullOpacity");
    }

    /// <summary>
    /// WS_EX_TRANSPARENT lets clicks fall through to whatever is underneath, so a
    /// pinned reference can sit over the app you are typing in.
    /// </summary>
    private void SetClickThrough(bool enabled)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;

        const int WS_EX_TRANSPARENT = 0x00000020;
        int exStyle = GetWindowLongW(handle, GWL_EXSTYLE);
        exStyle = enabled ? exStyle | WS_EX_TRANSPARENT : exStyle & ~WS_EX_TRANSPARENT;
        SetWindowLongW(handle, GWL_EXSTYLE, exStyle);

        // Once clicks pass through there is no way to right-click back out, so drop
        // the opacity as a visible reminder that the window is now inert.
        Opacity = enabled ? 0.85 : 1.0;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap.Dispose();
    }
}
