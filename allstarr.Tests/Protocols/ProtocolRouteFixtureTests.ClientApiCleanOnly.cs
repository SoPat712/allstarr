using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiCleanOnlyMatchReviewWithholdsExplicitStoredSource()
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), ClientCleanOnlyServices);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        await SeedClientDetailsAsync(factory, "listener", "explicit", personal: false);
        using var response = await client.GetAsync("/allstarr/v1/items/ext-deezer-song-explicit/match");
        await AssertClientApiError(response, 404, "not_found");
    }

    [Theory]
    [InlineData("Explicit")]
    [InlineData(null)]
    public async Task ClientApiCleanOnlyLyricsAndSourcesWithholdUnverifiedNativeItems(string? rating)
    {
        using var factory = ClientApiFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""")
            : ItemLookup(JsonSerializer.Serialize(new { Id = "native", Type = "Audio", OfficialRating = rating })), ClientCleanOnlyServices);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        foreach (var path in new[] { "/allstarr/v1/items/native/lyrics", "/allstarr/v1/items/native/lyrics/sources" })
        {
            using var response = await client.GetAsync(path);
            await AssertClientApiError(response, 404, "not_found");
        }
    }

    [Fact]
    public async Task ClientApiCleanOnlyPlaybackWithholdsPreviouslyExplicitPlayback()
    {
        var gateway = new Mock<IProtocolProviderGateway>();
        gateway.Setup(item => item.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "42"))
            .ReturnsAsync(new allstarr.Models.Domain.Song { Id = "ext-deezer-song-42", ExplicitContentLyrics = 1 });
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            ClientCleanOnlyServices(services);
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, "valid");
        await SetClientPlaybackAsync(factory, "listener", "phone", "ext-deezer-song-42");
        foreach (var path in new[] { $"/allstarr/v1/playback/{id:N}", "/allstarr/v1/playback/current" })
        {
            using var request = ClientPlaybackRequest(path);
            using var response = await client.SendAsync(request);
            await AssertClientApiError(response, 404, "not_found");
        }
    }

    [Fact]
    public async Task ClientApiCleanOnlyDetailsWithholdExplicitAndUnknownNativeItems()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""")
            : Json(200, """{"Items":[{"Id":"clean","Type":"Audio","OfficialRating":"Clean"},{"Id":"explicit","Type":"Audio","OfficialRating":"Explicit"},{"Id":"unknown","Type":"Audio"}]}"""), ClientCleanOnlyServices);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "clean", "explicit", "unknown" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var clean = Assert.Single(body.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("clean", clean.GetProperty("id").GetString());
        Assert.False(clean.GetProperty("explicit").GetBoolean());
        Assert.Equal(["explicit", "unknown"], body.RootElement.GetProperty("missing").EnumerateArray().Select(item => item.GetString()!).ToArray());
    }

    [Theory]
    [InlineData("Explicit", true)]
    [InlineData("Clean", false)]
    [InlineData("Edited", false)]
    [InlineData(null, null)]
    public async Task ClientApiNativeDetailsPreserveExplicitRatingForLyricsDisplays(string? rating, bool? expected)
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""")
            : Json(200, JsonSerializer.Serialize(new { Items = new[] { new { Id = "native", Type = "Audio", OfficialRating = rating } } })));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "native" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("explicit").Deserialize<bool?>());
    }

    [Fact]
    public async Task ClientApiCleanOnlyDetailsShowEditedRecordingAsClean()
    {
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), ClientCleanOnlyServices);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "edited", personal: false);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var snapshot = await db.ExternalMetadataSnapshots.SingleAsync(item => item.ResourceKind == "track");
            var clean = JsonSerializer.Deserialize<ExternalMetadataSnapshotRecord>(JsonSerializer.Serialize(snapshot))!;
            clean.Id = Guid.NewGuid();
            clean.SnapshotVersion++;
            clean.RetrievedAt = snapshot.RetrievedAt.AddSeconds(1);
            clean.PayloadJson = snapshot.PayloadJson.Replace("\"ExplicitContentLyrics\":1", "\"ExplicitContentLyrics\":3");
            clean.PayloadSha256 = allstarr.Core.Matching.CanonicalCatalogKeys.Hash(clean.PayloadJson);
            db.ExternalMetadataSnapshots.Add(clean);
            (await db.CanonicalRecordings.SingleAsync(item => item.Id == recording)).IsExplicit = true;
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-edited" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.False(Assert.Single(body.RootElement.GetProperty("items").EnumerateArray()).GetProperty("explicit").GetBoolean());
    }

    private static void ClientCleanOnlyServices(IServiceCollection services)
    {
        var policies = new Mock<IEffectiveProviderPolicyResolver>();
        policies.Setup(item => item.ResolveForUserAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid user, CancellationToken _) => new EffectiveProviderPolicySnapshot(
                ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>>.Empty,
                ImmutableHashSet<string>.Empty, AudioQualityPolicy.DefaultStep, .07)
            { UserId = user, Preferences = new("CleanOnly"), PreferenceRevision = "clean" });
        services.RemoveAll<IEffectiveProviderPolicyResolver>();
        services.AddSingleton(policies.Object);
    }
}
