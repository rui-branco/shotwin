using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shotwin.Services;

public sealed class AppSettings
{
    /// <summary>Where shots land. Defaults to Pictures\Shotwin so the desktop stays clean.</summary>
    public string SaveFolder { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Shotwin");

    /// <summary>
    /// Set when the app moved the save folder itself because the chosen one refused
    /// writes. Only used to explain the move in Settings; cleared once the user picks
    /// a folder of their own.
    /// </summary>
    public string? SaveFolderMovedFrom { get; set; }

    /// <summary>False until the first capture has asked where shots should go.</summary>
    public bool SaveFolderChosen { get; set; }

    /// <summary>strftime-ish template; {n} is a collision counter.</summary>
    public string FileNameTemplate { get; set; } = "Shot {yyyy-MM-dd} at {HH.mm.ss}";

    public bool CopyToClipboardOnCapture { get; set; } = true;

    /// <summary>
    /// Off by default: a capture saves, copies and drops a preview thumbnail in the
    /// corner, and the editor opens from there if you want it. Throwing a full editor
    /// window up after every screenshot interrupts whatever you were doing.
    /// </summary>
    public bool OpenEditorAfterCapture { get; set; }

    /// <summary>The floating thumbnail that appears in a corner after a capture.</summary>
    public bool ShowCapturePreview { get; set; } = true;

    /// <summary>Cursor, BottomRight, BottomLeft, TopRight or TopLeft.</summary>
    public string PreviewCorner { get; set; } = "Cursor";

    /// <summary>
    /// Write every capture to the save folder without being asked. With this off a shot
    /// only lives on the clipboard and in the preview, and is discarded unless you press
    /// Save there — useful if most of your captures are paste-once-and-forget.
    /// </summary>
    public bool AutoSaveEveryCapture { get; set; } = true;
    public bool PlayShutterSound { get; set; }
    /// <summary>The small chip beside the crosshair: cursor position, or selection size while dragging.</summary>
    public bool ShowCoordinates { get; set; } = true;
    public bool SnapToWindows { get; set; } = true;

    public uint AreaHotkeyModifiers { get; set; } = 0x0002 | 0x0004; // CTRL | SHIFT
    public uint AreaHotkeyKey { get; set; } = 0x32;                  // '2'
    public uint FullscreenHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint FullscreenHotkeyKey { get; set; } = 0x33;            // '3'
    public uint TextHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint TextHotkeyKey { get; set; } = 0x34;                  // '4'
    public uint ColourHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint ColourHotkeyKey { get; set; } = 0x35;                // '5'
    public uint ScrollingHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint ScrollingHotkeyKey { get; set; } = 0x36;             // '6'
    public uint RecordHotkeyModifiers { get; set; } = 0x0002 | 0x0004;
    public uint RecordHotkeyKey { get; set; } = 0x37;                // '7'

    /// <summary>
    /// Which generation of the shipped hotkey numbering this file was written against.
    /// A settings file saved before window capture was dropped carries no version at all;
    /// <see cref="SettingsService"/> reads that absence as 0 and renumbers what it finds.
    /// </summary>
    public int HotkeyLayoutVersion { get; set; } = 2;

    /// <summary>
    /// Frames a second for a screen recording. Thirty reads as smooth for a recording
    /// of a user interface; fifteen halves the file for something mostly still.
    ///
    /// Recordings land in <see cref="SaveFolder"/> with the shots. A folder of their
    /// own would be a second path to settle against Controlled Folder Access, and a
    /// second place to go looking for something the app just made.
    /// </summary>
    public int RecordingFramesPerSecond { get; set; } = 30;

    /// <summary>
    /// Draw the mouse pointer into each frame of a recording. On by default: the screen
    /// copy Windows hands back has no cursor in it, and a recording of someone working
    /// through an interface with no pointer in it is guesswork to follow.
    /// </summary>
    public bool RecordCursor { get; set; } = true;

    /// <summary>
    /// Seconds counted in before the first frame is grabbed, so the shortcut that asked
    /// for the recording is not the first thing in it. Zero starts on the spot.
    /// </summary>
    public int RecordCountdownSeconds { get; set; } = 3;

    /// <summary>Hex, Rgb or Hsl: what the colour picker puts on the clipboard.</summary>
    public string ColourFormat { get; set; } = "Hex";

    /// <summary>
    /// The culture the interface is shown in, as a name like "pt" or "pt-BR". Empty
    /// means follow whatever Windows is set to, which is what a fresh install does and
    /// what anyone who never opens the language list keeps.
    /// </summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>
    /// The tool the editor opens with, remembered across sessions. Crop and Cut are
    /// never stored: they are one-shot operations on the image, and reopening straight
    /// into a crop drag is not what anyone meant by "the tool I was using".
    /// </summary>
    public string LastTool { get; set; } = "Arrow";

    public string LastColorHex { get; set; } = "#FF3B30";
    public float LastStrokeWidth { get; set; } = 4f;
}

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Shotwin");

    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    /// <summary>Written first, then moved over the real file, so a save is all or nothing.</summary>
    private static readonly string TempPath = FilePath + ".tmp";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private static AppSettings? _current;

