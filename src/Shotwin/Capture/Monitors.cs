using System.Runtime.InteropServices;
using Shotwin.Interop;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>One physical display, in virtual-desktop physical pixels.</summary>
/// <param name="Work">
/// The part of <paramref name="Bounds"/> the taskbar has not taken. Anything the user is
/// meant to see whole belongs in here; a capture, which wants the pixels themselves,
/// still wants the full bounds.
/// </param>
internal sealed record MonitorInfo(NativeMethods.RECT Bounds, NativeMethods.RECT Work, double ScaleX, double ScaleY)
{
    public int Left => Bounds.Left;
    public int Top => Bounds.Top;
    public int Width => Bounds.Width;
    public int Height => Bounds.Height;

    public bool Contains(int x, int y) =>
        x >= Bounds.Left && x < Bounds.Right && y >= Bounds.Top && y < Bounds.Bottom;
}

internal static class Monitors
{
    /// <summary>Virtual desktop origin in physical pixels. Can be negative on multi-monitor setups.</summary>
    public static (int X, int Y, int Width, int Height) VirtualDesktop => (
        GetSystemMetrics(SM_XVIRTUALSCREEN),
        GetSystemMetrics(SM_YVIRTUALSCREEN),
        GetSystemMetrics(SM_CXVIRTUALSCREEN),
        GetSystemMetrics(SM_CYVIRTUALSCREEN));

    public static IReadOnlyList<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref NativeMethods.RECT rc, IntPtr _) =>
        {
            double sx = 1.0, sy = 1.0;
            if (GetDpiForMonitor(hMon, MDT_EFFECTIVE_DPI, out uint dx, out uint dy) == 0 && dx > 0)
            {
                sx = dx / 96.0;
                sy = dy / 96.0;
            }

            // A monitor Windows will not describe falls back to its full bounds: losing
            // the taskbar strip is better than losing the monitor.
            var info = new NativeMethods.MONITORINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFO>(),
            };
            var work = GetMonitorInfoW(hMon, ref info) ? info.rcWork : rc;

            list.Add(new MonitorInfo(rc, work, sx, sy));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo FromPoint(int x, int y)
    {
        var all = All();
        foreach (var m in all)
            if (m.Contains(x, y)) return m;
        return all.Count > 0 ? all[0] : new MonitorInfo(default, default, 1, 1);
    }
}
