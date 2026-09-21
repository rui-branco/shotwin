using System.Diagnostics;
using System.IO;

namespace Shotwin.Services;

/// <summary>
/// Whether Shotwin can actually write where it has been told to, and the one-click fix
/// when it cannot.
///
/// Windows Controlled Folder Access protects Pictures, Documents, Desktop and Videos by
/// default on many machines, and denies writes there with a FileNotFoundException on a
/// path whose parent plainly exists. Guessing from the path prefix produces false alarms
/// for people who have already allowed the app, so this probes for real.
/// </summary>
public static class FolderAccess
{
    /// <summary>Creates the folder and writes a probe file. The only honest test.</summary>
    public static bool IsWritable(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        string probe = Path.Combine(folder, $".shotwin-probe-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllBytes(probe, [0]);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException or NotSupportedException)
        {
            return false;
        }
        finally
        {
            try
            {
                if (File.Exists(probe)) File.Delete(probe);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>One of the folders Controlled Folder Access protects out of the box.</summary>
    public static bool IsProtectedByDefault(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        ];

        return roots.Any(root =>
            root.Length > 0 && folder.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The always-writable folder shots fall back to.</summary>
    public static string FallbackFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shotwin", "Shots");

    /// <summary>
    /// Settles the save folder at startup instead of leaving it for the user to sort
    /// out: if the configured folder refuses writes, move to one that does and persist
    /// that. Nobody should have to understand Controlled Folder Access to take a
    /// screenshot — the app knows the folder is broken, so it fixes it.
    /// </summary>
    /// <returns>The folder that was abandoned, or null if nothing changed.</returns>
    public static string? EnsureWritableSaveFolder()
    {
        var settings = SettingsService.Current;
        string configured = settings.SaveFolder;

        if (IsWritable(configured)) return null;

        string fallback = FallbackFolder;
        if (string.Equals(configured, fallback, StringComparison.OrdinalIgnoreCase)) return null;
        if (!IsWritable(fallback)) return null;

        settings.SaveFolder = fallback;
        settings.SaveFolderMovedFrom = configured;
        SettingsService.Save();
        return configured;
    }

    /// <summary>
    /// Asks Windows Security to let this exe write to protected folders. Adding an
    /// exclusion is a machine-wide Defender change, so it needs elevation — the UAC
    /// prompt is Windows asking, and declining it just returns false.
    /// </summary>
    public static bool TryAllowThisApp(out string? problem)
    {
        string exe = Environment.ProcessPath ?? string.Empty;
        if (exe.Length == 0)
        {
            problem = Localisation.Get("ErrorOwnPathUnknown");
            return false;
        }

        // Single-quoted PowerShell literal; doubling is how a quote is escaped there.
        string literal = exe.Replace("'", "''");
        string command =
            $"Add-MpPreference -ControlledFolderAccessAllowedApplications '{literal}'";

        try
        {
            var process = Process.Start(new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -Command \"{command}\"",
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
            });

            if (process is null)
            {
                problem = Localisation.Get("ErrorElevationFailed");
                return false;
            }

            process.WaitForExit(20_000);

            if (process.HasExited && process.ExitCode != 0)
            {
                problem = Localisation.Get("ErrorSecurityRefused");
                return false;
            }

            problem = null;
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // 1223 ERROR_CANCELLED, plus anything else the shell refuses.
            problem = Localisation.Get("ErrorAdminPromptDismissed");
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            problem = ex.Message;
            return false;
        }
    }
}
