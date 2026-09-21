using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Interop;
using System.Windows.Threading;
using Shotwin.Services;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// The small strip that says a recording is running, with the two ways to end it.
///
/// Built in code rather than XAML because the last one was a window that would not go
/// away, and the whole design here is about that: it owns a timer that asks, four times
/// a second, whether the recording it belongs to is still going, and closes itself the
/// moment the answer is no. Nothing outside it has to remember to tidy it up, so no
/// early return, no swallowed exception and no crash can leave it on screen.
///
/// It is hidden from screen capture rather than kept out of the way of it, so it can be
/// dragged anywhere — including right over the region being recorded — and still not
/// appear in the video.
/// </summary>
public sealed class RecordingIndicator : Window
{
    private readonly Func<bool> _stillRecording;
    private readonly Func<TimeSpan> _elapsed;
    private readonly SkiaSharp.SKRectI _region;
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    /// <summary>Set once dragged, so nothing repositions it afterwards.</summary>
    private bool _moved;

    private readonly TextBlock _clock;
    private readonly Ellipse _dot;
    private readonly TextBlock _countdown;
    private readonly StackPanel _running;

    public event Action? StopRequested;
    public event Action? CancelRequested;

    public RecordingIndicator(SkiaSharp.SKRectI region, Func<bool> stillRecording, Func<TimeSpan> elapsed)
    {
        _stillRecording = stillRecording;
        _elapsed = elapsed;
        _region = region;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        FlowDirection = Localisation.FlowDirection;

        _dot = new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x3B, 0x30)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 9, 0),
        };

        _clock = new TextBlock
        {
            FontFamily = new FontFamily("Consolas"),
            FontSize = 13,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE7, 0xEA)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 14, 0),
            Text = "0:00",
        };

        _running = new StackPanel { Orientation = Orientation.Horizontal };
        _running.Children.Add(_dot);
        _running.Children.Add(_clock);
        _running.Children.Add(Chip(Localisation.Get("RecordBarStop"), "Esc",
            () => StopRequested?.Invoke()));
        _running.Children.Add(Chip(Localisation.Get("RecordBarCancel"), "Shift+Esc",
            () => CancelRequested?.Invoke(), left: 8));

        // The press is acted on here rather than through Button.Click. This window never
        // takes activation — it must not pull focus off whatever is being recorded — and
        // an unactivated window eats the first click that reaches it, so Click fired only
        // on the second press, which reads as the buttons doing nothing at all.
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;
            if (ButtonUnder(source) is not { Tag: Action act }) return;

            e.Handled = true;
            act();
        };

        _countdown = new TextBlock
        {
            FontSize = 40,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE7, 0xEA)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 54,
            TextAlignment = TextAlignment.Center,
            Typography = { NumeralAlignment = System.Windows.FontNumeralAlignment.Tabular },
            Visibility = Visibility.Collapsed,
        };

        var content = new Grid();
        content.Children.Add(_countdown);
        content.Children.Add(_running);

        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x31, 0x31, 0x3A)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14, 9, 14, 9),
            Child = content,
        };

        _tick.Tick += (_, _) => Update();

        // Anywhere on the strip is a handle. There is no title bar to grab, and being
        // able to move it out of the way matters more here than anywhere else in the app.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState != System.Windows.Input.MouseButtonState.Pressed) return;

            // Not when the press started on a button. DragMove takes the mouse for the
            // whole gesture, so the button never sees its own click and Stop and Cancel
            // did nothing at all.
            if (e.OriginalSource is DependencyObject source && IsInsideButton(source)) return;

            // Only for as long as this strip is up. Remembering it across recordings
            // meant one drag pinned it there for good, and it never came back to the
            // corner it is supposed to start in.
            _moved = true;
            DragMove();
        };

        // Placed again whenever it changes shape — the count-in and the strip are nothing
        // like the same size, and a corner is measured from the window's own edges. Not
        // once it has been dragged, though: that would pull it back out of the user's hand.
        SizeChanged += (_, _) => { if (!_moved) PlaceClearOf(region); };

        Loaded += (_, _) =>
        {
            Reseat();
            HideFromCapture();
            _tick.Start();
        };

        Closed += (_, _) => _tick.Stop();
    }

    /// <summary>The button a press landed on, or null when it landed on the strip itself.</summary>
    private static ButtonBase? ButtonUnder(DependencyObject source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ButtonBase button) return button;

        return null;
    }

    private static bool IsInsideButton(DependencyObject source) => ButtonUnder(source) is not null;

    /// <summary>
    /// Takes the strip out of every screen capture, so wherever it is dragged it cannot
    /// appear in the recording it belongs to.
    /// </summary>
    private void HideFromCapture()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero) SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);
    }

    /// <summary>Shows the count-in in place of the strip, before any recording exists.</summary>
    public void ShowCountIn(int secondsLeft)
    {
        _countdown.Text = secondsLeft.ToString();
        _countdown.Visibility = Visibility.Visible;
        _running.Visibility = Visibility.Collapsed;
        Reseat();
    }

    public void ShowRecording()
    {
        _countdown.Visibility = Visibility.Collapsed;
        _running.Visibility = Visibility.Visible;
        Reseat();
    }

    /// <summary>
    /// Puts the window back in its corner after a change of shape.
    ///
    /// Waiting for SizeChanged was not enough: the window sizes itself to its content, so
    /// the corner has to be measured after the new content has been laid out, and the
    /// count-in is a completely different width from the strip. UpdateLayout forces that
    /// measurement now rather than at some later frame, by which time the count-in has
    /// already been seen in the wrong place.
    /// </summary>
    private void Reseat()
    {
        if (_moved) return;

        UpdateLayout();
        PlaceClearOf(_region);
    }

    private void Update()
    {
        // The one rule that matters: if the recording is over, this window goes, whoever
        // forgot to close it.
        if (!_stillRecording())
        {
            Close();
            return;
        }

        if (_running.Visibility != Visibility.Visible) return;

        var elapsed = _elapsed();
        _clock.Text = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";

        // A slow pulse, so a running recording is obvious without being a distraction.
        _dot.Opacity = elapsed.Milliseconds < 500 ? 1.0 : 0.35;
    }

    /// <summary>
    /// A labelled button with its shortcut printed on it, shaped like the buttons in the
    /// app rather than left as a bare WPF Button.
    ///
    /// Setting only Background on a Button changes nothing about how it behaves: the
    /// default template paints its own chrome over the top, which is why hovering turned
    /// these pale blue and square. A template is the only way to own both.
    /// </summary>
    private Button Chip(string label, string key, Action click, double left = 0)
    {
        var text = new TextBlock
        {
            Text = label,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // Bound to the button's own Foreground so the label follows the hover state.
        text.SetBinding(TextBlock.ForegroundProperty,
            new System.Windows.Data.Binding(nameof(Control.Foreground))
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1),
            });

        var cap = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1B)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = key,
                Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xB2)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 10.5,
            },
        };

        var inside = new StackPanel { Orientation = Orientation.Horizontal };
        inside.Children.Add(text);
        inside.Children.Add(cap);

        var body = new FrameworkElementFactory(typeof(Border));
        body.Name = "Bd";
        body.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
        body.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x35)));
        body.SetValue(Border.PaddingProperty, new Thickness(12, 6, 12, 6));

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        body.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = body };

        // Same darkening the app's own chips use, so these feel like the rest of it.
        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x44)), "Bd"));
        template.Triggers.Add(hover);

        var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
        pressed.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x45, 0x45, 0x52)), "Bd"));
        template.Triggers.Add(pressed);

        return new Button
        {
            Content = inside,
            Template = template,
            Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE7, 0xEA)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new Thickness(left, 0, 0, 0),
            Focusable = false,

            // Carried on the button rather than wired to Click, because the window reads
            // the press itself; see the handler in the constructor for why.
            Tag = click,
        };
    }

    /// <summary>
    /// Bottom right of the monitor being recorded, just above the taskbar — the corner
    /// notifications come from, so it is where the eye already goes and it is the same
    /// place every time rather than moving with the region.
    ///
    /// Against the work area, not the screen: the taskbar moves, hides and is a different
    /// height on every machine, so there is nothing to guess with.
    /// </summary>
    private void PlaceClearOf(SkiaSharp.SKRectI region)
    {
        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1;

        var monitor = Monitors.FromPoint(region.MidX, region.MidY);

        // Inset from the corner on both sides. Hard against the work area's edges puts
        // it flat on the taskbar and flat on the screen edge, which reads as having
        // slipped off rather than as sitting in the corner.
        const double Margin = 14;

        Left = monitor.Work.Right / scale - ActualWidth - Margin;
        Top = monitor.Work.Bottom / scale - ActualHeight - Margin;
    }
}
