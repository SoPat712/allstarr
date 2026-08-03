using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Models.Lyrics;

namespace allstarr.Tests;

public sealed class BuiltInLyricsCapabilityAdapterTests
{
    [Fact]
    public async Task FetchLyrics_PreservesTimedContentSourceAndStableRevision()
    {
        var adapter = new BuiltInLyricsCapabilityAdapter("lyricsplus", (_, _) => Task.FromResult<LyricsInfo?>(new()
        {
            PlainLyrics = "First line",
            SyncedLyrics = "[00:01.00]First line\n",
            Source = "musixmatch"
        }));

        var outcome = await adapter.FetchLyricsAsync(
            Context("lyricsplus"),
            new ProviderLyricsRequest(
                Guid.CreateVersion7(),
                new("lyricsplus", ProviderResourceKind.Track, "lookup"),
                preferredFormat: ProviderLyricsFormat.LineTimed,
                trackTitle: "Track",
                artistNames: ["Artist"]));

        Assert.True(outcome.IsSuccess);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, outcome.Value!.Availability);
        Assert.Equal("musixmatch", outcome.Value.Source);
        Assert.Equal(ProviderLyricsFormat.LineTimed, outcome.Value.Format);
        Assert.Equal("[00:01.00]First line\n", outcome.Value.Content);
        Assert.StartsWith("sha256:", outcome.Value.Revision, StringComparison.Ordinal);
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

    private static ProviderExecutionContext Context(string providerId) => new(
        new ProviderActorContext(
            Guid.CreateVersion7(),
            ProviderActorKind.User,
            Guid.CreateVersion7(),
            new ProviderBackendPrincipal("jellyfin", "primary", "user")),
        providerId,
        account: null,
        library: null,
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
