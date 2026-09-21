using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SkiaSharp;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// The only thing on screen while a scrolling capture runs: how much has been stitched
/// so far, and the way out of it.
///
/// It never takes focus, because the app being scrolled has to keep it. That rules out
/// a key handler for Esc — a window with no focus receives no keys — so Esc is polled
/// instead, the same way the preview thumbnail watches for clicks it can never be told
/// about.
/// </summary>
public partial class ScrollProgressWindow : Window
{
    private readonly SKRectI _region;
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(60) };

    /// <summary>Esc or the button. The session cancels and keeps what it already has.</summary>
    public event Action? StopRequested;

    public ScrollProgressWindow(SKRectI region)
    {
        InitializeComponent();

        FlowDirection = Services.Localisation.FlowDirection;

        _region = region;
        Report(0);

        StopButton.Click += (_, _) => Stop();

        _watch.Tick += (_, _) => { if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) Stop(); };

        SourceInitialized += (_, _) => Interop.WindowCorners.ApplyNative(this, small: true);
        Loaded += (_, _) => { PlaceClearOfRegion(); _watch.Start(); };
        Closed += (_, _) => _watch.Stop();
    }

    public void Report(int height) =>
        StatusText.Text = Services.Localisation.Format("CaptureScrollingProgress", height);

    private void Stop()
    {
        _watch.Stop();
        StopRequested?.Invoke();
    }

    /// <summary>
    /// A corner of the monitor the region is on, and never over the region itself —
    /// every frame is a live grab, so this window would otherwise end up in the shot.
    /// </summary>
    private void PlaceClearOfRegion()
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        int width = (int)Math.Ceiling(ActualWidth * scale);
        int height = (int)Math.Ceiling(ActualHeight * scale);
        const int Gap = 16;

        var monitor = Monitors.FromPoint(_region.MidX, _region.MidY);

        int nearRight = monitor.Bounds.Right - width - Gap;
        int nearLeft = monitor.Left + Gap;
        int nearBottom = monitor.Bounds.Bottom - height - Gap;
        int nearTop = monitor.Top + Gap;

        SKPointI[] corners =
        [
            new(nearRight, nearBottom),
            new(nearLeft, nearBottom),
            new(nearRight, nearTop),
            new(nearLeft, nearTop),
        ];

        var spot = corners[0];
        foreach (var corner in corners)
        {
            var box = SKRectI.Create(corner.X, corner.Y, width, height);
            if (box.IntersectsWith(_region)) continue;

            spot = corner;
            break;
        }

        Left = spot.X / scale;
        Top = spot.Y / scale;
    }
}
