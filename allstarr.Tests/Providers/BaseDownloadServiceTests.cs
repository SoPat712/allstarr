using System.Net;
using allstarr.Models.Domain;
using allstarr.Models.Settings;
using allstarr.Services;
using allstarr.Services.Common;
using allstarr.Services.Local;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class BaseDownloadServiceTests
{
    [Fact]
    public async Task FailedDownloadPublishesSafeActivityError()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"allstarr-download-{Guid.NewGuid():N}");
        var local = new Mock<ILocalLibraryService>(MockBehavior.Strict);
        local.Setup(item => item.GetLocalPathForExternalSongAsync("fixture", "track-1"))
            .ReturnsAsync((string?)null);
        var metadata = new Mock<IMusicMetadataService>(MockBehavior.Strict);
        metadata.Setup(item => item.GetSongAsync("fixture", "track-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Song
            {
                ExternalProvider = "fixture",
                ExternalId = "track-1",
                Title = "Track",
                Artist = "Artist"
            });
        var service = new FailingDownloadService(
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Library:DownloadPath"] = directory
            }).Build(),
            local.Object,
            metadata.Object);

        try
        {
            await Assert.ThrowsAsync<HttpRequestException>(() =>
                service.DownloadSongAsync("fixture", "track-1"));

            var activity = Assert.Single(service.GetActiveDownloads());
            Assert.Equal("Provider download request failed with HTTP 502.", activity.ErrorMessage);
            Assert.DoesNotContain("signed-token", activity.ErrorMessage, StringComparison.Ordinal);
            local.VerifyAll();
            metadata.VerifyAll();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class FailingDownloadService(
        IConfiguration configuration,
        ILocalLibraryService localLibrary,
        IMusicMetadataService metadata)
        : BaseDownloadService(
            configuration,
            localLibrary,
            metadata,
            new SubsonicSettings(),
            Mock.Of<IServiceProvider>(),
            NullLogger.Instance)
    {
        protected override string ProviderName => "fixture";

        public override Task<bool> IsAvailableAsync() => Task.FromResult(true);

        protected override Task<string> DownloadTrackAsync(
            string trackId,
            Song song,
            CancellationToken cancellationToken) => Task.FromException<string>(
                new HttpRequestException(
                    "https://provider.invalid/audio?signed-token=secret",
                    null,
                    HttpStatusCode.BadGateway));
    }
}