    public static AppSettings Current => _current ??= Load();

    private static AppSettings Load()
    {
        try
        {
            // A save that was interrupted leaves the half-written temp file behind. It is
            // the newer of the two, but only the complete one is worth reading, so the
            // scrap goes rather than being mistaken for the settings.
            if (File.Exists(TempPath)) TryDelete(TempPath);

            if (File.Exists(FilePath))
            {
                string json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, Options);
                if (loaded is not null)
                {
                    // A property the file never mentions keeps whatever default the class
                    // gives it, and that default is the current layout. So the version is
                    // read from the JSON itself: no version in the file means version 0.
                    using var document = JsonDocument.Parse(json);
                    if (!document.RootElement.TryGetProperty(nameof(AppSettings.HotkeyLayoutVersion), out _))
                        loaded.HotkeyLayoutVersion = 0;

                    MigrateHotkeyLayout(loaded);
                    return loaded;
                }
            }
        }
        catch (JsonException ex)
        {
            // Unreadable, so it cannot be used — but it is the only copy of everything
            // the user ever chose, and starting again from defaults silently is how they
            // find out. Keep it aside, say so, and let them put it back.
            CrashLog.Write("Settings", ex);
            TryMove(FilePath, FilePath + ".bad");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked or unreadable this once. Defaults for this run, and the file is left
            // alone so the next run can read it properly rather than overwriting it.
            CrashLog.Write("Settings", ex);
        }
        return new AppSettings();
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void TryMove(string from, string to)
    {
        try
        {
            File.Move(from, to, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Window capture owned Ctrl+Shift+3, and everything after it sat one number further
    /// along. Dropping that mode closes the gap, but new defaults only ever reach a fresh
    /// install: anyone who has run the app already has the old numbers on disk, so they
    /// are renumbered here. A combination the user chose is left where they put it.
    /// </summary>
    private static void MigrateHotkeyLayout(AppSettings settings)
    {
        if (settings.HotkeyLayoutVersion >= 2) return;

        const uint CtrlShift = 0x0002 | 0x0004;
        if (settings.FullscreenHotkeyModifiers == CtrlShift && settings.FullscreenHotkeyKey == 0x34)
            settings.FullscreenHotkeyKey = 0x33;
        if (settings.TextHotkeyModifiers == CtrlShift && settings.TextHotkeyKey == 0x35)
            settings.TextHotkeyKey = 0x34;
        if (settings.ColourHotkeyModifiers == CtrlShift && settings.ColourHotkeyKey == 0x36)
            settings.ColourHotkeyKey = 0x35;

        settings.HotkeyLayoutVersion = 2;
        Save(settings);
    }

    public static void Save() => Save(Current);

    /// <summary>
    /// Takes what to write rather than reading <see cref="Current"/>, so the migration can
    /// save from inside <see cref="Load"/> without asking for the settings it is loading.
    /// </summary>
    private static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);

            // Written to one side and then moved into place, because writing over the
            // real file is not atomic: the installer stops the app with Stop-Process, and
            // being killed part way through a write left a truncated file that would not
            // parse. Loading then fell back to defaults — which is exactly what "my
            // settings were not saved after updating" looked like. The move either
            // happens or it does not, so the file on disk is always a whole one.
            File.WriteAllText(TempPath, JsonSerializer.Serialize(settings, Options));
            File.Move(TempPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            TryDelete(TempPath);
        }
    }
}
