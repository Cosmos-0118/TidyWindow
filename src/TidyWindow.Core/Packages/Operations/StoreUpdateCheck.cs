using System.Text;
using System.Text.Json;

namespace TidyWindow.Next.Core;

/// <summary>A Microsoft Store app as Windows reports it installed: Store-signed, so a sideloaded or developer package is never in this list.</summary>
public sealed record StoreInstalled(string Name, string FamilyName, string Version);

/// <summary>
/// Finds the Microsoft Store updates WinGet cannot see. WinGet's own catalog lags a vendor's release by hours or days, and for a Store app it reports <c>Version: Unknown</c>,
/// so it never lists an update. A Store app that keeps itself current (ChatGPT, Windows Terminal, Photos) shows its own "Update available" while WinGet shows none.
/// This asks Microsoft's public Store catalog what the newest published version is. It only informs: WinGet cannot apply a Store update, so nothing here queues a command.
/// A lookup that fails for any reason says nothing, and never makes the update check fail.
/// </summary>
public sealed class StoreUpdateCheck : IDisposable
{
    private const string Catalog = "https://displaycatalog.mp.microsoft.com/v7.0/";
    private const string Query = "market=US&languages=en-US";
    private const int BatchSize = 20;

    private readonly ICommandRunner runner;
    private readonly HttpClient client;
    private readonly Lock gate = new();
    private readonly Dictionary<string, string?> products = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (DateTimeOffset At, Dictionary<string, Version> Latest)> versions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>How long a published version is reused. Overview-style re-reads happen after every finished job; a release does not change that often.</summary>
    public TimeSpan CacheFor { get; init; } = TimeSpan.FromMinutes(30);
    /// <summary>The whole check is given this long; a slow network must not hold up the list of updates.</summary>
    public TimeSpan Budget { get; init; } = TimeSpan.FromSeconds(25);

    /// <param name="handler">Tests replace the network.</param>
    public StoreUpdateCheck(ICommandRunner runner, HttpMessageHandler? handler = null)
    {
        this.runner = runner;
        client = new(handler ?? new SocketsHttpHandler()) { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWindow");
    }

    /// <summary>Package key → the newer Store version, for the installed apps whose Store listing is ahead of what is installed.</summary>
    public async Task<IReadOnlyDictionary<string, string>> NewerAsync(IReadOnlyList<ManagedPackage> installed, CancellationToken token = default)
    {
        var result = new Dictionary<string, string>();
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
            budget.CancelAfter(Budget);
            var apps = await ReadInstalledAsync(budget.Token).ConfigureAwait(false);
            var matches = Match(installed, apps);
            if (matches.Count == 0) return result;
            var latest = await LatestAsync(matches.Select(x => x.Store.FamilyName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), budget.Token).ConfigureAwait(false);
            foreach (var (app, store) in matches)
                if (latest.TryGetValue(store.FamilyName, out var newest) && IsNewer(store.Version, newest)) result[app.Key] = newest.ToString();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        // Offline, behind a firewall, too slow or an unreadable answer: no answer, not a problem.
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException or FormatException or IOException) { }
        return result;
    }

    /// <summary>
    /// True when the Store's version is a later release of the same line. Only the first three numbers count: the Store reserves the last one for itself, so a
    /// catalog entry that differs only there (1.2.20.0 and 1.2.20.70) is not an update. A different major or minor number is a different line, usually a second
    /// numbering scheme the Store lists for the same app (11.2607.0.0 and 2021.2607.0.0), and is never offered.
    /// </summary>
    public static bool IsNewer(string installed, Version latest) =>
        Version.TryParse(installed, out var current) && current.Major == latest.Major && current.Minor == latest.Minor && latest.Build > current.Build;

    /// <summary>Pairs each listed app with the Store app it is. A Store app WinGet lists by its package name carries it in its id; one WinGet knows by a catalog id (OpenAI.ChatGPT for the package OpenAI.Codex) is paired only when exactly one Store app has the same version and the id's publisher starts its name.</summary>
    public static IReadOnlyList<(ManagedPackage App, StoreInstalled Store)> Match(IReadOnlyList<ManagedPackage> installed, IReadOnlyList<StoreInstalled> stores)
    {
        var matches = new List<(ManagedPackage, StoreInstalled)>();
        foreach (var app in installed)
        {
            if (app.Manager != ManagerKind.Winget || app.Held || app.AvailableVersion is not null) continue;
            StoreInstalled? found = null;
            if (app.Origin == AppOrigin.StoreApp)
            {
                var parts = app.Id[5..].Split('_');
                if (parts.Length >= 5) found = stores.FirstOrDefault(x => x.FamilyName.Equals(parts[0] + "_" + parts[4], StringComparison.OrdinalIgnoreCase));
            }
            else if (app.Origin == AppOrigin.Package && app.Source.Equals("winget", StringComparison.OrdinalIgnoreCase) && app.Id.Contains('.') && app.Version.Length > 0)
            {
                var publisher = app.Id[..app.Id.IndexOf('.')] + ".";
                var candidates = stores.Where(x => x.Version == app.Version && x.Name.StartsWith(publisher, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (candidates.Length == 1) found = candidates[0];
            }
            if (found is not null) matches.Add((app, found));
        }
        return matches;
    }

    private async Task<IReadOnlyList<StoreInstalled>> ReadInstalledAsync(CancellationToken token)
    {
        var script = "[Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false); $ErrorActionPreference='Stop'; try { $items = @(Get-AppxPackage | Where-Object { $_.SignatureKind -eq 'Store' -and -not $_.IsFramework -and -not $_.IsResourcePackage -and -not $_.IsBundle } | " +
            "Select-Object Name,PackageFamilyName,@{n='Version';e={$_.Version.ToString()}}); Write-Output ('TIDYWINDOW_JSON:' + (ConvertTo-Json -InputObject $items -Compress -Depth 3)) } catch { [Console]::Error.WriteLine($_); exit 1 }";
        var command = new CommandSpec(ElevatedProgram.WindowsPowerShell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "RemoteSigned", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(script))], TimeSpan.FromSeconds(30));
        var run = await runner.RunAsync(command, token).ConfigureAwait(false);
        return run.ExitCode == 0 ? ParseInstalled(run.Output) : [];
    }

