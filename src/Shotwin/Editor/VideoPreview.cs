using System.IO;
using System.Threading;
using System.Windows.Media.Imaging;
using Windows.Media.Editing;
using Windows.Media.Core;
using Windows.Storage;

namespace Shotwin.Editor;

/// <summary>
/// Pulls single frames out of a video, for the scrubbing preview in the video editor.
///
/// WPF's MediaElement decodes through Windows Media Player's components rather than
/// Media Foundation, and on a machine without them it opens nothing at all: a black
/// picture and a duration of zero, which is exactly what the editor was showing. Every
/// other piece of video in this app already goes through Media Foundation and works, so
/// the preview goes the same way.
///
/// One frame at a time rather than live playback: the editor exists to trim, and Play on
/// the finished-recording card hands the file to whoever Windows opens videos with —
/// which will always be a better player than one built into a screenshot tool.
/// </summary>
public sealed class VideoPreview : IDisposable
{
    private MediaComposition? _composition;
    private string? _path;

    public TimeSpan Duration { get; private set; }
    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>True once the file has been opened and a frame can be asked for.</summary>
    public bool IsReady => _composition is not null;

    public async Task<bool> OpenAsync(string path)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var clip = await MediaClip.CreateFromFileAsync(file);

            var composition = new MediaComposition();
            composition.Clips.Add(clip);

            var properties = await file.Properties.GetVideoPropertiesAsync();

            _path = path;
            Width = (int)properties.Width;
            Height = (int)properties.Height;
            Duration = composition.Duration;
            _composition = composition;

            return Duration > TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException
                                      or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The frame at a given time, or null if it could not be read. Decoded at the size
    /// asked for rather than full resolution — this is a preview, and a 4K frame per
    /// scrub step would make dragging the playhead crawl.
    /// </summary>
    public async Task<BitmapSource?> FrameAtAsync(TimeSpan at, int width)
    {
        if (_composition is null || Width == 0) return null;

        // Past the end returns nothing at all, and the last frame is what anyone dragging
        // to the end of the track is looking for.
        var clamped = at >= Duration ? Duration - TimeSpan.FromMilliseconds(40) : at;
        if (clamped < TimeSpan.Zero) clamped = TimeSpan.Zero;

        int height = Math.Max(1, width * Height / Math.Max(1, Width));

        try
        {
            using var stream = await _composition.GetThumbnailAsync(
                clamped, width, height, VideoFramePrecision.NearestFrame);

            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = stream.AsStreamForRead();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();

            return image;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                      or ArgumentException or NotSupportedException
                                      or InvalidOperationException)
        {
            return null;
        }
    }

    // ---- Read-ahead ---------------------------------------------------------------

    /// <summary>
    /// Frames already decoded, keyed by their position in milliseconds.
    ///
    /// Nothing is decoded while playing: a frame asked for on its own costs about 400ms —
    /// measured, at every size, because the cost is opening a decode for it and not the
    /// pixels in it — so on-demand playback ran at under three frames a second however
    /// small the picture was. Asked for in a batch the same frames cost 17ms each, so the
    /// clip is read ahead in runs and playback only ever swaps pictures already in hand.
    /// </summary>
    private readonly SortedList<int, BitmapSource> _cache = [];

    /// <summary>
    /// Guards the cache. The read-ahead resumes on the interface thread today, so every
    /// touch is already on one thread — but this is the kind of thing that is true until
    /// someone adds a ConfigureAwait, and a torn SortedList is a crash, not a glitch.
    /// </summary>
    private readonly Lock _frames = new();

    /// <summary>
    /// Thirty a second — what the recorder writes. Fifteen was half the recording's own
    /// frame rate, and on a screen recording, where everything moves in straight lines,
    /// half rate does not read as slightly less smooth. It reads as broken.
    /// </summary>
    private int StepMilliseconds { get; set; } = 33;

    /// <summary>
    /// How much is kept ready in front of the playhead, and how much is kept behind it for
    /// stepping back. A window rather than the whole clip, because frames are held decoded
    /// and a long recording would otherwise be gigabytes.
    /// </summary>
    private const int AheadMilliseconds = 1500;
    private const int BehindMilliseconds = 600;

    /// <summary>
    /// A ceiling on the frames held, whatever the window in time works out to.
    ///
    /// Frames are kept decoded, and decoded is expensive: a 1280-wide one is under three
    /// megabytes, but a full-size 3440-wide one is twenty, and a second and a half of those
    /// is most of a gigabyte. Time alone is the wrong unit to bound this in.
    /// </summary>
    private const long BudgetBytes = 250L * 1024 * 1024;

    private long FrameBytes
    {
        get
        {
            long width = Math.Max(1, CacheWidth);
            long height = Math.Max(1, width * Height / Math.Max(1, Width));

            return width * height * 4;
        }
    }

