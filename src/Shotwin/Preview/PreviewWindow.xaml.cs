using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Shotwin.Services;
using SkiaSharp;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Preview;

/// <summary>
/// The floating thumbnail that appears after a capture and waits in the corner of the
/// screen.
///
/// It is a staging area, not a notification: click it to edit, drag it into any app to
/// drop the PNG, or use the actions along its bottom edge. It has no countdown — it
/// waits until the shot has been used, which means the clipboard moving on, you going
/// back to another window, or dismissing it yourself.
/// </summary>
public partial class PreviewWindow : Window
{
    /// <summary>Foreground changes are ignored for this long, or the capture itself dismisses it.</summary>
    private static readonly TimeSpan SettleDelay = TimeSpan.FromMilliseconds(1200);

    /// <summary>How long clicks anywhere are ignored, so the capture's own click cannot dismiss it.</summary>
    private static readonly TimeSpan ClickGrace = TimeSpan.FromMilliseconds(400);

    private readonly SKBitmap _shot;

    /// <summary>Where auto-save put this shot, or null when it exists only on the clipboard.</summary>
    private string? _savedPath;
    /// <summary>Watches for the shot being used; there is no expiry timer.</summary>
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(60) };

    private readonly DateTime _shownAt = DateTime.UtcNow;

    private uint _clipboardBaseline;
    private IntPtr _foregroundBaseline;
    private string? _droppedFile;
    private bool _closing;
    private Point _dragOrigin;

    public event Action<SKBitmap>? EditRequested;
    public event Action<SKBitmap>? PinRequested;

    /// <summary>Something worth telling the user, shown as a tray notification.</summary>
    public event Action<string>? Notify;

    /// <param name="savedPath">
    /// The file auto-save already wrote, or null when the capture exists only on the
    /// clipboard and in this window, and is discarded unless Save is pressed.
    /// </param>
    public PreviewWindow(SKBitmap shot, string? savedPath)
    {
        InitializeComponent();

        FlowDirection = Localisation.FlowDirection;

        _shot = shot;
        _savedPath = savedPath;

        // Nothing on disk means nothing to show, so the action is not offered. It used
        // to be, and reaching for it silently saved the shot first, which is the one
        // thing turning auto-save off is meant to prevent.
        FolderButton.Visibility = savedPath is null ? Visibility.Collapsed : Visibility.Visible;

        ThumbBrush.ImageSource = ImageIO.ToBitmapSource(shot);

        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        EditButton.Click += (_, _) => OpenEditor();
        CopyButton.Click += (_, _) => { CopyImage(); Dismiss(); };
        OcrButton.Click += (_, _) => _ = CopyTextAsync();
        SaveButton.Click += (_, _) => Save();
        PinButton.Click += (_, _) => { PinRequested?.Invoke(_shot.Copy()); Dismiss(); };
        FolderButton.Click += (_, _) => RevealSavedFile();
        DismissButton.Click += (_, _) => Dismiss();

        Loaded += OnLoaded;
        _watch.Tick += OnWatch;
    }

    // ---- Placement --------------------------------------------------------------

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PlaceInCorner();

        _clipboardBaseline = GetClipboardSequenceNumber();
        _foregroundBaseline = GetForegroundWindow();

        // Prime the "pressed since last call" bits so the click that finished the
        // capture is not read as a click outside the thumbnail.
        GetAsyncKeyState(VK_LBUTTON);
        GetAsyncKeyState(VK_RBUTTON);

        _watch.Start();

        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
    }

    private void PlaceInCorner()
    {
        string corner = SettingsService.Current.PreviewCorner;

        if (corner.Equals("Cursor", StringComparison.OrdinalIgnoreCase))
        {
            PlaceAtCursor();
            return;
        }

        var area = SystemParameters.WorkArea;
        const double Gap = 4;   // the Border already carries a 16px shadow margin

        bool right = corner.Contains("Right", StringComparison.OrdinalIgnoreCase);
        bool bottom = corner.Contains("Bottom", StringComparison.OrdinalIgnoreCase);

        Left = right ? area.Right - ActualWidth - Gap : area.Left + Gap;
        Top = bottom ? area.Bottom - ActualHeight - Gap : area.Top + Gap;
    }

    /// <summary>
    /// Drops the thumbnail where the drag finished, so it appears under the hand that
    /// just made it rather than somewhere the eye has to go looking for.
    /// </summary>
    private void PlaceAtCursor()
    {
        if (!GetCursorPos(out POINT cursor))
        {
            PlaceFallbackCorner();
            return;
        }

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        // The shadow margin already offsets the visible card, so this is a small nudge.
        double left = cursor.X / scale - ActualWidth / 2;

        // Clamp to the monitor the cursor is on, not the primary one.
        var monitor = Capture.Monitors.FromPoint(cursor.X, cursor.Y);
        double minX = monitor.Left / scale, minY = monitor.Top / scale;
        double maxX = monitor.Bounds.Right / scale - ActualWidth;
        double maxY = monitor.Bounds.Bottom / scale - ActualHeight;

        // Above the cursor by default: that is where the selection you just drew is, so
        // the thumbnail rises out of it instead of covering whatever is below. Only when
        // there is no room up there does it drop underneath.
        double above = cursor.Y / scale - ActualHeight - 8;
        double below = cursor.Y / scale + 8;
        double top = above >= minY ? above : below;

        Left = Math.Clamp(left, minX, Math.Max(minX, maxX));
        Top = Math.Clamp(top, minY, Math.Max(minY, maxY));
    }

    private void PlaceFallbackCorner()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth - 4;
        Top = area.Bottom - ActualHeight - 4;
    }

    // ---- Lifetime ---------------------------------------------------------------

    private void OnWatch(object? sender, EventArgs e)
    {
        if (_closing) return;

        // The clipboard moving on means the shot has been used, or replaced. Either
        // way there is nothing left to stage.
        if (GetClipboardSequenceNumber() != _clipboardBaseline)
        {
            Dismiss();
            return;
        }

        TimeSpan age = DateTime.UtcNow - _shownAt;

        // Switching apps is the clearest "done with it" signal, but the overlay closing
        // hands focus around on its own for a moment after a capture, so that shuffle
        // has to settle before a foreground change means anything.
        if (age > SettleDelay)
        {
            IntPtr foreground = GetForegroundWindow();
            if (foreground != _foregroundBaseline && foreground != Handle)
            {
                Dismiss();
                return;
            }
        }

        // A click needs far less grace: the button release that ended the capture is
        // already accounted for by priming the latch, so this only has to outlast a
        // double-click's second press.
        if (age <= ClickGrace) return;

        // The preview never takes focus, so it never receives a blur event, and carrying
        // on inside the app that was already in front changes no foreground window
        // either. Watching for a click outside our own rectangle is what actually
        // catches "the user went back to what they were doing".
        if (ClickedSinceLastTick() && !IsCursorOverWindow()) Dismiss();
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// True if a button went down at any point since the previous tick. Testing the
    /// held-down bit instead misses most clicks outright: a click lasts about a tenth
    /// of a second and the poll would have to land inside that window. The low bit of
    /// GetAsyncKeyState is latched on press and cleared by the read, so it survives
    /// between ticks — which is the whole reason it exists.
    /// </summary>
    private static bool ClickedSinceLastTick() =>
        (GetAsyncKeyState(VK_LBUTTON) & 0x0001) != 0 ||
        (GetAsyncKeyState(VK_RBUTTON) & 0x0001) != 0;

    private bool IsCursorOverWindow()
    {
        if (!GetCursorPos(out POINT cursor)) return false;
        if (!GetWindowRect(Handle, out var rect)) return false;

        // The window is larger than the card it draws: the outer Border carries a 16px
        // margin for its shadow, and clicks land on the desktop there, so that ring has
        // to count as outside or the card ignores clicks along its own edge.
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;
        int inset = (int)Math.Round(16 * scale);

        return cursor.X >= rect.Left + inset && cursor.X < rect.Right - inset
            && cursor.Y >= rect.Top + inset && cursor.Y < rect.Bottom - inset;
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        _watch.Stop();

        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    // ---- Interaction ------------------------------------------------------------

    private void OnMouseDown(object sender, MouseButtonEventArgs e) =>
        _dragOrigin = e.GetPosition(this);

    /// <summary>
    /// Dragging off the thumbnail hands the PNG to whatever is underneath, so a shot can
    /// go straight into a chat window or a folder without ever being saved by hand.
    /// </summary>
    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _closing) return;
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;

        var now = e.GetPosition(this);
        if (Math.Abs(now.X - _dragOrigin.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(now.Y - _dragOrigin.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        string? file = EnsureFileOnDisk();
        if (file is null) return;

        _watch.Stop();
        var data = new DataObject(DataFormats.FileDrop, new[] { file });
        data.SetData(DataFormats.Bitmap, ThumbBrush.ImageSource);

        DragDrop.DoDragDrop(this, data, DragDropEffects.Copy);
        Dismiss();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        // The action bar has its own verbs; a click there must not also open the editor.
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;

        var now = e.GetPosition(this);
        bool moved = Math.Abs(now.X - _dragOrigin.X) >= SystemParameters.MinimumHorizontalDragDistance
                  || Math.Abs(now.Y - _dragOrigin.Y) >= SystemParameters.MinimumVerticalDragDistance;

        if (!moved) OpenEditor();
    }

    private static bool IsInsideButton(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is System.Windows.Controls.Primitives.ButtonBase) return true;
        return false;
    }

    private void OpenEditor()
    {
        EditRequested?.Invoke(_shot.Copy());
        Dismiss();
    }

    /// <summary>
    /// Reads the text out of the shot and copies that instead of the image. The same
    /// engine the Grab text shortcut uses, offered here because whether you wanted the
    /// picture or the words in it is often only obvious once you see the thumbnail.
    /// </summary>
    private async Task CopyTextAsync()
    {
        _watch.Stop();
        OcrButton.IsEnabled = false;

        try
        {
            var result = await TextRecognition.RecognizeAsync(_shot);

            if (!result.Ok) Notify?.Invoke(result.Problem!);
            else if (!result.HasText) Notify?.Invoke(Localisation.Get("PreviewNoText"));
            else
            {
                ImageIO.CopyTextToClipboard(result.Text);
                Notify?.Invoke(Localisation.Plural("CaptureTextCopiedLines_One",
                    "CaptureTextCopiedLines_Many", result.LineCount, result.LineCount));
            }
        }
        finally
        {
            OcrButton.IsEnabled = true;
        }

        Dismiss();
    }

    private void CopyImage()
    {
        using var image = SKImage.FromBitmap(_shot);
        ImageIO.CopyToClipboard(image);
    }

    /// <summary>
    /// Keeps the shot. With auto-save off this is the only thing that writes it to disk,
    /// so it goes straight to the save folder with no dialog in the way. When the shot is
    /// already filed, the button means Save As instead.
    /// </summary>
    private void Save()
    {
        _watch.Stop();

        if (_savedPath is null)
        {
            using var image = SKImage.FromBitmap(_shot);
            var result = ImageIO.SaveToFolder(image);
            _savedPath = result.Path;
            Dismiss();
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = Localisation.Get("EditorSaveFilter"),
            FileName = $"Shot {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png",
            InitialDirectory = SettingsService.Current.SaveFolder,
        };

        if (dialog.ShowDialog() != true) return;

        using var copy = SKImage.FromBitmap(_shot);
        ImageIO.SaveAs(copy, dialog.FileName);
        Dismiss();
    }

    /// <summary>Opens the save folder, highlighting the file this capture wrote.</summary>
    /// <summary>
    /// Opens Explorer on the file this shot was saved as. Only reachable when there is
    /// one: it never writes anything, so the button cannot save behind your back.
    /// </summary>
    private void RevealSavedFile()
    {
        if (_savedPath is null) return;

        try
        {
            // Selecting the exact path, not the newest PNG in the folder: a burst of
            // captures would otherwise highlight whichever one landed last.
            System.Diagnostics.Process.Start(File.Exists(_savedPath)
                ? new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{_savedPath}\"")
                : new System.Diagnostics.ProcessStartInfo(SettingsService.Current.SaveFolder) { UseShellExecute = true });

            Dismiss();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Writes the PNG to temp the first time a drag needs a real file.</summary>
    private string? EnsureFileOnDisk()
    {
        if (_droppedFile is not null && File.Exists(_droppedFile)) return _droppedFile;

        try
        {
            string folder = Path.Combine(Path.GetTempPath(), "Shotwin");
            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, $"Shot {DateTime.Now:yyyy-MM-dd HH-mm-ss}.png");

            using var image = SKImage.FromBitmap(_shot);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            using var stream = File.Create(path);
            data.SaveTo(stream);

            _droppedFile = path;
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _watch.Stop();
        _shot.Dispose();
    }
}