    public static IReadOnlyList<StoreInstalled> ParseInstalled(string output)
    {
        var line = output.Split('\n').Select(x => x.Trim()).LastOrDefault(x => x.StartsWith("TIDYWINDOW_JSON:", StringComparison.Ordinal));
        if (line is null) return [];
        using var document = JsonDocument.Parse(line["TIDYWINDOW_JSON:".Length..]);
        // PowerShell writes a single object, not a one-item array, when only one package matched.
        var rows = document.RootElement.ValueKind == JsonValueKind.Array ? document.RootElement.EnumerateArray().ToArray() : [document.RootElement];
        var result = new List<StoreInstalled>();
        foreach (var row in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            string Field(string name) => row.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            if (Field("Name") is { Length: > 0 } name && Field("PackageFamilyName") is { Length: > 0 } family && Field("Version") is { Length: > 0 } version) result.Add(new(name, family, version));
        }
        return result;
    }

    private async Task<Dictionary<string, Version>> LatestAsync(IReadOnlyList<string> families, CancellationToken token)
    {
        using var limit = new SemaphoreSlim(6);
        async Task<(string Family, string? Product)> Lookup(string family)
        {
            lock (gate) if (products.TryGetValue(family, out var known)) return (family, known);
            await limit.WaitAsync(token).ConfigureAwait(false);
            try
            {
                using var response = await client.GetAsync($"{Catalog}products/lookup?alternateId=PackageFamilyName&value={Uri.EscapeDataString(family)}&{Query}", token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return (family, null);
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
                var product = document.RootElement.TryGetProperty("Products", out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
                    && list[0].TryGetProperty("ProductId", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
                // Remembered even when empty (a Store app that has since left the Store), so it is not asked about again and again.
                lock (gate) products[family] = product;
                return (family, product);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !token.IsCancellationRequested) { return (family, null); }
            finally { limit.Release(); }
        }

        var found = (await Task.WhenAll(families.Select(Lookup)).ConfigureAwait(false)).Where(x => x.Product is not null).ToArray();
        var wanted = found.Select(x => x.Product!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(x => { lock (gate) return !versions.TryGetValue(x, out var hit) || DateTimeOffset.UtcNow - hit.At >= CacheFor; }).ToArray();
        foreach (var chunk in wanted.Chunk(BatchSize))
        {
            try
            {
                using var response = await client.GetAsync($"{Catalog}products?bigIds={string.Join(',', chunk)}&{Query}&catalogIds=1", token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false), cancellationToken: token).ConfigureAwait(false);
                foreach (var (product, latest) in ParseCatalog(document.RootElement))
                    lock (gate) versions[product] = (DateTimeOffset.UtcNow, latest);
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException or OperationCanceledException && !token.IsCancellationRequested) { }
        }

        var result = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
        foreach (var (family, product) in found)
        {
            Dictionary<string, Version>? latest;
            lock (gate) latest = versions.TryGetValue(product!, out var hit) ? hit.Latest : null;
            if (latest is not null && latest.TryGetValue(family, out var newest)) result[family] = newest;
        }
        return result;
    }

    /// <summary>Product id → newest published version of each package family the product ships. The Store lists a package once per architecture, and old ones too, so the highest wins.</summary>
    public static IEnumerable<(string Product, Dictionary<string, Version> Latest)> ParseCatalog(JsonElement root)
    {
        if (!root.TryGetProperty("Products", out var list) || list.ValueKind != JsonValueKind.Array) yield break;
        foreach (var product in list.EnumerateArray())
        {
            if (!product.TryGetProperty("ProductId", out var id) || id.ValueKind != JsonValueKind.String || !product.TryGetProperty("DisplaySkuAvailabilities", out var skus) || skus.ValueKind != JsonValueKind.Array) continue;
            var latest = new Dictionary<string, Version>(StringComparer.OrdinalIgnoreCase);
            foreach (var sku in skus.EnumerateArray())
            {
                if (!sku.TryGetProperty("Sku", out var s) || !s.TryGetProperty("Properties", out var properties) || !properties.TryGetProperty("Packages", out var packages) || packages.ValueKind != JsonValueKind.Array) continue;
                foreach (var package in packages.EnumerateArray())
                {
                    if (!package.TryGetProperty("PackageFamilyName", out var family) || family.ValueKind != JsonValueKind.String
                        || !package.TryGetProperty("PackageFullName", out var full) || full.ValueKind != JsonValueKind.String) continue;
                    var parts = full.GetString()!.Split('_');
                    if (parts.Length < 2 || !Version.TryParse(parts[1], out var version)) continue;
                    if (!latest.TryGetValue(family.GetString()!, out var best) || version > best) latest[family.GetString()!] = version;
                }
            }
            yield return (id.GetString()!, latest);
        }
    }

    public void Dispose() => client.Dispose();
}
