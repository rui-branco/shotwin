using System.Threading;

namespace Shotwin.Services;

public enum AppCommand
{
    CaptureArea,
    CaptureScreen,

    /// <summary>Area capture that runs OCR and copies the text instead of the image.</summary>
    CaptureText,

    /// <summary>Loupe over the frozen desktop; a click copies the colour under it.</summary>
    PickColour,

    /// <summary>Wheels a region down and stitches the frames into one tall shot.</summary>
    CaptureScrolling,

    /// <summary>
    /// Opens the overlay to pick what to record, then records it to an MP4 until it is
    /// stopped from the bar on screen. Region, window or whole monitor is chosen there,
    /// so one command covers all three.
    /// </summary>
    Record,

    OpenSettings,

    /// <summary>Bring the home window up. What a bare launch means.</summary>
    ShowHome,

    /// <summary>Start into the tray with no window. What the run-at-login entry passes.</summary>
    StayHidden,
}

/// <summary>
/// Lets a second launch drive the running instance: <c>Shotwin.exe --area</c> pokes a
/// named event and exits, and the instance that owns the tray icon does the work.
///
/// This is what makes the app scriptable from a Stream Deck, AutoHotkey, or a shell,
/// without every caller having to fight over the global hotkey registration.
/// </summary>
public sealed class CommandChannel : IDisposable
{
    private static readonly (string Suffix, AppCommand Command)[] Commands =
    [
        ("Area", AppCommand.CaptureArea),
        ("Screen", AppCommand.CaptureScreen),
        ("Text", AppCommand.CaptureText),
        ("Colour", AppCommand.PickColour),
        ("Scrolling", AppCommand.CaptureScrolling),
        ("Record", AppCommand.Record),
        ("Settings", AppCommand.OpenSettings),
        ("Home", AppCommand.ShowHome),
    ];

    private readonly List<EventWaitHandle> _handles = [];
    private readonly List<RegisteredWaitHandle> _registrations = [];

    public event Action<AppCommand>? Requested;

    /// <summary>Called by the owning instance to start listening.</summary>
    public void Listen()
    {
        foreach (var (suffix, command) in Commands)
        {
            var handle = new EventWaitHandle(false, EventResetMode.AutoReset, EventName(suffix));
            _handles.Add(handle);

            var captured = command;
            _registrations.Add(ThreadPool.RegisterWaitForSingleObject(
                handle,
                (_, _) => Requested?.Invoke(captured),
                state: null,
                millisecondsTimeOutInterval: Timeout.Infinite,
                executeOnlyOnce: false));
        }
    }

    /// <summary>
    /// Called by a second launch. Returns false when no instance is listening, so the
    /// caller can fall back to starting up normally instead of silently doing nothing.
    /// </summary>
    public static bool Send(AppCommand command)
    {
        string suffix = Commands.First(c => c.Command == command).Suffix;
        try
        {
            using var handle = EventWaitHandle.OpenExisting(EventName(suffix));
            handle.Set();
            return true;
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
    }

    /// <summary>
    /// A bare launch means ShowHome, never "do nothing" — double-clicking the Start
    /// menu entry and seeing no reaction is indistinguishable from a broken app.
    /// </summary>
    public static AppCommand Parse(string[] args)
    {
        foreach (string arg in args)
        {
            switch (arg.TrimStart('-', '/').ToLowerInvariant())
            {
                case "area": return AppCommand.CaptureArea;
                case "screen":
                case "fullscreen": return AppCommand.CaptureScreen;
                case "text":
                case "ocr": return AppCommand.CaptureText;
                case "color":
                case "colour":
                case "pick": return AppCommand.PickColour;
                case "scrolling":
                case "scroll": return AppCommand.CaptureScrolling;
                case "record": return AppCommand.Record;
                case "settings":
                case "config": return AppCommand.OpenSettings;
                case "background":
                case "silent":
                case "hidden": return AppCommand.StayHidden;
            }
        }
        return AppCommand.ShowHome;
    }

    public static HotkeyAction? ToCaptureAction(AppCommand command) => command switch
    {
        AppCommand.CaptureArea => HotkeyAction.CaptureArea,
        AppCommand.CaptureScreen => HotkeyAction.CaptureFullscreen,
        AppCommand.CaptureText => HotkeyAction.CaptureText,
        AppCommand.PickColour => HotkeyAction.PickColour,
        AppCommand.CaptureScrolling => HotkeyAction.CaptureScrolling,
        AppCommand.Record => HotkeyAction.Record,
        _ => null,
    };

    private static string EventName(string suffix) => $@"Local\Shotwin.Capture.{suffix}";

    public void Dispose()
    {
        foreach (var registration in _registrations)
            registration.Unregister(null);
        foreach (var handle in _handles)
            handle.Dispose();
    }
}
