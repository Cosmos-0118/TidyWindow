using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TidyWindow.Next.Core;

/// <summary>
/// Finds TidyWindow 5 releases on GitHub and downloads the installer or the portable ZIP.
/// v4 releases are ignored: that app is a different product, and its installer must not replace this one.
/// </summary>
public sealed class AppUpdateService
{
    internal const string ReleasesUrl = "https://api.github.com/repos/Cosmos-0118/TidyWindow/releases?per_page=10";
    private const long MaxDownloadBytes = 1024L * 1024 * 1024;
    private static readonly Regex Heading = new(@"^\s{0,3}#{1,6}\s+(.*?)\s*#*\s*$", RegexOptions.Compiled);
    private static readonly Regex Bullet = new(@"^\s*[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex Rule = new(@"^\s*([-*_])\1{2,}\s*$", RegexOptions.Compiled);
    private static readonly Regex Image = new(@"!\[([^\]]*)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]]+)\]\([^)]+\)", RegexOptions.Compiled);
    private static readonly Regex Bold = new(@"(\*\*|__)(.*?)\1", RegexOptions.Compiled);
    private static readonly Lazy<HttpClient> Shared = new(CreateClient);

    private readonly HttpClient http;
    private readonly Func<bool> isInstalledBuild;
    private readonly string updatesFolder;

    public AppUpdateService(HttpClient? http = null, Version? currentVersion = null, Func<bool>? isInstalledBuild = null, string? updatesFolder = null)
    {
        this.http = http ?? Shared.Value;
        CurrentVersion = Normalize(currentVersion ?? ReadCurrentVersion());
        this.isInstalledBuild = isInstalledBuild ?? DefaultInstalled;
        this.updatesFolder = updatesFolder ?? Path.Combine(Path.GetTempPath(), "TidyWindow.Next", "Updates");
    }

    public Version CurrentVersion { get; }

    public static Version ReadCurrentVersion() => Normalize(Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0));

