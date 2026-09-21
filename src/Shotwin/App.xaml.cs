using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using H.NotifyIcon;
using Shotwin.Capture;
using Shotwin.Editor;
using Shotwin.Overlay;
using Shotwin.Pin;
using Shotwin.Preview;
using Shotwin.Services;
using Shotwin.Shell;
using SkiaSharp;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin;

public partial class App : Application
{
    private const string MutexName = "Shotwin.SingleInstance.v1";

    private Mutex? _instanceMutex;
    private TaskbarIcon? _tray;
    private HotkeyService? _hotkeys;
    private CommandChannel? _commands;
    private MainWindow? _shell;
    private readonly IScreenCapture _capture = new GdiScreenCapture();

    /// <summary>Guards against a second overlay stacking on the first if a hotkey repeats.</summary>
    private OverlayWindow? _overlay;

    /// <summary>Set for the next capture only: recognise text and copy that, not the image.</summary>
    private bool _captureTextInstead;

    private PreviewWindow? _preview;

    /// <summary>The recording in progress, and the guard against starting a second.</summary>
    private ScreenRecorder? _recorder;

    /// <summary>
    /// Watches for the keys that end a recording. There is no window to receive them:
    /// the on-screen bar was removed because it sat over the very thing being recorded,
    /// so the keys are polled, the same way the preview thumbnail polls for clicks it
    /// can never be told about.
    /// </summary>
    private System.Windows.Threading.DispatcherTimer? _recordingKeys;

    /// <summary>
    /// The strip on screen. It closes itself when the recording ends, so this reference
    /// is only for hiding it early — it is never the thing responsible for tidying up.
    /// </summary>
    private RecordingIndicator? _indicator;
    private RecordingFrame? _frame;

    private MenuItem? _stopRecordingItem;
    private MenuItem? _cancelRecordingItem;

    /// <summary>Ticks once a second to keep the tray tooltip's clock honest.</summary>
    private System.Windows.Threading.DispatcherTimer? _recordingTick;

    /// <summary>The icon the tray wears when nothing is being recorded.</summary>
    private BitmapImage? _idleIcon;

    /// <summary>
    /// What clicking the notification now on screen should show in Explorer, or null
    /// when the current one is about nothing on disk.
    /// </summary>
    private string? _notificationFile;

    /// <summary>The newer build the startup check found, if it found one.</summary>
    private ReleaseInfo? _update;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // First, before anything can put a word on screen: the tray menu, the first-run
        // folder question and the shell all read their text at construction time.
        Localisation.Instance.ApplyStored();

        InstallCrashHandlers();

        var requested = CommandChannel.Parse(e.Args);

