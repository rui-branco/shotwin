using SkiaSharp;
using Shotwin.Interop;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// Grabs the whole virtual desktop into an SKBitmap in one BitBlt.
///
/// BitBlt over the screen DC is the fast, dependency-free path (~10-25ms for a 4K
/// desktop) and is what the overlay freezes. It cannot see hardware-overlay or
/// DRM-protected surfaces (some video players, Netflix in Edge) — those come back
/// black. IScreenCapture exists so a DXGI Desktop Duplication implementation can
/// replace this later without touching callers.
/// </summary>
public interface IScreenCapture
{
    SKBitmap CaptureVirtualDesktop(out int originX, out int originY);
}

public sealed class GdiScreenCapture : IScreenCapture
{
    public SKBitmap CaptureVirtualDesktop(out int originX, out int originY)
    {
        var (vx, vy, vw, vh) = Monitors.VirtualDesktop;
        originX = vx;
        originY = vy;

        if (vw <= 0 || vh <= 0)
            return new SKBitmap(1, 1);

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr dib = IntPtr.Zero;
        IntPtr oldObj = IntPtr.Zero;

        try
        {
            // Negative height => top-down rows, which is the order SKBitmap wants.
            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = vw,
                    biHeight = -vh,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB,
                }
            };

            dib = CreateDIBSection(memDc, ref bmi, DIB_RGB_COLORS, out IntPtr bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                return new SKBitmap(1, 1);

            oldObj = SelectObject(memDc, dib);

            // CAPTUREBLT pulls in layered windows (tooltips, menus) the way the user sees them.
            BitBlt(memDc, 0, 0, vw, vh, screenDc, vx, vy, SRCCOPY | CAPTUREBLT);

            // GDI hands us BGRA with a garbage alpha byte; declaring it Opaque stops Skia
            // from un-premultiplying nonsense and washing the image out.
            var info = new SKImageInfo(vw, vh, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var bitmap = new SKBitmap(info);
            unsafe
            {
                Buffer.MemoryCopy((void*)bits, (void*)bitmap.GetPixels(),
                    (long)info.BytesSize, (long)vw * vh * 4);
            }
            return bitmap;
        }
        finally
        {
            if (oldObj != IntPtr.Zero) SelectObject(memDc, oldObj);
            if (dib != IntPtr.Zero) DeleteObject(dib);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
