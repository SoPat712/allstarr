using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task LibraryRecordingLink_SavedAppleMixAndPublicStreamPreferTheAccessibleNativeCopy()
    {
        const string publicId = "ext-apple-download-song-linked-track";
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        var nativeOpened = false;
        using var factory = ClientDetailsFactory(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/Users/Me") return Json(200, """{"Id":"listener"}""");
            if (path == "/Users/listener/Items") return Json(200,
                """{"Items":[{"Id":"native-mix-song","Type":"Audio","Name":"Stay Alive","Artists":["전정국"]}]}""");
            if (path == "/Audio/native-mix-song/stream")
            {
                nativeOpened = true;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x66, 0x4c, 0x61, 0x43]) };
            }
            return Json(200, """{"Items":[],"TotalRecordCount":0}""");
        }, services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
            services.RemoveAll<IBackendLibraryAccessResolver>();
            services.AddSingleton<IBackendLibraryAccessResolver>(provider => new TestBackendLibraryAccess(
                provider.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library"));
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, serviceSong: new()
        {
            Id = publicId,
            ExternalProvider = "apple-download",
            ExternalId = "linked-track",
            Title = "Stay Alive",
            Artist = "JUNG KOOK",
            Duration = 210
        });
        var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new("Jellyfin", "listener")))!.UserId;
        var sourceId = Guid.NewGuid();
        var localId = Guid.NewGuid();
        var linkId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.CanonicalRecordings.AddRange(new CanonicalRecordingRecord
            {
                Id = sourceId,
                CreatedByUserId = owner,
                PublicId = publicId,
                IsProvisional = true,
                CreatedAt = now,
                UpdatedAt = now
            }, new CanonicalRecordingRecord
            {
                Id = localId,
                CreatedByUserId = owner,
                PublicId = CanonicalRecordingRecord.DefaultPublicId(localId),
                CreatedAt = now.AddMinutes(-1),
                UpdatedAt = now,
                Identifiers = [new() { Id = Guid.NewGuid(), RecordingId = localId, Kind = RecordingIdentifierKinds.MusicBrainz,
                    Value = "61dc4fe7-f5e1-43b6-90d4-b568655aba87",
                    Source = "library",
                    CreatedAt = now }]
            });
            db.ProviderTrackIdentities.Add(new()
            {
                Id = linkId,
                CanonicalRecordingId = sourceId,
                ProviderId = "apple-download",
                ResourceKind = ProviderResourceKind.Track,
                ExternalId = "linked-track",
                ExternalIdHash = CanonicalCatalogKeys.Hash("linked-track"),
                Verification = ProviderIdentityVerification.Verified,
                VerificationMethod = "source-use",
                Scope = ProviderIdentityScope.Catalog,
                DecisionVersion = 1,
                CreatedAt = now,
                UpdatedAt = now,
                VerifiedAt = now
            });
            const string payload = """{"Title":"Stay Alive (Prod. SUGA of BTS)","Artist":"JUNG KOOK","DurationMilliseconds":210000}""";
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ProviderId = "apple-download",
                ResourceKind = "track",
                ProviderTrackIdentityId = linkId,
                ExternalIdHash = CanonicalCatalogKeys.Hash("linked-track"),
                PayloadJson = payload,
                PayloadSha256 = CanonicalCatalogKeys.Hash(payload),
                ProviderRevision = "1",
                Protocol = "jellyfin",
                BackendInstanceId = "primary",
                BackendPrincipalId = "listener",
                SnapshotVersion = 1,
                RetrievedAt = now
            });
            var local = await db.LibraryTracks.SingleAsync();
            local.Title = "Stay Alive";
            local.Artist = "전정국";
            local.AlbumArtist = "Jung Kook";
            local.DurationMilliseconds = 210928;
            local.CanonicalRecordingId = localId;
            local.MusicBrainzRecordingId = "61dc4fe7-f5e1-43b6-90d4-b568655aba87";
            await db.SaveChangesAsync();
        }
        var actor = new ProviderActorContext(ProviderActorKind.User, owner, new("jellyfin", "primary", "listener"));
        Assert.Equal(1, (await factory.Services.GetRequiredService<ITrackIdentityService>()
            .LinkLibraryRecordingsAsync(actor, "library-link")).Linked);
        var mixes = factory.Services.GetRequiredService<PersonalMixService>();
        var read = await mixes.GetAsync(owner, mix.Id);
        Assert.True(Assert.Single(read.Songs).Song.IsLocal);
        Assert.Equal("native-mix-song", read.Songs[0].Song.Id);
        await factory.Services.GetRequiredService<IDurableRuntimeSettings>().ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
        var context = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "primary", "listener",
            new AllstarrPrincipal(owner, "jellyfin", "primary", "listener", "Listener", false), "library-link", now.AddMinutes(5), default);
        var published = Assert.Single(await mixes.PublishedAsync(context, default, mix.Id));
        Assert.Equal("native-mix-song", Assert.Single(published.Tracks).BackendItemId);
        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("native-mix-song", item.GetProperty("itemId").GetString());
        Assert.False(item.GetProperty("isExternal").GetBoolean());
        using var stream = await client.GetAsync($"/Audio/{publicId}/stream");
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.True(nativeOpened);
        Assert.Equal(new byte[] { 0x66, 0x4c, 0x61, 0x43 }, await stream.Content.ReadAsByteArrayAsync());
        gateway.VerifyNoOtherCalls();
    }
}
