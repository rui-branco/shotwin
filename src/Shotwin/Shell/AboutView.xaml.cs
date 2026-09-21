using System.Reflection;
using System.Windows.Controls;
using Shotwin.Services;

namespace Shotwin.Shell;

public partial class AboutView : UserControl
{
    /// <summary>Raised by "Check again". The shell owns the check and feeds the answer back.</summary>
    public event Action? CheckRequested;

    /// <summary>What the check last found, so a language change can restate it.</summary>
    private ReleaseInfo? _release;

    public AboutView()
    {
        InitializeComponent();

        CheckAgainButton.Click += (_, _) => CheckRequested?.Invoke();

        Refresh();
    }

    /// <summary>Rewrites the two lines this page composes itself, in the current language.</summary>
    public void Refresh()
    {
        string version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "1.0.0";

        // Strip the +commit suffix the SDK appends to informational versions.
        int plus = version.IndexOf('+');
        if (plus > 0) version = version[..plus];

        VersionLine.Text = Localisation.Format("AboutVersionLine", version);

        ShowUpdate(_release);
    }

    /// <summary>A check asked for by hand should say it is running; the startup one never does.</summary>
    public void ShowChecking()
    {
        CheckAgainButton.IsEnabled = false;
        UpdateLine.Text = Localisation.Get("AboutChecking");
    }

    public void ShowUpdate(ReleaseInfo? release)
    {
        _release = release;
        CheckAgainButton.IsEnabled = true;

        UpdateLine.Text = release is null
            ? Localisation.Get("AboutUpToDate")
            : Localisation.Format("AboutUpdateAvailable", release.Version.ToString(3));
    }
}