    /// <summary>True when this copy was installed with Setup. A portable folder does not contain Inno Setup's uninstaller.</summary>
    public bool IsInstalledBuild() => isInstalledBuild();

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var request = ApiRequest(ReleasesUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Evaluate(ParseReleases(json));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("GitHub took too long to answer.");
        }
    }

    public UpdateCheckResult Evaluate(IReadOnlyList<AppRelease> releases)
    {
        var recent = releases.OrderByDescending(x => x.Version).ThenByDescending(x => x.PublishedAt).Take(10).ToArray();
        var latest = recent.FirstOrDefault();
        return new(CurrentVersion, isInstalledBuild(), latest, recent, latest is not null && latest.Version > CurrentVersion);
    }

    public static IReadOnlyList<AppRelease> ParseReleases(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("GitHub returned something that was not a list of releases.");
        var releases = new List<AppRelease>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (Flag(item, "draft") || Flag(item, "prerelease")) continue;
            var tag = Text(item, "tag_name");
            if (!TryParseTag(tag, out var version) || version.Major < 5) continue;
            var body = Text(item, "body");
            var title = Text(item, "name");
            releases.Add(new(tag, version, title.Length == 0 ? tag : title, body, CleanMarkdownNotes(body), Published(item), Text(item, "html_url"),
                Asset(item, version, installer: true), Asset(item, version, installer: false)));
        }
        return releases.OrderByDescending(x => x.Version).ThenByDescending(x => x.PublishedAt).ToArray();
    }

    /// <summary>Turns GitHub markdown into lines a plain text block can show: headings, bullets and links, without the markup.</summary>
    public static string CleanMarkdownNotes(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return "";
        var output = new List<string>();
        var code = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.TrimStart().StartsWith("```", StringComparison.Ordinal)) { code = !code; continue; }
            if (code) { output.Add(line); continue; }
            if (line.Trim().Length == 0)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add("");
                continue;
            }
            if (Rule.IsMatch(line)) continue;
            var heading = Heading.Match(line);
            if (heading.Success)
            {
                if (output.Count > 0 && output[^1].Length > 0) output.Add("");
                output.Add(CleanInline(heading.Groups[1].Value));
                continue;
            }
            var bullet = Bullet.Match(line);
            output.Add(bullet.Success ? "• " + CleanInline(bullet.Groups[1].Value) : CleanInline(line));
        }
        return string.Join("\n", output).Trim();
    }

    public Task<string> DownloadInstallerAsync(AppReleaseAsset asset, IProgress<DownloadProgress>? progress, CancellationToken token = default)
        => DownloadAsync(asset, progress, token, PayloadKind.Exe);

    public Task<string> DownloadPortableAsync(AppReleaseAsset asset, IProgress<DownloadProgress>? progress, CancellationToken token = default)
        => DownloadAsync(asset, progress, token, PayloadKind.Zip);

    /// <summary>Starts the installer. Setup closes this app itself when it needs to replace the files.</summary>
    public static void LaunchInstaller(string installerPath)
    {
        if (!File.Exists(installerPath)) throw new FileNotFoundException("The installer is no longer there.", installerPath);
        Process.Start(new ProcessStartInfo(installerPath) { UseShellExecute = true });
    }

    private async Task<string> DownloadAsync(AppReleaseAsset asset, IProgress<DownloadProgress>? progress, CancellationToken token, PayloadKind kind)
    {
        token.ThrowIfCancellationRequested();
        var name = SafeFileName(asset.Name);
        if (kind == PayloadKind.Exe && !IsInstallerFile(name)) throw new InvalidOperationException("The update is not a TidyWindow installer.");
        if (kind == PayloadKind.Zip && !IsPortableFile(name)) throw new InvalidOperationException("The update is not a TidyWindow portable archive.");
        RequireGitHubHttps(asset.DownloadUrl);
        if (asset.SizeBytes > MaxDownloadBytes) throw new IOException("The download is larger than TidyWindow will store.");
        Directory.CreateDirectory(updatesFolder);
        var part = Path.Combine(updatesFolder, name + ".part");
        var finalPath = Path.Combine(updatesFolder, name);
        try
        {
            using var request = DownloadRequest(asset.DownloadUrl);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var total = asset.SizeBytes > 0 ? asset.SizeBytes : response.Content.Headers.ContentLength;
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                var buffer = new byte[81920];
                long received = 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    received += read;
                    if (received > MaxDownloadBytes) throw new IOException("The download is larger than TidyWindow will store.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    int? percent = total is > 0 ? (int)Math.Min(100, received * 100 / total.Value) : null;
                    progress?.Report(new DownloadProgress(received, total, percent));
                }
            }
            var length = new FileInfo(part).Length;
            if (asset.SizeBytes > 0 && length != asset.SizeBytes)
                throw new IOException($"The download is {length} bytes, but the release lists {asset.SizeBytes}.");
            var header = new byte[2];
            int got;
            await using (var check = File.OpenRead(part))
                got = await check.ReadAsync(header, token).ConfigureAwait(false);
            if (!HeaderOk(kind, header.AsSpan(0, got)))
                throw new InvalidDataException(kind == PayloadKind.Exe ? "The download is not a Windows installer." : "The download is not a ZIP archive.");
            File.Move(part, finalPath, true);
            return finalPath;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        AutomaticDecompression = DecompressionMethods.All
    })
    { Timeout = Timeout.InfiniteTimeSpan };

    private static HttpRequestMessage ApiRequest(string url)
    {
        var request = DownloadRequest(url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return request;
    }

    private static HttpRequestMessage DownloadRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("TidyWindow");
        return request;
    }

    private static AppReleaseAsset? Asset(JsonElement release, Version version, bool installer)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = Text(asset, "name");
            var url = Text(asset, "browser_download_url");
            if (name.Length == 0 || url.Length == 0) continue;
            var match = installer ? IsInstaller(name, version) : IsPortable(name, version);
            if (!match) continue;
            long size = 0;
            if (asset.TryGetProperty("size", out var sizeValue) && sizeValue.TryGetInt64(out var bytes) && bytes > 0) size = bytes;
            return new(name, url, size);
        }
        return null;
    }

    internal static bool IsInstaller(string name, Version version) =>
        name.Equals($"TidyWindow-Setup-{version}.exe", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPortable(string name, Version version) =>
        name.Equals($"TidyWindow-{version}-win-x64.zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsInstallerFile(string name) =>
        name.StartsWith("TidyWindow-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsPortableFile(string name) =>
        name.StartsWith("TidyWindow-", StringComparison.OrdinalIgnoreCase) && name.EndsWith("-win-x64.zip", StringComparison.OrdinalIgnoreCase);

    private static bool TryParseTag(string tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (tag.Length < 6 || tag[0] != 'v' || !Regex.IsMatch(tag[1..], @"^\d+\.\d+\.\d+$")) return false;
        if (!Version.TryParse(tag[1..], out var parsed) || parsed is null) return false;
        version = Normalize(parsed);
        return true;
    }

    private static string CleanInline(string text)
    {
        text = Image.Replace(text, "$1");
        text = Link.Replace(text, "$1");
        text = Bold.Replace(text, "$2");
        return text.Replace("`", "").Trim();
    }

    private static string Text(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static bool Flag(JsonElement item, string name) =>
        item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset Published(JsonElement item) =>
        DateTimeOffset.TryParse(Text(item, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : default;

    private static Version Normalize(Version version) => new(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));

    private static bool DefaultInstalled() => File.Exists(Path.Combine(AppContext.BaseDirectory, "unins000.exe"));

    private static void RequireGitHubHttps(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("The download address is not a secure link.");
        var host = uri.IdnHost;
        var allowed = host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!allowed) throw new InvalidOperationException("The download address is not from GitHub.");
    }

    private static string SafeFileName(string name)
    {
        if (name.Length == 0 || name != Path.GetFileName(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("The download name is not safe.");
        return name;
    }

    private static bool HeaderOk(PayloadKind kind, ReadOnlySpan<byte> header) => kind switch
    {
        PayloadKind.Exe => header.Length >= 2 && header[0] == (byte)'M' && header[1] == (byte)'Z',
        _ => header.Length >= 2 && header[0] == (byte)'P' && header[1] == (byte)'K'
    };

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private enum PayloadKind { Exe, Zip }
}
