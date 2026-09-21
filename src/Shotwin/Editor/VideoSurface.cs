using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Shotwin.Editor;

/// <summary>
/// The video, played by Windows itself, at the recording's own size and speed.
///
/// Everything before this pulled still frames out of the file one at a time and swapped
/// them by hand, which put resolution and frame rate on the same budget: a frame costs
/// about the same to fetch whatever size it is, so a bigger picture bought fewer of them.
/// Measured, the best that trade ever offered was 1280 wide at thirty a second — a
/// quarter-size picture — and every attempt to buy detail back cost frames.
///
/// A real playback pipeline has no such trade. It decodes in order, on the graphics card,
/// and puts the frames on screen itself: full size, full rate, and seeking that lands in
/// no measurable time at all. MFPlay is the smallest door into it — one call to put a
/// player over a window handle — and this class is that window handle, hosted in WPF.
///
/// The cost is airspace: a child window like this one always draws over the WPF content
/// on top of it, so anything that has to appear over the picture has to be its own window.
/// That is what the play disc's popup is for.
/// </summary>
public sealed class VideoSurface : HwndHost
{
    private IMFPMediaPlayer? _player;
    private IntPtr _host;
    private string? _pending;

    /// <summary>Raised once the file is open and its size and length are known.</summary>
    public event Action? Opened;

    public TimeSpan Duration { get; private set; }
    public int VideoWidth { get; private set; }
    public int VideoHeight { get; private set; }

    public bool IsReady => _player is not null && Duration > TimeSpan.Zero;

    /// <summary>
    /// Opens a file. Safe before the window exists: it is remembered and opened as soon as
    /// there is a handle to play it into.
    /// </summary>
    public void Open(string path)
    {
        _pending = path;
        if (_host != IntPtr.Zero) Start();
    }

    private void Start()
    {
        if (_pending is not { } path) return;

        // Not started on creation: the editor decides when to play, and a video that
        // starts before its own window has been laid out shows its first second in the
        // wrong place.
        int hr = MFPCreateMediaPlayer(path, false, 0, IntPtr.Zero, _host, out var player);
        if (hr != 0 || player is null) return;

        _player = player;
        _pending = null;

        // Letterboxing matches the editor's own backdrop, so a picture that does not fill
        // the window does not look like a bug.
        player.SetBorderColor(0x00141417);

        // Nothing here has sound — the recorder writes video only — but a file that did
        // would otherwise start playing it over whatever else is running.
        player.SetVolume(0);

        WaitForOpen();
    }

