using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;

namespace allstarr.Tests;

public sealed partial class ProtocolLyricsAutomaticTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LyricsRepeatedEmptyAndFailedLookupsAvoidThePerRequestFallbackCost(bool failure)
    {
        var outcome = failure ? ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.TransientFailure)) :
            ProviderOutcome<ProviderLyricsResult>.Success(new(ProviderLyricsAvailabilityState.Unavailable, "lyricsplus"));
        var original = new Source("lyricsplus", outcome);
        await using (var before = new Fixture(original))
            for (var index = 0; index < 2; index++)
                Assert.Null(await before.FindAsync(new ProtocolLyricsSources { ForPermanentSidecar = true }));
        var cached = new Source("lyricsplus", outcome);
        await using (var after = new Fixture(cached))
            for (var index = 0; index < 2; index++) Assert.Null(await after.FindAsync());
        Assert.Equal(2, original.Calls);
        Assert.Equal(1, cached.Calls);
    }

    [Fact]
    public async Task VersionListAnswersOnceTheChoiceIsSettledAndAnnouncesThePendingRest()
    {
        var known = new Source("lrclib") { Cached = Result("lrclib", true) };
        var slow = new Source("lrcred");
        await using var fixture = new Fixture(known, slow);
        var song = new allstarr.Models.Domain.Song { Id = "song", Title = "Fixture", Artist = "Artist", Album = "Album", Duration = 120 };

        // The clock never moves, so only a settled choice can end this read.
        var answered = await fixture.Resolver.FindAllAsync(fixture.Protocol, song, "recording",
            new ProtocolLyricsSources { UntilChosen = true }).WaitAsync(TimeSpan.FromSeconds(5));
        var chosen = await fixture.Resolver.FindAsync(fixture.Protocol, song, "recording", new ProtocolLyricsSources())
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("ready", Assert.Single(answered, candidate => candidate.SourceId == "lrclib").State);
        Assert.Equal("pending", Assert.Single(answered, candidate => candidate.SourceId == "lrcred").State);
        Assert.Equal("lrclib", chosen?.Source);
        Assert.Empty(fixture.Events);
        await slow.WaitForCallsAsync(1);
        slow.CompleteAll();
        await fixture.EventPublished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lyrics.updated", Assert.Single(fixture.Events).Type);
    }

    [Fact]
    public async Task ChosenSourceIsWaitedForWhileAnotherSourceAlreadyHasLyrics()
    {
        var known = new Source("lrclib") { Cached = Result("lrclib", true) };
        var chosen = new Source("lrcred");
        await using var fixture = new Fixture(known, chosen) { Recording = Guid.NewGuid(), Preferred = "lrcred" };

        // The clock never moves, so only the chosen source's answer can end this read.
        var pending = fixture.FindAsync();
        await chosen.WaitForCallsAsync(1);
        Assert.False(pending.IsCompleted);
        chosen.Completion.TrySetResult(Result("lrcred", true));

        Assert.Equal("lrcred", (await pending.WaitAsync(TimeSpan.FromSeconds(5)))?.Source);
    }

    [Fact]
    public async Task LyricsUnavailableResultIsSharedAcrossReadsAndExpiresAfterSixHours()
    {
        var source = new Source("lyricsplus", ProviderOutcome<ProviderLyricsResult>.Success(
            new(ProviderLyricsAvailabilityState.Unavailable, "lyricsplus")));
        await using var fixture = new Fixture(source);
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1)));
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1)));
        Assert.Equal(1, source.Calls);
        fixture.Clock.Advance(TimeSpan.FromHours(6) - TimeSpan.FromSeconds(1));
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1)));
        Assert.Equal(1, source.Calls);
        fixture.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1)));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task LyricsFailureIsSkippedForTwoMinutesAcrossTracks()
    {
        var source = new Source("lyricsplus", ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.TransientFailure)));
        await using var fixture = new Fixture(source);
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1)));
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1), resourceKey: "second"));
        Assert.Equal(1, source.Calls);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Null(await fixture.FindAsync(deadline: TimeSpan.FromDays(1), resourceKey: "third"));
        Assert.Equal(2, source.Calls);
    }

    [Fact]
    public async Task LyricsEmptyBackendIsNotReadAgainUntilTheNegativeCacheExpires()
    {
        var source = new Source("lyricsplus", ProviderOutcome<ProviderLyricsResult>.Success(
            new(ProviderLyricsAvailabilityState.Unavailable, "lyricsplus")));
        await using var fixture = new Fixture(source);
        var calls = 0;
        var sources = new ProtocolLyricsSources(Backend: _ =>
        {
            calls++;
            return Task.FromResult<allstarr.Models.Lyrics.LyricsInfo?>(null);
        });
        Assert.Null(await fixture.FindAsync(sources, deadline: TimeSpan.FromDays(1)));
        Assert.Null(await fixture.FindAsync(sources, deadline: TimeSpan.FromDays(1)));
        Assert.Equal(1, calls);
        fixture.Clock.Advance(TimeSpan.FromHours(6));
        Assert.Null(await fixture.FindAsync(sources, deadline: TimeSpan.FromDays(1)));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LyricsNegativeCacheFollowsTheAccountAndSourceConfiguration()
    {
        var source = new Source("lyricsplus", ProviderOutcome<ProviderLyricsResult>.Success(
            new(ProviderLyricsAvailabilityState.Unavailable, "lyricsplus")));
        await using var fixture = new Fixture(source) { UseAccount = true };
        Assert.Null(await fixture.FindAsync());
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(1, source.Calls);
        fixture.AccountRevision++;
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(2, source.Calls);
        source.Version++;
        Assert.Null(await fixture.FindAsync());
        Assert.Equal(3, source.Calls);
    }

    [Fact]
    public async Task LyricsNegativeCacheDoesNotHideNewlyCachedLyrics()
    {
        var source = new Source("lyricsplus", ProviderOutcome<ProviderLyricsResult>.Success(
            new(ProviderLyricsAvailabilityState.Unavailable, "lyricsplus")));
        await using var fixture = new Fixture(source);
        Assert.Null(await fixture.FindAsync());
        source.Cached = Result("lyricsplus", true);
        Assert.Equal("lyricsplus", (await fixture.FindAsync())?.Source);
        Assert.Equal(1, source.Calls);
    }

    [Fact]
    public async Task LyricsSelectedSourceThatIgnoresCancellationIsBoundedAtThreeSeconds()
    {
        var source = new Source("lyricsplus");
        await using var fixture = new Fixture(source);
        var pending = fixture.Resolver.FindAllAsync(fixture.Protocol,
            new allstarr.Models.Domain.Song { Title = "Fixture", Artist = "Artist" }, "recording",
            new ProtocolLyricsSources { OnlySourceId = "lyricsplus", ReadOnly = true });
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(3));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(Assert.Single(await pending.WaitAsync(TimeSpan.FromSeconds(5))).Lyrics);
        Assert.False(source.Completion.Task.IsCompleted);
    }

    [Fact]
    public async Task LyricsSourceThatIgnoresCancellationReleasesTheLookupAtThreeSeconds()
    {
        var source = new Source("lyricsplus");
        await using var fixture = new Fixture(source);
        fixture.LookupOptions.ResponseBudgetMilliseconds = 4000;
        var pending = fixture.FindAsync();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.Clock.WaitForTimersAsync(TimeSpan.FromSeconds(3));
        fixture.Clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(await pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(source.Completion.Task.IsCompleted);
        Assert.Equal(1, source.Calls);
    }
}