    /// <summary>How far ahead to read: the shorter of the time window and what fits.</summary>
    private int Reach => Math.Min(
        AheadMilliseconds,
        (int)Math.Max(6, BudgetBytes / Math.Max(1, FrameBytes)) * StepMilliseconds);

    /// <summary>
    /// How many frames go into one request. Big enough that the per-call cost is shared
    /// out, small enough that a seek is picked up quickly and the other reader is not
    /// left waiting for a run to finish.
    /// </summary>
    private const int BatchFrames = 12;

    /// <summary>
    /// How many runs are read at once, each over its own composition.
    ///
    /// One reader tops out around seventeen frames a second — not because Media Foundation
    /// is that slow, but because reading a run and then turning it into pictures happen one
    /// after the other, so half the time each stage is idle. Two readers overlap those
    /// stages and measured about three times the throughput; a third and fourth made it
    /// worse again, so two it is.
    /// </summary>
    private const int Readers = 2;

    /// <summary>
    /// The fewest frames worth opening a read for, when there is no hurry. Below this the
    /// per-run cost is being paid for almost nothing.
    /// </summary>
    private const int MinimumRun = 8;

    /// <summary>
    /// Frames a reader has taken and not yet delivered, so the other does not read them
    /// a second time.
    /// </summary>
    private readonly HashSet<int> _claimed = [];

    /// <summary>
    /// The size frames are read at. Smaller than the window, because this is the moving
    /// picture: the sharp one is decoded separately whenever it stops moving.
    /// </summary>
    public int CacheWidth { get; set; } = 1280;

    /// <summary>The gap between cached frames, in milliseconds. Set before the pump starts.</summary>
    public int FrameGap { get => StepMilliseconds; set => StepMilliseconds = Math.Max(16, value); }

    /// <summary>
    /// Changes the size and rate frames are read at, mid-flight.
    ///
    /// What is already cached is thrown away: it is the wrong size, and it sits on a grid
    /// the new rate does not line up with, so keeping it would leave the old picture on
    /// screen at every position the new one happens to miss.
    /// </summary>
    public void Retune(int width, int gap)
    {
        lock (_frames)
        {
            _cache.Clear();

            CacheWidth = width;
            FrameGap = gap;
        }
    }

    /// <summary>Where the read-ahead should be working, set by whoever owns the playhead.</summary>
    private volatile int _follow;

    public void Follow(TimeSpan at) => _follow = (int)at.TotalMilliseconds;

    /// <summary>
    /// Keeps the window around the playhead full, for as long as the editor is open.
    ///
    /// It chases <see cref="Follow"/> rather than reading the clip start to end, so a seek
    /// into the middle is served in the time it takes to decode one run instead of after
    /// everything before it has been read.
    /// </summary>
    public async Task PumpAsync(CancellationToken token)
    {
        if (_composition is null || _path is null) return;

        var readers = new List<Task> { ReadAsync(_composition, token) };

        for (int i = 1; i < Readers; i++)
        {
            // Its own composition: two runs asked of the same one are served one after the
            // other, which is the very thing being avoided here.
            var file = await StorageFile.GetFileFromPathAsync(_path);
            var composition = new MediaComposition();
            composition.Clips.Add(await MediaClip.CreateFromFileAsync(file));

            readers.Add(ReadAsync(composition, token));
        }

        await Task.WhenAll(readers);
    }

