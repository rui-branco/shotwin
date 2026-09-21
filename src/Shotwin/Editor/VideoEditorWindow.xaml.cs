using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Shotwin.Services;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Shotwin.Editor;

/// <summary>
/// The editor a recording gets: play it, pick the span worth keeping, write that span
/// out as a new file.
///
/// Trimming is handed to <see cref="MediaTranscoder"/> with a start and a stop time,
/// which is the same Windows encoder the recorder itself writes through. Cutting frames
/// by hand would mean decoding, re-timing and re-encoding the whole clip here, and
/// every one of those is a way to produce a file that plays wrong.
/// </summary>
public partial class VideoEditorWindow : Window
{
    /// <summary>Half a trim handle, so the track ends where a handle can still sit on it.</summary>
    private const double TrackPad = 7;

    /// <summary>
    /// The shortest span worth writing. Below this the handles are on top of each other
    /// and the transcoder is being asked for a clip with nothing in it.
    /// </summary>
    private static readonly TimeSpan MinimumSpan = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// The shortest cut. Smaller than the shortest kept span, because taking a quarter of
    /// a second out of the middle is a reasonable thing to want and costs nothing.
    /// </summary>
    private static readonly TimeSpan MinimumCut = TimeSpan.FromMilliseconds(120);

    private readonly string _path;

