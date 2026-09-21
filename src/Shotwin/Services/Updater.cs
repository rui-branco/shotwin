using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Shotwin.Services;

/// <summary>A release newer than the running build, and the asset that carries it.</summary>
public sealed record ReleaseInfo(string Tag, Version Version, string DownloadUrl, long Size);

/// <summary>
/// Self-update against the project's GitHub releases.
///
/// The repository is pinned here on purpose: an updater that can be pointed somewhere
/// else by a config file is a way to make Shotwin run someone else's code.
/// </summary>
public static class Updater
{
    private const string Owner = "rui-branco";
    private const string Repo = "shotwin";
    private const string AssetName = "Shotwin.exe";

    private const string LatestApi = $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    /// <summary>Anything smaller than this is an error page, not a 74 MB single-file exe.</summary>
    private const long SmallestPlausibleBuild = 1024 * 1024;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Deliberately without a client-wide timeout, because HttpClient.Timeout covers
    /// reading the response body too: a fifteen-second ceiling would abort the download
    /// of a 74 MB exe part way through on any ordinary connection. The check gets its
    /// fifteen seconds from its own token instead.
    /// </summary>
    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    static Updater()
    {
        // GitHub refuses requests without a User-Agent, and the versioned media type is
        // what keeps the response shape from drifting under us.
        Http.DefaultRequestHeaders.Add("User-Agent", "Shotwin-Updater");
        Http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
    }

    /// <summary>The running build, from the version the csproj stamps into the assembly.</summary>
    public static Version Current
    {
        get
        {
            var assembly = Assembly.GetEntryAssembly();

            string? informational = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // The SDK appends +commit to the informational version, which Version cannot parse.
            int plus = informational?.IndexOf('+') ?? -1;
            if (plus > 0) informational = informational![..plus];

            return Version.TryParse(informational, out var stamped)
                ? Normalise(stamped)
                : Normalise(assembly?.GetName().Version);
        }
    }

    /// <summary>
    /// The exe that is running. Assembly.Location is an EMPTY STRING in a single-file
    /// publish, which is exactly how Shotwin ships (see install.ps1), so the path can
    /// only come from the process — anything else quietly points the update at "".
    /// </summary>
    private static string ExecutablePath => Environment.ProcessPath ?? string.Empty;

    /// <summary>
    /// A tag reads "v1.2" while an assembly version is always four parts, and Version
    /// treats a missing component as lower than zero. Pad both out before comparing.
    /// </summary>
    private static Version Normalise(Version? version) =>
        version is null
            ? new Version(0, 0, 0, 0)
            : new Version(version.Major, version.Minor,
                Math.Max(version.Build, 0), Math.Max(version.Revision, 0));

    private static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;

        string text = tag.Trim();
        if (text.StartsWith("v", StringComparison.OrdinalIgnoreCase)) text = text[1..];

        return Version.TryParse(text, out var parsed) ? Normalise(parsed) : null;
    }

    /// <summary>
    /// The newest release when it is newer than this build, otherwise null.
    ///
    /// Offline, rate-limited, renamed repo, a 404 because nothing has been published
    /// yet: none of it is worth a dialog, so every failure reads as "no update".
    /// </summary>
    public static async Task<ReleaseInfo?> CheckAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(CheckTimeout);

            await using var stream = await Http.GetStreamAsync(LatestApi, timeout.Token);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);

            var root = document.RootElement;
            if (Flag(root, "draft") || Flag(root, "prerelease")) return null;

            string? tag = Text(root, "tag_name");
            var version = ParseTag(tag);
            if (version is null || version <= Current) return null;

            // A release with no exe on it is nothing to install.
            if (Asset(root) is not { } asset) return null;

            return new ReleaseInfo(tag!, version, asset.Url, asset.Size);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                      or JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static (string Url, long Size)? Asset(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(Text(asset, "name"), AssetName, StringComparison.OrdinalIgnoreCase))
                continue;

            string? url = Text(asset, "browser_download_url");
            if (url is null || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                continue;

            long size = asset.TryGetProperty("size", out var bytes) && bytes.TryGetInt64(out long value)
                ? value
                : 0;

            return (url, size);
        }
        return null;
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Fetches the new build to "&lt;exe&gt;.new" and reports how far along it is.
    /// False means nothing usable was written, and nothing is left behind.
    /// </summary>
    public static async Task<bool> DownloadAsync(
        ReleaseInfo release, IProgress<double>? progress, CancellationToken token)
    {
        string exe = ExecutablePath;
        if (exe.Length == 0)
        {
            CrashLog.Note("Update", "No path for the running exe, so there is nothing to replace.");
            return false;
        }

        string staged = exe + ".new";
        try
        {
            using var response = await Http.GetAsync(
                release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? release.Size;

            // Straight to disk: the exe is around 74 MB, and buffering it first would be
            // 74 MB of managed heap for bytes that are on their way to a file anyway.
            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = new FileStream(staged, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] buffer = new byte[81920];
                long written = 0;

                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    written += read;
                    if (total > 0) progress?.Report((double)written / total);
                }
            }

            if (IsProgram(staged)) return true;

            CrashLog.Note("Update",
                $"Downloaded {new FileInfo(staged).Length} bytes from {release.DownloadUrl}, "
                + "and it is not a program.");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException
                                      or IOException or UnauthorizedAccessException
                                      or InvalidOperationException)
        {
            // Logged rather than swallowed. Every one of these paths ends with the button
            // quietly going back to offering the update, which from the outside is
            // indistinguishable from the click not registering at all — and with nothing
            // written down, the only way to find out which of them ran was to rebuild the
            // download by hand outside the app.
            CrashLog.Write("Update", ex);
        }

        // Half a file, or a redirect that landed on an HTML page, must never be left
        // where Apply would pick it up and install it.
        Delete(staged);
        return false;
    }

    /// <summary>A download is only a program if it is big enough to be one and says MZ.</summary>
    private static bool IsProgram(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length < SmallestPlausibleBuild) return false;

        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 'M' && stream.ReadByte() == 'Z';
    }

    /// <summary>
    /// Swaps the downloaded build in and restarts.
    ///
    /// Windows will not let a running exe be overwritten, but it will let it be renamed
    /// out of the way — so the old build steps aside and is swept up on the next start.
    /// </summary>
    public static void Apply()
    {
        string exe = ExecutablePath;
        string staged = exe + ".new";
        string old = exe + ".old";

        Delete(old);

        File.Move(exe, old);
        try
        {
            File.Move(staged, exe);
        }
        catch
        {
            // Put the working build back rather than leaving nothing to launch.
            try { File.Move(old, exe); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }

        // The swap is done by this point, so a launch that fails is not worth undoing: the
        // new build is the one on disk and the next start is already it. Caught because
        // Process.Start reports a refused launch as a Win32Exception, which is not one of
        // the file exceptions the caller expects — it went to the dispatcher instead, and
        // closed the app on the one path where the update had actually worked.
        try
        {
            Process.Start(exe);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            CrashLog.Write("Update", ex);
        }

        Application.Current.Shutdown();
    }

    /// <summary>Clears the build an update stepped aside. Called at startup.</summary>
    public static void CleanupOldBuild()
    {
        string exe = ExecutablePath;
        if (exe.Length == 0) return;

        Delete(exe + ".old");
        Delete(exe + ".new");
    }

    private static void Delete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
