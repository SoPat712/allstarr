using System.Diagnostics;
using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 13)]
    public async Task ClientApi_MixListsReadOnlyNeededArtworkAndKeepPictureOrder(bool sameAlbum, int expectedCalls)
    {
        var calls = 0;
        using var factory = ClientMixFactory(new(), nativeResponse: request =>
        {
            if (request.RequestUri!.AbsolutePath != "/Users/listener/Items") return null;
            Interlocked.Increment(ref calls);
            Thread.Sleep(150);
            var ids = QueryHelpers.ParseQuery(request.RequestUri.Query)["ids"].ToString().Split(',');
            return Json(200, JsonSerializer.Serialize(new
            {
                Items = ids.Select(id => new
                {
                    Id = id,
                    Type = "Audio",
                    AlbumId = sameAlbum ? "shared-album" : "album-" + id,
                    AlbumPrimaryImageTag = sameAlbum ? "shared-tag" : "tag-" + id
                })
            }));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientMixAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().SingleAsync(item => item.ResourceKind == "personal-mixes");
            var payload = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
            payload.Mixes.Clear();
            for (var index = 0; index < 25; index++)
            {
                var id = Guid.NewGuid();
                var songs = Enumerable.Range(0, 50).Select(position => new Song
                {
                    Id = $"artwork-{index}-{position}",
                    IsLocal = true,
                    Title = "Song " + position,
                    Artist = "Artist",
                    Album = "Album",
                    Duration = 180
                }).ToArray();
                db.LibraryTracks.AddRange(songs.Select(song => new LibraryTrackRecord
                {
                    Id = Guid.NewGuid(),
                    OwnerUserId = row.OwnerUserId,
                    Protocol = "jellyfin",
                    BackendInstanceId = "primary",
                    BackendLibraryId = "library",
                    BackendItemId = song.Id
                }));
                payload.Mixes["custom:" + id] = new(id, "custom", "Saved " + index, "Saved mix", 1, DateTimeOffset.UtcNow, new(true, "Ready"), false,
                    songs.Select(song => new PersonalMixItem(id, 1, song, "library", "Picked for you", 1)).ToArray());
            }
            row.Id = Guid.NewGuid(); row.SnapshotVersion++;
            row.PayloadJson = JsonSerializer.Serialize(payload, new JsonSerializerOptions { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var timer = Stopwatch.StartNew();
        var response = await client.GetStringAsync("/allstarr/v1/mixes?timeZoneId=UTC");
        _lyricsSearchOutput.WriteLine($"25 mixes / 1250 distinct local tracks: {timer.Elapsed.TotalMilliseconds:F1} ms, native batches={calls}, same album={sameAlbum}.");
        Assert.Equal(expectedCalls, calls);
        using var body = JsonDocument.Parse(response);
        var cards = body.RootElement.GetProperty("mixes").EnumerateArray().Where(card => card.GetProperty("kind").GetString() == "custom").ToArray();
        Assert.Equal(25, cards.Length);
        for (var index = 0; index < cards.Length; index++)
        {
            var pictures = cards[index].GetProperty("artworkUrls").EnumerateArray().Select(item => item.GetString()!).ToArray();
            Assert.Equal(sameAlbum ? 1 : 4, pictures.Length);
            for (var picture = 0; picture < pictures.Length; picture++)
                Assert.Contains(sameAlbum ? "shared-album" : $"album-artwork-{index}-{picture}", pictures[picture], StringComparison.Ordinal);
        }
    }
}
