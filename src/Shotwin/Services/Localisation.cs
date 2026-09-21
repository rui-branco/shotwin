using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Resources;
using System.Windows;

namespace Shotwin.Services;

/// <summary>
/// A window that must keep its own flow direction whatever the language is. The capture
/// overlay is the one: it draws a frozen copy of the desktop at virtual-desktop
/// coordinates, and mirroring that would put the cursor on the wrong pixels.
/// </summary>
public interface IFixedFlowDirection;

/// <summary>
/// Every user-visible string in the app, read out of Resources\Strings.resx and the
/// satellite assemblies built from its siblings.
///
/// A singleton with a string indexer rather than a generated class of properties,
/// because that is what a XAML binding can reach: <c>{loc:S CaptureArea}</c> binds to
/// <c>Instance[CaptureArea]</c>, and raising a change for the indexer re-reads every
/// one of those bindings at once. That is what makes the language switch in Settings
/// take effect without a restart.
///
/// Nothing here, and nothing that calls here, ever names a culture. Which languages
/// exist is discovered by asking the ResourceManager, so shipping a new translation is
/// dropping a Strings.&lt;culture&gt;.resx beside the English one and rebuilding.
/// </summary>
public sealed class Localisation : INotifyPropertyChanged
{
    /// <summary>The name the SDK compiles Resources\Strings.resx under.</summary>
    private const string BaseName = "Shotwin.Resources.Strings";

    private static readonly ResourceManager Resources =
        new(BaseName, typeof(Localisation).Assembly);

    /// <summary>
    /// What Windows was set to before anything in the app touched the thread culture.
    /// Captured in the static initialiser, which runs before the first Apply, so
    /// "follow Windows" can still be honoured after a session spent in another language.
    /// </summary>
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;

    public static Localisation Instance { get; } = new();

    private Localisation()
    {
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The language in force, or null while following Windows. Settings stores the
    /// name; this is the parsed form the UI selects with.
    /// </summary>
    public CultureInfo? Chosen { get; private set; }

    // ---- Reading ----------------------------------------------------------------

    /// <summary>
    /// What a XAML binding reads. "Item[]" is the property name WPF listens for, which
    /// is why <see cref="Apply"/> raises that and not the key.
    /// </summary>
    public string this[string key] => Get(key);

    /// <summary>
    /// The string for a key, in the current language.
    ///
    /// A key missing from a translation falls back to English through the
    /// ResourceManager's own parent chain, and a key missing from English too comes
    /// back as the key itself. A half-translated file must never produce a blank
    /// button or take the app down.
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        try
        {
            return Resources.GetString(key, CultureInfo.CurrentUICulture) ?? key;
        }
        catch (Exception ex) when (ex is MissingManifestResourceException
                                      or MissingSatelliteAssemblyException)
        {
            return key;
        }
    }

