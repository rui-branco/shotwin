using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Shotwin.Services;

/// <summary>
/// What Explorer already has open.
///
/// Used to avoid opening a second window onto a folder that is already on screen: saving
/// three trims in a row should not leave three identical windows behind.
/// </summary>
public static class FolderWindows
{
    /// <summary>
    /// True when some Explorer window is already showing this folder.
    ///
    /// Asked of the shell itself rather than guessed from window titles, which are the
    /// folder's display name and are neither unique nor the path.
    ///
    /// Reflection rather than <c>dynamic</c>: this is a handful of late-bound calls, and
    /// dynamic would pull the C# binder into a single-file build for them.
    /// </summary>
    public static bool IsOpen(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;

        object? shell = null;

        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null) return false;

            shell = Activator.CreateInstance(type);
            if (shell is null) return false;

            if (Call(shell, "Windows") is not { } windows) return false;
            if (Get(windows, "Count") is not int count) return false;

            for (int i = 0; i < count; i++)
            {
                if (OpenPath(windows, i) is not { } open) continue;

                if (string.Equals(Tidy(open), Tidy(folder), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex) when (ex is COMException or MissingMemberException
                                      or InvalidOperationException or NotSupportedException)
        {
            // The shell is not answering, so nothing is known to be open. Opening a window
            // that turns out to be a duplicate is the better failure.
            return false;
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
        }

        return false;
    }

    /// <summary>
    /// The folder one shell window is showing, or null when it is not showing one at all —
    /// the same collection holds browser windows, whose Document has no folder behind it.
    /// </summary>
    private static string? OpenPath(object windows, int index)
    {
        try
        {
            if (Call(windows, "Item", index) is not { } window) return null;
            if (Get(window, "Document") is not { } document) return null;
            if (Get(document, "Folder") is not { } folder) return null;
            if (Get(folder, "Self") is not { } self) return null;

            return Get(self, "Path") as string;
        }
        catch (Exception ex) when (ex is COMException or MissingMemberException
                                      or TargetInvocationException)
        {
            return null;
        }
    }

    private static object? Call(object target, string member, params object[] arguments) =>
        target.GetType().InvokeMember(member, BindingFlags.InvokeMethod, null, target, arguments);

    private static object? Get(object target, string member) =>
        target.GetType().InvokeMember(member, BindingFlags.GetProperty, null, target, null);

    private static string Tidy(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
}
