using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class FavoriteActionPipelineTests
{
    [Fact]
    public async Task PromotionMoveDoesNotHideRenewedSharedFavorite()
    {
        var request = await CreateSharedPromotionFavoriteAsync();
        var reader = new FavoriteViewReader(_factory);
        var original = Assert.Single(await reader.ReadAsync(request.ExecutionContext));
        await RecordPromotionMoveAsync(original);
        Assert.Empty(await reader.ReadAsync(request.ExecutionContext));

        await _pipeline.RecordAsync(request with { Operation = FavoriteOperation.Unfavorite, SourceRevision = "off" });
        await _pipeline.RecordAsync(request with { SourceRevision = "renewed" });

        var renewed = Assert.Single(await reader.ReadAsync(request.ExecutionContext));
        Assert.Equal(original.ItemId, renewed.ItemId);
        Assert.NotNull(original.HandoverVersion);
        Assert.NotEqual(original.HandoverVersion, renewed.HandoverVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PromotionMoveDoesNotHideSharedFavoriteForChangedBackendIdentity(bool replaceInstance)
    {
        var request = await CreateSharedPromotionFavoriteAsync();
        var reader = new FavoriteViewReader(_factory);
        var original = Assert.Single(await reader.ReadAsync(request.ExecutionContext));
        await RecordPromotionMoveAsync(original);
        Assert.Empty(await reader.ReadAsync(request.ExecutionContext));

        UserRecord replacement;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            replacement = await db.Users.SingleAsync(item => item.Id == _userId);
            if (replaceInstance) replacement.BackendInstanceId = "replacement-instance";
            else replacement.BackendPrincipalId = "replacement-principal";
            var member = await db.Set<ProviderFavoriteLinkMemberRecord>().SingleAsync(item => item.UserId == _userId);
            db.Remove(member);
            await db.SaveChangesAsync();
            db.Add(new ProviderFavoriteLinkMemberRecord
            {
                UserId = replacement.Id,
                LinkId = member.LinkId,
                Protocol = replacement.BackendType,
                BackendInstanceId = replacement.BackendInstanceId,
                BackendPrincipalId = replacement.BackendPrincipalId,
                IdentityFingerprint = FavoriteLinkScope.Fingerprint(replacement),
                ConfirmedDisplayName = replacement.DisplayName
            });
            await db.SaveChangesAsync();
        }
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, replacement.BackendInstanceId,
            replacement.BackendPrincipalId, new AllstarrPrincipal(replacement.Id, replacement.BackendType,
                replacement.BackendInstanceId, replacement.BackendPrincipalId, replacement.DisplayName, false),
            "promotion-replacement", _clock.UtcNow.AddMinutes(1), default);

        var current = Assert.Single(await reader.ReadAsync(execution));
        Assert.Equal(original.ItemId, current.ItemId);
        Assert.NotEqual(original.HandoverVersion, current.HandoverVersion);
    }

    private async Task<FavoriteMutationRequest> CreateSharedPromotionFavoriteAsync()
    {
        var request = Request(FavoriteOperation.Favorite, "promotion") with { ItemId = "ext-fixture-song-promoted" };
        var receipt = await _pipeline.RecordAsync(request);
        await using var db = await _factory.CreateDbContextAsync();
        var user = await db.Users.SingleAsync(item => item.Id == _userId);
        var peer = await db.Users.SingleAsync(item => item.Id == _otherUserId);
        peer.BackendType = "subsonic";
        var linkId = Guid.CreateVersion7();
        db.Add(new ProviderFavoriteLinkRecord
        {
            Id = linkId,
            PairKey = FavoriteLinkScope.Hash(user.Id.ToString("N"), peer.Id.ToString("N")),
            ConfirmedByUserId = user.Id,
            ConfirmationVersion = 1,
            CreatedAt = _clock.UtcNow,
            UnionJobId = receipt.JobId,
            UnionAttempt = 1,
            UnionAdministratorFingerprint = FavoriteLinkScope.Fingerprint(user),
            CompletedAt = _clock.UtcNow,
            Revision = 1
        });
        foreach (var member in new[] { user, peer })
            db.Add(new ProviderFavoriteLinkMemberRecord
            {
                UserId = member.Id,
                LinkId = linkId,
                Protocol = member.BackendType,
                BackendInstanceId = member.BackendInstanceId,
                BackendPrincipalId = member.BackendPrincipalId,
                IdentityFingerprint = FavoriteLinkScope.Fingerprint(member),
                ConfirmedDisplayName = member.DisplayName,
                UpperAt = member.Id == user.Id ? _clock.UtcNow : null,
                UpperId = member.Id == user.Id ? receipt.EventId : null
            });
        db.Add(FavoriteLinkScope.NewState(linkId, FavoriteLinkScope.Key(request.ItemId)!, true, _clock.UtcNow));
        await db.SaveChangesAsync();
        return request;
    }

    private async Task RecordPromotionMoveAsync(FavoriteViewItem item)
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.AuditEvents.Add(new()
        {
            Id = Guid.CreateVersion7(),
            ActorUserId = _userId,
            Category = "library-promotion",
            Action = "promotion.favorite-moved",
            Outcome = "succeeded",
            CorrelationId = "promotion",
            DetailsJson = JsonSerializer.Serialize(new
            {
                RecordingId = Guid.CreateVersion7(),
                BackendItemId = "native-promoted",
                Versions = new Dictionary<string, string?> { [item.ItemId] = item.HandoverVersion }
            }),
            CreatedAt = _clock.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
