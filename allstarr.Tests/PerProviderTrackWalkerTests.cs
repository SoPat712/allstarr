using allstarr.Models.Domain;
using allstarr.Models.Search;
using allstarr.Models.Subsonic;
using allstarr.Services;
using allstarr.Services.Spotify;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class PerProviderTrackWalkerTests
{
    [Fact]
    public async Task LocalShortCircuit_ReturnsLocalMatchWithoutWalking()
    {
        var local = NewSong("local-1", "Never Gonna Give You Up", "Rick Astley", isLocal: true);
        var walker = NewWalker();
        var source = NewSource("Never Gonna Give You Up", "Rick Astley");

        var result = await walker.WalkAsync(
            source,
            new[] { "deezer", "qobuz" },
            localMatch: local,
            localMatchScore: 95,
            default);

        Assert.Equal(local, result.MatchedSong);
        Assert.Equal(PerProviderTrackMatcher.MatchTypeLocal, result.MatchType);
        Assert.Equal("local", result.ProviderUsed);
        Assert.Empty(result.Walked);
    }

    [Fact]
    public async Task FirstProviderThatAccepts_StopsTheWalk()
    {
        var deezer = new DeezerFake(new[] { NewSong("ext-deezer-1", "Never Gonna Give You Up", "Rick Astley") });
        var qobuz = new QobuzFake(new[] { NewSong("ext-qobuz-1", "Never Gonna Give You Up", "Rick Astley") });
        var walker = NewWalker(deezer, qobuz);
        var source = NewSource("Never Gonna Give You Up", "Rick Astley");

        var result = await walker.WalkAsync(
            source,
            new[] { "deezer", "qobuz" },
            localMatch: null,
            localMatchScore: null,
            default);

        Assert.NotNull(result.MatchedSong);
        Assert.Equal("deezer", result.ProviderUsed);
        Assert.Equal(PerProviderTrackMatcher.MatchTypeProviderFuzzy, result.MatchType);
        Assert.Single(result.Walked);
        Assert.Equal("deezer", result.Walked[0].Provider);
        Assert.Equal(PerProviderTrackMatcher.OutcomeAccepted, result.Walked[0].Outcome);
    }

    [Fact]
    public async Task LowScoreProvider_AdvancesToNextProvider()
    {
        var deezer = new DeezerFake(new[] { NewSong("ext-deezer-1", "Completely Different Song", "Some Band") });
        var qobuz = new QobuzFake(new[] { NewSong("ext-qobuz-1", "Never Gonna Give You Up", "Rick Astley") });
        var walker = NewWalker(deezer, qobuz);
        var source = NewSource("Never Gonna Give You Up", "Rick Astley");

        var result = await walker.WalkAsync(
            source,
            new[] { "deezer", "qobuz" },
            localMatch: null,
            localMatchScore: null,
            default);

        Assert.NotNull(result.MatchedSong);
        Assert.Equal("qobuz", result.ProviderUsed);
        Assert.Equal(2, result.Walked.Count);
        Assert.Equal(PerProviderTrackMatcher.OutcomeLowScore, result.Walked[0].Outcome);
        Assert.Equal(PerProviderTrackMatcher.OutcomeAccepted, result.Walked[1].Outcome);
    }

    [Fact]
    public async Task IsrcHit_OnTheConfiguredProvider_IsAcceptedFirst()
    {
        var isrcSong = NewSong("ext-deezer-123", "Never Gonna Give You Up", "Rick Astley");
        isrcSong.Isrc = "GBANE0100001";
        var deezer = new DeezerFake(Array.Empty<Song>(), isrcSong);
        var walker = NewWalker(deezer);
        var source = NewSource("Never Gonna Give You Up", "Rick Astley", isrc: "GBANE0100001");

        var result = await walker.WalkAsync(
            source,
            new[] { "deezer" },
            localMatch: null,
            localMatchScore: null,
            default);

        Assert.Equal(isrcSong, result.MatchedSong);
        Assert.Equal("deezer", result.ProviderUsed);
        Assert.Equal(PerProviderTrackMatcher.MatchTypeIsrc, result.MatchType);
        Assert.Equal(PerProviderTrackMatcher.OutcomeAccepted, result.Walked[0].Outcome);
        Assert.StartsWith("isrc:", result.Walked[0].Query);
    }

    [Fact]
    public async Task TitleOnlyRetry_OnFirstProvider_CanFindAfterInitialLowScore()
    {
        var deezer = new DeezerFake(
            fuzzyResults: new[] { NewSong("ext-deezer-1", "Lost In Translation", "Other Artist") },
            titleOnlyResults: new[] { NewSong("ext-deezer-2", "Africa", "Toto") },
            titleOnlySentinel: "Toto");
        var walker = NewWalker(deezer);
        var source = NewSource("Africa", "Toto");

        var result = await walker.WalkAsync(
            source,
            new[] { "deezer" },
            localMatch: null,
            localMatchScore: null,
            default);

        var walkedSummary = string.Join(", ", result.Walked.Select(w => $"{w.Provider}={w.Outcome}:{w.ReasonCode}:score={w.TopScore}"));
        Assert.NotNull(result.MatchedSong);
        Assert.Equal(PerProviderTrackMatcher.MatchTypeTitleOnly, result.MatchType);
        Assert.True(result.Walked.Count > 0, $"walked: {walkedSummary}");
    }

    [Fact]
    public async Task NoMatch_RecordsEveryWalkedProvider()
    {
        var deezer = new DeezerFake(new[] { NewSong("ext-deezer-1", "Wrong Title", "Wrong Artist") });
        var qobuz = new QobuzFake(Array.Empty<Song>());
        var walker = NewWalker(deezer, qobuz);
        var source = NewSource("Never Gonna Give You Up", "Rick Astley");

        var result = await walker.WalkAsync(
            source,
            new[] { "deezer", "qobuz" },
            localMatch: null,
            localMatchScore: null,
            default);

        Assert.Null(result.MatchedSong);
        Assert.Equal(2, result.Walked.Count);
        Assert.Contains(result.Walked, attempt => attempt.Provider == "deezer"
            && attempt.Outcome == PerProviderTrackMatcher.OutcomeLowScore);
        Assert.Contains(result.Walked, attempt => attempt.Provider == "qobuz"
            && attempt.Outcome == PerProviderTrackMatcher.OutcomeEmpty);
    }

    [Fact]
    public async Task UnknownProviderId_RecordsNoServiceAndContinues()
    {
        var deezer = new DeezerFake(new[] { NewSong("ext-deezer-1", "Never Gonna Give You Up", "Rick Astley") });
        var walker = NewWalker(deezer);
        var source = NewSource("Never Gonna Give You Up", "Rick Astley");

        var result = await walker.WalkAsync(
            source,
            new[] { "mystery-provider", "deezer" },
            localMatch: null,
            localMatchScore: null,
            default);

        Assert.NotNull(result.MatchedSong);
        Assert.Equal("deezer", result.ProviderUsed);
        Assert.Equal(2, result.Walked.Count);
        Assert.Equal(PerProviderTrackMatcher.OutcomeNoService, result.Walked[0].Outcome);
    }

    [Fact]
    public async Task CollectAllProviderMatches_RetainsLocalAndAcceptsOneRoutePerProvider()
    {
        var local = NewSong("local-1", "Never Gonna Give You Up", "Rick Astley", isLocal: true);
        var deezer = new DeezerFake(new[]
        {
            NewSong("ext-deezer-1", "Never Gonna Give You Up", "Rick Astley")
        });
        var qobuz = new QobuzFake(new[]
        {
            NewSong("ext-qobuz-1", "Never Gonna Give You Up", "Rick Astley")
        });
        var walker = NewWalker(deezer, qobuz);

        var result = await walker.WalkAsync(
            NewSource("Never Gonna Give You Up", "Rick Astley"),
            new[] { "deezer", "qobuz" },
            localMatch: local,
            localMatchScore: 98,
            default,
            collectAllProviderMatches: true);

        Assert.Equal(local, result.MatchedSong);
        Assert.Equal("local", result.ProviderUsed);
        Assert.Equal(2, result.AcceptedMatches.Count);
        Assert.Equal(new[] { "deezer", "qobuz" }, result.AcceptedMatches.Select(match => match.Provider));
        Assert.Equal(2, result.Walked.Count);
    }

    [Fact]
    public async Task CollectAllProviderMatches_IsolatesProviderFailureAndKeepsLaterRoute()
    {
        var amazon = new AmazonFake();
        var deezer = new DeezerFake(new[]
        {
            NewSong("ext-deezer-1", "Never Gonna Give You Up", "Rick Astley")
        });
        var walker = NewWalker(amazon, deezer);

        var result = await walker.WalkAsync(
            NewSource("Never Gonna Give You Up", "Rick Astley"),
            new[] { "amazon", "deezer" },
            localMatch: null,
            localMatchScore: null,
            default,
            collectAllProviderMatches: true);

        Assert.Single(result.AcceptedMatches);
        Assert.Equal("deezer", result.AcceptedMatches[0].Provider);
        Assert.Contains(result.Walked, attempt =>
            attempt.Provider == "amazon" &&
            attempt.Outcome == PerProviderTrackMatcher.OutcomeError);
    }

    private static PerProviderTrackWalker NewWalker(params IConcreteMetadataService[] services) =>
        new(services, new PerProviderAcceptThresholds(), NullLogger.Instance);

    private static InjectedSourceTrack NewSource(
        string title,
        string primaryArtist,
        string? isrc = null) =>
        new(
            SourceId: "src-1",
            SourceProvider: "spotify",
            Title: title,
            Artists: new List<string> { primaryArtist },
            Isrc: isrc,
            DurationMs: 213000);

    private static Song NewSong(string id, string title, string artist, bool isLocal = false) =>
        new()
        {
            Id = id,
            Title = title,
            Artist = artist,
            IsLocal = isLocal,
            ExternalProvider = isLocal ? null : "deezer",
            ExternalId = isLocal ? null : id
        };

    private abstract class SearchFakeBase : IConcreteMetadataService
    {
        protected abstract IReadOnlyList<Song> SelectResults(string query);

        public virtual Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default) =>
            Task.FromResult<Song?>(null);

        public Task<List<Song>> SearchSongsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(SelectResults(query).Take(limit).ToList());

        public Task<List<Album>> SearchAlbumsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Album>());

        public Task<List<Artist>> SearchArtistsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Artist>());

        public Task<SearchResult> SearchAllAsync(string query, int songLimit = 20, int albumLimit = 20, int artistLimit = 20, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SearchResult());

        public Task<Song?> GetSongAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Song?>(null);

        public Task<Album?> GetAlbumAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Album?>(null);

        public Task<Artist?> GetArtistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<Artist?>(null);

        public Task<List<Album>> GetArtistAlbumsAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Album>());

        public Task<List<Song>> GetArtistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Song>());

        public Task<List<ExternalPlaylist>> SearchPlaylistsAsync(string query, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<ExternalPlaylist>());

        public Task<ExternalPlaylist?> GetPlaylistAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult<ExternalPlaylist?>(null);

        public Task<List<Song>> GetPlaylistTracksAsync(string externalProvider, string externalId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new List<Song>());
    }

    private sealed class DeezerFake : SearchFakeBase
    {
        private readonly IReadOnlyList<Song> _fuzzyResults;
        private readonly IReadOnlyList<Song> _titleOnlyResults;
        private readonly Song? _isrcResult;
        private readonly string? _titleOnlySentinel;

        public DeezerFake(IReadOnlyList<Song> fuzzyResults, Song? isrcResult = null)
        {
            _fuzzyResults = fuzzyResults;
            _titleOnlyResults = fuzzyResults;
            _isrcResult = isrcResult;
        }

        public DeezerFake(IReadOnlyList<Song> fuzzyResults, IReadOnlyList<Song> titleOnlyResults, string? titleOnlySentinel = null)
        {
            _fuzzyResults = fuzzyResults;
            _titleOnlyResults = titleOnlyResults;
            _isrcResult = null;
            _titleOnlySentinel = titleOnlySentinel;
        }

        public override Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default) =>
            Task.FromResult(_isrcResult);

        protected override IReadOnlyList<Song> SelectResults(string query) =>
            _titleOnlySentinel != null && !query.Contains(_titleOnlySentinel)
                ? _titleOnlyResults
                : _fuzzyResults;
    }

    private sealed class QobuzFake : SearchFakeBase
    {
        private readonly IReadOnlyList<Song> _fuzzyResults;
        private readonly Song? _isrcResult;

        public QobuzFake(IReadOnlyList<Song> fuzzyResults, Song? isrcResult = null)
        {
            _fuzzyResults = fuzzyResults;
            _isrcResult = isrcResult;
        }

        public override Task<Song?> FindSongByIsrcAsync(string isrc, CancellationToken cancellationToken = default) =>
            Task.FromResult(_isrcResult);

        protected override IReadOnlyList<Song> SelectResults(string query) => _fuzzyResults;
    }

    private sealed class AmazonFake : SearchFakeBase
    {
        protected override IReadOnlyList<Song> SelectResults(string query) =>
            throw new InvalidOperationException("Provider unavailable");
    }
}
