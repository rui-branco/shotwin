using System.Runtime.InteropServices;
using System.Threading;
using SkiaSharp;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// Wheels a region down a notch at a time, grabbing and stitching as it goes, until the
/// content stops moving.
///
/// Windows has no API for this, so the scroll is synthesized: the pointer is parked in
/// the middle of the region and sent wheel notches. The pointer is put back afterwards.
///
/// Synthesized wheel input is routed to the FOCUSED window, not the one under the
/// pointer — "scroll inactive windows on hover" is a shell feature that redirects real
/// device input and does not apply here. So the window under the region is brought to
/// the foreground first. Without that, the notches land wherever focus happened to go
/// when the selection overlay closed, nothing moves, and the capture reads three
/// identical frames as the end of the page and stops immediately.
///
/// The region is in virtual-desktop PHYSICAL pixels, because every frame is a fresh
/// live grab rather than a crop of anything frozen.
/// </summary>
public sealed class ScrollingCaptureSession
{
    /// <summary>
    /// Long enough for a page or list to finish repainting after a notch. Browsers
    /// animate a wheel notch over roughly 150ms, and grabbing mid-animation produces a
    /// frame that matches nothing.
    /// </summary>
    private static readonly TimeSpan FrameSettle = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// The overlay that chose the region is still coming down when this starts, and it
    /// covers the whole desktop — grabbing straight away photographs the overlay.
    /// </summary>
    private static readonly TimeSpan StartSettle = TimeSpan.FromMilliseconds(220);

    private const int MaxFrames = 400;

    /// <summary>Repeats that mean the end of the page rather than a slow repaint.</summary>
    private const int BottomedOutLimit = 3;

    /// <summary>
    /// Generous, because a single mismatch is usually a half-drawn frame rather than a
    /// page that changed. Stopping too eagerly truncates the capture silently, which is
    /// worse than a few wasted frames.
    /// </summary>
    private const int MismatchLimit = 4;

    private readonly IScreenCapture _capture;
    private readonly SKRectI _region;

    private volatile bool _cancelled;

    /// <summary>Accumulated height so far, for the progress window.</summary>
    public event Action<int>? Progress;

    public ScrollingCaptureSession(IScreenCapture capture, SKRectI region)
    {
        _capture = capture;
        _region = region;
    }

    public void Cancel() => _cancelled = true;

    public Task<SKBitmap?> RunAsync()
    {
        // Captured here rather than inside the loop: this is the UI thread, and every
        // Progress handler wants to be back on it.
        var context = SynchronizationContext.Current;
        return Task.Run(() => Run(context));
    }

    private SKBitmap? Run(SynchronizationContext? context)
    {
        bool hadCursor = GetCursorPos(out POINT origin);
        var stitcher = new ScrollStitcher();

        try
        {
            SetCursorPos(_region.MidX, _region.MidY);
            FocusWindowUnderPointer();
            Thread.Sleep(StartSettle);

            int bottomedOut = 0;
            int mismatches = 0;

            for (int frames = 0; frames < MaxFrames && !_cancelled; frames++)
            {
                using var frame = Grab();
                if (frame is null) break;

                var appended = stitcher.Append(frame);

                // Both are only ever believed in a row: one stale frame is a repaint
                // that has not caught up yet, three of them is the end of the page.
                bottomedOut = appended == AppendResult.NoNewContent ? bottomedOut + 1 : 0;
                mismatches = appended == AppendResult.Mismatch ? mismatches + 1 : 0;
                if (bottomedOut >= BottomedOutLimit || mismatches >= MismatchLimit) break;

                if (appended is AppendResult.First or AppendResult.Extended)
                    Report(context, stitcher.Height);

                Wheel();
                Thread.Sleep(FrameSettle);
            }
        }
        finally
        {
            // The pointer was borrowed to aim the wheel, so give it back.
            if (hadCursor) SetCursorPos(origin.X, origin.Y);
        }

        // The composed bitmap outlives the stitcher: the caller owns it from here, while
        // the frames kept behind to rebuild the chrome go now rather than whenever a
        // finaliser gets round to them.
        var result = stitcher.Result;
        stitcher.Dispose();
        return result;
    }

    private SKBitmap? Grab()
    {
        using var desktop = _capture.CaptureVirtualDesktop(out int originX, out int originY);

        var rect = new SKRectI(
            _region.Left - originX, _region.Top - originY,
            _region.Right - originX, _region.Bottom - originY);
        rect.Intersect(new SKRectI(0, 0, desktop.Width, desktop.Height));
        if (rect.Width < 1 || rect.Height < 1) return null;

        using var subset = new SKBitmap();
        desktop.ExtractSubset(subset, rect);
        return subset.Copy();
    }

    /// <summary>
    /// Gives the foreground to the app being captured, so the wheel notches reach it.
    /// WindowFromPoint lands on whichever child control is under the pointer, and only
    /// the top-level window it belongs to can be activated.
    /// </summary>
    private void FocusWindowUnderPointer()
    {
        var target = WindowFromPoint(new POINT { X = _region.MidX, Y = _region.MidY });
        if (target == IntPtr.Zero) return;

        var root = GetAncestor(target, GA_ROOT);
        SetForegroundWindow(root == IntPtr.Zero ? target : root);
    }

    /// <summary>One notch down, the same as a physical wheel click.</summary>
    private static void Wheel()
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            mi = new MOUSEINPUT { mouseData = -WHEEL_DELTA, dwFlags = MOUSEEVENTF_WHEEL },
        };

        SendInput(1, ref input, Marshal.SizeOf<INPUT>());
    }

    private void Report(SynchronizationContext? context, int height)
    {
        if (context is null)
        {
            Progress?.Invoke(height);
            return;
        }

        context.Post(_ => Progress?.Invoke(height), null);
    }
}