    /// <summary>
    /// A string with runtime values dropped into its numbered placeholders. Always this
    /// rather than concatenation: languages put the pieces in a different order, and a
    /// translator can only move a placeholder that is there.
    /// </summary>
    public static string Format(string key, params object?[] values)
    {
        string format = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, format, values);
        }
        catch (FormatException)
        {
            // A translation with a stray brace in it would otherwise crash the window
            // that showed it. The untranslated format is wrong but legible.
            return format;
        }
    }

    /// <summary>
    /// Picks the right form of a counted string for the current language.
    ///
    /// English has two forms and the call sites name both. Several languages have a
    /// third: Slavic ones inflect differently for counts ending 2-4, and Arabic has a
    /// separate form for 3-10. Those translations add a <c>_Few</c> entry beside the
    /// <c>_Many</c> one and it is used automatically; a translation that does not is
    /// unaffected, because a missing <c>_Few</c> falls back to <c>_Many</c>.
    ///
    /// This is a working subset of the CLDR rules, not the whole of them. It covers the
    /// languages actually shipped, and the fallback means getting it wrong for a new one
    /// reads a little clumsily rather than breaking.
    /// </summary>
    public static string Plural(string singularKey, string pluralKey, int count, params object?[] values)
    {
        if (WantsOneForm(CultureInfo.CurrentUICulture, count))
            return Format(singularKey, values);

        if (WantsFewForm(CultureInfo.CurrentUICulture, count)
            && Lookup(FewKey(pluralKey)) is { } few)
        {
            return Format(few, values);
        }

        return Format(pluralKey, values);
    }

    /// <summary>
    /// The _Few sibling of a _Many key. Named by convention rather than passed in, so
    /// adding a third form to a translation needs no change at the call site.
    /// </summary>
    private static string FewKey(string pluralKey) =>
        pluralKey.EndsWith("_Many", StringComparison.Ordinal)
            ? string.Concat(pluralKey.AsSpan(0, pluralKey.Length - 5), "_Few")
            : pluralKey + "_Few";

    /// <summary>The key if the current language actually defines it, otherwise null.</summary>
    private static string? Lookup(string key)
    {
        try
        {
            return Resources.GetString(key, CultureInfo.CurrentUICulture) is { Length: > 0 } ? key : null;
        }
        catch (Exception ex) when (ex is MissingManifestResourceException
                                      or MissingSatelliteAssemblyException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether this count takes the singular form.
    ///
    /// Usually that means exactly one, but not always. Russian and Ukrainian use the
    /// singular for anything ending in 1 except the teens, so 21, 101 and 1001 read
    /// "101 снимок", not "101 снимков" — testing <c>count == 1</c> alone got every such
    /// number wrong. Polish and Czech are deliberately not in that group: they really do
    /// take the plural at 21.
    ///
    /// The East Asian languages have no plural inflection at all, so they never take the
    /// singular; their two forms would otherwise have to be kept identical by hand.
    /// </summary>
    private static bool WantsOneForm(CultureInfo culture, int count) =>
        culture.TwoLetterISOLanguageName switch
        {
            "ja" or "ko" or "zh" or "th" or "vi" or "id" or "ms" => false,

            "ru" or "uk" or "be" or "hr" or "sr" =>
                count % 10 == 1 && count % 100 != 11,

            _ => count == 1,
        };

    private static bool WantsFewForm(CultureInfo culture, int count) =>
        culture.TwoLetterISOLanguageName switch
        {
            // Slavic: 2-4, but not 12-14, and not 22-24 either — it goes by the last
            // digit except in the teens.
            "ru" or "uk" or "pl" or "cs" or "sk" or "hr" or "sr" or "be" =>
                count % 10 is >= 2 and <= 4 && count % 100 is < 12 or > 14,

            "ar" => count % 100 is >= 3 and <= 10,

            // Romanian inserts "de" above 19 — "3 capturi" but "25 de capturi" — so the
            // _Few form is the plain one and _Many carries the "de".
            "ro" => count == 0 || (count % 100 is >= 1 and <= 19 && count != 1),

            _ => false,
        };

    // ---- Languages --------------------------------------------------------------

    private IReadOnlyList<CultureInfo>? _available;

    /// <summary>
    /// The languages this build can actually show, English included.
    ///
    /// Found by asking the ResourceManager which cultures resolve rather than reading a
    /// list someone has to remember to update: a satellite that is present answers, one
    /// that is not returns null. Computed once, on the first look, because the Settings
    /// page is the only thing that asks.
    /// </summary>
    public IReadOnlyList<CultureInfo> Available => _available ??= Discover();

    private static IReadOnlyList<CultureInfo> Discover()
    {
        // A ResourceManager of its own, used for nothing else.
        //
        // Reading a string for en-US falls back through the parent chain to the English
        // baseline and then caches that result under "en-US" — so probing the manager
        // the app reads through reports every culture anyone has already asked for as a
        // language of its own. This one only ever sees the probes below.
        var probe = new ResourceManager(BaseName, typeof(Localisation).Assembly);

        var found = new List<CultureInfo>();

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.AllCultures))
        {
            // The invariant culture resolves to the English baseline, which is already
            // listed under its own name.
            if (culture.Name.Length == 0) continue;

            try
            {
                // tryParents: false, or every culture on the machine would answer with
                // the English fallback and the list would be nine hundred entries long.
                if (probe.GetResourceSet(culture, createIfNotExists: true, tryParents: false) is not null)
                    found.Add(culture);
            }
            catch (Exception ex) when (ex is MissingManifestResourceException
                                          or MissingSatelliteAssemblyException
                                          or BadImageFormatException
                                          or FileNotFoundException
                                          or FileLoadException
                                          or CultureNotFoundException)
            {
                // A culture with no satellite, or one that will not load. Either way it
                // is not a language this build offers.
            }
        }

        found.Sort((a, b) => string.Compare(NativeName(a), NativeName(b), StringComparison.CurrentCulture));
        return found;
    }

    /// <summary>
    /// A language named the way its own speakers write it, which is the only name
    /// somebody who cannot read the current language will recognise. Title-cased with
    /// that language's own rules, because several of them write it lower case.
    /// </summary>
    public static string NativeName(CultureInfo culture) =>
        culture.TextInfo.ToTitleCase(culture.NativeName);

    /// <summary>
    /// Switches language. Null means follow Windows.
    ///
    /// Sets every culture the framework reads a resource through: the ambient one for
    /// this call, the current thread's own, and the default handed to threads started
    /// later — a background decode or an awaited continuation would otherwise report in
    /// whatever Windows is set to.
    /// </summary>
    public void Apply(CultureInfo? culture)
    {
        Chosen = culture;

        var effective = culture ?? SystemCulture;

        CultureInfo.CurrentUICulture = effective;
        Thread.CurrentThread.CurrentUICulture = effective;
        CultureInfo.DefaultThreadCurrentUICulture = effective;

        // Indexer bindings listen for "Item[]", so this is what refreshes every label
        // in every open window.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));

        // Windows opened from here on read the direction in their own constructor; the
        // ones already on screen are turned round here.
        if (Application.Current is { } app)
            foreach (Window window in app.Windows)
                if (window is not IFixedFlowDirection)
                    window.FlowDirection = FlowDirection;
    }

    /// <summary>
    /// Reads the stored setting and applies it. Called once at startup, before any
    /// window exists, so the first thing drawn is already in the right language.
    /// </summary>
    public void ApplyStored()
    {
        string stored = SettingsService.Current.Language;
        Apply(Parse(stored));
    }

    /// <summary>A stored language name back into a culture; empty or unknown means follow Windows.</summary>
    public static CultureInfo? Parse(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        try
        {
            return CultureInfo.GetCultureInfo(name.Trim());
        }
        catch (CultureNotFoundException)
        {
            // A language that was removed from the build, or a hand-edited settings
            // file. Following Windows is the safe answer, not a crash at startup.
            return null;
        }
    }

    // ---- Direction --------------------------------------------------------------

    /// <summary>
    /// Which way the current language runs. Every window sets this on itself, so an
    /// Arabic or Hebrew build mirrors its layout instead of reading right to left
    /// inside a left-to-right frame.
    /// </summary>
    public static FlowDirection FlowDirection =>
        CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;
}
