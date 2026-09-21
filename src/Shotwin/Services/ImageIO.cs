using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using SkiaSharp;

namespace Shotwin.Services;

/// <summary>Clipboard, disk and WPF-bridge plumbing for finished shots.</summary>
public static class ImageIO
{
    /// <summary>
    /// Puts the shot on the clipboard in three formats. The DIB that
    /// Clipboard.SetImage produces loses alpha, so PNG goes on first for apps that
    /// prefer it (Slack, Teams, Figma) and the bitmap is the fallback for Office.
    /// </summary>
    public static void CopyToClipboard(SKImage image)
    {
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        var pngStream = new MemoryStream(data.ToArray());

        var dataObject = new DataObject();
        dataObject.SetData("PNG", pngStream, autoConvert: false);
        dataObject.SetImage(ToBitmapSource(image));

        Retry(() => Clipboard.SetDataObject(dataObject, copy: true));
    }

    public static void CopyTextToClipboard(string text) =>
        Retry(() => Clipboard.SetText(text));

    /// <summary>
    /// The clipboard is a shared, single-owner resource; another app holding it open
    /// makes SetDataObject throw. Three quick retries covers virtually every case.
    /// </summary>
    private static void Retry(Action action)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                System.Threading.Thread.Sleep(40);
            }
        }
    }

    public static BitmapSource ToBitmapSource(SKImage image)
    {
        var writeable = new WriteableBitmap(
            image.Width, image.Height, 96, 96, System.Windows.Media.PixelFormats.Pbgra32, null);

        writeable.Lock();
        try
        {
            // Pbgra32 and Bgra8888/Premul are the same bytes, so Skia can write
            // straight into the WPF back buffer with no intermediate copy.
            var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            image.ReadPixels(info, writeable.BackBuffer, writeable.BackBufferStride, 0, 0);
            writeable.AddDirtyRect(new Int32Rect(0, 0, image.Width, image.Height));
        }
        finally
        {
            writeable.Unlock();
        }

        writeable.Freeze();
        return writeable;
    }

    public static BitmapSource ToBitmapSource(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        return ToBitmapSource(image);
    }

    public readonly record struct SaveResult(string? Path, string? Problem)
    {
        public bool Ok => Path is not null;
    }

    /// <summary>Never the configured folder; used only when that one refuses writes.</summary>
    private static string FallbackFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shotwin", "Shots");

    /// <summary>
    /// Writes a PNG to the configured folder, falling back to LocalAppData if that
    /// folder refuses the write.
    ///
    /// The fallback is not paranoia: Windows Controlled Folder Access protects
    /// Pictures and Documents by default on many machines, and it fails writes with
    /// a misleading FileNotFoundException rather than an access error. Losing a shot
    /// the user just framed is the one outcome worth avoiding, so we always land it
    /// somewhere and say where.
    /// </summary>
    public static SaveResult SaveToFolder(SKImage image)
    {
        var settings = SettingsService.Current;

        var attempt = TryWrite(image, settings.SaveFolder);
        if (attempt.Ok) return attempt;

        string fallback = FallbackFolder;
        if (!string.Equals(Path.GetFullPath(fallback), Path.GetFullPath(settings.SaveFolder),
                StringComparison.OrdinalIgnoreCase))
        {
            var retry = TryWrite(image, fallback);
            if (retry.Ok)
                return retry with
                {
                    Problem = Localisation.Format("ErrorSaveFolderBlocked",
                        settings.SaveFolder, attempt.Problem),
                };
        }

        return attempt;
    }

    private static SaveResult TryWrite(SKImage image, string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            string path = NextAvailablePath(
                folder, BuildFileName(SettingsService.Current.FileNameTemplate), ".png");

            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);
            return new SaveResult(path, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Controlled Folder Access surfaces as FileNotFoundException on a path
            // whose parent plainly exists, so name it rather than echoing the nonsense.
            bool looksBlocked = ex is UnauthorizedAccessException
                || (ex is FileNotFoundException && Directory.Exists(Path.GetDirectoryName(folder.TrimEnd('\\'))));

            return new SaveResult(null, looksBlocked
                ? Localisation.Get("ErrorSaveBlocked")
                : ex.Message);
        }
    }

    public static bool SaveAs(SKImage image, string path)
    {
        try
        {
            using var data = image.Encode(
                Path.GetExtension(path).Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    ? SKEncodedImageFormat.Jpeg
                    : SKEncodedImageFormat.Png,
                95);
            using var stream = File.Create(path);
            data.SaveTo(stream);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>What the next file would be called, for the settings preview.</summary>
    public static string PreviewFileName(string template) => BuildFileName(template);

    /// <summary>Expands {format} placeholders against the current time.</summary>
    private static string BuildFileName(string template)
    {
        var now = DateTime.Now;
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < template.Length)
        {
            if (template[i] == '{')
            {
                int close = template.IndexOf('}', i);
                if (close > i)
                {
                    string format = template[(i + 1)..close];
                    result.Append(format == "n" ? string.Empty : now.ToString(format));
                    i = close + 1;
                    continue;
                }
            }
            result.Append(template[i]);
            i++;
        }

        string name = result.ToString();
        foreach (char c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '-');
        return string.IsNullOrWhiteSpace(name) ? "Shot" : name;
    }

    private static string NextAvailablePath(string folder, string baseName, string extension)
    {
        string path = Path.Combine(folder, baseName + extension);
        int counter = 2;
        while (File.Exists(path))
            path = Path.Combine(folder, $"{baseName} ({counter++}){extension}");
        return path;
    }

    /// <summary>
    /// A free path in the save folder, named by the same template and collision rule
    /// every shot gets, for a file this class does not write itself.
    ///
    /// A screen recording needs this: the encoder is handed a path and streams into it
    /// for as long as the recording lasts, so the name has to be settled before there is
    /// anything to save. The same folder as the shots, and the same fallback when
    /// Controlled Folder Access has closed it since startup — a recording must not be
    /// the one thing that lands somewhere nobody looks.
    /// </summary>
    public static string ReserveInSaveFolder(string extension)
    {
        var settings = SettingsService.Current;

        string folder = FolderAccess.IsWritable(settings.SaveFolder)
            ? settings.SaveFolder
            : FallbackFolder;

        return NextAvailablePath(folder, BuildFileName(settings.FileNameTemplate), extension);
    }
}
