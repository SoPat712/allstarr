using System.Net;
using System.Text;
using allstarr.Models.Settings;
using allstarr.Services.AppleMusic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace allstarr.Tests;

public sealed class AppleMusicMetadataServiceTests
{
    [Fact]
    public async Task SearchUsesGatewayLimitAndStableProviderId()
    {
        var handler = new Handler();
        var service = new AppleMusicMetadataService(
            new Factory(new HttpClient(handler)),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-gateway:8000/" }),
            NullLogger<AppleMusicMetadataService>.Instance);

        var songs = await service.SearchSongsAsync("Choosin' Texas", 200);

        var song = Assert.Single(songs);
        Assert.Equal("apple-download", song.ExternalProvider);
        Assert.Equal("ext-apple-download-song-101", song.Id);
        Assert.Contains("limit=100", handler.RequestUri!.Query);
    }

    private sealed class Factory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """[{"id":"101","title":"Choosin' Texas","artist":"Ella Langley","album":"Dandelion","duration":231,"cover_url":"https://example.test/art.jpg"}]""",
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }
}
