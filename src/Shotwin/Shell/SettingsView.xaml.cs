using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using Shotwin.Services;

namespace Shotwin.Shell;

public partial class SettingsView : UserControl
{
    /// <summary>Raised after a save, so other views can pick up changed paths.</summary>
    public event Action? Saved;

    /// <summary>Set while Refresh is pushing stored values into the controls, whose
    /// change events would otherwise write those same values straight back out.</summary>
    private bool _loading;

    public SettingsView()
    {
        InitializeComponent();

        BrowseFolder.Click += (_, _) => Browse();
        OpenFolder.Click += (_, _) => OpenSaveFolder();
        AllowAppButton.Click += (_, _) => AllowThroughWindowsSecurity();

        FileTemplate.TextChanged += (_, _) => UpdatePreview();
        SaveFolder.TextChanged += (_, _) => UpdateFolderWarning();

        // Typing commits on the way out rather than per keystroke: a half-typed path is
        // not a setting, and the folder warning would flicker through every prefix.
        FileTemplate.LostFocus += (_, _) => Commit();
        SaveFolder.LostFocus += (_, _) => Commit();

        foreach (var box in new[] { OpenEditor, CopyClipboard, ShowCoordinates, SnapWindows,
                                    ShowPreview, AutoSave, ShowCursor, StartWithWindows })
        {
            box.Checked += (_, _) => Commit();
            box.Unchecked += (_, _) => Commit();
        }

        PreviewCorner.SelectionChanged += (_, _) => Commit();
        FrameRate.SelectionChanged += (_, _) => Commit();
        Countdown.SelectionChanged += (_, _) => Commit();
        ColourFormat.SelectionChanged += (_, _) => Commit();
        LanguageBox.SelectionChanged += (_, _) => Commit();

        // The notation names and the worked examples beside them are not translated:
        // hex and rgb() mean the same thing in every language.
        ColourFormat.ItemsSource = new[] { "Hex, #1B1B1F", "RGB, rgb(27, 27, 31)", "HSL, hsl(240, 7%, 11%)" };

        ShowPreview.Checked += (_, _) => UpdatePreviewOptionState();
        ShowPreview.Unchecked += (_, _) => UpdatePreviewOptionState();
        OpenEditor.Checked += (_, _) => UpdatePreviewOptionState();
        OpenEditor.Unchecked += (_, _) => UpdatePreviewOptionState();

        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusRow.Visibility = Visibility.Collapsed;
        };

