using System.Net;
using System.Xml;
using System.Xml.Linq;

namespace TidyWindow.Next.Core;

/// <summary>Searches the Chocolatey community feed directly. Starting <c>choco.exe</c> for every search costs seconds; asking the feed itself takes a fraction of one.</summary>
public interface IChocolateyFeed
{
    Task<IReadOnlyList<Package>> SearchAsync(string query, int limit, CancellationToken token = default);
    /// <summary>Opens the connection ahead of the first search, so that search does not pay for DNS and TLS.</summary>
    Task WarmAsync(CancellationToken token = default);
}

public sealed class ChocolateyFeed : IChocolateyFeed
{
    // The same source the install command uses, so what is found is what can be installed.
    private const string Endpoint = "https://community.chocolatey.org/api/v2/";
    // A search answer is a few hundred KB; anything near this is not one.
    private const int MaxBodyBytes = 4 * 1024 * 1024;
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWindow/" + (typeof(ChocolateyFeed).Assembly.GetName().Version?.ToString(3) ?? "0"));
        client.DefaultRequestHeaders.Accept.ParseAdd("application/atom+xml");
        return client;
    });

    private readonly HttpClient http;
    private readonly TimeProvider time;
    private readonly TimeSpan deadline;
    private DateTimeOffset pausedUntil = DateTimeOffset.MinValue;

    public ChocolateyFeed() : this(null) { }
    /// <param name="deadline">The most time one search may take, from the request to the last byte of the answer. The client's own timeout covers only the headers.</param>
    internal ChocolateyFeed(HttpClient? http, TimeProvider? clock = null, TimeSpan? deadline = null)
    {
        this.http = http ?? Shared.Value;
        time = clock ?? TimeProvider.System;
        this.deadline = deadline ?? TimeSpan.FromSeconds(12);
    }

    public async Task<IReadOnlyList<Package>> SearchAsync(string query, int limit, CancellationToken token = default)
    {
        // A feed that asked for a pause is left alone for that long; the command line answers meanwhile, so the feed is not asked twice per keystroke.
        if (time.GetUtcNow() < pausedUntil) throw new HttpRequestException("The feed asked for a pause after a busy or unavailable answer.");
        // Most downloaded first. The command line lists matches alphabetically, so "chrome" cut at 60 never reached GoogleChrome.
        var address = $"{Endpoint}Search()?$filter=IsLatestVersion&$orderby=DownloadCount%20desc&$skip=0&$top={limit}&searchTerm='{Uri.EscapeDataString(query.Replace("'", "''"))}'&targetFramework=''&includePrerelease=false";
        // One deadline for the whole exchange: the body is read from the network too, and a server that sends headers then stalls must not hold a search forever.
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(token);
        timer.CancelAfter(deadline);
        using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, timer.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
        {
            var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(60);
            pausedUntil = time.GetUtcNow() + (wait > TimeSpan.FromMinutes(10) ? TimeSpan.FromMinutes(10) : wait);
        }
        response.EnsureSuccessStatusCode();
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type is not null && !type.Contains("xml", StringComparison.OrdinalIgnoreCase)) throw new HttpRequestException($"The feed answered with {type} instead of a package list.");
        if (response.Content.Headers.ContentLength > MaxBodyBytes) throw new HttpRequestException("The feed's answer is larger than a search result can be.");
        await using var body = await response.Content.ReadAsStreamAsync(timer.Token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await body.ReadAsync(chunk, timer.Token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBodyBytes) throw new HttpRequestException("The feed's answer is larger than a search result can be.");
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        using var reader = XmlReader.Create(buffer, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4_000_000 });
        return Parse(XDocument.Load(reader), limit);
    }

    public async Task WarmAsync(CancellationToken token = default)
    {
        try { await SearchAsync("a", 1, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or XmlException || ex is OperationCanceledException && !token.IsCancellationRequested) { }
    }

    internal static IReadOnlyList<Package> Parse(XDocument document, int limit)
    {
        var packages = new List<Package>();
        foreach (var entry in document.Descendants(Atom + "entry"))
        {
            // An entry's title is the package id; the friendly name is a separate property.
            var id = entry.Element(Atom + "title")?.Value.Trim() ?? "";
            var properties = entry.Element(Meta + "properties");
            var version = properties?.Element(Data + "Version")?.Value.Trim() ?? "";
            if (!PackageOutputParser.IsSafeId(id) || version.Length == 0) continue;
            var title = properties?.Element(Data + "Title")?.Value.Trim() ?? "";
            packages.Add(new(ManagerKind.Chocolatey, id, title.Length is > 0 and <= 100 && !title.Any(char.IsControl) ? title : id, version, "chocolatey"));
        }
        return packages.DistinctBy(x => x.Key).Take(limit).ToArray();
    }
}
