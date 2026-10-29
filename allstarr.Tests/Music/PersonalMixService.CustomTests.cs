using allstarr.Core.Enrichment;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;
using allstarr.Core.Protocols;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task Moods_use_tag_fallback_readiness_gate_and_card_sections()
    {
        await Seed();
        foreach (var song in songs.Values) await tags.SaveAsync(SharedTagStore.RecordingKey(song.CanonicalRecordingId!.Value),
            [new("provider", [new("chill"), new("ambient"), new("energetic"), new("dance"), new("calm")], clock.UtcNow)], [], false, default);
        var moods = (await service.GenerateAsync(owner)).Where(item => item.Section == "moods").ToArray();
        Assert.Equal(5, moods.Length);
        Assert.All(moods, mix =>
        {
            Assert.True(mix.Readiness.Ready);
            Assert.Equal(40, mix.Songs.Count);
            Assert.All(mix.Songs, item => Assert.Equal("Tagged " + mix.Kind, item.Reason));
        });
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
        publish = true;
        foreach (var mood in moods) await service.SetInAppsAsync(owner, mood.Id, true, default);
        Assert.Equal(5, (await service.PublishedAsync(Context(owner), default)).Count(item => PersonalMixMoods.Kinds.Contains(item.SourcePlaylistId)));
        for (var index = 24; index < 40; index++) await taste.PutNudgeAsync(owner, new("artist", "Artist " + index, "never"));
        Assert.All((await service.ListAsync(owner)).Where(item => item.Section == "moods"), mix => Assert.False(mix.Readiness.Ready));
        Assert.DoesNotContain(await service.PublishedAsync(Context(owner), default), item => PersonalMixMoods.Kinds.Contains(item.SourcePlaylistId));
        await using var db = factory.CreateDbContext();
        foreach (var song in songs.Values.Skip(24)) song.Title = "Unavailable";
        clock.UtcNow += TimeSpan.FromMinutes(16);
        var chill = (await service.GenerateAsync(owner, moods[0].Id)).Single(item => item.Id == moods[0].Id);
        Assert.False(chill.Readiness.Ready);
        Assert.Empty(chill.Songs);
    }

    [Theory]
    [InlineData("song")]
    [InlineData("artist")]
    public async Task Start_save_keeps_stable_custom_id_is_private_and_never_refreshes(string kind)
    {
        await Seed();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        gateway.Setup(item => item.GetArtistAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0"))
            .ReturnsAsync(new Artist { Id = "ext-deezer-artist-0", Name = "Seed" });
        gateway.Setup(item => item.GetArtistTracksAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0"))
            .ReturnsAsync(songs.Values.ToList());
        var preview = await service.StartAsync(owner, new(kind, "ext-deezer-" + kind + "-0", 25));
        Assert.Equal(25, preview.Songs.Count);
        Assert.All(preview.Songs, item => Assert.NotEmpty(item.Reason));
        var response = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(await Controller(owner, true)
            .Start(new(kind, "ext-deezer-" + kind + "-0", 25), default));
        Assert.True(System.Text.Json.JsonSerializer.SerializeToElement(response.Value).GetProperty("previewId").GetGuid() != Guid.Empty);
        Assert.DoesNotContain((await service.ListAsync(owner)), item => item.Id == preview.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.SaveCustomAsync(other, preview.Id, "Saved"));
        var saved = await service.SaveCustomAsync(owner, preview.Id, "Saved");
        Assert.Equal(saved.Id, (await NewService().SaveCustomAsync(owner, preview.Id, "Saved again")).Id);
        Assert.Equal("custom", saved.Kind);
        Assert.Equal("custom", saved.Section);
        Assert.Empty(await service.PublishedAsync(Context(owner), default));
        publish = true;
        var projected = Assert.Single(await service.PublishedAsync(Context(owner), default), item => item.LinkId == saved.Id);
        Assert.Equal(preview.SeedName, projected.MixCover!.Seed);
        Assert.False(string.IsNullOrWhiteSpace(projected.MixCover.Seed));
        Assert.NotEqual("Saved", projected.MixCover.Seed);
        Assert.Equal(saved.CoverTag ?? "", projected.MixCover.Tag);
        clock.UtcNow += TimeSpan.FromDays(8);
        var after = (await service.GenerateAsync(owner)).Single(item => item.Id == saved.Id);
        Assert.Equal(saved.Generation, after.Generation);
        Assert.Equal(saved.UpdatedAt, after.UpdatedAt);
        await Assert.ThrowsAsync<ArgumentException>(() => service.GenerateAsync(owner, saved.Id));
        await service.SetInAppsAsync(owner, saved.Id, false, default);
        Assert.DoesNotContain(await service.PublishedAsync(Context(owner), default), item => item.LinkId == saved.Id);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => service.DeleteCustomAsync(other, saved.Id));
        await service.DeleteCustomAsync(owner, saved.Id);
        Assert.DoesNotContain(await service.ListAsync(owner), item => item.Id == saved.Id);
    }

    [Fact]
    public async Task Start_respects_clean_never_and_length_and_save_rechecks_current_rules()
    {
        await Seed();
        gateway.Setup(item => item.GetSimilarSongsAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "0", It.IsAny<int>()))
            .ReturnsAsync(songs.Values.ToList());
        filter = "CleanOnly";
        songs["1"].ExplicitContentLyrics = 1;
        await taste.PutNudgeAsync(owner, new("artist", "Artist 2", "never"));
        var preview = await service.StartAsync(owner, new("song", songs["0"].Id, 50));
        Assert.DoesNotContain(preview.Songs, item => item.Song.ExternalId is "0" or "1" or "2");
        await taste.PutNudgeAsync(owner, new("artist", "Artist 3", "never"));
        var saved = await service.SaveCustomAsync(owner, preview.Id, "Saved");
        Assert.DoesNotContain(saved.Songs, item => item.Song.ExternalId == "3");
        await taste.PutNudgeAsync(owner, new("artist", "Artist 4", "never"));
        Assert.DoesNotContain((await service.SaveCustomAsync(owner, preview.Id, "Saved")).Songs, item => item.Song.ExternalId == "4");
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(owner, new("song", songs["0"].Id, 24)));
        await Assert.ThrowsAsync<ArgumentException>(() => service.StartAsync(owner, new("artist", "bad", 51)));
    }
}
