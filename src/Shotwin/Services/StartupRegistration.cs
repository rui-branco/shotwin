using Microsoft.Win32;

namespace Shotwin.Services;

/// <summary>
/// Run-at-login, via the per-user Run key.
///
/// A Startup-folder shortcut would work too, but the Run key needs no COM shell
/// plumbing and is a single source of truth the app itself can read back, so the
/// settings checkbox can never drift out of sync with reality.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Shotwin";

    private static string ExecutablePath =>
        Environment.ProcessPath ?? System.Reflection.Assembly.GetEntryAssembly()?.Location ?? string.Empty;

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    /// <summary>Returns false when the registry refused the write, so the UI can revert.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key is null) return false;

            if (enabled)
            {
                string path = ExecutablePath;
                if (string.IsNullOrEmpty(path)) return false;

                // --background, or the home window would pop up at every sign-in.
                key.SetValue(ValueName, $"\"{path}\" --background");
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
