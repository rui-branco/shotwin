using System.IO;
using System.Text;

namespace Shotwin.Services;

/// <summary>
/// Last-resort error handling.
///
/// Shotwin lives in the tray with no main window, so an unhandled exception used to
/// take the whole process down silently: shortcuts stop working and nothing says why.
/// One bad click should cost you that click, not the app, so UI-thread exceptions are
/// logged, surfaced, and swallowed.
/// </summary>
public static class CrashLog
{
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shotwin");

    public static string FilePath => Path.Combine(Folder, "errors.log");

    /// <summary>
    /// A failure with no exception behind it: something that returned false, or refused
    /// to go on, where the only other record would be nothing at all.
    /// </summary>
    public static void Note(string context, string message) => Append(context, message);

    public static void Write(string context, Exception exception)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);

            var text = new StringBuilder()
                .AppendLine()
                .AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {context} ===")
                .AppendLine(exception.ToString());

            // Inner exceptions carry the real cause for most WPF binding and
            // template failures, and ToString does not always unwrap them fully.
            var inner = exception.InnerException;
            int depth = 0;
            while (inner is not null && depth++ < 5)
            {
                text.AppendLine($"--- inner {depth} ---").AppendLine(inner.ToString());
                inner = inner.InnerException;
            }

            File.AppendAllText(FilePath, text.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Nothing sensible left to do if even the log cannot be written.
        }
    }

    private static void Append(string context, string body)
    {
        try
        {
            System.IO.Directory.CreateDirectory(Folder);

            File.AppendAllText(FilePath, new StringBuilder()
                .AppendLine()
                .AppendLine($"=== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {context} ===")
                .AppendLine(body)
                .ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
