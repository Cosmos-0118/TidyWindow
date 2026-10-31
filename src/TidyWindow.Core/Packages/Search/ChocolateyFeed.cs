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
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    private static readonly XNamespace Data = "http://schemas.microsoft.com/ado/2007/08/dataservices";
    private static readonly XNamespace Meta = "http://schemas.microsoft.com/ado/2007/08/dataservices/metadata";
    private static readonly Lazy<HttpClient> Shared = new(() =>
    {
        var client = new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(12) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TidyWindow");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/atom+xml");
        return client;
    });

    private readonly HttpClient http;

    public ChocolateyFeed() : this(null) { }
    internal ChocolateyFeed(HttpClient? http) => this.http = http ?? Shared.Value;

    public async Task<IReadOnlyList<Package>> SearchAsync(string query, int limit, CancellationToken token = default)
    {
        // Most downloaded first. The command line lists matches alphabetically, so "chrome" cut at 60 never reached GoogleChrome.
        var address = $"{Endpoint}Search()?$filter=IsLatestVersion&$orderby=DownloadCount%20desc&$skip=0&$top={limit}&searchTerm='{Uri.EscapeDataString(query.Replace("'", "''"))}'&targetFramework=''&includePrerelease=false";
        using var response = await http.GetAsync(address, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var reader = XmlReader.Create(body, new XmlReaderSettings { Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 16_000_000 });
        var document = await XDocument.LoadAsync(reader, LoadOptions.None, token).ConfigureAwait(false);
        return Parse(document, limit);
    }

    public async Task WarmAsync(CancellationToken token = default)
    {
        try { await SearchAsync("a", 1, token).ConfigureAwait(false); }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or TaskCanceledException) { }
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
