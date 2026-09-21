using System.Globalization;
using System.Windows.Data;
using System.Windows.Markup;

// Every XAML file declares xmlns:loc="clr-namespace:Shotwin.Services" once and then
// reads Text="{loc:S CaptureArea}". An assembly-level XmlnsDefinition would shorten the
// declaration, but WPF's markup compiler does not honour one from the assembly it is
// building — it fails pass one with "the tag S does not exist".

namespace Shotwin.Services;

/// <summary>
/// The XAML side of <see cref="Localisation"/>: <c>Text="{loc:S CaptureArea}"</c>.
///
/// It hands back a binding rather than the string itself. Resolving once would be
/// simpler and would also freeze every label at whatever language the window was built
/// in — binding to the singleton's indexer is what lets Settings change the language
/// and have the open window redraw itself.
///
/// The class is named SExtension because XAML drops the Extension suffix, which is what
/// keeps the usage down to two characters.
/// </summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class SExtension : MarkupExtension
{
    public SExtension()
    {
    }

    public SExtension(string key) => Key = key;

    /// <summary>The name of the entry in Strings.resx.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>
    /// Fills {0} in the resource. Used for tooltips that name a key combination, so
    /// "Ctrl+Z" stays out of the translated string — there is nothing to translate in
    /// it, and a translator who changed it would break the hint.
    /// </summary>
    public string? Arg { get; set; }

    /// <summary>Fills {1}, for the one tooltip that names two combinations.</summary>
    public string? Arg2 { get; set; }

    public override object ProvideValue(IServiceProvider serviceProvider)
    {
        var binding = new Binding($"[{Key}]")
        {
            Source = Localisation.Instance,
            Mode = BindingMode.OneWay,
        };

        if (Arg is not null)
        {
            binding.Converter = ArgumentFormatter.Instance;
            binding.ConverterParameter = Arg2 is null ? new[] { Arg } : new[] { Arg, Arg2 };
        }

        // Let the binding provide the value rather than returning it raw: on a
        // dependency property that yields a live BindingExpression, and inside a setter
        // or a template it yields the binding itself, which is what those expect.
        return binding.ProvideValue(serviceProvider);
    }

    /// <summary>Drops the fixed arguments into the resource the binding just read.</summary>
    private sealed class ArgumentFormatter : IValueConverter
    {
        internal static readonly ArgumentFormatter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is not string format || parameter is not object[] values) return value;

            try
            {
                return string.Format(CultureInfo.CurrentCulture, format, values);
            }
            catch (FormatException)
            {
                return format;
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
            Binding.DoNothing;
    }
}
