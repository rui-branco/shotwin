using System.Runtime.InteropServices;
using Shotwin.Interop;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

internal sealed record CapturableWindow(IntPtr Handle, string Title, NativeMethods.RECT Bounds)
{
    public int Area => Math.Max(0, Bounds.Width) * Math.Max(0, Bounds.Height);
}

/// <summary>
/// Top-level windows in z-order, for the "hover a window to snap to it" mode in the
/// overlay. Filters out the invisible plumbing (cloaked UWP hosts, tool windows,
/// zero-size shells) that would otherwise sit under the cursor and swallow hovers.
/// </summary>
internal static class WindowEnumerator
{
    public static List<CapturableWindow> Enumerate(IntPtr excludeHwnd)
    {
        var results = new List<CapturableWindow>();
        var buffer = new char[512];

        EnumWindows((hwnd, _) =>
        {
            if (hwnd == excludeHwnd) return true;
            if (!IsWindowVisible(hwnd)) return true;

            int style = GetWindowLongW(hwnd, GWL_STYLE);
            if ((style & WS_CHILD) != 0) return true;

            int exStyle = GetWindowLongW(hwnd, GWL_EXSTYLE);
            if ((exStyle & WS_EX_TOOLWINDOW) != 0) return true;

            // Cloaked = alive but not rendered (background UWP apps, other virtual desktops).
            if (DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            var bounds = GetVisibleBounds(hwnd);
            if (bounds.Width < 8 || bounds.Height < 8) return true;

            int len = GetWindowTextW(hwnd, buffer, buffer.Length);
            string title = len > 0 ? new string(buffer, 0, len) : string.Empty;

            results.Add(new CapturableWindow(hwnd, title, bounds));
            return true;
        }, IntPtr.Zero);

        return results;
    }

    /// <summary>
    /// The frame the user actually sees. GetWindowRect includes ~7px of invisible
    /// resize border on Win10+, which would put a halo around every window snap.
    /// </summary>
    public static NativeMethods.RECT GetVisibleBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out NativeMethods.RECT frame,
                Marshal.SizeOf<NativeMethods.RECT>()) == 0
            && frame.Width > 0 && frame.Height > 0)
        {
            return frame;
        }

        GetWindowRect(hwnd, out NativeMethods.RECT rc);
        return rc;
    }

    /// <summary>Topmost window under a virtual-desktop point. EnumWindows is already z-ordered.</summary>
    public static CapturableWindow? HitTest(List<CapturableWindow> windows, int x, int y)
    {
        foreach (var w in windows)
        {
            if (x >= w.Bounds.Left && x < w.Bounds.Right && y >= w.Bounds.Top && y < w.Bounds.Bottom)
                return w;
        }
        return null;
    }
}
