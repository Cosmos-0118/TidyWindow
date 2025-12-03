using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using TidyWindow.App.Services;
using Xunit;

namespace TidyWindow.App.Tests;

public sealed class UpdateServiceTests
{
    [Fact]
    public async Task CheckForUpdatesAsync_WhenManifestHasNewerVersion_ReturnsUpdateAvailable()
    {
        using var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new UpdateService(httpClient, "https://example.test/latest.json");

        const string newerVersion = "999.0.0";
        var manifest = $"{{\n  \"version\": \"{newerVersion}\",\n  \"channel\": \"stable\",\n  \"summary\": \"Latest build.\",\n  \"downloadUrl\": \"https://example.test/TidyWindow.exe\",\n  \"releaseNotesUrl\": \"https://example.test/release\",\n  \"publishedAt\": \"2025-12-01T04:30:18Z\",\n  \"installerSizeBytes\": 123456,\n  \"sha256\": \"abc123\"\n}}";
        handler.ResponseFactory = _ => CreateResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest));

        var result = await service.CheckForUpdatesAsync();

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal(newerVersion, result.LatestVersion);
        Assert.Equal("stable", result.Channel);
        Assert.Equal(new Uri("https://example.test/TidyWindow.exe"), result.DownloadUri);
        Assert.Equal(new Uri("https://example.test/release"), result.ReleaseNotesUri);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenManifestMatchesCurrentVersion_ReturnsNoUpdate()
    {
        using var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new UpdateService(httpClient, "https://example.test/latest.json");

        var manifest = $"{{\n  \"version\": \"{service.CurrentVersion}\",\n  \"downloadUrl\": \"https://example.test/TidyWindow.exe\"\n}}";
        handler.ResponseFactory = _ => CreateResponse(HttpStatusCode.OK, Encoding.UTF8.GetBytes(manifest));

        var result = await service.CheckForUpdatesAsync();

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal(service.CurrentVersion, result.LatestVersion);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenManifestMissingVersion_Throws()
    {
        using var handler = new StubHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        using var service = new UpdateService(httpClient, "https://example.test/latest.json");

        handler.ResponseFactory = _ => CreateResponse(HttpStatusCode.OK, "{ }"u8.ToArray());

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.CheckForUpdatesAsync());
    }

    [Fact]
    public async Task CheckForUpdatesAsync_WhenHttpFails_LoadsFallbackManifest()
    {
        using var handler = new StubHttpMessageHandler();
        handler.ResponseFactory = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        using var httpClient = new HttpClient(handler);

        var tempManifest = Path.GetTempFileName();
        var manifestJson = "{\n  \"version\": \"4.5.6\",\n  \"downloadUrl\": \"https://example.test/TidyWindow.exe\"\n}";
        await File.WriteAllTextAsync(tempManifest, manifestJson);

        try
        {
            using var service = new UpdateService(httpClient, "https://example.test/latest.json", tempManifest);
            var result = await service.CheckForUpdatesAsync();

            Assert.Equal("4.5.6", result.LatestVersion);
            Assert.True(result.IsUpdateAvailable);
        }
        finally
        {
            File.Delete(tempManifest);
        }
    }

    private static HttpResponseMessage CreateResponse(HttpStatusCode statusCode, byte[] payload)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new ByteArrayContent(payload)
            {
                Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
            }
        };
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler, IDisposable
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? ResponseFactory { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (ResponseFactory is null)
            {
                throw new InvalidOperationException("ResponseFactory must be set before issuing a request.");
            }

            return Task.FromResult(ResponseFactory(request));
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);
        }
    }
}
