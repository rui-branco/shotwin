using System.Windows.Interop;
using static Shotwin.Interop.NativeMethods;

namespace Shotwin.Services;

public enum HotkeyAction
{
    CaptureArea = 1,
    // 2 was window capture, retired: area capture with snapping already grabs a window
    // whole, so the values stay put rather than shuffling under a live registration.
    CaptureFullscreen = 3,
    CaptureText = 4,
    PickColour = 5,
    CaptureScrolling = 6,
    Record = 7,
}

/// <summary>
/// The names people see for these actions. The enum names reached the screen verbatim
/// in the messages about clashes, which meant "CaptureFullscreen" in a sentence that
/// was otherwise English — and untranslatable.
/// </summary>
public static class HotkeyActionNames
{
    public static string Of(HotkeyAction action) => Localisation.Get(action switch
    {
        HotkeyAction.CaptureFullscreen => "ShortcutsCaptureScreen",
        HotkeyAction.CaptureText => "ShortcutsGrabText",
        HotkeyAction.PickColour => "ShortcutsPickColour",
        HotkeyAction.CaptureScrolling => "ShortcutsScrolling",
        HotkeyAction.Record => "RecordShortcutName",
        _ => "ShortcutsCaptureArea",
    });

    /// <summary>The list as it appears inside a sentence.</summary>
    public static string Join(IEnumerable<HotkeyAction> actions) =>
        string.Join(", ", actions.Select(Of));
}

/// <summary>
/// System-wide hotkeys via RegisterHotKey, pumped through a message-only window so
/// the app needs no visible window to listen. Registration fails silently per-hotkey
/// when another app already owns the combination; <see cref="Failed"/> lists those.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly HwndSource _source;
    private readonly List<HotkeyAction> _registered = [];

    public event Action<HotkeyAction>? Pressed;

    public List<HotkeyAction> Failed { get; } = [];

    public HotkeyService()
    {
        var parameters = new HwndSourceParameters("ShotwinHotkeySink")
        {
            Width = 0,
            Height = 0,
            ParentWindow = new IntPtr(-3), // HWND_MESSAGE
        };
        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public void Register(HotkeyAction action, uint modifiers, uint key)
    {
        const uint MOD_NOREPEAT = 0x4000;
        if (RegisterHotKey(_source.Handle, (int)action, modifiers | MOD_NOREPEAT, key))
            _registered.Add(action);
        else
            Failed.Add(action);
    }

    public void RegisterFromSettings()
    {
        UnregisterAll();
        Failed.Clear();

        var s = SettingsService.Current;
        Register(HotkeyAction.CaptureArea, s.AreaHotkeyModifiers, s.AreaHotkeyKey);
        Register(HotkeyAction.CaptureFullscreen, s.FullscreenHotkeyModifiers, s.FullscreenHotkeyKey);
        Register(HotkeyAction.CaptureText, s.TextHotkeyModifiers, s.TextHotkeyKey);
        Register(HotkeyAction.PickColour, s.ColourHotkeyModifiers, s.ColourHotkeyKey);
        Register(HotkeyAction.CaptureScrolling, s.ScrollingHotkeyModifiers, s.ScrollingHotkeyKey);
        Register(HotkeyAction.Record, s.RecordHotkeyModifiers, s.RecordHotkeyKey);
    }

    private void UnregisterAll()
    {
        foreach (var action in _registered)
            UnregisterHotKey(_source.Handle, (int)action);
        _registered.Clear();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            var action = (HotkeyAction)wParam.ToInt32();
            if (Enum.IsDefined(action))
            {
                Pressed?.Invoke(action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.RemoveHook(WndProc);
        _source.Dispose();
    }
}
