using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Shotwin.Services;

namespace Shotwin.Shell;

public enum ShellPage
{
    Capture,
    Recent,
    Shortcuts,
    Settings,
    About,
}

/// <summary>
/// The single window the app shows. Capture, Recent, Shortcuts, Settings and About are
/// pages inside it rather than separate windows, so switching between them never
/// spawns anything new — the editor and pinned shots are the only other windows, and
/// those are per-shot by nature.
///
/// Every page stays constructed and just toggles visibility, so a half-typed file-name
/// template or a scrolled gallery survives a trip to another page and back.
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>Raised when a page asks for a capture. The shell hides itself first.</summary>
    public event Action<AppCommand>? CaptureRequested;

    public event Action? HotkeysChanged;
    public event Action<string>? EditFileRequested;
    public event Action<string>? PinFileRequested;

    public MainWindow()
    {
        InitializeComponent();

        FlowDirection = Localisation.FlowDirection;

        // XAML labels re-read themselves through their bindings, but the text this
        // window and its pages compose in code does not, so the pages are asked to
        // rebuild theirs whenever the language changes.
        Localisation.Instance.PropertyChanged += (_, _) => RefreshLanguage();

        NavCaptureItem.Checked += (_, _) => Navigate(ShellPage.Capture);
        NavRecentItem.Checked += (_, _) => Navigate(ShellPage.Recent);
        NavShortcutsItem.Checked += (_, _) => Navigate(ShellPage.Shortcuts);
        NavSettingsItem.Checked += (_, _) => Navigate(ShellPage.Settings);
        NavAboutItem.Checked += (_, _) => Navigate(ShellPage.About);

        CapturePage.Capture += command =>
        {
            Hide();
            CaptureRequested?.Invoke(command);
        };

        RecentPage.EditRequested += path => { Hide(); EditFileRequested?.Invoke(path); };
        RecentPage.PinRequested += path => PinFileRequested?.Invoke(path);

        // Rebinding a shortcut changes the keycap printed on the Capture buttons.
        ShortcutsPage.Applied += () =>
        {
            HotkeysChanged?.Invoke();
            CapturePage.Refresh();
        };

        // A changed save folder is what Recent reads, so keep it in step.
        SettingsPage.Saved += () => RecentPage.Refresh();

        VersionLabel.Text = $"Shotwin {Updater.Current.ToString(3)}";
        UpdateButton.Click += (_, _) => _ = InstallUpdateAsync();
        AboutPage.CheckRequested += () => _ = CheckForUpdateAsync();

        MinimiseButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximiseButton.Click += (_, _) => ToggleMaximise();
        CloseButton.Click += (_, _) => Hide();
        StateChanged += (_, _) => RefreshMaximiseGlyph();

        PreviewKeyDown += OnPreviewKeyDown;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Interop.WindowCorners.ApplyNative(this);
    }

    /// <summary>Rebuilds everything this window and its pages write out by hand.</summary>
    private void RefreshLanguage()
    {
        RefreshMaximiseGlyph();
        ShowUpdate(_update);

        CapturePage.Refresh();
        RecentPage.Refresh();
        ShortcutsPage.Refresh();
        SettingsPage.Refresh();
        AboutPage.Refresh();
    }

    // ---- Navigation -------------------------------------------------------------

    public void Navigate(ShellPage page)
    {
        // Leaving a page must not leave a timer ticking or a hotkey recorder armed.
        CapturePage.CancelCountdown();
        ShortcutsPage.CancelRecording();

        CapturePage.Visibility = page == ShellPage.Capture ? Visibility.Visible : Visibility.Collapsed;
        RecentPage.Visibility = page == ShellPage.Recent ? Visibility.Visible : Visibility.Collapsed;
        ShortcutsPage.Visibility = page == ShellPage.Shortcuts ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == ShellPage.Settings ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = page == ShellPage.About ? Visibility.Visible : Visibility.Collapsed;

        switch (page)
        {
            case ShellPage.Capture: CapturePage.Refresh(); break;
            case ShellPage.Recent: RecentPage.Refresh(); break;
            case ShellPage.Shortcuts: ShortcutsPage.Refresh(); break;
            case ShellPage.Settings: SettingsPage.Refresh(); break;
        }
    }

    /// <summary>Shows the window on a given page, from the tray or a second launch.</summary>
    public void ShowPage(ShellPage page)
    {
        SelectNavItem(page);
        Navigate(page);

        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();

        // Activate alone is not enough. Shotwin normally sits in the tray with no
        // foreground window, so Windows treats it as a background app and silently
        // refuses the focus change — the window comes back behind whatever you were
        // using, which looks exactly like launching it did nothing.
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            Interop.NativeMethods.SetForegroundWindow(handle);
            Interop.NativeMethods.SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0,
                Interop.NativeMethods.SWP_NOMOVE | Interop.NativeMethods.SWP_NOSIZE
                | Interop.NativeMethods.SWP_SHOWWINDOW);
        }
    }

    private void SelectNavItem(ShellPage page)
    {
        var item = page switch
        {
            ShellPage.Recent => NavRecentItem,
            ShellPage.Shortcuts => NavShortcutsItem,
            ShellPage.Settings => NavSettingsItem,
            ShellPage.About => NavAboutItem,
            _ => NavCaptureItem,
        };

        // Checked fires Navigate; setting it directly when it is already checked does not.
        item.IsChecked = true;
    }

    // ---- Keyboard ---------------------------------------------------------------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // An armed hotkey recorder must eat every key, or Tab moves focus and Enter
        // hits a button while the user is trying to record that exact combination.
        if (ShortcutsPage.Visibility == Visibility.Visible && ShortcutsPage.HandleKey(e))
        {
            e.Handled = true;
            return;
        }

        if (e.Key == Key.A && (Keyboard.Modifiers & ModifierKeys.Control) != 0
            && RecentPage.Visibility == Visibility.Visible)
        {
            RecentPage.SelectAll();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        // Esc never hides the window. It used to, which read as the app quitting — and
        // it is the natural key for "undo my selection", which is all it does now.
        if (RecentPage.Visibility == Visibility.Visible && RecentPage.ClearSelection())
            e.Handled = true;
    }

    // ---- Caption ----------------------------------------------------------------

    private void ToggleMaximise() =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    /// <summary>
    /// Square when the window can grow, two offset squares when it can only shrink —
    /// the same language the system caption uses.
    /// </summary>
    private void RefreshMaximiseGlyph()
    {
        bool maximised = WindowState == WindowState.Maximized;

        MaximiseButton.Tag = FindResource(maximised ? "GlyphRestore" : "GlyphMaximise");

        MaximiseButton.ToolTip = Localisation.Get(maximised ? "NavRestore" : "NavMaximise");

        // Without WindowStyle the maximised frame spills past the work area and hides
        // the taskbar, so the lost border is added back as padding.
        RootLayout.Margin = maximised
            ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1)
            : default;
    }

    // ---- Updates ----------------------------------------------------------------

    private ReleaseInfo? _update;

    /// <summary>
    /// What a check found, from the startup one or from About. The version line in the
    /// rail is where it shows: with an update waiting it turns into the way to install it.
    /// </summary>
    public void ShowUpdate(ReleaseInfo? release)
    {
        _update = release;

        VersionLabel.Visibility = release is null ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.Visibility = release is null ? Visibility.Collapsed : Visibility.Visible;
        if (release is not null)
            UpdateButton.Content = Localisation.Format("NavUpdateTo", release.Version.ToString(3));

        AboutPage.ShowUpdate(release);
    }

    private async Task CheckForUpdateAsync()
    {
        AboutPage.ShowChecking();
        ShowUpdate(await Updater.CheckAsync());
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is null) return;

        UpdateButton.IsEnabled = false;

        var progress = new Progress<double>(fraction =>
            UpdateButton.Content = Localisation.Format("NavUpdateDownloading", (int)(fraction * 100)));

        if (await Updater.DownloadAsync(_update, progress, CancellationToken.None))
        {
            try
            {
                // Before the swap, not after: Apply launches the new build itself, and
                // it has to find the single-instance mutex free.
                (Application.Current as App)?.ReleaseSingleInstanceLock();

                Updater.Apply();
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Apply puts the working build back before it rethrows, so the app is
                // still the one that was launched and the offer can simply stand.
                CrashLog.Write("Update", ex);
            }
        }

        ShowUpdate(_update);
        UpdateButton.IsEnabled = true;
    }

    // ---- Chrome -----------------------------------------------------------------

    public void ReportHotkeyConflicts(IReadOnlyList<HotkeyAction> failed) =>
        ShortcutsPage.ReportConflicts(failed);

    /// <remarks>
    /// Application.Shutdown does not raise Closing, so cancelling here cannot trap the
    /// app; Quit still quits.
    /// </remarks>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        e.Cancel = true;
        Hide();
    }
}
