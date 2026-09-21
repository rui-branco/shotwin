using System.Text;
using System.Windows.Input;
using WpfKey = System.Windows.Input.Key;

namespace Shotwin.Services;

/// <summary>
/// One hotkey combination, in the RegisterHotKey vocabulary (Win32 modifier flags plus
/// a virtual-key code) rather than WPF's, because that is what actually gets registered.
/// </summary>
/// <remarks>
/// The <c>Key</c> property shadows <see cref="System.Windows.Input.Key"/> inside this
/// type, hence the WpfKey alias everywhere below.
/// </remarks>
public readonly record struct HotkeyBinding(uint Modifiers, uint Key)
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;

    public bool IsValid => Key != 0 && Modifiers != 0;

    /// <summary>
    /// Builds a binding from a live key press, or null if this press is not yet a
    /// usable combination (a bare modifier, or a key with no modifier at all).
    /// </summary>
    public static HotkeyBinding? FromKeyEvent(KeyEventArgs e)
    {
        var pressed = e.Key == WpfKey.System ? e.SystemKey : e.Key;

        if (pressed is WpfKey.LeftCtrl or WpfKey.RightCtrl
                    or WpfKey.LeftShift or WpfKey.RightShift
                    or WpfKey.LeftAlt or WpfKey.RightAlt
                    or WpfKey.LWin or WpfKey.RWin)
        {
            return null;
        }

        uint modifiers = 0;
        var wpf = Keyboard.Modifiers;
        if ((wpf & ModifierKeys.Control) != 0) modifiers |= MOD_CONTROL;
        if ((wpf & ModifierKeys.Shift) != 0) modifiers |= MOD_SHIFT;
        if ((wpf & ModifierKeys.Alt) != 0) modifiers |= MOD_ALT;
        if ((wpf & ModifierKeys.Windows) != 0) modifiers |= MOD_WIN;

        // PrintScreen is the one key Windows users expect to work on its own.
        if (modifiers == 0 && pressed != WpfKey.PrintScreen) return null;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(pressed);
        return vk == 0 ? null : new HotkeyBinding(modifiers, vk);
    }

    public override string ToString()
    {
        // The key names themselves are not translated: Ctrl and Shift are printed on
        // the keyboard in front of the user whatever language the app is in.
        if (Key == 0) return Localisation.Get("ShortcutsNotSet");

        var text = new StringBuilder();
        if ((Modifiers & MOD_CONTROL) != 0) text.Append("Ctrl + ");
        if ((Modifiers & MOD_SHIFT) != 0) text.Append("Shift + ");
        if ((Modifiers & MOD_ALT) != 0) text.Append("Alt + ");
        if ((Modifiers & MOD_WIN) != 0) text.Append("Win + ");

        WpfKey named = KeyInterop.KeyFromVirtualKey((int)Key);
        text.Append(named switch
        {
            >= WpfKey.D0 and <= WpfKey.D9 => ((char)('0' + (named - WpfKey.D0))).ToString(),
            >= WpfKey.NumPad0 and <= WpfKey.NumPad9 => $"Num {(char)('0' + (named - WpfKey.NumPad0))}",
            WpfKey.PrintScreen => "Print Screen",
            WpfKey.OemComma => ",",
            WpfKey.OemPeriod => ".",
            WpfKey.OemMinus => "-",
            WpfKey.OemPlus => "+",
            _ => named.ToString(),
        });

        return text.ToString();
    }
}
