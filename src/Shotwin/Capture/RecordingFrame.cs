using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// The red ring drawn around the part of the screen being recorded.
///
/// It says what is in shot, which nothing else does: a region recording looks exactly like
/// no recording at all once the count-in is over, and the strip in the corner says that
/// something is being recorded without saying what.
///
/// Three things make it work rather than get in the way. It is hidden from screen capture,
/// so the ring is never in the recording it is drawing attention to. It passes every click
/// through, so working inside the region is no different from working anywhere else. And
/// it asks, four times a second, whether the recording it belongs to is still going, and
/// closes itself the moment the answer is no — the same rule as
/// <see cref="RecordingIndicator"/>, for the same reason: nothing outside it has to
/// remember to tidy it up.
/// </summary>
public sealed class RecordingFrame : Window
{
    /// <summary>Thick enough to see against a busy screen, thin enough not to hide it.</summary>
    private const double Thickness = 3;

    private readonly Func<bool> _stillRecording;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public RecordingFrame(SkiaSharp.SKRectI region, Func<bool> stillRecording)
    {
        _stillRecording = stillRecording;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        AllowsTransparency = true;
        Background = Brushes.Transparent;

        // Never mirrored: this is a rectangle over a place on the screen, and a mirrored
        // rectangle is over the wrong place.
        FlowDirection = FlowDirection.LeftToRight;

        Content = new Border
        {
            BorderThickness = new Thickness(Thickness),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),

            // Hollow: the ring is the whole point, and anything painted inside it would be
            // a film over the thing being recorded.
            Background = Brushes.Transparent,
        };

        Place(region);

        _tick.Tick += (_, _) => { if (!_stillRecording()) Close(); };

        Loaded += (_, _) =>
        {
            // Placed again now the window knows which screen it is on: asked before it is
            // shown, WPF answers with the primary screen's scaling, which puts the ring in
            // the wrong place on any other one.
            Place(region);

            var handle = new WindowInteropHelper(this).Handle;

            if (handle != IntPtr.Zero)
            {
                // Out of every screen capture, so the ring cannot appear in the recording.
                SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);

                // And out of the way of the mouse. A transparent window still takes the
                // clicks that land on it; WS_EX_TRANSPARENT is what makes them fall
                // through to whatever is being recorded underneath.
                int style = GetWindowLongW(handle, GWL_EXSTYLE);
                SetWindowLongW(handle, GWL_EXSTYLE, style | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);
            }

            _tick.Start();
        };

        Closed += (_, _) => _tick.Stop();
    }

    /// <summary>
    /// Sits just outside the region, so the ring frames what is being recorded rather than
    /// covering its edges — the first and last few pixels of a window are usually where its
    /// border is, and a ring drawn on top of them hides the thing it is pointing at.
    ///
    /// Except where that would put it off the screen, which is exactly what recording a
    /// whole screen does: there the ring comes back inside the region, since a ring nobody
    /// can see is not worth drawing.
    /// </summary>
    private void Place(SkiaSharp.SKRectI region)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var screen = Monitors.FromPoint(region.MidX, region.MidY).Bounds;

        double left = Math.Max(region.Left - Thickness * scale, screen.Left);
        double top = Math.Max(region.Top - Thickness * scale, screen.Top);
        double right = Math.Min(region.Right + Thickness * scale, screen.Right);
        double bottom = Math.Min(region.Bottom + Thickness * scale, screen.Bottom);

        Left = left / scale;
        Top = top / scale;
        Width = Math.Max(Thickness * 2, (right - left) / scale);
        Height = Math.Max(Thickness * 2, (bottom - top) / scale);
    }
}
