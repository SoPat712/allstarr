using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed class DownloadedSongMappingRepresentationTests
{
    [Fact]
    public void DefaultScopeHashesRemainCompatibleWithExistingMappings()
    {
        Assert.Equal("c37c9e9b0b63a5c507eb4d3eb5cf1964ad08ba6cb86bee49292123b615599d4e", DownloadedSongMappingScope.LegacyKey);
        Assert.Equal(DownloadedSongMappingScope.LegacyKey, new DownloadedSongMappingScope(null, ProviderAudioQuality.Any).Key);
        Assert.Equal("a5127ae453853220fba375521415ef2df5f98a26759abf2a64a0afdf5ad551e7",
            new DownloadedSongMappingScope(Guid.Parse("20000000-0000-0000-0000-000000000001"), ProviderAudioQuality.Lossless).Key);
    }

    [Theory]
    [InlineData("representation")]
    [InlineData("revision")]
    [InlineData("account")]
    [InlineData("quality")]
    public void OriginalScopeIdentityIncludesItsRepresentationAndSourceBinding(string changed)
    {
        var original = new DownloadedSongMappingScope(Guid.NewGuid(), ProviderAudioQuality.Any, DownloadedSongRepresentation.Original, 7);
        DownloadedSongMappingScope other = changed switch
        {
            "representation" => new(original.ProviderAccountId, ProviderAudioQuality.Any),
            "revision" => original with { SourceAccountRevision = 8 },
            "account" => original with { ProviderAccountId = Guid.NewGuid() },
            _ => original with { AudioQuality = ProviderAudioQuality.Lossless }
        };
        Assert.NotEqual(original.Key, other.Key);
        Assert.Equal(64, original.Key.Length);
    }

    [Fact]
    public async Task OriginalAndPlaybackMappingsCoexistWithoutOverwritingEitherRepresentationOrRevision()
    {
        await using var fixture = await RetainedOriginalCacheFixture.CreateAsync();
        var playback = new DownloadedSongMappingScope(fixture.Account, ProviderAudioQuality.Any);
        var original = fixture.OriginalScope;
        var nextRevision = original with { SourceAccountRevision = 8 };
        foreach (var (scope, name) in new[] { (playback, "playback"), (original, "original"), (nextRevision, "next") })
        {
            await fixture.Mappings.UpsertAsync(new()
            {
                Id = Guid.CreateVersion7(),
                ScopeKey = scope.Key,
                ProviderAccountId = scope.ProviderAccountId,
                AudioQuality = scope.AudioQuality,
                ProviderId = "deezer",
                ExternalId = "original-track",
                LocalPath = Path.Combine(fixture.Root, name + ".flac"),
                Title = name,
                Artist = "Artist",
                Album = "Album",
                DownloadedAt = DateTimeOffset.UtcNow
            });
        }
        await using var db = fixture.Contexts.CreateDbContext();
        Assert.Equal(3, await db.DownloadedSongMappings.CountAsync());
        Assert.Equal("playback", (await fixture.Mappings.FindAsync(playback, "deezer", "original-track"))!.Title);
        Assert.Equal("original", (await fixture.Mappings.FindAsync(original, "deezer", "original-track"))!.Title);
        Assert.Equal("next", (await fixture.Mappings.FindAsync(nextRevision, "deezer", "original-track"))!.Title);
    }
}
