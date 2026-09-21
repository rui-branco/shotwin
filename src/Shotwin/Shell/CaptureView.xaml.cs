using System.Windows.Controls;
using System.Windows.Threading;
using Shotwin.Services;

namespace Shotwin.Shell;

public partial class CaptureView : UserControl
{
    private readonly DispatcherTimer _countdown = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _remaining;

    /// <summary>Raised when a capture should start. The shell hides itself first.</summary>
    public event Action<AppCommand>? Capture;

    public CaptureView()
    {
        InitializeComponent();

        AreaButton.Click += (_, _) => Request(AppCommand.CaptureArea);
        ScreenButton.Click += (_, _) => Request(AppCommand.CaptureScreen);
        ScrollingButton.Click += (_, _) => Request(AppCommand.CaptureScrolling);
        RecordButton.Click += (_, _) => Request(AppCommand.Record);
        TextButton.Click += (_, _) => Request(AppCommand.CaptureText);
        ColourButton.Click += (_, _) => Request(AppCommand.PickColour);

        Delay3.Click += (_, _) => StartCountdown(3);
        Delay5.Click += (_, _) => StartCountdown(5);
        Delay10.Click += (_, _) => StartCountdown(10);

        _countdown.Tick += OnTick;

        Loaded += (_, _) => Refresh();
    }

    /// <summary>
    /// Re-reads the bindings so the keycap printed on each button is the shortcut that
    /// is actually registered, not the default it shipped with.
    /// </summary>
    public void Refresh()
    {
        var s = SettingsService.Current;
        AreaKeys.Text = Compact(new HotkeyBinding(s.AreaHotkeyModifiers, s.AreaHotkeyKey));
        ScreenKeys.Text = Compact(new HotkeyBinding(s.FullscreenHotkeyModifiers, s.FullscreenHotkeyKey));
        ScrollingKeys.Text = Compact(new HotkeyBinding(s.ScrollingHotkeyModifiers, s.ScrollingHotkeyKey));
        RecordKeys.Text = Compact(new HotkeyBinding(s.RecordHotkeyModifiers, s.RecordHotkeyKey));
        TextKeys.Text = Compact(new HotkeyBinding(s.TextHotkeyModifiers, s.TextHotkeyKey));
        ColourKeys.Text = Compact(new HotkeyBinding(s.ColourHotkeyModifiers, s.ColourHotkeyKey));

        // The delay is part of the label, so the three buttons are written here rather
        // than carrying "3 seconds" as a literal each.
        Delay3.Content = Localisation.Format("CaptureDelaySeconds", 3);
        Delay5.Content = Localisation.Format("CaptureDelaySeconds", 5);
        Delay10.Content = Localisation.Format("CaptureDelaySeconds", 10);

        // Say up front when Windows has no OCR language rather than after a capture.
        var languages = TextRecognition.AvailableLanguages;
        bool available = TextRecognition.IsAvailable;
        TextButton.IsEnabled = available;

        SecondaryHint.Text = available
            ? Localisation.Format("CaptureSecondaryHint", string.Join(", ", languages.Take(3)))
            : Localisation.Get("CaptureSecondaryHintNoOcr");
    }

    /// <summary>"Ctrl + Shift + 2" is too wide for a button badge; drop the spaces.</summary>
    private static string Compact(HotkeyBinding binding) => binding.ToString().Replace(" + ", "+");

    private void Request(AppCommand command) => Capture?.Invoke(command);

    private void StartCountdown(int seconds)
    {
        _remaining = seconds;
        CountdownText.Text = Localisation.Format("CaptureCountdown", _remaining);
        SetDelayButtonsEnabled(false);
        _countdown.Start();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        _remaining--;
        if (_remaining > 0)
        {
            CountdownText.Text = Localisation.Format("CaptureCountdown", _remaining);
            return;
        }

        _countdown.Stop();
        CountdownText.Text = string.Empty;
        SetDelayButtonsEnabled(true);
        Request(AppCommand.CaptureArea);
    }

    /// <summary>Stops a running countdown, for when the view is navigated away from.</summary>
    public void CancelCountdown()
    {
        if (!_countdown.IsEnabled) return;
        _countdown.Stop();
        CountdownText.Text = string.Empty;
        SetDelayButtonsEnabled(true);
    }

    private void SetDelayButtonsEnabled(bool enabled)
    {
        Delay3.IsEnabled = enabled;
        Delay5.IsEnabled = enabled;
        Delay10.IsEnabled = enabled;
    }
}
