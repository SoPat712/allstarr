using System.Text.Json;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task Mix_cards_use_the_server_cover_for_local_and_service_songs_and_keep_empty_gradients()
    {
        await Seed();
        await service.GenerateAsync(owner);
        await using (var db = factory.CreateDbContext())
        {
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().Where(item => item.OwnerUserId == owner && item.ResourceKind == "personal-mixes")
                .OrderByDescending(item => item.SnapshotVersion).FirstAsync();
            var document = JsonSerializer.Deserialize<PersonalMixDocument>(row.PayloadJson)!;
            var workout = document.Mixes["workout"];
            document.Mixes["workout"] = workout with
            {
                Songs = Enumerable.Range(0, 4).Select(index => new PersonalMixItem(workout.Id, 1,
                    new Song { Id = $"native-{index}", AlbumId = $"album-{index}", IsLocal = true }, "library", "", 1)).ToArray()
            };
            document.Mixes["discover-weekly"] = document.Mixes["discover-weekly"] with { Songs = document.Mixes["on-repeat"].Songs };
            document.Mixes["release-radar"] = document.Mixes["release-radar"] with { Songs = [] };
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            row.ProviderRevision = row.SnapshotVersion.ToString();
            row.PayloadJson = JsonSerializer.Serialize(document);
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var mixes = await service.ListCardsAsync(owner);
        var result = Assert.IsType<OkObjectResult>(await Controller(owner, true).List());
        var cards = JsonSerializer.SerializeToElement(result.Value).EnumerateArray().ToArray();
        foreach (var kind in new[] { "workout", "discover-weekly" })
        {
            var mix = mixes.Single(item => item.Kind == kind);
            Assert.NotEmpty(mix.Songs);
            var card = cards.Single(item => item.GetProperty("Kind").GetString() == kind);
            Assert.Equal($"/api/admin/mixes/{mix.Id}/cover?v={MixCoverImageService.Version(MixCoverInput.From(mix))}",
                Assert.Single(card.GetProperty("coverArtwork").EnumerateArray()).GetString());
        }
        Assert.Empty(cards.Single(item => item.GetProperty("Kind").GetString() == "release-radar").GetProperty("coverArtwork").EnumerateArray());
    }
}
