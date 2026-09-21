using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Shotwin.Services;
using SkiaSharp;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace Shotwin.Shell;

/// <summary>One saved shot or recording on disk, as shown in the gallery.</summary>
public sealed class ShotEntry : INotifyPropertyChanged
{
    private bool _isSelected;
    private BitmapSource? _thumbnail;
    private string _detail = string.Empty;

    public required string Path { get; init; }
    public required string Name { get; init; }

    /// <summary>
    /// Decides the play badge, and which of Edit, Copy and Pin still make sense. Read
    /// from the extension at scan time, so nothing has to open the file to ask.
    /// </summary>
    public required bool IsVideo { get; init; }

    /// <summary>
    /// The second caption line. Settable because a recording's length only arrives once
    /// Windows has been asked for it, which is long after the card went up.
    /// </summary>
    public required string Detail
    {
        get => _detail;
        set
        {
            if (_detail == value) return;
            _detail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Detail)));
        }
    }

    /// <summary>Filled in once the decode finishes, which is why this is not init-only.</summary>
    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (ReferenceEquals(_thumbnail, value)) return;
            _thumbnail = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// The shots already on disk, so a capture you took yesterday is one click from being
/// copied, pinned or re-edited. Without it the save folder is where shots go to be
/// forgotten.
/// </summary>
public partial class RecentView : UserControl
{
    private const int MaxShots = 120;

    /// <summary>
    /// What a card needs beyond its file listing. The duration rides along with the
    /// thumbnail because both come out of the same look at the file, and both have to
    /// survive a cached card being shown again without a second one.
    /// </summary>
    private readonly record struct Preview(BitmapSource? Image, TimeSpan Duration);

    /// <summary>
    /// Decoded thumbnails, keyed by path and write time so an edited shot is re-read.
    /// Switching to this tab used to decode every visible shot again from scratch.
    /// </summary>
    private static readonly Dictionary<(string Path, long Ticks), Preview> Cache = [];

    /// <summary>Bounded because these are live bitmaps, not bytes on disk.</summary>
    private const int MaxCached = 240;

    /// <summary>Cancels the decodes still queued when the folder is rescanned.</summary>
    private CancellationTokenSource? _loading;

    private readonly ObservableCollection<ShotEntry> _shots = [];

    /// <summary>Where a Shift-click measures its range from.</summary>
    private ShotEntry? _anchor;

    public event Action<string>? EditRequested;
    public event Action<string>? PinRequested;

    /// <summary>Columns in the gallery, recomputed from the width so cards stay readable.</summary>
    public static readonly DependencyProperty ColumnsProperty =
        DependencyProperty.Register(nameof(Columns), typeof(int), typeof(RecentView),
            new PropertyMetadata(3));

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public static readonly DependencyProperty CardHeightProperty =
        DependencyProperty.Register(nameof(CardHeight), typeof(double), typeof(RecentView),
            new PropertyMetadata(170.0));

    public double CardHeight
    {
        get => (double)GetValue(CardHeightProperty);
        set => SetValue(CardHeightProperty, value);
    }

    public RecentView()
    {
        InitializeComponent();

        ShotList.ItemsSource = _shots;

        OpenButton.Click += (_, _) => ForEachSelected(p => EditRequested?.Invoke(p));
        PinButton.Click += (_, _) => ForEachSelected(p => PinRequested?.Invoke(p));
        CopyButton.Click += (_, _) => CopySelection();
        RevealButton.Click += (_, _) => { if (SelectedPaths.FirstOrDefault() is { } p) Reveal(p); };
        DeleteButton.Click += (_, _) => DeleteSelection();
        RevealFolderButton.Click += (_, _) => OpenFolder();

        Scroller.SizeChanged += (_, _) => UpdateLayoutMetrics();

        UpdateActionState();
    }

    /// <summary>
    /// Aims for cards around 210px wide. Fewer, larger cards beat a dense grid here:
    /// the whole point of the thumbnail is recognising which shot it is.
    /// </summary>
    private void UpdateLayoutMetrics()
    {
        const double TargetCardWidth = 210;

        double available = Scroller.ActualWidth - 18;
        if (available < 100) return;

        int columns = Math.Max(1, (int)Math.Round(available / TargetCardWidth));
        Columns = columns;

        // Keep a roughly 16:10 image plus the two caption lines.
        double cardWidth = available / columns - 10;
        CardHeight = Math.Clamp(cardWidth * 0.62 + 48, 120, 260);
    }

