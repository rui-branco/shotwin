using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Interop;
using System.Windows.Threading;
using Shotwin.Services;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Capture;

/// <summary>
/// The card that appears when a recording finishes, the way the preview thumbnail
/// appears after a screenshot: a frame from the video, how long it ran, and the things
/// worth doing with it.
///
/// Like the recording strip it owns its own life — a timer closes it, and every action
/// closes it — so nothing outside has to remember, and it cannot be left on screen.
/// </summary>
public sealed class RecordingDonePopover : Window
{
    /// <summary>Long enough to notice and act on, short enough to stay out of the way.</summary>
    private static readonly TimeSpan Linger = TimeSpan.FromSeconds(12);

    private readonly string _path;
    private readonly DispatcherTimer _life = new() { Interval = Linger };

    public event Action<string>? EditRequested;

    public RecordingDonePopover(string path, ImageSource? thumbnail, TimeSpan duration)
    {
        _path = path;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        Background = Brushes.Transparent;
        AllowsTransparency = true;
        FlowDirection = Localisation.FlowDirection;

        var frame = new Border
        {
            Width = 268,
            Height = 150,
            CornerRadius = new CornerRadius(7, 7, 0, 0),
            Background = thumbnail is null
                ? new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x17))

                // Painted as the Border's own brush rather than hosted as a child Image:
                // clipping is rectangular, so a child punches square corners through the
                // top of the card.
                : new ImageBrush(thumbnail) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top },
        };

        var length = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x14, 0x14, 0x17)),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2, 7, 2),
            Margin = new Thickness(0, 0, 8, 8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Child = new TextBlock
            {
                Text = $"{(int)duration.TotalMinutes}:{duration.Seconds:00}",
                Foreground = Brushes.White,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11.5,
            },
        };

        var thumb = new Grid();
        thumb.Children.Add(frame);
        thumb.Children.Add(PlayDisc());
        thumb.Children.Add(length);

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        // Icons alone, exactly like the screenshot preview's row: the names ride in the
        // tooltips, and four labelled buttons would make this card twice as wide as the
        // picture it is showing.
        actions.Children.Add(ActionIcon("\uE70F", Localisation.Get("RecentEdit"), () =>
        {
            EditRequested?.Invoke(_path);
            Dismiss();
        }));

        actions.Children.Add(ActionIcon("\uE8C8", Localisation.Get("RecentCopy"), Copy));
        actions.Children.Add(ActionIcon("\uE838", Localisation.Get("RecentShowInFolder"), Reveal));
        actions.Children.Add(ActionIcon("\uE711", Localisation.Get("PreviewDismiss"), Dismiss));

        var stack = new StackPanel();
        stack.Children.Add(thumb);
        stack.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x26)),
            CornerRadius = new CornerRadius(0, 0, 7, 7),
            Padding = new Thickness(6, 7, 6, 7),
            Child = actions,
        });

        Content = new Border
        {
            Margin = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 26,
                ShadowDepth = 6,
                Direction = 270,
                Opacity = 0.6,
                Color = Colors.Black,
            },
            Child = stack,
        };

        // The card never takes activation either, so its presses are read the same way
        // the recording strip reads its own.
        PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject source) return;

            for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
            {
                if (node is not ButtonBase { Tag: Action act }) continue;

                e.Handled = true;
                act();
                return;
            }
        };

        _life.Tick += (_, _) => Dismiss();
        Loaded += (_, _) =>
        {
            PlaceBottomRight();

            // A second recording started while this is still up must not film it.
            var handle = new WindowInteropHelper(this).Handle;
            if (handle != IntPtr.Zero) SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE);

            _life.Start();
        };
        Closed += (_, _) => _life.Stop();
    }

    /// <summary>
    /// One icon, no label, with the name in its tooltip — the screenshot preview's row.
    /// A bare WPF Button repaints its own chrome on hover, square and pale blue, so the
    /// template is the only way to keep it looking like the rest of the app.
    /// </summary>
    private Button ActionIcon(string glyph, string name, Action click)
    {
        var body = new FrameworkElementFactory(typeof(Border));
        body.Name = "Bd";
        body.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
        body.SetValue(Border.BackgroundProperty, Brushes.Transparent);

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        body.AppendChild(presenter);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = body };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty,
            new SolidColorBrush(Color.FromRgb(0x3D, 0x8B, 0xFD)), "Bd"));
        template.Triggers.Add(hover);

        return new Button
        {
            Width = 34,
            Height = 28,
            Margin = new Thickness(2, 0, 2, 0),
            Template = template,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = name,
            Tag = click,
            Content = new TextBlock
            {
                Text = glyph,

                // Named on its own, never in a comma-separated list: WPF reads the whole
                // string as one family name and finds nothing, which is how icons in this
                // app shipped as tofu twice before.
                FontFamily = new FontFamily("Segoe Fluent Icons"),
                FontSize = 14,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE7, 0xE7, 0xEA)),
            },
        };
    }

    /// <summary>
    /// The play button, over the middle of the picture rather than in the row below —
    /// where a play button belongs on a video, and where the eye already is.
    /// </summary>
    private Button PlayDisc()
    {
        var disc = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        disc.Name = "Disc";
        disc.SetValue(System.Windows.Shapes.Shape.FillProperty,
            new SolidColorBrush(Color.FromArgb(0xB4, 0x14, 0x14, 0x17)));

        var ring = new FrameworkElementFactory(typeof(System.Windows.Shapes.Ellipse));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeProperty,
            new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)));
        ring.SetValue(System.Windows.Shapes.Shape.StrokeThicknessProperty, 1.0);

        // Nudged right of centre, because a triangle's visual weight sits left of its box.
        var triangle = new FrameworkElementFactory(typeof(System.Windows.Shapes.Path));
        triangle.SetValue(System.Windows.Shapes.Shape.FillProperty, Brushes.White);
        triangle.SetValue(System.Windows.Shapes.Shape.StretchProperty, Stretch.Uniform);
        triangle.SetValue(FrameworkElement.WidthProperty, 17.0);
        triangle.SetValue(FrameworkElement.HeightProperty, 17.0);
        triangle.SetValue(FrameworkElement.MarginProperty, new Thickness(4, 0, 0, 0));
        triangle.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        triangle.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        triangle.SetValue(System.Windows.Shapes.Path.DataProperty,
            Geometry.Parse("M0,0 L20,11 L0,22 Z"));

        var stack = new FrameworkElementFactory(typeof(Grid));
        stack.AppendChild(disc);
        stack.AppendChild(ring);
        stack.AppendChild(triangle);

        var template = new ControlTemplate(typeof(Button)) { VisualTree = stack };

        var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(System.Windows.Shapes.Shape.FillProperty,
            new SolidColorBrush(Color.FromArgb(0xE0, 0x14, 0x14, 0x17)), "Disc"));
        template.Triggers.Add(hover);

        return new Button
        {
            Width = 52,
            Height = 52,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Template = template,
            Cursor = System.Windows.Input.Cursors.Hand,
            Focusable = false,
            ToolTip = Localisation.Get("VideoPlay"),
            Tag = (Action)PlayInSystemPlayer,
        };
    }

    /// <summary>
    /// Hands the file to whatever Windows opens .mp4 with, rather than playing it here.
    /// Everyone already has a video player they like, and it will be better than one
    /// bolted into a screenshot tool.
    /// </summary>
    private void PlayInSystemPlayer()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
        }

        Dismiss();
    }

    private void Copy()
    {
        try
        {
            // The file itself, not a frame of it: what anyone wants from a recording is
            // something they can paste into Explorer or a chat.
            Clipboard.SetFileDropList(new System.Collections.Specialized.StringCollection { _path });
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException)
        {
        }

        Dismiss();
    }

    private void Reveal()
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\""));
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
        }

        Dismiss();
    }

    private void Dismiss()
    {
        _life.Stop();
        Close();
    }

    /// <summary>
    /// The corner the recording strip was just in, so the card appears where the eye
    /// already is rather than somewhere it has to go looking.
    /// </summary>
    private void PlaceBottomRight()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Right - ActualWidth;
        Top = area.Bottom - ActualHeight;
    }
}
