using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Matching;
using Microsoft.EntityFrameworkCore;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_MixItemUsesAccessibleLocalRecordingAfterLinking()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        using var fixtures = new ProviderIconFiles();
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "mix-song", personal: false);
        var mix = await SeedClientMixAsync(factory, serviceSong: ClientMixSong());
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync(track => track.BackendItemId == "native-mix-song");
            local.CanonicalRecordingId = recording;
            await db.SaveChangesAsync();
        }
        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.Equal("native-mix-song", item.GetProperty("itemId").GetString());
        Assert.False(item.GetProperty("isExternal").GetBoolean());
        AssertLibraryProvider(item.GetProperty("provider"));
        gateway.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ClientApi_ItemDetailsUsesAccessibleLocalRecordingAfterLinking()
    {
        using var factory = ClientMixFactory(new Mock<IProtocolProviderGateway>(MockBehavior.Strict));
        using var client = CreateClientApiClient(factory);
        using var fixtures = new ProviderIconFiles();
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "linked-details", personal: false);
        await SeedClientMixAsync(factory);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            var local = await db.LibraryTracks.SingleAsync(track => track.BackendItemId == "native-mix-song");
            local.CanonicalRecordingId = recording;
            await db.SaveChangesAsync();
        }
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { "ext-deezer-song-linked-details" } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.False(item.GetProperty("isExternal").GetBoolean());
        AssertLibraryProvider(item.GetProperty("provider"));
        Assert.Equal(recording.ToString("D"), item.GetProperty("canonicalRecordingId").GetString());
    }

    [Theory]
    [InlineData("qobuz")]
    [InlineData(null)]
    public async Task ClientApi_CanonicalMixItemReportsProjectedStreamingProvider(string? serving)
    {
        var recording = Guid.NewGuid();
        var id = CanonicalRecordingRecord.DefaultPublicId(recording);
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", recording.ToString("D")))
            .ReturnsAsync((Song?)null);
        gateway.Setup(service => service.GetStreamingProviderAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", recording.ToString("D")))
            .ReturnsAsync(serving);
        using var factory = ClientMixFactory(gateway);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var mix = await SeedClientMixAsync(factory, serviceSong: new()
        {
            Id = id,
            ExternalProvider = "allstarr",
            ExternalId = recording.ToString("D"),
            Title = "Song",
            Artist = "Artist"
        });
        using var response = await client.GetAsync($"/allstarr/v1/mixes/{mix.Id}/items");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("isExternal").GetBoolean());
        if (serving == null) Assert.Equal(JsonValueKind.Null, item.GetProperty("provider").ValueKind);
        else Assert.Equal(serving, item.GetProperty("provider").GetProperty("id").GetString());
        gateway.VerifyAll();
    }

    [Theory]
    [InlineData("qobuz")]
    [InlineData(null)]
    public async Task ClientApi_CanonicalItemDetailsReportsProjectedStreamingProvider(string? serving)
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Strict);
        gateway.Setup(service => service.GetStreamingProviderAsync(It.IsAny<ProtocolExecutionContext>(), "allstarr", It.IsAny<string>()))
            .ReturnsAsync(serving);
        using var factory = ClientDetailsFactory(_ => Json(200, """{"Id":"listener"}"""), services =>
        {
            services.RemoveAll<IProtocolProviderGateway>();
            services.AddSingleton(gateway.Object);
        });
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        var recording = await SeedClientDetailsAsync(factory, "listener", "projected", personal: false);
        using var response = await client.PostAsJsonAsync("/allstarr/v1/items/details", new { ids = new[] { CanonicalRecordingRecord.DefaultPublicId(recording) } });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var item = Assert.Single(json.RootElement.GetProperty("items").EnumerateArray());
        Assert.True(item.GetProperty("isExternal").GetBoolean());
        if (serving == null) Assert.Equal(JsonValueKind.Null, item.GetProperty("provider").ValueKind);
        else Assert.Equal(serving, item.GetProperty("provider").GetProperty("id").GetString());
        gateway.VerifyAll();
    }
}
