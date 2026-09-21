using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Shotwin.Services;

namespace Shotwin.Shell;

public partial class ShortcutsView : UserControl
{
    private HotkeyBinding _area;
    private HotkeyBinding _screen;
    private HotkeyBinding _text;
    private HotkeyBinding _colour;
    private HotkeyBinding _scrolling;
    private HotkeyBinding _record;

    private Button? _recording;

    /// <summary>Raised after the bindings are written, so the app can re-register them.</summary>
    public event Action? Applied;

    public ShortcutsView()
    {
        InitializeComponent();

        AreaHotkey.Click += (s, _) => BeginRecording((Button)s!);
        ScreenHotkey.Click += (s, _) => BeginRecording((Button)s!);
        TextHotkey.Click += (s, _) => BeginRecording((Button)s!);
        ColourHotkey.Click += (s, _) => BeginRecording((Button)s!);
        ScrollingHotkey.Click += (s, _) => BeginRecording((Button)s!);
        RecordHotkey.Click += (s, _) => BeginRecording((Button)s!);

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
        _area = new HotkeyBinding(s.AreaHotkeyModifiers, s.AreaHotkeyKey);
        _screen = new HotkeyBinding(s.FullscreenHotkeyModifiers, s.FullscreenHotkeyKey);
        _text = new HotkeyBinding(s.TextHotkeyModifiers, s.TextHotkeyKey);
        _colour = new HotkeyBinding(s.ColourHotkeyModifiers, s.ColourHotkeyKey);
        _scrolling = new HotkeyBinding(s.ScrollingHotkeyModifiers, s.ScrollingHotkeyKey);
        _record = new HotkeyBinding(s.RecordHotkeyModifiers, s.RecordHotkeyKey);
        _recording = null;
        RefreshLabels();
        Warning.Visibility = Visibility.Collapsed;
    }

    // ---- Recording --------------------------------------------------------------

    private void BeginRecording(Button target)
    {
        _recording = target;
        target.Content = Localisation.Get("ShortcutsPressCombination");
        target.Focus();
        Warning.Visibility = Visibility.Collapsed;
    }

    public void CancelRecording()
    {
        if (_recording is null) return;
        _recording = null;
        RefreshLabels();
    }

    /// <summary>
    /// Handled by the shell, which routes key presses here while this view is showing.
    /// Returns true when the press was consumed by an armed recorder.
    /// </summary>
    public bool HandleKey(KeyEventArgs e)
    {
        if (_recording is null) return false;

        if (e.Key == Key.Escape)
        {
            CancelRecording();
            return true;
        }

        if (HotkeyBinding.FromKeyEvent(e) is not { } captured) return true;  // bare modifier

        if (ReferenceEquals(_recording, AreaHotkey)) _area = captured;
        else if (ReferenceEquals(_recording, ScreenHotkey)) _screen = captured;
        else if (ReferenceEquals(_recording, TextHotkey)) _text = captured;
        else if (ReferenceEquals(_recording, ColourHotkey)) _colour = captured;
        else if (ReferenceEquals(_recording, ScrollingHotkey)) _scrolling = captured;
        else if (ReferenceEquals(_recording, RecordHotkey)) _record = captured;

        _recording = null;
        RefreshLabels();
        Apply();

        return true;
    }

    private void RefreshLabels()
    {
        AreaHotkey.Content = _area.ToString();
        ScreenHotkey.Content = _screen.ToString();
        TextHotkey.Content = _text.ToString();
        ColourHotkey.Content = _colour.ToString();
        ScrollingHotkey.Content = _scrolling.ToString();
        RecordHotkey.Content = _record.ToString();
    }

    private bool AllDistinct()
    {
        HotkeyBinding[] all = [_area, _screen, _text, _colour, _scrolling, _record];
        return all.Distinct().Count() == all.Length;
    }

    private void Warn(string message)
    {
        Warning.Text = message;
        Warning.Visibility = Visibility.Visible;
    }

    // ---- Apply ------------------------------------------------------------------

    /// <summary>
    /// Writes every binding and re-registers them. Called as soon as a combination is
    /// recorded rather than from a button, so what the page shows is always what Windows
    /// has been told.
    /// </summary>
    private void Apply()
    {
        // A clash would mean registering one action over another, so nothing is written
        // until it is resolved. The page keeps showing the new combination, which is what
        // needs changing.
        if (!AllDistinct())
        {
            Warn(Localisation.Get("ShortcutsClash"));
            return;
        }

        var s = SettingsService.Current;
        s.AreaHotkeyModifiers = _area.Modifiers;
        s.AreaHotkeyKey = _area.Key;
        s.FullscreenHotkeyModifiers = _screen.Modifiers;
        s.FullscreenHotkeyKey = _screen.Key;
        s.TextHotkeyModifiers = _text.Modifiers;
        s.TextHotkeyKey = _text.Key;
        s.ColourHotkeyModifiers = _colour.Modifiers;
        s.ColourHotkeyKey = _colour.Key;
        s.ScrollingHotkeyModifiers = _scrolling.Modifiers;
        s.ScrollingHotkeyKey = _scrolling.Key;
        s.RecordHotkeyModifiers = _record.Modifiers;
        s.RecordHotkeyKey = _record.Key;
        SettingsService.Save();

        Warning.Visibility = Visibility.Collapsed;

        // The combinations above are live either way — they are registered from what is in
        // memory. Only the record of them is missing, so this says the write was refused
        // rather than pretending it happened.
        Flash(Localisation.Get(
            SettingsService.LoadFailed ? "SettingsNotWritten" : "ShortcutsSaved"));
        Applied?.Invoke();
    }

    /// <summary>Reports registration failures raised after Apply, from the app.</summary>
    public void ReportConflicts(IReadOnlyList<HotkeyAction> failed)
    {
        if (failed.Count == 0) return;
        Warn(Localisation.Format("ShortcutsRefused", HotkeyActionNames.Join(failed)));
    }

    /// <summary>
    /// Collapsed rather than blanked between messages: an empty TextBlock still claims a
    /// line of height, which reads as unexplained padding under the page.
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
