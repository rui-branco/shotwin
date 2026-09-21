using SkiaSharp;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace Shotwin.Services;

public sealed record OcrResult(string Text, int LineCount, string? Problem)
{
    public bool Ok => Problem is null;
    public bool HasText => Ok && Text.Length > 0;
}

/// <summary>
/// Text recognition using the OCR engine built into Windows.
///
/// No model to download and no network call: Windows.Media.Ocr ships with the OS and
/// runs offline, covering whatever language packs are installed (Chinese included).
/// That is the whole reason this app is C# rather than Rust — the same feature would
/// otherwise mean bundling Tesseract and its training data.
/// </summary>
public static class TextRecognition
{
    /// <summary>
    /// Anything smaller than roughly two megapixels gets doubled before recognition.
    ///
    /// Keyed on area, not width: a 1200x150 strip of UI text is wide but has tiny
    /// glyphs, and at native size the engine reads "rn" as "m". Upscaling adds no
    /// detail, but it gives the recognizer the stroke thickness it expects.
    /// </summary>
    private const long UpscaleBelowPixels = 2_000_000;

    public static bool IsAvailable => CreateEngine() is not null;

    public static IReadOnlyList<string> AvailableLanguages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages
                    .Select(l => l.DisplayName)
                    .ToList();
            }
            catch (Exception ex) when (ex is TypeLoadException or NotSupportedException)
            {
                return [];
            }
        }
    }

    public static async Task<OcrResult> RecognizeAsync(SKBitmap bitmap)
    {
        var engine = CreateEngine();
        if (engine is null)
        {
            return new OcrResult(string.Empty, 0, Localisation.Get("ErrorNoOcrLanguage"));
        }

        try
        {
            using var prepared = Prepare(bitmap);
            using var software = await ToSoftwareBitmapAsync(prepared);

            var result = await engine.RecognizeAsync(software);

            // result.Text collapses everything onto one line; joining the lines
            // ourselves keeps the layout of a code block or a list intact.
            string text = string.Join(Environment.NewLine, result.Lines.Select(l => l.Text)).Trim();
            return new OcrResult(text, result.Lines.Count, null);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or System.Runtime.InteropServices.COMException)
        {
            return new OcrResult(string.Empty, 0, ex.Message);
        }
    }

    private static OcrEngine? CreateEngine()
    {
        try
        {
            // The user's own languages first; English is the fallback that is almost
            // always present even when the display language is something else.
            return OcrEngine.TryCreateFromUserProfileLanguages()
                ?? OcrEngine.TryCreateFromLanguage(new Language("en-US"));
        }
        catch (Exception ex) when (ex is ArgumentException or TypeLoadException or NotSupportedException)
        {
            return null;
        }
    }

    private static SKBitmap Prepare(SKBitmap source)
    {
        if ((long)source.Width * source.Height >= UpscaleBelowPixels)
            return source.Copy();

        var scaled = source.Resize(
            new SKImageInfo(source.Width * 2, source.Height * 2, source.ColorType, source.AlphaType),
            new SKSamplingOptions(SKCubicResampler.Mitchell));

        return scaled ?? source.Copy();
    }

    /// <summary>
    /// Goes through PNG rather than poking at the pixel buffer directly. Writing into a
    /// SoftwareBitmap needs the IMemoryBufferByteAccess COM interface and unsafe code;
    /// an encode plus decode costs a few milliseconds and avoids all of it.
    /// </summary>
    private static async Task<SoftwareBitmap> ToSoftwareBitmapAsync(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        byte[] bytes = encoded.ToArray();

        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }

        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        return await decoder.GetSoftwareBitmapAsync(
            BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
    }
}
