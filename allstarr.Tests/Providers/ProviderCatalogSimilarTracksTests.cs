using allstarr.Core.Capabilities;
using allstarr.Core.Providers;
using allstarr.Models.Domain;
using allstarr.Models.Subsonic;

namespace allstarr.Tests;

public sealed class ProviderCatalogSimilarTracksTests
{
    [Fact]
    public async Task LegacyCatalogWithoutRecommendations_ReportsCapabilityUnavailable()
    {
        var catalog = new SimilarCatalog(similar: null);

        var outcome = await catalog.GetSimilarTracksAsync(Context(), Request("42", 5));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, outcome.Error!.Kind);
        Assert.Equal([("42", 5)], catalog.Calls);
    }

    [Fact]
    public async Task SupportedCatalog_ReturnsMappedRecommendationsInOrder()
    {
        var catalog = new SimilarCatalog(
        [
            new Song
            {
                Id = "ext-deezer-song-43",
                ExternalId = "43",
                ExternalProvider = "deezer",
                Title = "First Recommendation",
                Artist = "Related Artist",
                Artists = ["Related Artist"],
                ArtistIds = ["77"],
                Album = "Related Album",
                AlbumId = "ext-deezer-album-88",
                Duration = 200
            },
            new Song
            {
                Id = "ext-deezer-song-44",
                ExternalId = "44",
                ExternalProvider = "deezer",
                Title = "Second Recommendation",
                Artist = "Seed Artist",
                Artists = ["Seed Artist"]
            }
        ]);

        var outcome = await catalog.GetSimilarTracksAsync(Context(), Request("42", 2));

        Assert.True(outcome.IsSuccess);
        var page = outcome.RequireValue();
        Assert.Equal("deezer", page.ProviderId);
        Assert.Equal(["43", "44"], page.Items.Select(track => track.Id.Value));
        Assert.All(page.Items, track => Assert.Equal(ProviderResourceKind.Track, track.Id.ResourceKind));
        Assert.Equal("77", page.Items[0].Artists[0].ArtistId!.Value);
        Assert.Equal("88", page.Items[0].AlbumId!.Value);
        Assert.Equal(TimeSpan.FromSeconds(200), page.Items[0].Duration);
        Assert.Equal([("42", 2)], catalog.Calls);
    }

    [Fact]
    public async Task SupportedCatalogWithNoRecommendations_IsAnEmptySuccess()
    {
        var catalog = new SimilarCatalog([]);

        var outcome = await catalog.GetSimilarTracksAsync(Context(), Request("42", 5));

        Assert.True(outcome.IsSuccess);
        Assert.Empty(outcome.RequireValue().Items);
    }

    [Fact]
    public async Task ProviderFailure_IsATypedOutcome()
    {
        var catalog = new SimilarCatalog([], new HttpRequestException("provider down"));

        var outcome = await catalog.GetSimilarTracksAsync(Context(), Request("42", 5));

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.TransientFailure, outcome.Error!.Kind);
    }

    [Fact]
    public async Task ForeignSeed_IsRejectedBeforeTheCatalogIsAsked()
    {
        var catalog = new SimilarCatalog([]);

        await Assert.ThrowsAsync<ArgumentException>(() => catalog.GetSimilarTracksAsync(
            Context(),
            new ProviderSimilarTracksRequest(new ProviderExternalResourceId("qobuz", ProviderResourceKind.Track, "42"), 5)));
        Assert.Empty(catalog.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(201)]
    public void Request_RejectsLimitsOutsideTheContract(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Request("42", limit));

    private static ProviderSimilarTracksRequest Request(string seed, int limit) =>
        new(new ProviderExternalResourceId("deezer", ProviderResourceKind.Track, seed), limit);

    private static ProviderExecutionContext Context() => new(
        new ProviderActorContext(
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "fixture", "fixture-user")),
        "deezer",
        account: null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: false,
            allowManagedDownloads: false,
            allowedProviderIds: ["deezer"]),
        operationId: "metadata-similar-tracks",
        correlationId: "metadata-similar-tracks-fixture",
        deadline: DateTimeOffset.UtcNow.AddMinutes(1),
        cancellationToken: CancellationToken.None);

    private sealed class SimilarCatalog(List<Song>? similar, Exception? failure = null) : ProviderCatalogMetadata("deezer")
    {
        public List<(string ExternalId, int Limit)> Calls { get; } = [];

        public override Task<List<Song>?> GetSimilarTracksAsync(
            string externalId, int limit, CancellationToken cancellationToken = default)
        {
            Calls.Add((externalId, limit));
            if (failure != null) throw failure;
            return similar == null ? base.GetSimilarTracksAsync(externalId, limit, cancellationToken) : Task.FromResult<List<Song>?>(similar);
        }

        public override Task<List<Song>> SearchSongsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<List<Album>> SearchAlbumsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<List<Artist>> SearchArtistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit = 20, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public override Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