    // ---- Loading ----------------------------------------------------------------

    /// <summary>Rescans the save folders. Called every time the view is shown.</summary>
    public void Refresh()
    {
        string? previous = Selected?.Path;

        // Whatever is still queued describes the listing about to be replaced.
        _loading?.Cancel();
        _loading?.Dispose();
        _loading = new CancellationTokenSource();

        _shots.Clear();

        var files = new List<FileInfo>();
        foreach (string folder in Folders())
        {
            try
            {
                if (!Directory.Exists(folder)) continue;

                var directory = new DirectoryInfo(folder);
                files.AddRange(directory.EnumerateFiles("*.png"));
                files.AddRange(directory.EnumerateFiles("*.jpg"));

                // Recordings land in the same folder under the same name template, and
                // leaving them out was how a recording became invisible the moment it
                // finished.
                files.AddRange(directory.EnumerateFiles("*.mp4"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // The cards go up straight away with whatever is already decoded, and the rest
        // arrive as they are read. Decoding a hundred and twenty PNGs inline froze the
        // window for as long as it took, every single time the tab was opened.
        var pending = new List<(ShotEntry Entry, (string, long) Key)>();

        foreach (var file in files.OrderByDescending(f => f.LastWriteTimeUtc).Take(MaxShots))
        {
            var key = (file.FullName, file.LastWriteTimeUtc.Ticks);

            var entry = new ShotEntry
            {
                Path = file.FullName,
                Name = System.IO.Path.GetFileNameWithoutExtension(file.Name),
                IsVideo = file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase),
                Detail = Localisation.Format("RecentCardDetail",
                    file.LastWriteTime.ToString("d MMM, HH:mm", CultureInfo.CurrentCulture),
                    FormatSize(file.Length)),
            };

            if (Cache.TryGetValue(key, out var cached)) Apply(entry, cached);
            else pending.Add((entry, key));

            _shots.Add(entry);
        }

        if (previous is not null)
        {
            var again = _shots.FirstOrDefault(s =>
                string.Equals(s.Path, previous, StringComparison.OrdinalIgnoreCase));
            if (again is not null) again.IsSelected = true;
        }

        FolderLabel.Text = _shots.Count == 0
            ? SettingsService.Current.SaveFolder
            : Localisation.Plural("RecentShotCount_One", "RecentShotCount_Many",
                _shots.Count, _shots.Count);

        EmptyText.Visibility = _shots.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateLayoutMetrics();
        UpdateActionState();

        if (pending.Count > 0) _ = LoadThumbnailsAsync(pending, _loading.Token);
    }

    /// <summary>
    /// Decodes off the UI thread and hands each image over as it lands. Frozen bitmaps
    /// cross threads safely, and awaiting on the UI context means the assignment — and
    /// the binding update it raises — happens back on the right thread.
    /// </summary>
    private static async Task LoadThumbnailsAsync(
        List<(ShotEntry Entry, (string Path, long Ticks) Key)> pending, CancellationToken token)
    {
        foreach (var (entry, key) in pending)
        {
            if (token.IsCancellationRequested) return;

            Preview preview;
            try
            {
                // A recording is asked of Windows, which is asynchronous in itself; an
                // image is decoded on a worker thread as it always was.
                preview = entry.IsVideo
                    ? await LoadVideoPreviewAsync(key.Path)
                    : new Preview(await Task.Run(() => LoadThumbnail(key.Path), token), TimeSpan.Zero);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (token.IsCancellationRequested) return;
            if (preview.Image is null && preview.Duration == TimeSpan.Zero) continue;

            // Cleared rather than trimmed: the cost of a miss is one decode, and tracking
            // ages for a cache this small would cost more than it saves.
            if (Cache.Count >= MaxCached) Cache.Clear();

            Cache[key] = preview;
            Apply(entry, preview);
        }
    }

    /// <summary>
    /// Hands a card what was read for it. The length goes on the end of the detail line
    /// rather than replacing it, so a recording still says when it was taken and how big
    /// it is like everything else in the grid.
    /// </summary>
    private static void Apply(ShotEntry entry, Preview preview)
    {
        entry.Thumbnail = preview.Image;

        if (entry.IsVideo && preview.Duration > TimeSpan.Zero)
            entry.Detail = Localisation.Format("VideoCardDetail",
                entry.Detail, FormatDuration(preview.Duration));
    }

    /// <summary>
    /// A frame out of the recording and how long it runs, both from Windows itself:
    /// VideosView hands back a real frame from the file, which is the difference between
    /// recognising a recording and seeing a row of identical film icons.
    ///
    /// Anything that goes wrong leaves the card on its plain dark tile. A folder holding
    /// one unreadable file must not be able to stop the gallery loading the rest.
    /// </summary>
    private static async Task<Preview> LoadVideoPreviewAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var properties = await file.Properties.GetVideoPropertiesAsync();

            using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.VideosView, 340);
            if (thumbnail is not { Size: > 0 }) return new Preview(null, properties.Duration);

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = thumbnail.AsStreamForRead();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();

            return new Preview(image, properties.Duration);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or NotSupportedException or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            return default;
        }
    }

    /// <summary>The configured folder plus the fallback, since blocked saves land there.</summary>
    private static IEnumerable<string> Folders()
    {
        string configured = SettingsService.Current.SaveFolder;
        yield return configured;

        string fallback = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Shotwin", "Shots");

        if (!string.Equals(System.IO.Path.GetFullPath(fallback),
                System.IO.Path.GetFullPath(configured), StringComparison.OrdinalIgnoreCase))
        {
            yield return fallback;
        }
    }

    /// <summary>
    /// Decodes at thumbnail size and closes the file immediately, so the gallery does
    /// not hold 120 full-resolution screenshots in memory or lock them against Delete.
    /// </summary>
    private static BitmapSource? LoadThumbnail(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(path);
            image.DecodePixelWidth = 340;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UriFormatException)
        {
            return null;
        }
    }

