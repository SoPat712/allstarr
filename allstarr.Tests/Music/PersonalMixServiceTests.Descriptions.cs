using System.Globalization;
using System.Text.Json.Nodes;
using allstarr.Core.Music;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Theory]
    [InlineData("on-repeat", "What you're playing most right now")]
    [InlineData("rewind", "Songs you loved and haven't played lately")]
    [InlineData("release-radar", "New from artists you play")]
    [InlineData("discover-weekly", "New to you, based on your taste")]
    [InlineData("daily-1", "Familiar favorites and new songs in your top styles")]
    [InlineData("daily-2", "Familiar favorites and new songs in your top styles")]
    [InlineData("daily-3", "Familiar favorites and new songs in your top styles")]
    [InlineData("daily-4", "Familiar favorites and new songs in your top styles")]
    [InlineData("chill", "Music for chill, updated Monday")]
    [InlineData("focus", "Music for focus, updated Monday")]
    [InlineData("workout", "Music for workout, updated Monday")]
    [InlineData("party", "Music for party, updated Monday")]
    [InlineData("sleep", "Music for sleep, updated Monday")]
    public async Task MixDescriptions_ExplainEveryKindBeforeAndAfterEmptyRefresh(string kind, string description)
    {
        var initial = (await service.ListAsync(owner)).Single(mix => mix.Kind == kind);
        AssertMixDescription(initial, description);
        var refreshed = (await service.GenerateAsync(owner, initial.Id, allowProviderReads: false)).Single(mix => mix.Id == initial.Id);
        AssertMixDescription(refreshed, description);
        AssertMixDescription(await NewService().GetAsync(owner, initial.Id), description);
    }

    [Theory]
    [InlineData("on-repeat", 1, false, "Your most played, last 30 days")]
    [InlineData("on-repeat", 60, false, "Your most played, last 90 days")]
    [InlineData("on-repeat", 240, true, null)]
    [InlineData("rewind", 240, false, null)]
    [InlineData("daily-1", 1, false, "Rock you play most")]
    public async Task MixDescriptions_UseSelectedHistoryAndSurviveReads(string kind, int age, bool imported, string? description)
    {
        await Seed(count: 2, plays: 20, age: age);
        if (!imported)
        {
            await using var db = factory.CreateDbContext();
            await db.ListeningEvents.ExecuteUpdateAsync(update => update.SetProperty(item => item.SourceKind, "protocol"));
        }
        var mixes = await service.GenerateAsync(owner, allowProviderReads: false);
        var mix = mixes.Single(mix => mix.Kind == kind);
        description ??= kind == "rewind"
            ? "Not played since " + clock.UtcNow.AddDays(-age).ToString("MMMM yyyy", CultureInfo.InvariantCulture)
            : "Your most played, including imported " + mix.Evidence!.HistoricalWindow;
        Assert.NotEmpty(mix.Songs);
        AssertMixDescription(mix, description);
        AssertMixDescription(await NewService().GetAsync(owner, mix.Id), description);
    }

    [Fact]
    public async Task MixDescriptions_CountDistinctSelectedReleaseArtists()
    {
        await SeedReleaseInbox();
        var id = (await service.ListAsync(owner)).Single(mix => mix.Kind == "release-radar").Id;
        var mix = (await service.GenerateAsync(owner, id)).Single(mix => mix.Id == id);
        Assert.Equal(3, mix.Songs.Count);
        AssertMixDescription(mix, "New from 2 artists you follow or play");
        AssertMixDescription(await NewService().GetAsync(owner, id), mix.Description);
    }

    [Fact]
    public async Task MixDescriptions_RepairStoredKindsWithoutRefreshingOrWriting()
    {
        await Seed(count: 2, plays: 20);
        await service.GenerateAsync(owner, allowProviderReads: false);
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstAsync();
        var payload = JsonNode.Parse(row.PayloadJson)!;
        foreach (var (kind, value) in payload["Mixes"]!.AsObject())
        {
            var mix = value!.AsObject();
            mix["Added"] = true;
            mix["Description"] = "Made for you · " + mix["Name"]!.GetValue<string>();
            if (kind.StartsWith("daily-", StringComparison.Ordinal))
            {
                mix["Name"] = "Daily Mix " + kind[^1] + " · rock";
                mix["CoverTag"] = null;
            }
        }
        row.Id = Guid.NewGuid(); row.SnapshotVersion++; row.PayloadJson = payload.ToJsonString();
        db.ExternalMetadataSnapshots.Add(row);
        await db.SaveChangesAsync();
        var count = await db.ExternalMetadataSnapshots.CountAsync();
        gateway.Invocations.Clear();
        publish = true;
        var mixes = await NewService().ListAsync(owner);
        Assert.Equal(PersonalMixRules.Kinds.Length, mixes.Count);
        foreach (var mix in mixes)
        {
            AssertMixDescription(mix, mix.Description);
            Assert.DoesNotContain("Made for you", mix.Description, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(payload["Mixes"]![mix.Kind]!["Generation"]!.GetValue<int>(), mix.Generation);
            Assert.Equal(payload["Mixes"]![mix.Kind]!["Songs"]!.AsArray().Count, mix.Songs.Count);
        }
        Assert.All(mixes.Where(mix => mix.Kind.StartsWith("daily-", StringComparison.Ordinal)), mix => Assert.Equal("Rock you play most", mix.Description));
        var published = await NewService().PublishedAsync(Context(owner), default);
        Assert.Equal(mixes.Select(mix => mix.Description), published.Select(mix => mix.Description));
        Assert.Equal(count, await db.ExternalMetadataSnapshots.CountAsync());
        Assert.Empty(gateway.Invocations);
    }

    private static void AssertMixDescription(PersonalMix mix, string description)
    {
        Assert.Equal(description, mix.Description);
        Assert.False(string.IsNullOrWhiteSpace(mix.Description));
        Assert.DoesNotContain(mix.Name, mix.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\n', mix.Description);
        Assert.DoesNotContain('\r', mix.Description);
    }
}