        Loaded += (_, _) => Refresh();
    }

    public void Refresh()
    {
        var s = SettingsService.Current;
        _loading = true;

        // The two lists whose entries are themselves translated have to be rebuilt
        // here, not filled once in the constructor, or they keep the wording of
        // whatever language the page was first shown in.
        PreviewCorner.ItemsSource = new[]
        {
            Localisation.Get("SettingsCornerCursor"),
            Localisation.Get("SettingsCornerBottomRight"),
            Localisation.Get("SettingsCornerBottomLeft"),
            Localisation.Get("SettingsCornerTopRight"),
            Localisation.Get("SettingsCornerTopLeft"),
        };

        // Rebuilt here rather than filled once in the constructor: "30 fps" is a
        // translated string with the number dropped into it, so it has to follow a
        // language change like the two lists above.
        FrameRate.ItemsSource = FrameRates
            .Select(f => Localisation.Format("RecordFrameRateOption", f))
            .ToArray();

        // Same again for the count-in, where the entry for no countdown at all is a word
        // rather than a number of seconds.
        Countdown.ItemsSource = CountdownSeconds
            .Select(seconds => seconds == 0
                ? Localisation.Get("RecordCountdownNone")
                : Localisation.Format("RecordCountdownOption", seconds))
            .ToArray();

        RefreshLanguages();

        OpenEditor.IsChecked = s.OpenEditorAfterCapture;
        CopyClipboard.IsChecked = s.CopyToClipboardOnCapture;
        ShowCoordinates.IsChecked = s.ShowCoordinates;
        SnapWindows.IsChecked = s.SnapToWindows;

        ShowPreview.IsChecked = s.ShowCapturePreview;
        PreviewCorner.SelectedIndex = CornerToIndex(s.PreviewCorner);
        AutoSave.IsChecked = s.AutoSaveEveryCapture;
        ColourFormat.SelectedIndex = FormatToIndex(s.ColourFormat);
        FrameRate.SelectedIndex = FrameRateToIndex(s.RecordingFramesPerSecond);
        ShowCursor.IsChecked = s.RecordCursor;
        Countdown.SelectedIndex = CountdownToIndex(s.RecordCountdownSeconds);

        SaveFolder.Text = s.SaveFolder;
        FileTemplate.Text = s.FileNameTemplate;
        StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
        LanguageBox.SelectedIndex = LanguageToIndex(s.Language);

        UpdatePreview();
        UpdateFolderWarning();
        UpdatePreviewOptionState();

        _loading = false;
    }

    /// <summary>
    /// The preview only ever appears when the editor does not, so its options grey out
    /// rather than sitting there pretending to do something.
    /// </summary>
    private void UpdatePreviewOptionState()
    {
        bool live = ShowPreview.IsChecked == true && OpenEditor.IsChecked != true;
        PreviewOptions.IsEnabled = live;
        PreviewOptions.Opacity = live ? 1.0 : 0.4;
    }

    // ---- Language ---------------------------------------------------------------

    /// <summary>
    /// The cultures behind the list, in the same order, with a null in front for
    /// "follow Windows". Rebuilt with the list so the two cannot drift apart.
    /// </summary>
    private CultureInfo?[] _languages = [null];

    private void RefreshLanguages()
    {
        _languages = [null, .. Localisation.Instance.Available];

        LanguageBox.ItemsSource = _languages
            .Select(c => c is null
                ? Localisation.Get("SettingsLanguageAutomatic")
                : Localisation.NativeName(c))
            .ToArray();
    }

    private int LanguageToIndex(string stored)
    {
        var culture = Localisation.Parse(stored);
        if (culture is null) return 0;

        int index = Array.FindIndex(_languages,
            c => c is not null && string.Equals(c.Name, culture.Name, StringComparison.OrdinalIgnoreCase));

        // A language that was dropped from the build reads as automatic rather than
        // leaving the list on nothing.
        return index < 0 ? 0 : index;
    }

    private static readonly string[] FormatKeys = ["Hex", "Rgb", "Hsl"];

    private static int FormatToIndex(string format)
    {
        int index = Array.FindIndex(FormatKeys,
            k => string.Equals(k, format, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// The two rates offered, smoothest first. Anything finer is a menu of numbers
    /// nobody can choose between by reading them.
    /// </summary>
    /// <summary>
    /// Sixty for anything with motion in it, thirty as the sane default, and the lower
    /// rates for a mostly static screen where file size matters more than smoothness.
    ///
    /// A hundred and twenty is offered but only a small region will reach it: measured
    /// on this capture path, 720p grabs at about 175 frames a second, 1080p at 87 and an
    /// ultrawide at 45. Falling short is not a failure — every frame carries the time it
    /// was taken, so the recording plays at the right speed with fewer frames in it.
    /// </summary>
    private static readonly int[] FrameRates = [120, 60, 30, 24, 15];

    private static int FrameRateToIndex(int fps)
    {
        int index = Array.IndexOf(FrameRates, fps);
        return index < 0 ? 0 : index;
    }

    /// <summary>
    /// How long the recording is counted in for. None for anyone who would rather have
    /// it start where they pressed, then three to gather yourself, and two longer ones
    /// for getting to another window first.
    /// </summary>
    private static readonly int[] CountdownSeconds = [0, 3, 5, 10];

    private static int CountdownToIndex(int seconds)
    {
        int index = Array.IndexOf(CountdownSeconds, seconds);

        // Falls back to the three-second default rather than to the first entry the way
        // the lists above do: first here is no countdown at all, and a stored value the
        // list does not hold is no reason to show the feature switched off.
        return index < 0 ? 1 : index;
    }

    private static readonly string[] CornerKeys =
        ["Cursor", "BottomRight", "BottomLeft", "TopRight", "TopLeft"];

    private static int CornerToIndex(string corner)
    {
        int index = Array.FindIndex(CornerKeys,
            k => string.Equals(k, corner, StringComparison.OrdinalIgnoreCase));
        return index < 0 ? 0 : index;
    }

    private void Commit()
    {
        if (_loading) return;

        var s = SettingsService.Current;

        s.OpenEditorAfterCapture = OpenEditor.IsChecked == true;
        s.CopyToClipboardOnCapture = CopyClipboard.IsChecked == true;
        s.ShowCoordinates = ShowCoordinates.IsChecked == true;
        s.ShowCapturePreview = ShowPreview.IsChecked == true;
        s.PreviewCorner = CornerKeys[Math.Max(0, PreviewCorner.SelectedIndex)];
        s.AutoSaveEveryCapture = AutoSave.IsChecked == true;
        s.ColourFormat = FormatKeys[Math.Max(0, ColourFormat.SelectedIndex)];
        s.RecordingFramesPerSecond = FrameRates[Math.Max(0, FrameRate.SelectedIndex)];
        s.RecordCursor = ShowCursor.IsChecked == true;
        s.RecordCountdownSeconds = CountdownSeconds[Math.Max(0, Countdown.SelectedIndex)];
        s.SnapToWindows = SnapWindows.IsChecked == true;

        if (!string.IsNullOrWhiteSpace(SaveFolder.Text)) s.SaveFolder = SaveFolder.Text.Trim();
        if (!string.IsNullOrWhiteSpace(FileTemplate.Text)) s.FileNameTemplate = FileTemplate.Text.Trim();

        var language = _languages[Math.Clamp(LanguageBox.SelectedIndex, 0, _languages.Length - 1)];
        s.Language = language?.Name ?? string.Empty;

        SettingsService.Save();

        // Applied after the save, so the setting on disk is the one on screen even if
        // the switch throws. Everything bound through {loc:S} redraws from here; this
        // page is rebuilt too, which is why the guard above is still up.
        if (!Equals(language, Localisation.Instance.Chosen))
            Localisation.Instance.Apply(language);

        bool wantStartup = StartWithWindows.IsChecked == true;
        if (wantStartup != StartupRegistration.IsEnabled && !StartupRegistration.Set(wantStartup))
        {
            // Putting the box back is the honest report, but it fires Unchecked and
            // would re-enter here, so the guard goes up for the correction.
            _loading = true;
            StartWithWindows.IsChecked = StartupRegistration.IsEnabled;
            _loading = false;
            Flash(Localisation.Get("SettingsStartupRefused"));
        }
        else
        {
            Flash(Localisation.Get("SettingsSaved"));
        }

        Saved?.Invoke();
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Localisation.Get("SettingsChooseFolderTitle"),
            InitialDirectory = Directory.Exists(SaveFolder.Text)
                ? SaveFolder.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) == true)
            SaveFolder.Text = dialog.FolderName;
    }

    private void OpenSaveFolder()
    {
        try
        {
            Directory.CreateDirectory(SaveFolder.Text);
            Process.Start(new ProcessStartInfo(SaveFolder.Text) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            Flash(Localisation.Get("SettingsFolderOpenFailed"));
        }
    }

    /// <summary>
    /// Probes the folder for real rather than guessing from its path, and only complains
    /// when a write genuinely fails — someone who has already allowed Shotwin through
    /// Windows Security should never see a warning about it.
    /// </summary>
    private void UpdateFolderWarning()
    {
        FolderFixStatus.Text = string.Empty;

        string folder = SaveFolder.Text?.Trim() ?? string.Empty;
        if (folder.Length == 0 || FolderAccess.IsWritable(folder))
        {
            FolderProblem.Visibility = Visibility.Collapsed;
            return;
        }

        bool protectedFolder = FolderAccess.IsProtectedByDefault(folder);

        FolderWarning.Text = Localisation.Get(protectedFolder
            ? "SettingsFolderProtected"
            : "SettingsFolderUnwritable");

        AllowAppButton.Visibility = protectedFolder ? Visibility.Visible : Visibility.Collapsed;
        FolderProblem.Visibility = Visibility.Visible;
    }

    private void AllowThroughWindowsSecurity()
    {
        FolderFixStatus.Text = Localisation.Get("SettingsWaitingForAdmin");
        AllowAppButton.IsEnabled = false;

        try
        {
            if (!FolderAccess.TryAllowThisApp(out string? problem))
            {
                FolderFixStatus.Text = problem;
                return;
            }

            // Defender applies the exclusion immediately, so re-probing settles it.
            UpdateFolderWarning();

            FolderFixStatus.Text = FolderProblem.Visibility == Visibility.Visible
                ? Localisation.Get("SettingsSecurityStillBlocked")
                : Localisation.Get("SettingsSecurityDone");
        }
        finally
        {
            AllowAppButton.IsEnabled = true;
        }
    }


    private void UpdatePreview()
    {
        string template = FileTemplate.Text ?? string.Empty;
        // Get, not Format: the hint's own braces are the example, so nothing may go
        // looking for placeholders in it.
        FilePreview.Text = template.Length == 0
            ? Localisation.Get("SettingsFileTemplateHint")
            : Localisation.Format("SettingsNextFile", ImageIO.PreviewFileName(template));
    }

    /// <summary>
    /// Shows a short confirmation under the page, then takes the row back out of the
    /// layout. Collapsing rather than blanking it: an empty TextBlock still claims a
    /// line of height, which reads as unexplained padding along the bottom edge.
    ///
    /// One shared timer, restarted on each message — settings now save on every change,
    /// so a new timer per save would leave a pile of them ticking.
    /// </summary>
    private readonly System.Windows.Threading.DispatcherTimer _statusTimer =
        new() { Interval = TimeSpan.FromSeconds(2.5) };

    private void Flash(string message)
    {
        StatusText.Text = message;
        StatusRow.Visibility = Visibility.Visible;

        _statusTimer.Stop();
        _statusTimer.Start();
    }
}
