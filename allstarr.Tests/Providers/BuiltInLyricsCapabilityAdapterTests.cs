using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class BuiltInLyricsCapabilityAdapterTests
{
    [Fact]
    public async Task CacheOnlyDoesNotInvokeAnAdapterWithoutACompletionAwareCache()
    {
        var called = false;
        var adapter = new BuiltInLyricsCapabilityAdapter("fixture-lyrics", (_, _) =>
        {
            called = true;
            return Task.FromResult<LyricsInfo?>(new() { PlainLyrics = "network" });
        });

        var outcome = await ((IInternalLyricsCapability)adapter).FetchAsync(
            Context("fixture-lyrics"),
            new(Guid.CreateVersion7(), new("fixture-lyrics", ProviderResourceKind.Track, "lookup")),
            new(LyricsCompletionMode.CacheOnly, await ((IInternalLyricsCapability)adapter).CaptureSourceAsync(default)));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, outcome.Value!.Availability);
        Assert.False(called);
    }

    [Fact]
    public async Task FetchLyrics_PreservesTimedContentSourceAndStableRevision()
    {
        var adapter = new BuiltInLyricsCapabilityAdapter("fixture-lyrics", (_, _) => Task.FromResult<LyricsInfo?>(new()
        {
            PlainLyrics = "First line",
            SyncedLyrics = "[00:01.00]First line\n",
            Source = "musixmatch"
        }));

        var outcome = await adapter.FetchLyricsAsync(
            Context("fixture-lyrics"),
            new ProviderLyricsRequest(
                Guid.CreateVersion7(),
                new("fixture-lyrics", ProviderResourceKind.Track, "lookup"),
                preferredFormat: ProviderLyricsFormat.LineTimed,
                trackTitle: "Track",
                artistNames: ["Artist"]));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, outcome.Value!.Availability);
        Assert.Equal("musixmatch", outcome.Value.Source);
        Assert.Equal(ProviderLyricsFormat.LineTimed, outcome.Value.Format);
        Assert.Equal("[00:01.00]First line\n", outcome.Value.Content);
        Assert.StartsWith("sha256:", outcome.Value.Revision, StringComparison.Ordinal);
        Assert.Null(outcome.Value.Versions);
    }

    [Fact]
    public async Task FetchLyrics_HonorsPlainAndAvailabilityOnlyRequests()
    {
        var adapter = new BuiltInLyricsCapabilityAdapter("lrclib", (_, _) => Task.FromResult<LyricsInfo?>(new()
        {
            PlainLyrics = "Plain",
            SyncedLyrics = "[00:01.00]Timed"
        }));
        var track = new ProviderExternalResourceId("lrclib", ProviderResourceKind.Track, "lookup");

        var plain = await adapter.FetchLyricsAsync(Context("lrclib"), new(
            Guid.CreateVersion7(), track, preferredFormat: ProviderLyricsFormat.PlainText));
        var availability = await adapter.FetchLyricsAsync(Context("lrclib"), new(
            Guid.CreateVersion7(), track, availabilityOnly: true));

        Assert.Equal((ProviderLyricsFormat.PlainText, "Plain"),
            (plain.Value!.Format, plain.Value.Content));
        Assert.Equal(ProviderLyricsAvailabilityState.Available, availability.Value!.Availability);
        Assert.Null(availability.Value.Content);
        Assert.Null(availability.Value.Format);
        Assert.NotNull(availability.Value.Revision);
    }

    [Fact]
    public async Task FetchLyrics_PreservesEachVersionsMetadataTimingAndRevision()
    {
        var timedLines = new[] { new ProviderLyricsTimedLine("First", TimeSpan.FromSeconds(1),
            segments: [new("First", TimeSpan.FromSeconds(1), TimeSpan.FromMilliseconds(400))]) };
        var best = new LyricsInfo
        {
            SyncedLyrics = "[00:01.00]First",
            Revision = "best-revision",
            TimedLines = timedLines,
            Metadata = new("42", "Provider title", "Provider artist", null, 180125),
            Versions =
            [
                new()
                {
                    SyncedLyrics = "[00:01.00]First",
                    Revision = "best-revision",
                    TimedLines = timedLines,
                    Metadata = new("42", "Provider title", "Provider artist", null, 180125)
                },
                new()
                {
                    PlainLyrics = "Alternate",
                    Source = "alternate-source",
                    Metadata = new("43", null, null, "Alternate album", null)
                }
            ]
        };
        var adapter = new BuiltInLyricsCapabilityAdapter("lrclib", (_, _) => Task.FromResult<LyricsInfo?>(best));

        var outcome = await adapter.FetchLyricsAsync(Context("lrclib"), new(
            Guid.CreateVersion7(), new("lrclib", ProviderResourceKind.Track, "lookup")));

        Assert.True(outcome.IsSuccess);
        var result = outcome.Value!;
        Assert.Equal("[00:01.00]First", result.Content);
        Assert.Equal("best-revision", result.Revision);
        Assert.Equal("42", result.Metadata!.VersionKey);
        Assert.Equal(180125L, result.Metadata.DurationMs);
        Assert.Same(timedLines[0], Assert.Single(result.TimedLines!));
        Assert.Equal(2, result.Versions!.Count);
        Assert.Equal(result.Metadata, result.Versions[0].Metadata);
        Assert.Equal(result.Revision, result.Versions[0].Revision);
        Assert.Same(timedLines[0], Assert.Single(result.Versions[0].TimedLines!));
        Assert.Equal("43", result.Versions[1].Metadata!.VersionKey);
        Assert.Null(result.Versions[1].Metadata!.Title);
        Assert.Equal("alternate-source", result.Versions[1].Source);
        Assert.Equal(ProviderLyricsFormat.PlainText, result.Versions[1].Format);
        Assert.Equal("Alternate", result.Versions[1].Content);
        Assert.StartsWith("sha256:", result.Versions[1].Revision, StringComparison.Ordinal);
        Assert.All(result.Versions, version => Assert.Null(version.Versions));
    }

    [Fact]
    public async Task FetchLyrics_AvailabilityOnlyOmitsAllVersionsContentAndTiming()
    {
        var metadata = new ProviderLyricsMetadata("42", "Provider title", null, null, null);
        var version = new LyricsInfo
        {
            SyncedLyrics = "[00:01.00]First",
            TimedLines = [new("First", TimeSpan.FromSeconds(1))],
            Revision = "revision",
            Metadata = metadata
        };
        var adapter = new BuiltInLyricsCapabilityAdapter("lrclib", (_, _) => Task.FromResult<LyricsInfo?>(new()
        {
            SyncedLyrics = version.SyncedLyrics,
            TimedLines = version.TimedLines,
            Revision = version.Revision,
            Metadata = metadata,
            Versions = [version]
        }));

        var outcome = await adapter.FetchLyricsAsync(Context("lrclib"), new(
            Guid.CreateVersion7(), new("lrclib", ProviderResourceKind.Track, "lookup"), availabilityOnly: true));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(metadata, outcome.Value!.Metadata);
        Assert.Null(outcome.Value.Content);
        Assert.Null(outcome.Value.Format);
        Assert.Null(outcome.Value.TimedLines);
        var result = Assert.Single(outcome.Value.Versions!);
        Assert.Equal(metadata, result.Metadata);
        Assert.Equal("revision", result.Revision);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.Availability);
        Assert.Null(result.Content);
        Assert.Null(result.Format);
        Assert.Null(result.TimedLines);
    }

    private static ProviderExecutionContext Context(string providerId) => new(
        new ProviderActorContext(
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "primary", "user")),
        providerId,
        account: null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: false,
            allowManagedDownloads: false,
            [providerId]),
        "lyrics-test",
        "correlation-test",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);
}
