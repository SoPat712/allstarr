using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Playlists;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("lastfm", "top:1month", false)]
    [InlineData("listenbrainz", "createdfor:weekly-jams", false)]
    [InlineData("lastfm", "top:1month", true)]
    [InlineData("listenbrainz", "createdfor:weekly-jams", true)]
    public async Task ServiceMixCoversMatchAcrossPlaylistImagesAndTheMixRoute(string provider, string sourceId, bool empty)
    {
        var link = Guid.NewGuid();
        var account = Guid.NewGuid();
        byte[]? expected = null;
        string? expectedTag = null;
        foreach (var backend in new[] { "Jellyfin", "Subsonic" })
        {
            var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
            gateway.Setup(item => item.GetPlayableProviderOrderAsync(It.IsAny<ProviderActorContext>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([]);
            using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath switch
            {
                "/Users/Me" => Json(200, """{"Id":"listener"}"""),
                "/rest/ping.view" => Json(200, """{"subsonic-response":{"status":"ok","version":"1.16.1"}}"""),
                "/rest/getSong.view" => Json(200, JsonSerializer.Serialize(new Dictionary<string, object>
                {
                    ["subsonic-response"] = new
                    {
                        status = "ok",
                        version = "1.16.1",
                        song = new { id = QueryHelpers.ParseQuery(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult())["id"].ToString(), title = "Song", artist = "Artist", isDir = false, type = "music", duration = 180 }
                    }
                })),
                _ => throw new InvalidOperationException("Unexpected cover backend request")
            }, services =>
            {
                services.RemoveAll<IProtocolProviderGateway>();
                services.AddSingleton(gateway.Object);
                services.RemoveAll<IBackendLibraryAccessResolver>();
                services.AddSingleton<IBackendLibraryAccessResolver>(container => new TestBackendLibraryAccess(
                    container.GetRequiredService<IDbContextFactory<AllstarrDbContext>>(), "library"));
                services.RemoveAll<MixCoverImageService>();
                services.AddSingleton(new MixCoverImageService(MixCoverRenderer.Render, (reference, _) =>
                {
                    using var bitmap = new SKBitmap(30, 30);
                    bitmap.Erase(new SKColor((byte)(int.Parse(reference[^1..]) * 50), 80, 160));
                    using var image = SKImage.FromBitmap(bitmap);
                    return Task.FromResult<byte[]?>(image.Encode(SKEncodedImageFormat.Png, 100).ToArray());
                }));
            }, backend);
            using var client = CreateClientApiClient(factory);
            var owner = (await factory.Services.GetRequiredService<BackendIdentityResolver>().ResolveAsync(new(backend, "listener")))!.UserId;
            await SeedServiceMixCoverAsync(factory, owner, backend.ToLowerInvariant(), provider, sourceId, link, account, empty ? 0 : 4);
            await factory.Services.GetRequiredService<IDurableRuntimeSettings>()
                .ApplyBatchAsync([new(PersonalMixService.PublishKey, "true")], "fixture");
            var playlist = PlaylistVirtualizationService.CreateProtocolId(link);
            var path = backend == "Jellyfin" ? $"/Items/{playlist}/Images/Primary?api_key=listener-token" :
                $"/rest/getCoverArt.view?id={playlist}-cover-1&u=listener&p=fixture&v=1.16.1&c=fixture";
            using var response = await client.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK,
                $"{backend} cover returned {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            Assert.Equal(empty ? "image/png" : "image/jpeg", response.Content.Headers.ContentType?.MediaType);
            var bytes = await response.Content.ReadAsByteArrayAsync();
            Assert.NotNull(response.Headers.ETag);
            if (backend == "Jellyfin")
            {
                client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
                using var mix = await client.GetAsync($"/allstarr/v1/mixes/{link}/cover");
                Assert.Equal(HttpStatusCode.OK, mix.StatusCode);
                Assert.Equal(bytes, await mix.Content.ReadAsByteArrayAsync());
                Assert.Equal(response.Headers.ETag, mix.Headers.ETag);
                client.DefaultRequestHeaders.Remove("X-Emby-Token");
                using var anonymous = await client.GetAsync($"/Items/{playlist}/Images/Primary");
                Assert.Equal(HttpStatusCode.OK, anonymous.StatusCode);
                Assert.Equal(bytes, await anonymous.Content.ReadAsByteArrayAsync());
                Assert.Equal(response.Headers.ETag, anonymous.Headers.ETag);
                expected = bytes;
                expectedTag = response.Headers.ETag?.Tag;
            }
            else
            {
                Assert.Equal(expected, bytes);
                Assert.Equal(expectedTag, response.Headers.ETag?.Tag);
            }
        }
    }

    private static async Task SeedServiceMixCoverAsync(ProtocolFactory factory, Guid owner, string backend,
        string provider, string sourceId, Guid link, Guid account, int count)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var snapshot = Guid.NewGuid();
        db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = provider, Enabled = true });
        db.PlaylistLinks.Add(new()
        {
            Id = link,
            OwnerUserId = owner,
            ProviderAccountId = account,
            Enabled = true,
            SourceProviderId = provider,
            SourcePlaylistId = sourceId,
            SourcePlaylistIdHash = new('a', 64),
            TargetProtocol = backend,
            TargetBackendInstanceId = "primary",
            Mode = PlaylistLinkMode.Virtual,
            ImportMode = PlaylistImportMode.Linked,
            RuleVersion = "1",
            PolicyVersion = "1"
        });
        db.PlaylistSourceSnapshots.Add(new()
        {
            Id = snapshot,
            OwnerUserId = owner,
            PlaylistLinkId = link,
            ProviderAccountId = account,
            SnapshotVersion = 1,
            ProviderRevision = "1",
            Name = "Weekly songs",
            PayloadSha256 = new('b', 64),
            CorrelationId = "cover-fixture",
            PublishedAt = DateTimeOffset.UtcNow
        });
        for (var position = 0; position < count; position++)
        {
            var metadata = Guid.NewGuid();
            var track = Guid.NewGuid();
            var match = Guid.NewGuid();
            var hash = new string((char)('c' + position), 64);
            db.LibraryTracks.Add(new()
            {
                Id = track,
                OwnerUserId = owner,
                Protocol = backend,
                BackendInstanceId = "primary",
                BackendLibraryId = "library",
                BackendItemId = "native-" + position,
                Title = "Song " + position,
                Artist = "Artist",
                Revision = 1
            });
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = metadata,
                OwnerUserId = owner,
                ProviderAccountId = account,
                ProviderId = provider,
                Protocol = backend,
                BackendInstanceId = "primary",
                BackendPrincipalId = "listener",
                ResourceKind = "track",
                ExternalIdHash = hash,
                SnapshotVersion = 1,
                PayloadSha256 = hash,
                RetrievedAt = DateTimeOffset.UtcNow,
                PayloadJson = JsonSerializer.Serialize(new PlaylistSourceMetadata("Song " + position, ["Artist"]))
            });
            db.TrackMatches.Add(new()
            {
                Id = match,
                OwnerUserId = owner,
                ExternalSnapshotId = metadata,
                LibraryTrackId = track,
                State = TrackMatchState.Accepted,
                DecisionVersion = 1,
                SourceSnapshotVersion = 1,
                Confidence = 1,
                Threshold = .9
            });
            db.PlaylistSourceEntries.Add(new()
            {
                Id = Guid.NewGuid(),
                PlaylistSourceSnapshotId = snapshot,
                ExternalMetadataSnapshotId = metadata,
                SourcePosition = position,
                SourceEntryIdHash = hash,
                PublishedTrackMatchId = match
            });
        }
        await db.SaveChangesAsync();
    }
}