        _instanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool isFirstInstance);
        if (!isFirstInstance)
        {
            // Hotkeys are process-global; a second copy would just fight the first.
            // Hand the request to the instance that owns them, then leave. Launching
            // the app again while it runs therefore raises its window rather than
            // looking like nothing happened.
            if (requested != AppCommand.StayHidden)
                CommandChannel.Send(requested);
            Shutdown();
            return;
        }

        // Only the instance that owns the app sweeps up after an update, so a second
        // launch cannot delete a download the first one is part way through.
        Updater.CleanupOldBuild();

        // Settle the save folder before anything can try to write to it.
        FolderAccess.EnsureWritableSaveFolder();

        _commands = new CommandChannel();
        _commands.Requested += command => Dispatcher.Invoke(() => RunCommand(command));
        _commands.Listen();

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += OnHotkey;
        _hotkeys.RegisterFromSettings();

        BuildTrayIcon();
        WarnAboutHotkeyConflicts();

        // A first launch that already asked for something should not wait for a hotkey.
        Dispatcher.BeginInvoke(() => RunCommand(requested));

        _ = CheckForUpdateAsync();
    }

    /// <summary>
    /// Keeps a UI-thread exception from taking the tray app down with it. The shortcuts
    /// have to keep working even when one window misbehaves.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            CrashLog.Write("Dispatcher", args.Exception);
            args.Handled = true;

            // Whatever threw, the encoder is still running and the window that would
            // have stopped it may be the one that died. Close the recording out rather
            // than leave it filling the disk with nothing on screen to end it.
            if (_recorder is not null) _ = FinishRecordingAsync(keep: true);

            Notify(Localisation.Format("TrayCrash", args.Exception.Message, CrashLog.FilePath));
        };

        // Non-UI threads cannot be recovered, but they can at least leave a trace.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) CrashLog.Write("AppDomain", ex);
        };
    }

    private void BuildTrayIcon()
    {
        var menu = new ContextMenu();

        // The gestures beside each item are key names, so they stay as they are.
        menu.Items.Add(MenuItem("TrayCaptureArea", "Ctrl+Shift+2", StartCapture));
        menu.Items.Add(MenuItem("TrayCaptureScreen", "Ctrl+Shift+3", CaptureCurrentMonitor));
        menu.Items.Add(MenuItem("TrayGrabText", "Ctrl+Shift+4", () => RunCommand(AppCommand.CaptureText)));
        menu.Items.Add(MenuItem("TrayPickColour", "Ctrl+Shift+5", () => RunCommand(AppCommand.PickColour)));
        menu.Items.Add(MenuItem("TrayScrollingCapture", "Ctrl+Shift+6", StartScrollingCapture));
        menu.Items.Add(MenuItem("RecordTrayItem", "Ctrl+Shift+7", StartRecording));

        // The two ways out of a recording. Hidden the rest of the time rather than
        // greyed: a menu that offers stopping when nothing is running reads as broken.
        // This is the whole visible state of a recording now — there is no bar on screen,
        // because a bar on screen ends up inside the recording.
        _stopRecordingItem = MenuItem("RecordTrayStopItem", "Esc", () => _ = FinishRecordingAsync(keep: true));
        _cancelRecordingItem = MenuItem("RecordTrayCancelItem", "Shift+Esc", () => _ = FinishRecordingAsync(keep: false));

        _stopRecordingItem.Visibility = Visibility.Collapsed;
        _cancelRecordingItem.Visibility = Visibility.Collapsed;

        menu.Items.Add(_stopRecordingItem);
        menu.Items.Add(_cancelRecordingItem);

        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("TrayOpen", null, () => ShowShell(ShellPage.Capture)));
        menu.Items.Add(MenuItem("TrayRecent", null, () => ShowShell(ShellPage.Recent)));
        menu.Items.Add(MenuItem("TraySettings", null, () => ShowShell(ShellPage.Settings)));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("TrayQuit", null, Shutdown));

        _tray = new TaskbarIcon
        {
            ToolTipText = "Shotwin",
            IconSource = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
            ContextMenu = menu,
            NoLeftClickDelay = true,
        };
        _idleIcon = new BitmapImage(new Uri("pack://application:,,,/Assets/app.ico"));

        _tray.TrayLeftMouseUp += (_, _) => StartCapture();
        _tray.TrayBalloonTipClicked += (_, _) => RevealNotifiedFile();
        _tray.ForceCreate();
    }

    /// <summary>
    /// Bound rather than assigned, so the menu follows a language change without the
    /// tray icon being torn down and rebuilt.
    /// </summary>
    private static MenuItem MenuItem(string headerKey, string? gesture, Action action)
    {
        var item = new MenuItem { InputGestureText = gesture ?? string.Empty };

        item.SetBinding(HeaderedItemsControl.HeaderProperty, new System.Windows.Data.Binding($"[{headerKey}]")
        {
            Source = Localisation.Instance,
            Mode = System.Windows.Data.BindingMode.OneWay,
        });

        item.Click += (_, _) => action();
        return item;
    }

    // ---- Notifications ----------------------------------------------------------

    /// <summary>
    /// Every message the app raises, and the file — when there is one — that clicking
    /// it should show in Explorer.
    ///
    /// A recording needs that: it never reaches the Recent gallery, which lists images,
    /// and it opens no editor, so without this there is nothing pointing at the file
    /// but its name in a notification that is about to disappear.
    /// </summary>
    private void Notify(string message, string? reveals = null)
    {
        _notificationFile = reveals;
        _tray?.ShowNotification("Shotwin", message);
    }

    private void RevealNotifiedFile()
    {
        if (_notificationFile is not { } path || !File.Exists(path)) return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                      or InvalidOperationException)
        {
            // Explorer refused to come. The file is still where the message said it was.
        }
    }

    // ---- Commands ---------------------------------------------------------------

    private void RunCommand(AppCommand command)
    {
        switch (command)
        {
            case AppCommand.StayHidden:
                return;

            case AppCommand.ShowHome:
                ShowShell(ShellPage.Capture);
                return;

            case AppCommand.OpenSettings:
                ShowShell(ShellPage.Settings);
                return;

        }

        if (CommandChannel.ToCaptureAction(command) is { } action)
            OnHotkey(action);
    }

    private void OnHotkey(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.CaptureArea: StartCapture(); break;
            case HotkeyAction.CaptureFullscreen: CaptureCurrentMonitor(); break;

            case HotkeyAction.CaptureText:
                _captureTextInstead = true;
                StartCapture();
                break;

            case HotkeyAction.PickColour:
                OpenOverlay(OverlayWindow.OverlayMode.ColourPick);
                break;

            case HotkeyAction.CaptureScrolling: StartScrollingCapture(); break;

            case HotkeyAction.Record: StartRecording(); break;
        }
    }

    // ---- Shell ------------------------------------------------------------------

    private void ShowShell(ShellPage page)
    {
        if (_shell is null)
        {
            _shell = new MainWindow();
            _shell.CaptureRequested += RunCommand;
            _shell.EditFileRequested += OpenFileInEditor;
            _shell.PinFileRequested += PinFile;
            _shell.HotkeysChanged += () =>
            {
                _hotkeys?.RegisterFromSettings();
                if (_hotkeys is not null)
                    _shell?.ReportHotkeyConflicts(_hotkeys.Failed);
            };

            // The check usually finishes long before the window is first opened.
            if (_update is not null) _shell.ShowUpdate(_update);
        }

        _shell.ShowPage(page);
    }

    // ---- Updates ----------------------------------------------------------------

    /// <summary>
    /// One silent look at GitHub per run. Shotwin spends most of its life in the tray
    /// with nothing on screen to interrupt, so a newer build is never announced: it only
    /// changes what the rail offers next time the window is open.
    /// </summary>
    private async Task CheckForUpdateAsync()
    {
        _update = await Updater.CheckAsync();
        if (_update is not null) _shell?.ShowUpdate(_update);
    }

    private void WarnAboutHotkeyConflicts()
    {
        if (_hotkeys is not { Failed.Count: > 0 }) return;

        string names = HotkeyActionNames.Join(_hotkeys.Failed);
        Notify(Localisation.Format("TrayHotkeyConflicts", names));
    }

    // ---- Capture ----------------------------------------------------------------

    private void StartCapture() => OpenOverlay(OverlayWindow.OverlayMode.Area);

    private void StartScrollingCapture() => OpenOverlay(OverlayWindow.OverlayMode.ScrollingArea);

    private void OpenOverlay(OverlayWindow.OverlayMode mode)
    {
        if (_overlay is not null)
        {
            _overlay.Activate();
            return;
        }

        var frozen = _capture.CaptureVirtualDesktop(out int originX, out int originY);

        _overlay = new OverlayWindow(frozen, originX, originY, mode);
        _overlay.Captured += OnCaptured;
        _overlay.ColourPicked += OnColourPicked;
        _overlay.ScrollingAreaChosen += region => _ = RunScrollingCaptureAsync(region);
        _overlay.RecordAreaChosen += region => _ = BeginRecordingAsync(region);
        _overlay.Closed += (_, _) => _overlay = null;
        _overlay.Show();
    }

    /// <summary>
    /// Wheels the chosen region and stitches the frames, then hands the result to the
    /// ordinary post-capture path: a long screenshot is still a screenshot, and it
    /// should copy, save and preview like any other.
    /// </summary>
    private async Task RunScrollingCaptureAsync(SKRectI region)
    {
        var session = new ScrollingCaptureSession(_capture, region);
        var progress = new ScrollProgressWindow(region);

        progress.StopRequested += session.Cancel;
        session.Progress += progress.Report;
        progress.Show();

        SKBitmap? stitched;
        try
        {
            stitched = await session.RunAsync();
        }
        finally
        {
            progress.Close();
        }

        if (stitched is null) return;
        OnCaptured(stitched, region);
    }

    // ---- Recording --------------------------------------------------------------

    /// <summary>
    /// The one way into a recording. What gets recorded is settled in the overlay — a
    /// dragged region, a window clicked whole, or the monitor under the cursor with
    /// Ctrl+A — so there is nothing to decide before it opens, and nothing is encoded
    /// until a region comes back from it.
    /// </summary>
    private void StartRecording()
    {
        if (AlreadyRecording()) return;

        OpenOverlay(OverlayWindow.OverlayMode.RecordArea);
    }

    /// <summary>
    /// True when a recording is already running, and says so on the way out. The hotkey,
    /// the tray item and the Capture page button all come through here, so one guard
    /// covers all of them. Two encoders over the same screen would compete for the same
    /// hardware and write two stuttering files.
    /// </summary>
    private bool AlreadyRecording()
    {
        if (_recorder is null) return false;

        Notify(Localisation.Get("RecordTrayBusy"));
        return true;
    }

    /// <summary>
    /// Counts the recording in, starts the encoder on the chosen region and leaves the
    /// bar on screen.
    ///
    /// Nothing else from the capture path applies: there is no bitmap to put on the
    /// clipboard and no preview thumbnail that could show a frame of it. The file and a
    /// notification naming it are the whole result; trimming it is something the gallery
    /// offers afterwards, not something this path waits for.
    /// </summary>
    private async Task BeginRecordingAsync(SKRectI region)
    {
        var settings = SettingsService.Current;

        // The same settled save folder every shot goes to, named by the same template,
        // so a recording is never the one thing that lands somewhere else.
        string path = ImageIO.ReserveInSaveFolder(".mp4");

        var recorder = new ScreenRecorder(
            new SKRectIRegion(region.Left, region.Top, region.Width, region.Height),
            path, settings.RecordingFramesPerSecond, settings.RecordCursor);

        // Claimed before the first await: the count-in and preparing the transcode
        // together take long enough for another hotkey press to arrive, and that one has
        // to find the slot taken.
        _recorder = recorder;

        // Counted in before the encoder is asked for a single frame, so none of the
        // count-in can reach the file — and neither can the second spent letting go of
        // the shortcut that asked for the recording, which is what it is for.
        //
        // Counted down on the tray icon rather than in a window on screen. A window over
        // the screen being recorded is the thing that went wrong before; the icon is
        // always visible, cannot cover anything, and cannot be left behind.
        // Asked whether the recording is still on rather than told: if anything goes
        // wrong between here and the end, the strip sees _recorder go null and closes.
        var indicator = new RecordingIndicator(region,
            () => ReferenceEquals(_recorder, recorder),
            () => recorder.Elapsed);

        indicator.StopRequested += () => _ = FinishRecordingAsync(keep: true);
        indicator.CancelRequested += () => _ = FinishRecordingAsync(keep: false);

        _indicator = indicator;
        indicator.Show();

        // A ring around what is in shot. Nothing else on screen says which part of it is
        // being recorded — once the count-in is over a region recording looks exactly like
        // no recording at all. It is hidden from capture and passes clicks through, so it
        // costs nothing to leave up, and it closes itself with the recording.
        var frame = new RecordingFrame(region, () => ReferenceEquals(_recorder, recorder));

        _frame = frame;
        frame.Show();

        for (int left = settings.RecordCountdownSeconds; left > 0; left--)
        {
            indicator.ShowCountIn(left);
            ShowCountInInTray(left);
            await Task.Delay(TimeSpan.FromSeconds(1));

            // Esc during the count-in calls the whole thing off, which is the last moment
            // it can be called off without a file existing.
            if ((GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0)
            {
                _recorder = null;
                ClearRecordingFromTray();
                recorder.Dispose();
                Discard(path);
                return;
            }
        }

        if (!await recorder.StartAsync())
        {
            _recorder = null;

            // The count-in already put a number on the icon, and nothing else takes it
            // off on this path — a recording that never started would otherwise sit in
            // the tray wearing a red "1".
            ClearRecordingFromTray();

            // Disposed first: the file is created before the encoder is asked whether it
            // can transcode at all, and it is still open when the answer is no.
            recorder.Dispose();
            Discard(path);

            Notify(Localisation.Get("RecordTrayFailed"));
            return;
        }

        indicator.ShowRecording();

        WatchForRecordingKeys();
        ShowRecordingInTray(recorder);
    }

    /// <summary>
    /// Esc keeps the recording, Shift+Esc throws it away.
    ///
    /// Polled rather than handled, because nothing on screen has focus to handle it —
    /// and nothing on screen is the point: a bar hovering over the recording ends up in
    /// the recording. The tray menu offers the same two ways out for anyone who would
    /// rather click than remember a key.
    /// </summary>
    private void WatchForRecordingKeys()
    {
        _recordingKeys?.Stop();
        _recordingKeys = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(60),
        };

        // Primed, so the Esc that cancelled something a moment ago is not read as the
        // one ending this recording.
        GetAsyncKeyState(VK_ESCAPE);

        _recordingKeys.Tick += (_, _) =>
        {
            if (_recorder is null)
            {
                StopWatchingForRecordingKeys();
                ClearRecordingFromTray();

                // Belt as well as braces: the strip already closes itself the moment
                // _recorder stops being the one it was given, so this only makes it go a
                // quarter of a second sooner.
                _indicator?.Close();
                _indicator = null;

                _frame?.Close();
                _frame = null;
                return;
            }

            if ((GetAsyncKeyState(VK_ESCAPE) & 0x0001) == 0) return;

            // Shift is read on the same tick rather than trusted from an earlier one, and
            // cancel is asked first so one press can only ever do one of the two.
            bool discard = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
            _ = FinishRecordingAsync(keep: !discard);
        };

        _recordingKeys.Start();
    }

    private void StopWatchingForRecordingKeys()
    {
        _recordingKeys?.Stop();
        _recordingKeys = null;
    }

    /// <summary>
    /// Puts the recording where it can always be seen and always be stopped, without
    /// putting anything over the screen being recorded: a red dot on the tray icon, the
    /// running time in its tooltip, and Stop and Cancel in its menu.
    /// </summary>
    private void ShowRecordingInTray(ScreenRecorder recorder)
    {
        if (_tray is null) return;

        SetTrayBadge(null);

        if (_stopRecordingItem is not null) _stopRecordingItem.Visibility = Visibility.Visible;
        if (_cancelRecordingItem is not null) _cancelRecordingItem.Visibility = Visibility.Visible;

        _recordingTick?.Stop();
        _recordingTick = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1),
        };

        void Tick()
        {
            var elapsed = recorder.Elapsed;
            _tray.ToolTipText = Localisation.Format("RecordTrayTip",
                $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}");
        }

        _recordingTick.Tick += (_, _) => Tick();
        _recordingTick.Start();
        Tick();
    }

    private void ClearRecordingFromTray()
    {
        _recordingTick?.Stop();
        _recordingTick = null;

        if (_stopRecordingItem is not null) _stopRecordingItem.Visibility = Visibility.Collapsed;
        if (_cancelRecordingItem is not null) _cancelRecordingItem.Visibility = Visibility.Collapsed;

        if (_tray is null) return;

        _tray.ToolTipText = "Shotwin";

        // Back to the icon that shipped with the app — drawn through the same Win32 path
        // the badge uses, not by clearing Icon. Nulling it only unhooks the handle: the
        // IconSource set at startup is not re-read, so the tray was left with a blank
        // space where the app icon should be. Drawing the plain icon puts a real one back.
        DrawTrayIcon(null, badge: false);
    }

    /// <summary>
    /// The seconds left, on the tray icon and in its tooltip, so the count-in is visible
    /// without putting anything over the screen that is about to be recorded.
    /// </summary>
    private void ShowCountInInTray(int secondsLeft)
    {
        if (_tray is null) return;

        SetTrayBadge(secondsLeft.ToString());
        _tray.ToolTipText = Localisation.Format("RecordTrayCountingIn", secondsLeft);
    }

    /// <summary>
    /// Draws the app icon with a red badge on it — a plain dot while recording, a number
    /// during the count-in — and hands the tray a real Win32 icon.
    ///
    /// Not through IconSource: that path only accepts an image that came from a URI, and
    /// reads UriSource off it to reload the bytes. A drawn one has no URI, so it either
    /// threw outright or dereferenced null, which is what put a crash notification on
    /// screen every second of the count-in.
    /// </summary>
    private void SetTrayBadge(string? number) => DrawTrayIcon(number, badge: true);

    /// <summary>
    /// The one place the tray icon is drawn, badged or plain, so the icon it goes back to
    /// when a recording ends is the same kind of object as the one it wore during it.
    /// </summary>
    private void DrawTrayIcon(string? number, bool badge)
    {
        if (_tray is null) return;

        // Nothing to draw the badge onto, so the badge would be all there was and the
        // plain icon would be nothing at all. The startup IconSource is still the right
        // answer in that case.
        if (_idleIcon is null)
        {
            if (!badge) _tray.Icon = null;
            return;
        }

        const int Size = 32;

        var visual = new System.Windows.Media.DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawImage(_idleIcon, new System.Windows.Rect(0, 0, Size, Size));

            if (badge)
            {
                // A number has to stay legible at 16px in the tray, so it fills the icon
                // rather than sitting in a corner the way the plain dot does.
                var centre = number is null
                    ? new System.Windows.Point(Size - 9, Size - 9)
                    : new System.Windows.Point(Size / 2.0, Size / 2.0);

                double radius = number is null ? 9 : 15;

                drawing.DrawEllipse(new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x14, 0x14, 0x17)), null, centre, radius, radius);
                drawing.DrawEllipse(new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xFF, 0x3B, 0x30)), null, centre, radius - 2.5, radius - 2.5);

                if (number is not null)
                {
                    var text = new System.Windows.Media.FormattedText(
                        number,
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new System.Windows.Media.Typeface(
                            new System.Windows.Media.FontFamily("Segoe UI"),
                            System.Windows.FontStyles.Normal,
                            System.Windows.FontWeights.Bold,
                            System.Windows.FontStretches.Normal),
                        21,
                        System.Windows.Media.Brushes.White,
                        96);

                    drawing.DrawText(text,
                        new System.Windows.Point(centre.X - text.Width / 2, centre.Y - text.Height / 2));
                }
            }
        }

        var target = new System.Windows.Media.Imaging.RenderTargetBitmap(
            Size, Size, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
        target.Render(visual);

        // Straight to a Win32 icon through the raw pixels. The handle belongs to this
        // process until DestroyIcon, so the one being replaced goes first — thirty of
        // these a recording would otherwise be thirty leaked handles.
        var pixels = new byte[Size * Size * 4];
        target.CopyPixels(pixels, Size * 4, 0);

        var bitmap = new System.Drawing.Bitmap(Size, Size,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        var locked = bitmap.LockBits(
            new System.Drawing.Rectangle(0, 0, Size, Size),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, locked.Scan0, pixels.Length);
        bitmap.UnlockBits(locked);

        IntPtr handle = bitmap.GetHicon();
        bitmap.Dispose();

        var previous = _trayIconHandle;
        _tray.Icon = System.Drawing.Icon.FromHandle(handle);
        _trayIconHandle = handle;

        if (previous != IntPtr.Zero) DestroyIcon(previous);
    }

    /// <summary>The Win32 icon the tray is wearing, so it can be destroyed when replaced.</summary>
    private IntPtr _trayIconHandle;

    private async Task FinishRecordingAsync(bool keep)
    {
        if (_recorder is not { } recorder) return;
        _recorder = null;

        StopWatchingForRecordingKeys();

        // Cleared here rather than left to the watcher, which is the timer the line above
        // just stopped: the tick that noticed _recorder had gone null never runs again, so
        // every ordinary stop and cancel used to leave the red dot on the tray for the
        // rest of the session. Cleared before the await, too — StopAsync has to flush the
        // encoder, and the icon should stop claiming to be recording the moment it isn't.
        ClearRecordingFromTray();

        string? finished = await recorder.StopAsync();
        recorder.Dispose();

        if (!keep || finished is null)
        {
            // StopAsync reports an unusable recording by returning null, but it leaves
            // the file where it is. A zero-byte mp4 sitting among the shots is worse
            // than no file at all, so it goes here.
            Discard(recorder.Path);

            if (keep) Notify(Localisation.Get("RecordTrayFailed"));
            return;
        }

        if (SettingsService.Current.ShowCapturePreview)
            await ShowRecordingDoneAsync(finished, recorder.Elapsed);
        else
            Notify(Localisation.Format("RecordTraySaved", Path.GetFileName(finished)), finished);
    }

    /// <summary>
    /// The card that stands in for a screenshot's preview thumbnail: a frame from the
    /// recording, its length, and the things worth doing with it. Shown only once the
    /// file is closed and playable, so Edit and Copy cannot reach a half-written file.
    /// </summary>
    private async Task ShowRecordingDoneAsync(string path, TimeSpan duration)
    {
        System.Windows.Media.ImageSource? thumbnail = null;

        try
        {
            // Windows keeps a frame for any video it can decode, which is far cheaper
            // than opening the file and seeking one out ourselves.
            var file = await global::Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var source = await file.GetThumbnailAsync(
                global::Windows.Storage.FileProperties.ThumbnailMode.VideosView, 540);

            if (source is not null && source.Type != global::Windows.Storage.FileProperties.ThumbnailType.Icon)
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.StreamSource = source.AsStreamForRead();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();
                thumbnail = image;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException
                                      or NotSupportedException)
        {
            // A card with a dark tile where the frame would be is still worth showing.
        }

        var popover = new RecordingDonePopover(path, thumbnail, duration);
        popover.EditRequested += OpenFileInEditor;
        popover.Show();
    }

    /// <summary>
    /// Takes a recording back off disk. <paramref name="onlyIfEmpty"/> is for the way
    /// out at shutdown, where a file the encoder did write frames into is worth keeping
    /// even though nothing closed it properly.
    /// </summary>
    private static void Discard(string path, bool onlyIfEmpty = false)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return;
            if (onlyIfEmpty && file.Length > 0) return;

            file.Delete();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The encoder has not let go of it yet. One stray file is the worst of it.
        }
    }

    /// <summary>
    /// The monitor the pointer is on, or null when Windows will not say where it is.
    /// The whole-monitor grab is the one path that skips the overlay, so it is the one
    /// that has to settle its own monitor.
    /// </summary>
    private static MonitorInfo? CursorMonitor() =>
        GetCursorPos(out POINT cursor) ? Monitors.FromPoint(cursor.X, cursor.Y) : null;

    /// <summary>Whole-monitor grab with no overlay: the one case where nothing needs picking.</summary>
    private void CaptureCurrentMonitor()
    {
        if (CursorMonitor() is not { } monitor) return;

        var frozen = _capture.CaptureVirtualDesktop(out int originX, out int originY);
        try
        {
            var rect = new SKRectI(
                monitor.Left - originX, monitor.Top - originY,
                monitor.Bounds.Right - originX, monitor.Bounds.Bottom - originY);
            rect.Intersect(new SKRectI(0, 0, frozen.Width, frozen.Height));
            if (rect.Width < 1 || rect.Height < 1) return;

            using var subset = new SKBitmap();
            frozen.ExtractSubset(subset, rect);
            OnCaptured(subset.Copy(), new SKRectI(
                rect.Left + originX, rect.Top + originY,
                rect.Right + originX, rect.Bottom + originY));
        }
        finally
        {
            frozen.Dispose();
        }
    }

    private void OnCaptured(SKBitmap crop, SKRectI absoluteRect)
    {
        if (_captureTextInstead)
        {
            _captureTextInstead = false;
            _ = RecognizeAndCopyAsync(crop);
            return;
        }

        var settings = SettingsService.Current;

        // Asked once, and only now: the question means something when there is a shot
        // waiting for an answer. The bitmap is held until it is settled, so nothing is
        // lost while the dialog is open.
        if (!settings.SaveFolderChosen) AskWhereToSave();

        var document = new ShotDocument(crop);

        if (settings.CopyToClipboardOnCapture)
        {
            using var flat = document.Flatten();
            ImageIO.CopyToClipboard(flat);
        }

        if (settings.OpenEditorAfterCapture)
        {
            OpenEditor(() => new EditorWindow(document));
            return;
        }

        string? savedPath = null;

        if (settings.AutoSaveEveryCapture)
        {
            using var image = document.Flatten();
            var result = ImageIO.SaveToFolder(image);
            savedPath = result.Path;

            if (!result.Ok)
                Notify(Localisation.Format("TraySaveFailed", result.Problem));
        }

        if (settings.ShowCapturePreview)
            ShowPreview(document.Base.Copy(), savedPath);

        document.Dispose();
    }

    private void AskWhereToSave()
    {
        var settings = SettingsService.Current;
        settings.SaveFolderChosen = true;

        try
        {
            var dialog = new FirstRunFolderWindow();
            if (dialog.ShowDialog() == true)
            {
                settings.SaveFolder = dialog.ChosenFolder;
                settings.SaveFolderMovedFrom = null;
            }
        }
        catch (InvalidOperationException)
        {
            // Shutting down mid-capture; the fallback folder still applies.
        }

        SettingsService.Save();
    }

    /// <summary>
    /// The picked colour goes straight to the clipboard in the configured notation.
    /// Reading a colour is only ever a step towards pasting it somewhere.
    /// </summary>
    private void OnColourPicked(SKColor colour)
    {
        string text = OverlayWindow.ColourText(colour, SettingsService.Current.ColourFormat);
        ImageIO.CopyTextToClipboard(text);
        Notify(Localisation.Format("TrayColourCopied", text));
    }

    private void ShowPreview(SKBitmap shot, string? savedPath)
    {
        // One at a time; a burst of captures should replace the thumbnail, not stack it.
        _preview?.Dismiss();

        _preview = new PreviewWindow(shot, savedPath);
        _preview.EditRequested += bitmap => OpenEditor(() => new EditorWindow(new ShotDocument(bitmap)));
        _preview.PinRequested += bitmap => new PinWindow(bitmap).Show();
        _preview.Notify += message => Notify(message);
        _preview.Closed += (_, _) => _preview = null;
        _preview.Show();
    }

    /// <summary>
    /// Text capture: nothing is saved and no editor opens, the recognised text just
    /// lands on the clipboard. Grabbing an error message out of a dialog should be one
    /// gesture, not a screenshot you then have to retype.
    /// </summary>
    private async Task RecognizeAndCopyAsync(SKBitmap crop)
    {
        try
        {
            var result = await TextRecognition.RecognizeAsync(crop);

            if (!result.Ok)
            {
                Notify(result.Problem!);
                return;
            }

            if (!result.HasText)
            {
                Notify(Localisation.Get("TrayNoText"));
                return;
            }

            ImageIO.CopyTextToClipboard(result.Text);
            Notify(Localisation.Plural("CaptureTextCopiedLines_One", "CaptureTextCopiedLines_Many",
                result.LineCount, result.LineCount));
        }
        finally
        {
            crop.Dispose();
        }
    }

    // ---- Editing ----------------------------------------------------------------

    /// <summary>
    /// The editor window, while one is open. One at a time, the way the preview thumbnail
    /// is one at a time: editors are full windows, and a morning of captures left a stack
    /// of them behind with no way to tell which held what.
    /// </summary>
    private Window? _editor;

    /// <summary>
    /// Opens an editor, or brings the open one forward. The window is built only when
    /// there is room for it, so nothing is decoded for an editor that will not appear.
    /// </summary>
    private void OpenEditor(Func<Window?> build)
    {
        if (_editor is { } open)
        {
            if (open.WindowState == WindowState.Minimized) open.WindowState = WindowState.Normal;

            open.Activate();
            return;
        }

        if (build() is not { } window) return;

        _editor = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_editor, window)) _editor = null; };
        window.Show();
    }

    // ---- Reopening saved shots --------------------------------------------------

    private void OpenFileInEditor(string path)
    {
        // A recording has its own editor. Sent to the image one it would only come back
        // as an unreadable file, which is what the gallery did with recordings before.
        if (Path.GetExtension(path).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            OpenEditor(() => new VideoEditorWindow(path));
            return;
        }

        OpenEditor(() =>
        {
            var bitmap = SKBitmap.Decode(path);
            if (bitmap is not null) return new EditorWindow(new ShotDocument(bitmap));

            Notify(Localisation.Get("TrayFileUnreadable"));
            return null;
        });
    }

    private void PinFile(string path)
    {
        var bitmap = SKBitmap.Decode(path);
        if (bitmap is null)
        {
            Notify(Localisation.Get("TrayFileUnreadable"));
            return;
        }

        new PinWindow(bitmap).Show();
    }

    // ---- Tray actions -----------------------------------------------------------

    /// <summary>
    /// Hands the single-instance lock back before an update relaunches the app.
    /// The replacement starts while this process is still shutting down, so it would
    /// otherwise find the mutex held, decide it is a second copy, and exit — leaving
    /// the user with no running Shotwin and no sign of why.
    /// </summary>
    public void ReleaseSingleInstanceLock()
    {
        try
        {
            _instanceMutex?.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not the owning thread, which means it is not ours to hand back anyway.
        }

        _instanceMutex?.Dispose();
        _instanceMutex = null;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Quitting mid-recording. There is no time left to wait for the encoder to
        // flush, so the file is closed where it stands: a partly written mp4 at least
        // has frames in it, while one the encoder never reached is a zero-byte file
        // sitting among the shots with no way to tell what it was.
        if (_recorder is { } recorder)
        {
            _recorder = null;
            recorder.Dispose();
            Discard(recorder.Path, onlyIfEmpty: true);
        }

        SettingsService.Save();
        _commands?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();

        // The drawn icon outlives the tray it was handed to, so it goes back after it.
        if (_trayIconHandle != IntPtr.Zero)
        {
            DestroyIcon(_trayIconHandle);
            _trayIconHandle = IntPtr.Zero;
        }

        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
