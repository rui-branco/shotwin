using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

namespace Shotwin.Capture;

/// <summary>
/// Records a region of the screen to an MP4.
///
/// Encoding goes through the H.264 encoder built into Windows, the same choice as the
/// text recognition: no FFmpeg to bundle, nothing to download, and hardware
/// acceleration where the machine has it. Frames are pushed as uncompressed BGRA into a
/// <see cref="MediaStreamSource"/> and a <see cref="MediaTranscoder"/> pulls them
/// through the encoder, which is the only route Windows offers for encoding frames that
/// do not already exist as a file.
///
/// The region is in virtual-desktop PHYSICAL pixels, like every other capture here.
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    private readonly RegionGrabber _grabber;
    private readonly string _path;
    private readonly int _framesPerSecond;
    private readonly byte[] _frame;

    private MediaStreamSource? _source;
    private IRandomAccessStream? _output;
    private Stream? _file;

    /// <summary>How many frames have been handed over, which sets when the next one is due.</summary>
    private int _frameIndex;

    /// <summary>The transcode, which only finishes once the stream has ended.</summary>
    private Task? _transcode;

    private readonly Stopwatch _clock = new();
    private volatile bool _stopping;

    /// <summary>How long has been recorded, for the on-screen timer.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    public string Path => _path;

    /// <param name="fps">
    /// Frames a second. Thirty reads as smooth for a UI recording; fifteen halves the
    /// file for something mostly static.
    /// </param>
    /// <param name="cursor">
    /// Draw the mouse pointer into each frame. It is not in the screen copy the grabber
    /// takes, so without this the recording has no pointer in it at all.
    /// </param>
    public ScreenRecorder(SKRectIRegion region, string path, int fps = 30, bool cursor = false)
    {
        // H.264 macroblocks are 16x16 and the encoder rejects odd dimensions outright,
        // so the region is trimmed rather than left to fail at the first frame.
        int width = region.Width & ~1;
        int height = region.Height & ~1;

        _grabber = new RegionGrabber(region.Left, region.Top, width, height, cursor);
        _path = path;
        // Measured on this capture path: a 720p region grabs at about 175 frames a
        // second, 1080p at 87, an ultrawide at 45. The Windows encoder accepted 120 at
        // every size tried, so the grab is the ceiling, not the encoding. Asking for
        // more than the region can manage is harmless — frames carry the time they were
        // actually taken, so the result plays at the right speed with fewer of them
        // rather than running slow.
        _framesPerSecond = Math.Clamp(fps, 5, 120);
        _frame = new byte[_grabber.FrameBytes];
    }

    public async Task<bool> StartAsync()
    {
        if (!_grabber.IsReady) return false;

        var properties = VideoEncodingProperties.CreateUncompressed(
            MediaEncodingSubtypes.Bgra8, (uint)_grabber.Width, (uint)_grabber.Height);

        var descriptor = new VideoStreamDescriptor(properties);
        _source = new MediaStreamSource(descriptor)
        {
            // Live frames: there is no future to buffer, and a non-zero buffer time makes
            // the transcoder wait for samples that only exist once they are grabbed.
            BufferTime = TimeSpan.Zero,
        };

        _source.Starting += OnStarting;
        _source.SampleRequested += OnSampleRequested;

        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)_grabber.Width;
        profile.Video.Height = (uint)_grabber.Height;
        profile.Video.FrameRate.Numerator = (uint)_framesPerSecond;
        profile.Video.FrameRate.Denominator = 1;

        // No audio track at all rather than a silent one: an empty track makes some
        // players report the file as broken.
        profile.Audio = null;

        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            _file = File.Create(_path);
            _output = _file.AsRandomAccessStream();

            var transcoder = new MediaTranscoder { HardwareAccelerationEnabled = true };
            var prepared = await transcoder.PrepareMediaStreamSourceTranscodeAsync(
                _source, _output, profile);

            if (!prepared.CanTranscode) return false;

            _clock.Start();

            // Kept rather than discarded: this completes only once the source reports the
            // end of the stream, and waiting on it is how Stop knows the file is finished.
            _transcode = prepared.TranscodeAsync().AsTask();
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or System.Runtime.InteropServices.COMException)
        {
            Cleanup();
            return false;
        }
    }

    private void OnStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args) =>
        args.Request.SetActualStartPosition(TimeSpan.Zero);

    /// <summary>
    /// Grabs the screen on demand rather than on a timer. The transcoder asks for the
    /// next frame when it is ready for one, so the encoder sets the pace and a slow
    /// machine drops to a lower real frame rate instead of building an unbounded queue.
    /// </summary>
    private void OnSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
    {
        var request = args.Request;

        if (_stopping)
        {
            // A null sample is how a MediaStreamSource says the stream has ended; the
            // transcode then finishes and releases the file.
            request.Sample = null;
            return;
        }

        var deferral = request.GetDeferral();
        try
        {
            // Paced against the wall clock. The transcoder asks for the next frame the
            // moment it has encoded the last one, so without this it ran at whatever the
            // encoder could manage — about a hundred and seventy frames a second — and
            // stamping those a thirtieth apart turned three seconds of screen into
            // eighteen seconds of slow motion.
            var due = TimeSpan.FromSeconds(_frameIndex / (double)_framesPerSecond);
            var wait = due - _clock.Elapsed;
            if (wait > TimeSpan.Zero) Thread.Sleep(wait);

            if (!_grabber.Grab(_frame))
            {
                request.Sample = null;
                return;
            }

            // Stamped with when it was actually taken rather than when it was due: a
            // machine that cannot keep up should drop to a lower frame rate, not stretch
            // the recording out past real time.
            var sample = MediaStreamSample.CreateFromBuffer(_frame.AsBuffer(), _clock.Elapsed);
            sample.Duration = TimeSpan.FromSeconds(1.0 / _framesPerSecond);
            request.Sample = sample;

            _frameIndex++;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ObjectDisposedException)
        {
            request.Sample = null;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <summary>
    /// Ends the stream and waits for the encoder to flush. Returns the file once it is
    /// closed and playable, or null if nothing usable was written.
    /// </summary>
    public async Task<string?> StopAsync()
    {
        if (_stopping) return null;

        _stopping = true;
        _clock.Stop();

        // The transcode finishes asynchronously after the null sample, and the moov atom
        // is only written when it does. Handing back the path before that gives the
        // caller a file no player will open.
        //
        // Awaiting the transcode itself rather than watching the stream: the file handle
        // only closes when the transcoder disposes it, which it may not do promptly, so
        // polling for that spent the full timeout on every single recording.
        if (_transcode is not null)
        {
            try
            {
                await _transcode.WaitAsync(TimeSpan.FromSeconds(15));
            }
            catch (Exception ex) when (ex is TimeoutException or System.Runtime.InteropServices.COMException)
            {
                // Whatever reached the file is better than nothing; the checks below
                // decide whether it amounts to anything.
            }
        }

        Cleanup();

        var info = new FileInfo(_path);
        return info.Exists && info.Length > 0 ? _path : null;
    }

    private void Cleanup()
    {
        if (_source is not null)
        {
            _source.Starting -= OnStarting;
            _source.SampleRequested -= OnSampleRequested;
            _source = null;
        }

        _output?.Dispose();
        _output = null;

        _file?.Dispose();
        _file = null;
    }

    public void Dispose()
    {
        _stopping = true;
        Cleanup();
        _grabber.Dispose();
    }
}

/// <summary>
/// A screen rectangle in physical pixels. Its own type rather than SKRectI so the
/// recorder does not drag Skia into the encoding path, which deals only in raw bytes.
/// </summary>
public readonly record struct SKRectIRegion(int Left, int Top, int Width, int Height);
