using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Shotwin.Interop;

/// <summary>
/// Opts a window into the corner rounding the system is currently using.
///
/// Windows 11 rounds top-level windows in the compositor, but a window with
/// WindowStyle="None" has to ask for it, and one with AllowsTransparency="True" cannot
/// get it at all — layered windows are composited by WPF, not DWM, so those draw their
/// own corners instead. Asking DWM rather than hard-coding a radius means the app
/// follows whatever the OS does, including squaring off on Windows 10.
/// </summary>
public static class WindowCorners
{
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private enum CornerPreference
    {
        Default = 0,
        DoNotRound = 1,
        Round = 2,
        RoundSmall = 3,
    }

    /// <summary>
    /// The radius Windows 11 uses for a standard window. Only for the transparent
    /// windows that have to draw their own corners; everything else asks DWM.
    /// </summary>
    public const double NativeRadius = 8;

    /// <summary>Small popups and flyouts get the tighter radius.</summary>
    public const double NativeRadiusSmall = 4;

    /// <summary>Call once the window has an HWND, from OnSourceInitialized or Loaded.</summary>
    public static void ApplyNative(Window window, bool small = false)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        Apply(handle, small ? CornerPreference.RoundSmall : CornerPreference.Round);
    }

    /// <summary>Squares a window off, for the full-screen capture overlay.</summary>
    public static void ApplySquare(Window window)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        Apply(handle, CornerPreference.DoNotRound);
    }

    private static void Apply(IntPtr handle, CornerPreference preference)
    {
        try
        {
            int value = (int)preference;

            // Unsupported before Windows 11 build 22000; the call simply fails there,
            // which is the correct outcome — those windows should stay square.
            _ = DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE,
                ref value, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}
