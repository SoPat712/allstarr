using allstarr.Core.Capabilities;
using allstarr.Core.Matching;

namespace allstarr.Tests;

public sealed partial class TrackIdentityServiceTests
{
    [Fact]
    public async Task StoredArtwork_IsFoundForSongsWhoseIdIsTheHashOfTheSourceId()
    {
        // A collected playlist keeps the hash of the Spotify id, and its songs use that hash as their id.
        var hashed = CanonicalCatalogKeys.Hash("4uLU6hMCjMI75M1A2tKUQC");
        var payload = """{"ProviderTrackIdHash":"HASH","Title":"Iris","Artists":["The Goo Goo Dolls"],"ArtworkUrl":"https://images.example.test/iris.jpg"}"""
            .Replace("HASH", hashed, StringComparison.Ordinal);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.CreateVersion7(),
                OwnerUserId = _userA,
                BackendInstanceId = "fixture",
                BackendPrincipalId = _userA.ToString("N"),
                Protocol = "jellyfin",
                ProviderId = "spotify",
                ResourceKind = "track",
                ExternalIdHash = hashed,
                SnapshotVersion = 1,
                ProviderRevision = "collected",
                PayloadJson = payload,
                PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
                CorrelationId = "collected-playlist",
                RetrievedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal("https://images.example.test/iris.jpg",
            (await _service.GetStoredArtworkUriAsync(new("spotify", ProviderResourceKind.Track, hashed)))?.AbsoluteUri);
        Assert.Equal("https://images.example.test/iris.jpg",
            (await _service.GetStoredArtworkUriAsync(Track("spotify", "4uLU6hMCjMI75M1A2tKUQC")))?.AbsoluteUri);
        Assert.Null(await _service.GetStoredArtworkUriAsync(new("spotify", ProviderResourceKind.Track, hashed.ToUpperInvariant())));
    }

    [Theory]
    [InlineData("4uLU6hMCjMI75M1A2tKUQC", 1)]
    [InlineData("1234567890", 1)]
    [InlineData("c343adaad187f75ee7b91f9ffb8a22fbfcbb55c9cddde4b1314c3a0e822392cb", 2)]
    public void StoredKeys_TreatOnlyLowercaseSha256TextAsAlreadyHashed(string value, int count)
    {
        var keys = CanonicalCatalogKeys.StoredKeys(value);
        Assert.Equal(count, keys.Length);
        Assert.Equal(CanonicalCatalogKeys.Hash(value), keys[0]);
    }
}