    /// <summary>One reader: claim the next run that is missing, read it, decode it, repeat.</summary>
    private async Task ReadAsync(MediaComposition composition, CancellationToken token)
    {
        int total = (int)Duration.TotalMilliseconds;

        while (!token.IsCancellationRequested)
        {
            // Read each time round: the window can be resized under it, and the frames
            // read after that should suit the size they are going to be shown at.
            int width = CacheWidth;
            int height = Math.Max(1, width * Height / Math.Max(1, Width));

            int from = _follow;
            var wanted = new List<TimeSpan>();

            lock (_frames)
            {
                var candidates = new List<int>();

                int reach = Reach;

                for (int at = Align(from); at < from + reach && at < total; at += StepMilliseconds)
                {
                    if (at < 0 || _cache.ContainsKey(at) || _claimed.Contains(at)) continue;

                    candidates.Add(at);
                    if (candidates.Count >= BatchFrames) break;
                }

                // Only take it if it is worth opening a read for. A run of one frame costs
                // very nearly what a run of twelve costs, so once the window is nearly full
                // the readers were spending 400ms a frame keeping it topped up — and losing
                // to the playhead, which is what a slightly smaller buffer exposed.
                //
                // Unless the playhead is nearly out of frames, or the end of the clip is in
                // reach: then whatever is missing is worth having at any price.
                bool starved = Unbroken(Align(from)) < StepMilliseconds * 12;
                bool ending = from + reach >= total;

                if (candidates.Count >= MinimumRun || starved || ending)
                {
                    foreach (int at in candidates)
                    {
                        _claimed.Add(at);
                        wanted.Add(TimeSpan.FromMilliseconds(at));
                    }
                }
            }

            if (wanted.Count == 0)
            {
                // Everything within reach is read. Wait for the playhead to move rather
                // than spinning on a full cache.
                try
                {
                    await Task.Delay(40, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                continue;
            }

            try
            {
                var thumbnails = await composition.GetThumbnailsAsync(
                    wanted, width, height, VideoFramePrecision.NearestFrame);

                // Decoded off the interface thread and frozen, so handing two dozen
                // pictures over at once does not show up as a stutter in the window.
                var frames = await Task.Run(() =>
                {
                    var built = new List<BitmapSource?>(thumbnails.Count);
                    foreach (var thumbnail in thumbnails) built.Add(Decode(thumbnail));
                    return built;
                }, token);

                lock (_frames)
                {
                    for (int i = 0; i < wanted.Count && i < frames.Count; i++)
                        if (frames[i] is { } frame) _cache[(int)wanted[i].TotalMilliseconds] = frame;

                    Evict(_follow);
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException
                                          or ArgumentException or NotSupportedException
                                          or InvalidOperationException or ObjectDisposedException)
            {
                // The file went away, or Windows will not decode this run. Nothing to be
                // done from here; playback falls back to whatever is already cached.
                return;
            }
            finally
            {
                // Always given back, delivered or not. A frame left claimed after a failed
                // run is one neither reader will ever pick up again.
                lock (_frames)
                {
                    foreach (var at in wanted) _claimed.Remove((int)at.TotalMilliseconds);
                }
            }
        }
    }

    private static BitmapSource? Decode(Windows.Storage.Streams.IRandomAccessStream stream)
    {
        try
        {
            using (stream)
            {
                var image = new BitmapImage();
                image.BeginInit();

                // A clone, which comes positioned at the start and is independent of
                // whatever else is reading. GetInputStreamAt(0) looks like the obvious way
                // to say the same thing and is not: it hands back a stream that cannot
                // seek, and BitmapImage silently decodes nothing from it.
                image.StreamSource = stream.CloneStream().AsStreamForRead();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.EndInit();
                image.Freeze();

                return image;
            }
        }
        catch (Exception ex) when (ex is NotSupportedException or ArgumentException
                                      or IOException or FileFormatException
                                      or System.Runtime.InteropServices.COMException)
        {
            // One unreadable frame is a gap in the cache, not a reason to stop reading.
            return null;
        }
    }

    /// <summary>
    /// Drops what the playhead has left well behind, so the window stays bounded. Called
    /// with <see cref="_frames"/> already held.
    /// </summary>
    private void Evict(int from)
    {
        for (int i = _cache.Count - 1; i >= 0; i--)
        {
            int at = _cache.Keys[i];
            if (at < from - BehindMilliseconds || at > from + Reach * 2)
                _cache.RemoveAt(i);
        }
    }

    private int Align(int milliseconds) =>
        milliseconds / StepMilliseconds * StepMilliseconds;

    /// <summary>
    /// Waits until there is enough read ahead of a point to play from it without the
    /// picture standing still, or until it is clear nothing more is coming.
    /// </summary>
    public async Task WaitForAsync(TimeSpan at, TimeSpan enough, CancellationToken token)
    {
        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        while (!token.IsCancellationRequested && DateTime.UtcNow < giveUp)
        {
            if (Ready(at) >= enough) return;

            try
            {
                await Task.Delay(50, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>How much unbroken material is cached from a point onwards.</summary>
    public TimeSpan Ready(TimeSpan at)
    {
        lock (_frames)
        {
            return TimeSpan.FromMilliseconds(Unbroken(Align((int)at.TotalMilliseconds)));
        }
    }

    /// <summary>
    /// Milliseconds of unbroken cache from an aligned point. Called with
    /// <see cref="_frames"/> already held.
    /// </summary>
    private int Unbroken(int start)
    {
        int run = start;
        while (_cache.ContainsKey(run)) run += StepMilliseconds;

        return run - start;
    }

    /// <summary>
    /// The cached frame nearest a time, or null when that part of the clip has not been
    /// read yet. Never more than half a step away, so it cannot show the wrong moment.
    /// </summary>
    public BitmapSource? Cached(TimeSpan at)
    {
        int wanted = (int)at.TotalMilliseconds;
        int rounded = (int)Math.Round(wanted / (double)StepMilliseconds) * StepMilliseconds;

        lock (_frames)
        {
            return _cache.TryGetValue(rounded, out var frame) ? frame : null;
        }
    }

    public void Dispose()
    {
        _composition = null;

        lock (_frames) _cache.Clear();
    }
}
