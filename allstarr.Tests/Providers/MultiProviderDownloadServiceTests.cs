using allstarr.Models.Download;
using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.SquidWTF;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;

namespace allstarr.Tests;

public sealed class MultiProviderDownloadServiceTests
{
    [Fact]
    public async Task StreamingPropagatesClientCancellation()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_STREAMING_ORDER"] = "apple-download"
            })
            .Build();
        var clients = new HttpFactory();
        var status = new ProviderStatusManager(
            configuration,
            clients,
            NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-gateway" }),
            Options.Create(new DeezerSettings()),
            Options.Create(new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            new SquidWtfEndpointCatalog([], []));
        var service = new MultiProviderDownloadService(
            [new AppleMusicCancelingService()],
            [],
            Mock.Of<IMusicMetadataService>(),
            status,
            new OdesliService(
                clients,
                NullLogger<OdesliService>.Instance,
                Mock.Of<IApplicationCache>()),
            NullLogger<MultiProviderDownloadService>.Instance);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.DownloadAndStreamAsync(
                "apple-download",
                "track-1",
                cancellationToken: cancellation.Token));
    }

    private sealed class HttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }

    private sealed class AppleMusicCancelingService : IConcreteDownloadService
    {
        public Task<string> DownloadSongAsync(
            string externalProvider,
            string externalId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> DownloadAndStreamAsync(
            string externalProvider,
            string externalId,
            StreamQuality? qualityOverride = null,
            CancellationToken cancellationToken = default) =>
            Task.FromCanceled<Stream>(cancellationToken);

        public void DownloadRemainingAlbumTracksInBackground(
            string externalProvider,
            string albumExternalId,
            string excludeTrackExternalId)
        {
        }

        public DownloadInfo? GetDownloadStatus(string songId) => null;

        public IReadOnlyList<DownloadInfo> GetActiveDownloads() => [];

        public Task<string?> GetLocalPathIfExistsAsync(string externalProvider, string externalId) =>
            Task.FromResult<string?>(null);

        public Task<bool> IsAvailableAsync() => Task.FromResult(true);
    }
}
