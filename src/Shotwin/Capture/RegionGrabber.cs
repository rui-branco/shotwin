using System.Runtime.InteropServices;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// Repeatedly copies one rectangle of the screen into the same buffer.
///
/// A recording wants thirty of these a second, so unlike
/// <see cref="IScreenCapture.CaptureVirtualDesktop"/> nothing is allocated per frame:
/// the device contexts and the DIB section are made once and reused until disposal.
/// Grabbing the whole virtual desktop and cropping — what the scrolling capture does,
/// where a frame every couple of hundred milliseconds makes it moot — would copy several
/// megabytes a frame to keep a few hundred kilobytes.
///
/// Pixels come out as BGRA, bottom-up rows flipped to top-down, which is what both
/// Skia and the video encoder expect.
/// </summary>
public sealed class RegionGrabber : IDisposable
{
    private readonly int _left;
    private readonly int _top;
    private readonly bool _drawCursor;

    private IntPtr _screenDc;
    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _previous;
    private IntPtr _bits;

    public int Width { get; }
    public int Height { get; }

    /// <summary>Bytes in one frame, which is what the caller must have room for.</summary>
    public int FrameBytes => Width * Height * 4;

    /// <param name="drawCursor">
    /// Paint the mouse pointer into each frame. Off unless asked for: a still capture
    /// wants the screen as it looks in a screenshot, and only a recording is watched
    /// for what someone was doing with the pointer.
    /// </param>
    public RegionGrabber(int left, int top, int width, int height, bool drawCursor = false)
    {
        _left = left;
        _top = top;
        _drawCursor = drawCursor;
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);

        _screenDc = GetDC(IntPtr.Zero);
        _memoryDc = CreateCompatibleDC(_screenDc);

        // POSITIVE height, which is a bottom-up bitmap: the first row in memory is the
        // bottom of the image. That is what the BGRA8 media type means by a frame, and
        // handing the encoder top-down rows recorded everything upside down.
        //
        // GDI still draws in top-left coordinates whichever way the rows are stored, so
        // the cursor draw below is unaffected.
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = Width,
                biHeight = Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0,
            },
        };

        _bitmap = CreateDIBSection(_screenDc, ref info, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap != IntPtr.Zero) _previous = SelectObject(_memoryDc, _bitmap);
    }

    public bool IsReady => _bitmap != IntPtr.Zero && _bits != IntPtr.Zero;

    /// <summary>
    /// Copies the region into <paramref name="destination"/>, which must be at least
    /// <see cref="FrameBytes"/> long. CAPTUREBLT is included so layered windows — every
    /// modern drop shadow, tooltip and popup — appear in the recording.
    /// </summary>
    public bool Grab(Span<byte> destination)
    {
        if (!IsReady || destination.Length < FrameBytes) return false;

        if (!BitBlt(_memoryDc, 0, 0, Width, Height, _screenDc, _left, _top, SRCCOPY | CAPTUREBLT))
            return false;

        // After the blit, never before: the copy of the screen would paint straight over it.
        if (_drawCursor) DrawCursor();

        unsafe
        {
            new ReadOnlySpan<byte>((void*)_bits, FrameBytes).CopyTo(destination);
        }

        return true;
    }

    /// <summary>The cursor the cached hotspot was measured from; a shape keeps its handle.</summary>
    private IntPtr _hotspotOwner;
    private POINT _hotspot;

    /// <summary>
    /// Paints the pointer into the frame that was just blitted. Without it a recording
    /// shows menus opening and text being selected by nothing at all, which is most of
    /// what a screen recording is for.
    /// </summary>
    private void DrawCursor()
    {
        var cursor = new CURSORINFO { cbSize = (uint)Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref cursor)) return;

        // Hidden while typing, and gone entirely on a locked or screensaved desktop.
        if ((cursor.flags & CURSOR_SHOWING) == 0 || cursor.hCursor == IntPtr.Zero) return;

        // Nothing to draw for a pointer on another monitor, and skipping it is cheaper
        // than letting GDI clip a bitmap that lands nowhere near the region.
        int x = cursor.ptScreenPos.X - _left;
        int y = cursor.ptScreenPos.Y - _top;
        if (x < 0 || y < 0 || x >= Width || y >= Height) return;

        // The hotspot is the pixel of the image that sits under the reported position —
        // the tip of the arrow, the middle of the crosshair — so the image goes that far
        // up and left of it. Drawing at the position itself puts every cursor out by as
        // much as its own size.
        var hotspot = HotspotOf(cursor.hCursor);

        DrawIconEx(_memoryDc, x - hotspot.X, y - hotspot.Y, cursor.hCursor,
            0, 0, 0, IntPtr.Zero, DI_NORMAL);
    }

    /// <summary>
    /// Where the given cursor's hotspot sits inside its image, remembered per shape:
    /// GetIconInfo hands back a fresh copy of the mask and colour bitmaps on every call,
    /// and thirty of those a second is thirty GDI objects a second to delete.
    /// </summary>
    private POINT HotspotOf(IntPtr cursor)
    {
        if (cursor == _hotspotOwner) return _hotspot;

        if (!GetIconInfo(cursor, out ICONINFO icon)) return default;

        // Both bitmaps are ours now whether they were wanted or not. A colour cursor
        // leaves hbmColor set and a monochrome one leaves it null, so both are checked.
        if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
        if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);

        _hotspot = new POINT(icon.xHotspot, icon.yHotspot);
        _hotspotOwner = cursor;
        return _hotspot;
    }

    public void Dispose()
    {
        if (_previous != IntPtr.Zero) SelectObject(_memoryDc, _previous);
        if (_bitmap != IntPtr.Zero) DeleteObject(_bitmap);
        if (_memoryDc != IntPtr.Zero) DeleteDC(_memoryDc);
        if (_screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, _screenDc);

        _previous = _bitmap = _memoryDc = _screenDc = _bits = IntPtr.Zero;
    }
}