    /// <summary>
    /// Polls until the player knows what it is playing.
    ///
    /// MFPlay reports that through a callback, which means a COM object implemented on
    /// this side and a set of event codes to get wrong. Asking it every fiftieth of a
    /// second whether it knows the duration yet answers the same question, and the answer
    /// arrives within a frame or two of the callback's.
    /// </summary>
    private void WaitForOpen()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(20),
        };

        var giveUp = DateTime.UtcNow + TimeSpan.FromSeconds(5);

        timer.Tick += (s, _) =>
        {
            if (_player is not { } player)
            {
                ((System.Windows.Threading.DispatcherTimer)s!).Stop();
                return;
            }

            var type = Guid.Empty;

            if (player.GetDuration(ref type, out var duration) == 0 && duration.Value > 0)
            {
                ((System.Windows.Threading.DispatcherTimer)s!).Stop();

                Duration = TimeSpan.FromTicks(duration.Value);

                if (player.GetNativeVideoSize(out var size, out Size2 aspect) == 0)
                {
                    VideoWidth = size.Width;
                    VideoHeight = size.Height;
                }

                Opened?.Invoke();
                return;
            }

            if (DateTime.UtcNow > giveUp) ((System.Windows.Threading.DispatcherTimer)s!).Stop();
        };

        timer.Start();
    }

    public void Play() => _player?.Play();

    public void Pause() => _player?.Pause();

    public TimeSpan Position
    {
        get
        {
            if (_player is not { } player) return TimeSpan.Zero;

            var type = Guid.Empty;
            return player.GetPosition(ref type, out var at) == 0
                ? TimeSpan.FromTicks(at.Value)
                : TimeSpan.Zero;
        }
    }

    public void Seek(TimeSpan to)
    {
        if (_player is not { } player) return;

        var type = Guid.Empty;
        var at = new PropVariant { Type = VT_I8, Value = Math.Max(0, to.Ticks) };

        player.SetPosition(ref type, ref at);
    }

    protected override HandleRef BuildWindowCore(HandleRef parent)
    {
        // A plain child window, which is all the player needs: somewhere to draw.
        _host = CreateWindowExW(0, "STATIC", string.Empty,
            WS_CHILD | WS_VISIBLE, 0, 0, 1, 1, parent.Handle, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        Start();

        return new HandleRef(this, _host);
    }

    protected override void DestroyWindowCore(HandleRef window)
    {
        // Shut down before the window goes: the player is drawing into it, and one still
        // holding a destroyed handle takes the process with it.
        _player?.Shutdown();
        _player = null;

        if (_host != IntPtr.Zero)
        {
            DestroyWindow(_host);
            _host = IntPtr.Zero;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);

        // The picture follows the window. Without this the player keeps the size it was
        // given when it started and the video sits in a corner of a resized editor.
        _player?.UpdateVideo();
    }

    // ---- Media Foundation ---------------------------------------------------------

    [DllImport("mfplay.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int MFPCreateMediaPlayer(
        string url, [MarshalAs(UnmanagedType.Bool)] bool startPlayback,
        uint options, IntPtr callback, IntPtr window, out IMFPMediaPlayer player);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(
        int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const ushort VT_I8 = 20;

    [StructLayout(LayoutKind.Sequential)]
    private struct Size2 { public int Width; public int Height; }

    /// <summary>
    /// Only ever holds a time, so only the two fields a time needs are named. The size is
    /// what a PROPVARIANT is on 64-bit Windows, which is what matters: anything short
    /// would have the player writing past the end of it.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }

    /// <summary>
    /// Declared in vtable order, in full. The methods this app never calls still have to
    /// be here: leave one out and every method after it is called through the wrong slot.
    /// </summary>
    [ComImport, Guid("A714590A-58AF-430a-85BF-44F5EC838D85"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMFPMediaPlayer
    {
        [PreserveSig] int Play();
        [PreserveSig] int Pause();
        [PreserveSig] int Stop();
        [PreserveSig] int FrameStep();
        [PreserveSig] int SetPosition(ref Guid positionType, ref PropVariant position);
        [PreserveSig] int GetPosition(ref Guid positionType, out PropVariant position);
        [PreserveSig] int GetDuration(ref Guid positionType, out PropVariant duration);
        [PreserveSig] int SetRate(float rate);
        [PreserveSig] int GetRate(out float rate);
        [PreserveSig] int GetSupportedRates(bool forwards, out float slowest, out float fastest);
        [PreserveSig] int GetState(out uint state);
        [PreserveSig] int CreateMediaItemFromURL(string url, bool sync, uint userData, out IntPtr item);
        [PreserveSig] int CreateMediaItemFromObject(IntPtr source, bool sync, uint userData, out IntPtr item);
        [PreserveSig] int SetMediaItem(IntPtr item);
        [PreserveSig] int ClearMediaItem();
        [PreserveSig] int GetMediaItem(out IntPtr item);
        [PreserveSig] int GetVolume(out float volume);
        [PreserveSig] int SetVolume(float volume);
        [PreserveSig] int GetBalance(out float balance);
        [PreserveSig] int SetBalance(float balance);
        [PreserveSig] int GetMute(out bool muted);
        [PreserveSig] int SetMute(bool muted);
        [PreserveSig] int GetNativeVideoSize(out Size2 video, out Size2 aspectRatio);
        [PreserveSig] int GetIdealVideoSize(out Size2 smallest, out Size2 largest);
        [PreserveSig] int SetVideoSourceRect(IntPtr rectangle);
        [PreserveSig] int GetVideoSourceRect(IntPtr rectangle);
        [PreserveSig] int SetAspectRatioMode(uint mode);
        [PreserveSig] int GetAspectRatioMode(out uint mode);
        [PreserveSig] int GetVideoWindow(out IntPtr window);
        [PreserveSig] int UpdateVideo();
        [PreserveSig] int SetBorderColor(uint colour);
        [PreserveSig] int GetBorderColor(out uint colour);
        [PreserveSig] int InsertEffect(IntPtr effect, bool optional);
        [PreserveSig] int RemoveEffect(IntPtr effect);
        [PreserveSig] int RemoveAllEffects();
        [PreserveSig] int Shutdown();
    }
}
