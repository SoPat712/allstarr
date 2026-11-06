using System.Text.Json;
using allstarr.Core.Enrichment;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace allstarr.Tests;

public sealed partial class LibraryPromotionTests
{
    [Fact]
    public async Task PromotionRequestReceipts_IncludeRequesterAndExcludePrivatePlan()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var batch = RequestService();
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        var receipt = Assert.Single(await batch.ListRequestReceiptsAsync(administrator, "pending"));
        Assert.Equal(pending.Id, receipt.Id);
        Assert.Equal("Owner", receipt.Requester.DisplayName);
        var json = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain("RequestJson", json);
        Assert.DoesNotContain("SongsJson", json);
        Assert.Empty(await batch.ListRequestReceiptsAsync(await AddPromotionUserAsync(false), "pending"));
    }

    [Fact]
    public async Task PromotionRequestPreview_UsesRequesterProfileAndDurableSongs()
    {
        await SetPromotionRoleAsync(false, false);
        var administrator = await AddPromotionUserAsync(true);
        var catalog = new Mock<IPromotionCatalog>();
        catalog.Setup(item => item.ResolveAsync(It.IsAny<allstarr.Core.Protocols.ProtocolExecutionContext>(), recordingId, "album", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new Song { CanonicalRecordingId = recordingId, Title = "Song", Album = "Album", Artist = "Artist",
                ExternalProvider = "qobuz", ExternalId = "song", Codec = "flac", Container = "flac", BitDepth = 16, SampleRate = 44100 } });
        var batch = BatchService(catalog.Object, Mock.Of<IPromotionAcquisition>());
        var pending = (await batch.StartOrRequestAsync(Context, Request())).Request!;
        await new PromotionProfiles(factory).SaveMineAsync(Context, new() { NamingScript = "Requester/%title%.%_extension%", FakeLosslessCheck = false });
        await new PromotionProfiles(factory).SaveMineAsync(administrator, new() { NamingScript = "Administrator/%title%.%_extension%" });
        catalog.Reset();
        var preview = await batch.PreviewRequestAsync(administrator, pending.Id);
        Assert.Contains("Requester", preview.DestinationPath);
        Assert.DoesNotContain("Administrator", preview.DestinationPath);
        Assert.False(preview.CanAddDirectly);
        Assert.False(Assert.Single(preview.ProfileSteps, step => step.Key == "quality").Enabled);
        Assert.Equal("Album", preview.Album!.Title);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.PreviewRequestAsync(Context, pending.Id));
        await using var db = factory.CreateDbContext();
        (await db.Users.SingleAsync(item => item.Id == owner)).Enabled = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => batch.PreviewRequestAsync(administrator, pending.Id));
        Assert.Empty(await db.Set<PromotionRunRecord>().ToArrayAsync());
    }
}
