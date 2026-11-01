using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;
using SkiaSharp;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    private const string AnonymousCoverId = "ext-allstarr-song-01a18a9f-eab7-7b33-9215-e57fe3837fac";
    private const string AnonymousCoverQuery = "fillHeight=96&fillWidth=96&quality=96&v=" + AnonymousCoverId + "-art-v3";

    [Theory]
    [InlineData("song", true)]
    [InlineData("song", false)]
    [InlineData("album", true)]
    [InlineData("artist", true)]
    public async Task AnonymousPrimaryArtwork_UsesStoredCoverWithoutProviderCalls(string kind, bool songCover)
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var original = AnonymousCoverBytes();
        var imageRequests = 0;
        using var factory = new ProtocolFactory("Jellyfin", request =>
        {
            Assert.Equal("https://images.example.test/stored.png", request.RequestUri!.AbsoluteUri);
            imageRequests++;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(original) { Headers = { ContentType = new("image/png") } } };
        }, services =>
        {
            services.RemoveAll<IProviderRouter>();
            services.AddSingleton(router.Object);
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready, "fixture");
        await SeedAnonymousCoverAsync(factory, songCover);
        var id = kind switch
        {
            "album" => "ext-apple-download-album-release",
            "artist" => "ext-apple-download-artist-credit:stored",
            _ => AnonymousCoverId
        };
        using var response = await client.GetAsync($"/Items/{id}/Images/Primary?{AnonymousCoverQuery}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        using var resized = SKBitmap.Decode(bytes);
        Assert.Equal(96, resized.Width);
        Assert.Equal(48, resized.Height);
        Assert.True(bytes.Length < original.Length);
        using var repeated = await client.GetAsync($"/Items/{id}/Images/Primary?{AnonymousCoverQuery}");
        Assert.Equal(bytes, await repeated.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, imageRequests);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("Primary", true)]
    [InlineData("Backdrop", true)]
    [InlineData("Primary", false)]
    public async Task AnonymousMissingArtwork_IsNotFoundWithoutProviderCalls(string imageType, bool known)
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        var requests = 0;
        using var factory = new ProtocolFactory("Jellyfin", _ =>
        {
            requests++;
            throw new InvalidOperationException("No image fetch expected");
        }, services =>
        {
            services.RemoveAll<IProviderRouter>();
            services.AddSingleton(router.Object);
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready, "fixture");
        if (known) await SeedAnonymousCoverAsync(factory, false, false);
        using var response = await client.GetAsync($"/Items/{AnonymousCoverId}/Images/{imageType}?{AnonymousCoverQuery}");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(0, requests);
        router.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("", 1500, 750)]
    [InlineData("?fillWidth=96", 96, 48)]
    [InlineData("?fillHeight=96", 192, 96)]
    [InlineData("?width=96", 96, 48)]
    [InlineData("?height=96", 192, 96)]
    [InlineData("/0/tag/png/300/300/0/0?fillWidth=96&fillHeight=96", 96, 48)]
    [InlineData("/0/tag/png/300/300/0/0?width=96&height=96", 96, 48)]
    public async Task AnonymousPrimaryArtwork_ReusesListenerCacheAndSizeVariants(string suffix, int width, int height)
    {
        var router = new Mock<IProviderRouter>(MockBehavior.Strict);
        using var factory = new ProtocolFactory("Jellyfin", _ => throw new InvalidOperationException("Cached artwork must avoid upstream"), services =>
        {
            services.RemoveAll<IProviderRouter>();
            services.AddSingleton(router.Object);
        });
        using var client = factory.CreateClient();
        factory.Services.GetRequiredService<DurableStorageState>().Set(DurableStorageReadiness.Ready, "fixture");
        var owner = await SeedAnonymousCoverAsync(factory, false, false);
        var resolver = factory.Services.GetRequiredService<IMediaAssetResolver>();
        await resolver.ResolveAsync(new(owner, null, "allstarr", "song", AnonymousCoverId[18..]),
            _ => Task.FromResult<MediaAssetSource?>(new(AnonymousCoverBytes(), "image/png")), 10 * 1024 * 1024);
        using var response = await client.GetAsync($"/Items/{AnonymousCoverId}/Images/Primary{suffix}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var bitmap = SKBitmap.Decode(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(width, bitmap.Width);
        Assert.Equal(height, bitmap.Height);
        using var backdrop = await client.GetAsync($"/Items/{AnonymousCoverId}/Images/Backdrop");
        Assert.Equal(HttpStatusCode.NotFound, backdrop.StatusCode);
        Assert.True(backdrop.Headers.CacheControl?.NoStore);
        router.VerifyNoOtherCalls();
    }

    private static byte[] AnonymousCoverBytes()
    {
        using var bitmap = new SKBitmap(1500, 750);
        bitmap.Erase(SKColors.Red);
        using var image = SKImage.FromBitmap(bitmap);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return encoded.ToArray();
    }

    private static async Task<Guid> SeedAnonymousCoverAsync(ProtocolFactory factory, bool songCover, bool albumCover = true)
    {
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var owner = Guid.NewGuid();
        db.Users.Add(new() { Id = owner, BackendType = "jellyfin", BackendInstanceId = "primary", BackendPrincipalId = "cover-listener" });
        var account = Guid.NewGuid();
        db.ProviderAccounts.Add(new() { Id = account, OwnerUserId = owner, ProviderId = "apple-musickit", Enabled = true });
        var recording = Guid.Parse(AnonymousCoverId[18..]);
        db.CanonicalRecordings.Add(new() { Id = recording, CreatedByUserId = owner, PublicId = AnonymousCoverId });
        var identity = Guid.NewGuid();
        db.ProviderTrackIdentities.Add(new()
        {
            Id = identity,
            CanonicalRecordingId = recording,
            ProviderId = "apple-musickit",
            ExternalId = "stored",
            ExternalIdHash = CoverHash("stored"),
            ResourceKind = ProviderResourceKind.Track,
            Scope = ProviderIdentityScope.Account,
            ProviderAccountId = account,
            Verification = ProviderIdentityVerification.Verified,
            VerificationMethod = "provider",
            DecisionVersion = 1
        });
        AddSnapshot("track", "stored", new
        {
            Title = "Song",
            Artist = "Artist",
            ExternalProvider = "apple-musickit",
            ExternalId = "stored",
            AlbumId = "ext-apple-download-album-release",
            CoverArtUrl = songCover ? "https://images.example.test/stored.png" : null
        }, identity);
        AddSnapshot("album", "release", new { Id = "ext-apple-download-album-release", CoverArtUrl = albumCover ? "https://images.example.test/stored.png" : null });
        AddSnapshot("artist", "credit:stored", new { Id = "ext-apple-download-artist-credit:stored", ImageUrl = "https://images.example.test/stored.png" });
        await db.SaveChangesAsync();
        return owner;

        void AddSnapshot(string kind, string id, object value, Guid? trackIdentity = null)
        {
            var payload = JsonSerializer.Serialize(value);
            db.ExternalMetadataSnapshots.Add(new()
            {
                Id = Guid.NewGuid(),
                OwnerUserId = owner,
                ProviderAccountId = account,
                ProviderId = "apple-musickit",
                ResourceKind = kind,
                ExternalIdHash = CoverHash(id),
                ProviderTrackIdentityId = trackIdentity,
                SnapshotVersion = 1,
                BackendInstanceId = "primary",
                BackendPrincipalId = "cover-listener",
                Protocol = "jellyfin",
                PayloadJson = payload,
                PayloadSha256 = CoverHash(payload),
                RetrievedAt = DateTimeOffset.UtcNow
            });
        }
    }

    private static string CoverHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
