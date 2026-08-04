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
    public async Task StreamingUsesAccountFreeAffinityAndDeniesAccountRequiredFallback()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["MULTI_PROVIDER_STREAMING_ORDER"] = "apple-download,deezer",
                ["MULTI_PROVIDER_DOWNLOAD_ORDER"] = "apple-download,deezer"
            })
            .Build();
        var clients = new HttpFactory();
        var status = new ProviderStatusManager(
            configuration,
            clients,
            NullLogger<ProviderStatusManager>.Instance,
            Options.Create(new SpotifyApiSettings()),
            Options.Create(new AppleDownloadSettings { BaseUrl = "http://apple-gateway" }),
            Options.Create(new DeezerSettings { Arl = "configured-arl" }),
            Options.Create(new QobuzSettings()),
            Options.Create(new SquidWTFSettings()),
            new SquidWtfEndpointCatalog([], []));
        var apple = new AppleMusicRecordingService();
        var deezer = new DeezerRecordingService();
        var metadata = new Mock<IMusicMetadataService>();
        var service = new MultiProviderDownloadService(
            [apple, deezer],
            [],
            metadata.Object,
            status,
            new OdesliService(
                clients,
                NullLogger<OdesliService>.Instance,
                Mock.Of<IApplicationCache>()),
            NullLogger<MultiProviderDownloadService>.Instance);

        await using var stream = await service.DownloadAndStreamAsync("apple-download", "track-1");

        Assert.Equal(("apple-download", "track-1"), apple.Call);
        metadata.Verify(
            item => item.GetSongAsync(
                It.IsAny<string>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.DownloadAndStreamAsync("deezer", "track-2"));
        Assert.Null(deezer.Call);
    }

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

    private abstract class RecordingService : IConcreteDownloadService
    {
        public (string Provider, string Id)? Call { get; private set; }

        public Task<string> DownloadSongAsync(
            string externalProvider,
            string externalId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> DownloadAndStreamAsync(
            string externalProvider,
            string externalId,
            StreamQuality? qualityOverride = null,
            CancellationToken cancellationToken = default)
        {
            Call = (externalProvider, externalId);
            return Task.FromResult<Stream>(new MemoryStream([1]));
        }

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

    private sealed class AppleMusicRecordingService : RecordingService
    {
    }

    private sealed class DeezerRecordingService : RecordingService
    {
    }
}