    /// <summary>Minutes and seconds, counted the way the recording bar counts them.</summary>
    private static string FormatDuration(TimeSpan length) =>
        $"{(int)length.TotalMinutes}:{length.Seconds:00}";

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => Localisation.Format("RecentSizeBytes", bytes),
        < 1024 * 1024 => Localisation.Format("RecentSizeKilobytes",
            (bytes / 1024.0).ToString("0", CultureInfo.CurrentCulture)),
        _ => Localisation.Format("RecentSizeMegabytes",
            (bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.CurrentCulture)),
    };

    // ---- Selection --------------------------------------------------------------

    private ShotEntry? Selected => _shots.FirstOrDefault(s => s.IsSelected);

    private List<string> SelectedPaths => _shots.Where(s => s.IsSelected).Select(s => s.Path).ToList();

    private void ForEachSelected(Action<string> action)
    {
        foreach (string path in SelectedPaths) action(path);
    }

    /// <summary>
    /// Selection behaves the way a file list does: a plain click replaces the selection,
    /// Ctrl adds or removes one, Shift takes the range from the last plain click.
    /// Handled here rather than through IsChecked, because the toggle alone cannot tell
    /// those three apart.
    /// </summary>
    private void OnCardMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (((ToggleButton)sender).DataContext is not ShotEntry clicked) return;

        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (shift && _anchor is not null)
        {
            int from = _shots.IndexOf(_anchor);
            int to = _shots.IndexOf(clicked);

            if (from >= 0 && to >= 0)
            {
                if (!ctrl)
                    foreach (var shot in _shots) shot.IsSelected = false;

                for (int i = Math.Min(from, to); i <= Math.Max(from, to); i++)
                    _shots[i].IsSelected = true;
            }
        }
        else if (ctrl)
        {
            clicked.IsSelected = !clicked.IsSelected;
            _anchor = clicked;
        }
        else
        {
            foreach (var shot in _shots)
                shot.IsSelected = ReferenceEquals(shot, clicked);
            _anchor = clicked;
        }

        UpdateActionState();

        // The toggle is driven above, so stop it flipping the state back.
        e.Handled = true;
    }

    /// <summary>Ctrl+A from the shell selects everything on this page.</summary>
    public void SelectAll()
    {
        foreach (var shot in _shots) shot.IsSelected = true;
        UpdateActionState();
    }

    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (((ToggleButton)sender).DataContext is ShotEntry shot)
            EditRequested?.Invoke(shot.Path);
    }

    /// <summary>Deselects whatever is selected. Returns false when nothing was.</summary>
    public bool ClearSelection()
    {
        if (!_shots.Any(s => s.IsSelected)) return false;

        foreach (var shot in _shots) shot.IsSelected = false;
        _anchor = null;
        UpdateActionState();
        return true;
    }

    private void UpdateActionState()
    {
        int count = _shots.Count(s => s.IsSelected);
        bool any = count > 0;

        OpenButton.IsEnabled = any;
        CopyButton.IsEnabled = any;
        RevealButton.IsEnabled = any;
        DeleteButton.IsEnabled = any;

        // Pinning floats a still above the other windows, and a recording is not one.
        // A mixed selection disables it too: pinning the images and quietly dropping the
        // recordings would be a button doing something other than what it says.
        PinButton.IsEnabled = any && !_shots.Any(s => s.IsSelected && s.IsVideo);

        StatusText.Text = count > 1
            ? Localisation.Plural("RecentSelected_One", "RecentSelected_Many", count, count)
            : string.Empty;
    }

    // ---- Actions ----------------------------------------------------------------

    /// <summary>
    /// One shot copies as an image, several copy as files. Pasting six images into one
    /// place is meaningless; pasting six files into a folder or a chat is not.
    ///
    /// Anything with a recording in it copies as files whatever its size: there is no
    /// bitmap of a video to hand over, and the frame that happens to be its thumbnail is
    /// not what Copy was asked for.
    /// </summary>
    private void CopySelection()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;

        if (paths.Count == 1 && !_shots.Any(s => s.IsSelected && s.IsVideo))
        {
            using var bitmap = SKBitmap.Decode(paths[0]);
            if (bitmap is null)
            {
                Flash(Localisation.Get("RecentFileUnreadable"));
                return;
            }

            using var image = SKImage.FromBitmap(bitmap);
            ImageIO.CopyToClipboard(image);
            Flash(Localisation.Get("RecentCopied"));
            return;
        }

        try
        {
            var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
            Clipboard.SetDataObject(data, copy: true);
            Flash(Localisation.Plural("RecentCopiedFiles_One", "RecentCopiedFiles_Many",
                paths.Count, paths.Count));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Flash(Localisation.Get("RecentClipboardBusy"));
        }
    }

    private void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\""));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Flash(Localisation.Get("RecentExplorerFailed"));
        }
    }

    /// <summary>One confirmation for the whole selection, not one per file.</summary>
    private void DeleteSelection()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;

        // The two questions name different things — one file, or a count — so they are
        // separate entries rather than one with a placeholder that changes meaning.
        string question = paths.Count == 1
            ? Localisation.Format("RecentDeleteConfirm_One", System.IO.Path.GetFileName(paths[0]))
            : Localisation.Format("RecentDeleteConfirm_Many", paths.Count);

        var answer = MessageBox.Show(question, "Shotwin",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);

        if (answer != MessageBoxResult.OK) return;

        int failed = 0;
        foreach (string path in paths)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }

        _anchor = null;
        Flash(failed == 0
            ? Localisation.Plural("RecentDeleted_One", "RecentDeleted_Many",
                paths.Count, paths.Count)
            : Localisation.Plural("RecentDeletedPartial_One", "RecentDeletedPartial_Many",
                failed, paths.Count - failed, failed));

        Refresh();
    }

    private void OpenFolder()
    {
        string folder = SettingsService.Current.SaveFolder;
        if (!Directory.Exists(folder))
        {
            folder = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Shotwin", "Shots");
            Directory.CreateDirectory(folder);
        }
        Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
    }

    private void Flash(string message)
    {
        StatusText.Text = message;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (s, _) =>
        {
            ((System.Windows.Threading.DispatcherTimer)s!).Stop();
            StatusText.Text = string.Empty;
        };
        timer.Start();
    }
}
