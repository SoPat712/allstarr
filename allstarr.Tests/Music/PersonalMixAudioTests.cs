using System.Collections.Immutable;
using System.Text.Json;
using allstarr.Core.AudioMuse;
using allstarr.Core.Enrichment;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using Microsoft.EntityFrameworkCore;
using Moq;
using static allstarr.Tests.AudioMuseTests;
using static allstarr.Tests.AudioMuseDiscoveryTests;

namespace allstarr.Tests;

public sealed class PersonalMixAudioTests
{
    private static PersonalMixService Service(Fixture f, Mock<IProtocolProviderGateway> gateway)
    {
        var clock = new SystemPlatformClock();
        var tags = new SharedTagStore(f.Factory, clock);
        var policy = new Mock<IEffectiveProviderPolicyResolver>();
        policy.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<allstarr.Core.Capabilities.ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, "Lossless", .07)
            { Preferences = new("All") });
        var settings = new Mock<IDurableRuntimeSettings>();
        settings.Setup(item => item.GetPreferencesAsync(It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalListeningPreferences(new("All"), new(), false, "1"));
        return new(f.Factory, clock, tags, new(f.Factory, tags, clock), new(gateway.Object), gateway.Object,
            new NewReleasesService(f.Factory, clock, Mock.Of<IProviderRouter>(), settings.Object),
            settings.Object, policy.Object, f.Access.Object, f.Discovery);
    }

    [Theory]
    [InlineData("chill", .7, .4, .7, .7, .7, .6, .5, 100)]
    [InlineData("focus", .8, .2, .4, .2, .3, .5, .3, 90)]
    [InlineData("workout", .3, .8, .8, .6, .7, .2, .8, 140)]
    [InlineData("party", .6, .6, .8, .8, .8, .4, .8, 125)]
    [InlineData("sleep", .8, .2, .2, .1, .2, .8, .2, 65)]
    public async Task Moods_use_library_analysis_payloads(string kind, double relaxed, double aggressive, double danceable,
        double party, double happy, double sad, double energy, double tempo)
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Enumerable.Range(0, 25).Select(index => Track(index.ToString(), "Artist " + index)).ToArray());
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath == "/external/get_score" ? Fixture.Json(JsonSerializer.Serialize(new
        {
            item_id = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query)["id"].ToString(),
            energy,
            tempo,
            mood_vector = "ambient:0.6,electronic:0.5",
            other_features = FormattableString.Invariant($"danceable:{danceable},aggressive:{aggressive},happy:{happy},party:{party},relaxed:{relaxed},sad:{sad}")
        })) : original(request);
        var service = Service(f, new());
        var mix = (await service.GenerateAsync(f.Actor.AllstarrUserId!.Value)).Single(item => item.Kind == kind);
        Assert.True(mix.Readiness.Ready);
        Assert.Equal(25, mix.Songs.Count);
        Assert.Equal("moods-v1", mix.RecipeVersion);
        Assert.All(mix.Songs, item => { Assert.True(item.Song.IsLocal); Assert.Equal(kind switch { "workout" => "Energetic sound", "party" => "Party sound", _ => "Calm sound" }, item.Reason); });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Local_seed_adds_analyzed_neighbors_only_when_indexed_and_rechecks_library_access(bool analyzed)
    {
        await using var f = await ReadyAsync();
        await AddAsync(f, Track("seed", "Seed"), Track("neighbor", "Neighbor"));
        var denied = Track("denied", "Denied");
        denied.BackendLibraryId = "restricted";
        await AddAsync(f, denied);
        var original = f.Respond;
        f.Respond = request => request.RequestUri!.AbsolutePath switch
        {
            "/external/get_embedding" when !analyzed => Fixture.Json("{}", System.Net.HttpStatusCode.NotFound),
            "/api/similar_tracks" => Fixture.Json("[{\"item_id\":\"seed\"},{\"item_id\":\"neighbor\"},{\"item_id\":\"denied\"}]"),
            _ => original(request)
        };
        var service = Service(f, new());
        var preview = await service.StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "seed", 25));
        if (analyzed)
        {
            var song = Assert.Single(preview.Songs);
            Assert.Equal("neighbor", song.Song.Id);
            Assert.Equal("Similar sound", song.Reason);
            await using var db = f.Factory.CreateDbContext();
            (await db.LibraryTracks.SingleAsync(item => item.BackendItemId == "neighbor")).BackendLibraryId = "restricted";
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<ArgumentException>(() => service.SaveCustomAsync(f.Actor.AllstarrUserId.Value, preview.Id, "Saved"));
        }
        else Assert.Empty(preview.Songs);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.StartAsync(f.Actor.AllstarrUserId!.Value, new("song", "denied", 25)));
    }
}