    /// <summary>
    /// Follows the player while it plays. The player keeps its own clock and draws its own
    /// frames, so this only reads where it has got to — thirty times a second is finer
    /// than the playhead can be seen to move.
    /// </summary>
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(33) };


    private TimeSpan _duration;
    private bool _playing;

    /// <summary>The trimmed file once one has been written. Copy and Show follow it.</summary>
    private string? _exported;

    private bool _exporting;

    public VideoEditorWindow(string path)
    {
        InitializeComponent();

        FlowDirection = Localisation.FlowDirection;

        // The caption, the clock and the trim line are written here rather than bound,
        // so they are rebuilt when the language changes under an open window.
        void OnLanguageChanged(object? s, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RefreshCaption();
            UpdateTransport();
            ShowHint();
        }

        Localisation.Instance.PropertyChanged += OnLanguageChanged;
        Closed += (_, _) => Localisation.Instance.PropertyChanged -= OnLanguageChanged;

        _path = path;

        MinimiseButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaximiseButton.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CaptionCloseButton.Click += (_, _) => Close();
        StateChanged += (_, _) => RefreshCaption();

        OpenInPlayerButton.Click += (_, _) => TogglePlayback();

        PlayPauseButton.Click += (_, _) => TogglePlayback();
        SaveButton.Click += (_, _) => _ = SaveAsync();
        CopyButton.Click += (_, _) => CopyFile();
        RevealButton.Click += (_, _) => Reveal();

        Timeline.MouseLeftButtonDown += OnTimelineDown;
        Timeline.MouseMove += OnTimelineMove;
        Timeline.MouseLeftButtonUp += OnTimelineUp;
        Timeline.SizeChanged += (_, _) => LayoutTimeline();

        ShowHint();

        // Preview, because Space on a focused button presses that button: without
        // handling it first, Space next to Save would save rather than pause.
        PreviewKeyDown += OnWindowKeyDown;

        Loaded += (_, _) => { Activate(); Focus(); };
        Closing += OnClosing;

        _ticker.Tick += (_, _) => OnTick();

        UpdateTransport();
        Open(path);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        Shotwin.Interop.WindowCorners.ApplyNative(this);
    }

    /// <summary>Where the playhead sits, as the player reports it.</summary>
    private TimeSpan _position;

    /// <summary>
    /// Opens the recording in the player, and lays the window out once it says what it is
    /// holding. Nothing is known before that — not its size, not its length — so the
    /// window sizes itself and fills its timeline from there rather than from here.
    /// </summary>
    private void Open(string path)
    {
        Surface.Opened += () =>
        {
            _duration = Surface.Duration;

            if (_duration <= TimeSpan.Zero)
            {
                Flash(Localisation.Get("VideoUnreadable"));
                return;
            }

            // The whole recording, as one piece. Everything after this is taking material
            // out of it or putting another piece back.
            if (_pieces.Count == 0) AddPiece(TimeSpan.Zero, _duration);

            SizeToVideo();
            RefreshCaption();
            LayoutTimeline();
            UpdateTransport();

            _ticker.Start();
            Play();
        };

        Surface.Open(path);
    }

    /// <summary>Moves the playhead, and the picture with it.</summary>
    private void Seek(TimeSpan to)
    {
        _position = Clamp(to, TimeSpan.Zero, _duration);
        Surface.Seek(_position);
    }

    /// <summary>Mirrors the editor: the caption says what the window is holding.</summary>
    private void RefreshCaption()
    {
        MaximiseButton.ToolTip = Localisation.Get(
            WindowState == WindowState.Maximized ? "NavRestore" : "NavMaximise");

        CaptionTitle.Text = Localisation.Format("VideoCaption",
            Surface.VideoWidth, Surface.VideoHeight, Clock(_duration));
    }

    /// <summary>
    /// Open at 1:1 where it fits, capped to 85% of the work area, like the editor. The
    /// size is only known once the media opens, which is after the window was centred,
    /// so it centres itself again on the size it ended up with.
    /// </summary>
    private void SizeToVideo()
    {
        if (Surface.VideoWidth == 0) return;

        var area = SystemParameters.WorkArea;
        double scale = Math.Max(1.0, VisualTreeHelper.GetDpi(this).DpiScaleX);

        Width = Math.Clamp(Surface.VideoWidth / scale + 40, MinWidth, area.Width * 0.85);
        Height = Math.Clamp(Surface.VideoHeight / scale + 170, MinHeight, area.Height * 0.85);

        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    // ---- Playback ---------------------------------------------------------------

    private void TogglePlayback()
    {
        if (_playing) Pause();
        else Play();
    }

    private void Play()
    {
        if (!Surface.IsReady) return;

        // Sitting past the end, or in a gap, pressing play would play nothing at all.
        if (PieceAt(_position) is null) Seek(PlayFrom);

        _playing = true;

        // The disc is a play button, so it goes while it is playing; the picture is clear
        // to watch, and clicking it pauses.
        PlayOverlay.IsOpen = false;

        Surface.Play();
        UpdateTransport();
    }

    private void Pause()
    {
        _playing = false;

        Surface.Pause();

        PlayOverlay.IsOpen = Surface.IsReady;
        UpdateTransport();
    }

    /// <summary>
    /// Follows the player: where it has got to, whether it has run into a gap, and whether
    /// it has reached the end of the last piece.
    ///
    /// The player keeps its own clock and draws its own frames, so there is nothing to
    /// drive here — only to read, and to steer it over the parts being cut out.
    /// </summary>
    private void OnTick()
    {
        if (_playing)
        {
            _position = Surface.Position;

            // The saved file is the pieces joined end to end, so playback jumps the gaps:
            // what plays here is what will come out.
            if (PieceAt(_position) is null)
            {
                var resume = NextPieceStart(_position);

                if (resume >= PlayTo)
                {
                    Seek(PlayTo);
                    Pause();
                }
                else
                {
                    Seek(resume);
                }
            }
            else if (_position >= PlayTo)
            {
                Seek(PlayTo);
                Pause();
            }
        }

        LayoutPlayhead();

        // Only when the reading would actually change: formatting two clocks thirty times
        // a second to print the same thing is work the window does not need to do.
        if (_position.Seconds != _shownSecond)
        {
            _shownSecond = _position.Seconds;
            UpdateClock();
        }
    }

    /// <summary>The second the clock is showing, so it is only rewritten when it moves.</summary>
    private int _shownSecond = -1;

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space:
                TogglePlayback();
                e.Handled = true;
                break;

            case Key.C:
                AddCut();
                e.Handled = true;
                break;

            case Key.Delete:
            case Key.Back:
                if (_pieces.Count > 1 && PieceAt(_position) is { } piece) RemovePiece(piece);
                e.Handled = true;
                break;

            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    // ---- Timeline ---------------------------------------------------------------

    private double TrackWidth => Math.Max(0, Timeline.ActualWidth - TrackPad * 2);

    private double XOf(TimeSpan at) => _duration <= TimeSpan.Zero
        ? TrackPad
        : TrackPad + TrackWidth * (at.TotalSeconds / _duration.TotalSeconds);

    private TimeSpan TimeAt(double x) => _duration <= TimeSpan.Zero || TrackWidth <= 0
        ? TimeSpan.Zero
        : TimeSpan.FromSeconds(Math.Clamp((x - TrackPad) / TrackWidth, 0, 1) * _duration.TotalSeconds);

    private void LayoutTimeline()
    {
        Track.Width = TrackWidth;
        Canvas.SetLeft(Track, TrackPad);

        LayoutPieces();
        LayoutPlayhead();
    }

    private void LayoutPlayhead() =>
        Canvas.SetLeft(Playhead, XOf(_position) - Playhead.Width / 2);

    private static TimeSpan Clamp(TimeSpan value, TimeSpan low, TimeSpan high) =>
        value < low ? low : value > high ? high : value;

    // ---- The pieces that stay ----------------------------------------------------

    /// <summary>
    /// One stretch of the recording that will be in the saved file, with the controls that
    /// belong to it.
    ///
    /// The clip is a list of these rather than one span with holes punched in it. That was
    /// the earlier design and it could only ever describe one run of material: there was no
    /// way to say "keep this bit at the start and that bit near the end", which is the
    /// ordinary thing to want from a recording.
    ///
    /// The visuals are built once and moved rather than rebuilt, because a Thumb replaced
    /// mid-gesture loses the mouse and the drag dies under your hand.
    /// </summary>
    private sealed class Piece
    {
        public TimeSpan From;
        public TimeSpan To;

        public required Thumb Bar { get; init; }
        public required Thumb Left { get; init; }
        public required Thumb Right { get; init; }
        public required Button Remove { get; init; }

        /// <summary>How far this piece's bar has been dragged, to tell a drag from a click.</summary>
        public double Dragged;

        public TimeSpan Length => To - From;
    }

    private readonly List<Piece> _pieces = [];

    /// <summary>Movement below this is a click rather than a drag.</summary>
    private const double ClickSlop = 4;

    /// <summary>Puts the playhead at a place on the track.</summary>
    private void SeekTo(double x)
    {
        Seek(Clamp(TimeAt(x), TimeSpan.Zero, _duration));
        LayoutPlayhead();
        UpdateClock();
    }

    /// <summary>Where the saved clip starts and ends: the outermost pieces.</summary>
    private TimeSpan PlayFrom => _pieces.Count == 0 ? TimeSpan.Zero : _pieces.Min(k => k.From);
    private TimeSpan PlayTo => _pieces.Count == 0 ? _duration : _pieces.Max(k => k.To);

    /// <summary>The piece a moment falls inside, or null when it falls in a gap.</summary>
    private Piece? PieceAt(TimeSpan at) =>
        _pieces.FirstOrDefault(k => at >= k.From && at < k.To);

    /// <summary>
    /// Where playback resumes from a moment in a gap: the start of the next piece, or the
    /// end of everything when there is none.
    /// </summary>
    private TimeSpan NextPieceStart(TimeSpan after)
    {
        var next = _pieces.Where(k => k.From > after).OrderBy(k => k.From).FirstOrDefault();
        return next?.From ?? PlayTo;
    }

    private Piece AddPiece(TimeSpan from, TimeSpan to)
    {
        var piece = new Piece
        {
            From = Clamp(from, TimeSpan.Zero, _duration),
            To = Clamp(to, TimeSpan.Zero, _duration),
            Bar = new Thumb { Style = (Style)FindResource("PieceBar") },
            Left = new Thumb { Style = (Style)FindResource("PieceHandle") },
            Right = new Thumb { Style = (Style)FindResource("PieceHandle") },
            Remove = new Button
            {
                Style = (Style)FindResource("PieceRemove"),
                ToolTip = Localisation.Get("VideoCutRemove"),
            },
        };

        piece.Bar.DragStarted += (_, _) => piece.Dragged = 0;

        piece.Bar.DragDelta += (_, e) =>
        {
            piece.Dragged += e.HorizontalChange;

            // The first few pixels are held back, so a press that turns out to be a click
            // has not already nudged the piece out of place.
            if (Math.Abs(piece.Dragged) < ClickSlop) return;

            MovePiece(piece, e.HorizontalChange);
        };
        piece.Left.DragDelta += (_, e) => ResizePiece(piece, start: true, e.HorizontalChange);
        piece.Right.DragDelta += (_, e) => ResizePiece(piece, start: false, e.HorizontalChange);

        // Two pieces dragged into each other are one piece, and are only joined once the
        // hand lets go: merging mid-drag takes the Thumb out from under the mouse.
        piece.Bar.DragCompleted += (_, _) =>
        {
            // A press on a piece that went nowhere is a click, and a click anywhere on this
            // timeline moves the playhead. Without this the blue bars were dead to clicks:
            // the only way to reach a moment inside one was to aim at the gaps between.
            if (Math.Abs(piece.Dragged) < ClickSlop)
            {
                SeekTo(Mouse.GetPosition(Timeline).X);
                return;
            }

            SettlePieces();
        };
        piece.Left.DragCompleted += (_, _) => SettlePieces();
        piece.Right.DragCompleted += (_, _) => SettlePieces();

        piece.Remove.Click += (_, _) => RemovePiece(piece);

        // Under the playhead, so a two-pixel line is never buried by a bar.
        int at = Timeline.Children.IndexOf(Kept) + 1;
        Timeline.Children.Insert(at++, piece.Bar);
        Timeline.Children.Insert(at++, piece.Left);
        Timeline.Children.Insert(at++, piece.Right);
        Timeline.Children.Insert(at, piece.Remove);

        _pieces.Add(piece);
        return piece;
    }

    private void RemovePiece(Piece piece)
    {
        Timeline.Children.Remove(piece.Bar);
        Timeline.Children.Remove(piece.Left);
        Timeline.Children.Remove(piece.Right);
        Timeline.Children.Remove(piece.Remove);

        _pieces.Remove(piece);

        LayoutTimeline();
        UpdateTransport();
    }

    private void MovePiece(Piece piece, double delta)
    {
        var length = piece.Length;

        var from = Clamp(TimeAt(XOf(piece.From) + delta), TimeSpan.Zero, _duration - length);
        piece.From = from;
        piece.To = from + length;

        Pause();
        Seek(piece.From);
        LayoutTimeline();
        UpdateTransport();
    }

    private void ResizePiece(Piece piece, bool start, double delta)
    {
        if (start) piece.From = Clamp(TimeAt(XOf(piece.From) + delta), TimeSpan.Zero, piece.To - MinimumCut);
        else piece.To = Clamp(TimeAt(XOf(piece.To) + delta), piece.From + MinimumCut, _duration);

        // The frame at the edge being dragged is the whole question being asked: is this
        // where the piece should start, or has it lost something worth keeping?
        Pause();
        Seek(start ? piece.From : piece.To);

        LayoutTimeline();
        UpdateTransport();
    }

    private void SettlePieces()
    {
        MergePieces();
        LayoutTimeline();
        UpdateTransport();
    }

    /// <summary>
    /// Folds pieces that touch or overlap into one. The saved file would be the same
    /// either way, but two bars stacked on each other are two things to drag where there
    /// is visibly one piece of film.
    /// </summary>
    private void MergePieces()
    {
        var ordered = _pieces.OrderBy(k => k.From).ToList();

        for (int i = 0; i < ordered.Count - 1; i++)
        {
            var current = ordered[i];
            var next = ordered[i + 1];

            if (next.From > current.To) continue;

            current.To = next.To > current.To ? next.To : current.To;
            RemovePiece(next);
            ordered.RemoveAt(i + 1);
            i--;
        }
    }

    /// <summary>What the saved file will contain, in order.</summary>
    private List<(TimeSpan From, TimeSpan To)> Segments() => _pieces
        .Where(k => k.Length >= MinimumCut)
        .OrderBy(k => k.From)
        .Select(k => (From: k.From, To: k.To))
        .ToList();

    /// <summary>How long the finished clip runs.</summary>
    private TimeSpan KeptLength()
    {
        var total = TimeSpan.Zero;
        foreach (var segment in Segments()) total += segment.To - segment.From;
        return total;
    }

    private void LayoutPieces()
    {
        foreach (var piece in _pieces)
        {
            double from = XOf(piece.From);
            double width = Math.Max(2, XOf(piece.To) - from);

            piece.Bar.Width = width;
            Canvas.SetLeft(piece.Bar, from);
            Canvas.SetTop(piece.Bar, 10);
            piece.Bar.ToolTip = $"{Clock(piece.From)} — {Clock(piece.To)}";

            Canvas.SetLeft(piece.Left, from - piece.Left.Width / 2);
            Canvas.SetTop(piece.Left, 8);

            Canvas.SetLeft(piece.Right, from + width - piece.Right.Width / 2);
            Canvas.SetTop(piece.Right, 8);

            // Only when the bar is wide enough to hold it without covering its own
            // handles, and never on the last piece standing — dropping it would leave
            // nothing to save. Dropping a piece is the cross or the Delete key and nothing
            // else: it was also a double-click, which is two clicks, and clicking a piece
            // is how you seek inside it. Losing your work by clicking twice in the same
            // place is not a shortcut worth having.
            bool room = width >= 22 && _pieces.Count > 1;
            piece.Remove.Visibility = room ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(piece.Remove, from + width / 2 - piece.Remove.Width / 2);
            Canvas.SetTop(piece.Remove, 12);
        }
    }

    /// <summary>
    /// Takes a stretch out at the playhead, splitting the piece it lands in.
    ///
    /// The scissors and the bars say the same thing from two ends: this removes material
    /// from a piece, and dragging on empty track puts material back.
    /// </summary>
    private void AddCut()
    {
        if (_duration <= TimeSpan.Zero) return;

        // Cutting where you stopped is the point of the button, so it stops first.
        Pause();

        // Nothing to cut here: the playhead is already in a gap.
        if (PieceAt(_position) is not { } piece) return;

        var length = TimeSpan.FromSeconds(Math.Clamp(_duration.TotalSeconds * 0.1, 0.4, 2));

        var from = _position;
        var to = Clamp(from + length, piece.From, piece.To);

        if (to - from < MinimumCut)
        {
            Flash(Localisation.Get("VideoSpanTooShort"));
            return;
        }

        if (to >= piece.To)
        {
            // It reaches the end of the piece, so the piece simply ends sooner.
            piece.To = from;
        }
        else if (from <= piece.From)
        {
            piece.From = to;
        }
        else
        {
            // A hole in the middle: what is after the hole becomes a piece of its own.
            AddPiece(to, piece.To);
            piece.To = from;
        }

        // A sliver left behind is not a piece of film.
        foreach (var left in _pieces.Where(k => k.Length < MinimumCut).ToList()) RemovePiece(left);

        Seek(to);
        LayoutTimeline();
        UpdateTransport();
    }

    // ---- Drawing a new piece ------------------------------------------------------

    /// <summary>Where a drag across empty track began, while one is in progress.</summary>
    private double? _drawingFrom;

    /// <summary>Shown while dragging, so the piece is visible before it exists.</summary>
    private Border? _drawing;

    /// <summary>
    /// A press on empty track either seeks or starts drawing a new piece — which it turns
    /// out to be is only known when the mouse comes up, so both are prepared for here.
    /// The bars and handles take their own presses, so this only ever runs on bare track.
    /// </summary>
    private void OnTimelineDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= TimeSpan.Zero) return;

        _drawingFrom = e.GetPosition(Timeline).X;
        Timeline.CaptureMouse();
    }

    private void OnTimelineMove(object sender, MouseEventArgs e)
    {
        if (_drawingFrom is not { } from || e.LeftButton != MouseButtonState.Pressed) return;

        double now = e.GetPosition(Timeline).X;
        if (Math.Abs(now - from) < ClickSlop) return;

        _drawing ??= ShowDrawing();

        double left = Math.Min(from, now);

        _drawing.Width = Math.Abs(now - from);
        Canvas.SetLeft(_drawing, left);
        Canvas.SetTop(_drawing, 10);
    }

    private Border ShowDrawing()
    {
        var outline = new Border
        {
            Height = 20,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromArgb(0x40, 0x3D, 0x8B, 0xFD)),
            BorderBrush = (Brush)FindResource("Accent"),
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
        };

        Timeline.Children.Insert(Timeline.Children.IndexOf(Kept) + 1, outline);
        return outline;
    }

    private void OnTimelineUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawingFrom is not { } from) return;

        Timeline.ReleaseMouseCapture();
        _drawingFrom = null;

        if (_drawing is not null)
        {
            Timeline.Children.Remove(_drawing);
            _drawing = null;
        }

        double now = e.GetPosition(Timeline).X;

        // A press that went nowhere is a click, and a click on the timeline seeks.
        if (Math.Abs(now - from) < ClickSlop)
        {
            SeekTo(now);
            return;
        }

        var began = TimeAt(Math.Min(from, now));
        var ended = TimeAt(Math.Max(from, now));

        if (ended - began < MinimumCut) return;

        Pause();
        AddPiece(began, ended);
        MergePieces();

        Seek(began);
        LayoutTimeline();
        UpdateTransport();
    }

    // ---- Saving -----------------------------------------------------------------

    /// <summary>
    /// Writes the chosen span out as a new file. Always a new one: a trim that could
    /// overwrite the recording would make every experiment with the handles a risk.
    ///
    /// The transcode runs asynchronously and reports as it goes, so a long clip leaves
    /// the window usable and says how far along it is instead of appearing to hang.
    /// </summary>
    private async Task SaveAsync()
    {
        if (_exporting || _duration <= TimeSpan.Zero) return;

        var segments = Segments();

        if (segments.Count == 0)
        {
            Flash(Localisation.Get("VideoNothingLeft"));
            return;
        }

        if (KeptLength() < MinimumSpan)
        {
            Flash(Localisation.Get("VideoSpanTooShort"));
            return;
        }

        _exporting = true;
        SaveButton.IsEnabled = false;
        StatusText.Text = Localisation.Format("VideoSaving", 0);

        try
        {
            var source = await StorageFile.GetFileFromPathAsync(_path);

            // The same folder, the same name template and the same collision rule every
            // shot gets, rather than a second set of naming rules for recordings.
            string reserved = ImageIO.ReserveInSaveFolder(".mp4");
            var folder = await StorageFolder.GetFolderFromPathAsync(
                System.IO.Path.GetDirectoryName(reserved)!);
            var destination = await folder.CreateFileAsync(
                System.IO.Path.GetFileName(reserved), CreationCollisionOption.GenerateUniqueName);

            // Read off the recording itself, so the clip comes out as the same kind of
            // file it went in as.
            var profile = await MediaEncodingProfile.CreateFromFileAsync(source);

            // Progress arrives on an encoder thread, so it is marshalled rather than
            // written straight onto the status line.
            void Report(double percent) => Dispatcher.InvokeAsync(
                () => StatusText.Text = Localisation.Format("VideoSaving", (int)percent));

            bool written = segments.Count == 1
                ? await TrimToAsync(source, destination, profile, segments[0], Report)
                : await StitchToAsync(source, destination, profile, segments, Report);

            if (!written)
            {
                // Nothing was written into the file that was made for it, and an empty
                // mp4 left in the save folder would show up in the gallery as a card.
                await destination.DeleteAsync();
                Flash(Localisation.Get("VideoSaveFailed"));
                return;
            }

            _exported = destination.Path;
            Flash(Localisation.Format("VideoSaved", _exported));

            // Straight to the file, because a save you cannot find is half a save. Not if
            // that folder is already on screen, though: saving three trims in a row should
            // not leave three identical windows behind.
            if (Path.GetDirectoryName(_exported) is { } saved && !FolderWindows.IsOpen(saved))
                Reveal();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or ArgumentException
                                      or System.Runtime.InteropServices.COMException)
        {
            Flash(Localisation.Get("VideoSaveFailed"));
        }
        finally
        {
            _exporting = false;
            SaveButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// One span, handed to the same Windows encoder the recorder writes through. It only
    /// has to copy a range, which it does without ever decoding the picture.
    /// </summary>
    private static async Task<bool> TrimToAsync(
        StorageFile source, StorageFile destination, MediaEncodingProfile profile,
        (TimeSpan From, TimeSpan To) span, Action<double> report)
    {
        var transcoder = new MediaTranscoder
        {
            TrimStartTime = span.From,
            TrimStopTime = span.To,
            HardwareAccelerationEnabled = true,
        };

        var prepared = await transcoder.PrepareFileTranscodeAsync(source, destination, profile);
        if (!prepared.CanTranscode) return false;

        var operation = prepared.TranscodeAsync();
        operation.Progress = (_, percent) => report(percent);

        await operation;
        return true;
    }

    /// <summary>
    /// Several spans, joined end to end into one file.
    ///
    /// Each piece is its own clip over the same recording, trimmed to the part being kept;
    /// Windows renders the composition as a single continuous video, re-timing it so the
    /// cuts do not leave gaps. The transcoder above cannot do this — it takes one range
    /// and nothing else — which is the only reason there are two paths here.
    /// </summary>
    private static async Task<bool> StitchToAsync(
        StorageFile source, StorageFile destination, MediaEncodingProfile profile,
        List<(TimeSpan From, TimeSpan To)> segments, Action<double> report)
    {
        var composition = new MediaComposition();

        foreach (var segment in segments)
        {
            // A clip of its own per piece: one clip cannot be in a composition twice, and
            // each carries its own trim.
            var clip = await MediaClip.CreateFromFileAsync(source);

            clip.TrimTimeFromStart = segment.From;
            clip.TrimTimeFromEnd = clip.OriginalDuration - segment.To;

            composition.Clips.Add(clip);
        }

        var operation = composition.RenderToFileAsync(
            destination, MediaTrimmingPreference.Precise, profile);

        operation.Progress = (_, percent) => report(percent);

        return await operation == TranscodeFailureReason.None;
    }

    /// <summary>
    /// The file itself, not a frame of it: a video on the clipboard is a file drop, which
    /// is what a folder window or a chat takes. The trimmed clip once there is one, so
    /// Copy hands over whatever this window last produced.
    /// </summary>
    private void CopyFile()
    {
        try
        {
            var data = new DataObject(DataFormats.FileDrop, new[] { _exported ?? _path });
            Clipboard.SetDataObject(data, copy: true);
            Flash(Localisation.Get("VideoCopied"));
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Flash(Localisation.Get("VideoClipboardBusy"));
        }
    }

    private void Reveal()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_exported ?? _path}\""));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            Flash(Localisation.Get("VideoExplorerFailed"));
        }
    }

    // ---- Status -----------------------------------------------------------------

    private void UpdateTransport()
    {
        // The glyph is the state: one button that says what pressing it will do, rather
        // than a play button that stays a play button while the video runs.
        PlayPauseButton.Tag = FindResource(_playing ? "IconPause" : "IconPlay");
        PlayPauseButton.ToolTip = Localisation.Get(_playing ? "VideoPause" : "VideoPlay");

        // The length quoted is what comes out, so it counts the cuts: a clip trimmed to
        // thirty seconds with ten cut out of the middle is a twenty second clip.
        TrimText.Text = Localisation.Format("VideoTrimSpan",
            Clock(PlayFrom), Clock(PlayTo), Clock(KeptLength()));

        // Every gap between pieces is a cut, which is the word the rest of the window uses.
        int gaps = Math.Max(0, _pieces.Count - 1);

        if (gaps > 0)
            TrimText.Text += "   ·   " + Localisation.Plural(
                "VideoCutCount_One", "VideoCutCount_Many", gaps, gaps);

        UpdateClock();
    }

    private void UpdateClock() =>
        PositionText.Text = Localisation.Format("VideoPosition", Clock(_position), Clock(_duration));

    /// <summary>Minutes and seconds, counted the way the recording bar counts them.</summary>
    private static string Clock(TimeSpan at) => $"{(int)at.TotalMinutes}:{at.Seconds:00}";

    private void Flash(string message)
    {
        StatusText.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (s, _) =>
        {
            ((DispatcherTimer)s!).Stop();
            ShowHint();
        };
        timer.Start();
    }

    /// <summary>
    /// The line that says how to make another piece. It lives where a status message would
    /// be and comes back whenever one clears, because a timeline you can draw on does not
    /// look like one — there is nothing on screen to suggest dragging the empty track.
    /// </summary>
    private void ShowHint() =>
        StatusText.Text = Localisation.Get("VideoHintDrawPiece");

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _ticker.Stop();

        // The popup is a window of its own, so it does not go when this one does.
        PlayOverlay.IsOpen = false;

        // Shut down rather than only paused: the player holds the file open, and a
        // recording you have just trimmed is one you may well want to delete.
        Surface.Dispose();
    }
}
