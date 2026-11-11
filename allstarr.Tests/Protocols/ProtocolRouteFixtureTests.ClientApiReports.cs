using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Theory]
    [InlineData("wrong-song", "wrong-match")]
    [InlineData("wrong-lyrics", "wrong-lyrics")]
    [InlineData("wont-play", "playback")]
    [InlineData("bad-quality", "playback")]
    [InlineData("wrong-info", "metadata")]
    [InlineData("other", "other")]
    public async Task ClientApi_ReportKindsUseExistingInboxKinds(string kind, string expected)
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind, note = "Fixture report" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var receipt = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Single(receipt.AsObject());
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var report = await db.ProblemReports.SingleAsync();
        Assert.Equal(receipt["id"]!.GetValue<Guid>(), report.Id);
        Assert.Equal(expected, report.Kind);
        Assert.Equal("Fixture report", report.Note);
    }

    [Fact]
    public async Task ClientApi_ReportCapturesOwnedPlaybackRequestAndRejectsForeignOrOtherDevices()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, "valid");
        foreach (var (user, device, status) in new[] { ("listener", "phone", 201), ("other", "phone", 404), ("listener", "tablet", 404) })
        {
            using var request = ClientPlaybackRequest("/allstarr/v1/reports", user, device);
            request.Method = HttpMethod.Post;
            request.Content = JsonContent.Create(new { kind = "wont-play", note = "Fixture report", playbackRequestId = id });
            using var response = await client.SendAsync(request);
            Assert.Equal(status, (int)response.StatusCode);
            if (status == 404) await AssertClientApiError(response, 404, "not_found");
        }
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var report = await db.ProblemReports.SingleAsync();
        var context = JsonSerializer.Deserialize<ProblemReportContext>(report.ContextJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(id, context.PlaybackRequestId);
        Assert.Equal("ext-deezer-song-42", context.Song!.ItemId);
        Assert.Equal("deezer", context.Playback!.ProviderId);
        Assert.Equal(report.ActorUserId, context.SubjectUserId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2001)]
    public async Task ClientApi_ReportNoteUsesExistingLengthLimit(int length)
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "other", note = new string('x', length) });
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApi_ReportCapturesCurrentPlaybackOnTheCallersDevice()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        await SetClientPlaybackAsync(factory, "listener", "phone", "ext-deezer-song-42", 12345);
        await SetClientPlaybackAsync(factory, "other", "phone", "ext-deezer-song-private");
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "bad-quality", note = new string('x', 2000) });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var report = await db.ProblemReports.SingleAsync();
        var context = JsonSerializer.Deserialize<ProblemReportContext>(report.ContextJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("ext-deezer-song-42", context.Song!.ItemId);
        Assert.Equal(12345, context.Playback!.PositionTicks);
        Assert.Equal(report.ActorUserId, context.SubjectUserId);
        Assert.Equal(2000, report.Note.Length);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("expired")]
    [InlineData("legacy")]
    [InlineData("mismatched-owner")]
    public async Task ClientApi_ReportCannotAttachUnavailablePlayback(string kind)
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, kind);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "other", note = "Fixture", playbackRequestId = id });
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 404, "not_found");
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"kind\":\"unsupported\",\"note\":\"Fixture\"}")]
    [InlineData("{\"kind\":\"other\",\"note\":null}")]
    [InlineData("{\"kind\":\"other\",\"note\":\"Fixture\",\"subjectUserId\":\"11111111-1111-4111-8111-111111111111\"}")]
    [InlineData("{\"kind\":\"other\",\"note\":\"Fixture\",\"playbackRequestId\":\"not-a-guid\"}")]
    [InlineData("{\"kind\":\"other\",\"note\":\"Fixture\",\"playbackRequestId\":\"{11111111-1111-4111-8111-111111111111}\"}")]
    [InlineData("{\"kind\":\"other\",\"note\":\"Fixture\",\"playbackRequestId\":\"1111111111114111811111111111111\"}")]
    [InlineData("{\"kind\":\"other\",\"note\":\"Fixture\",\"playbackRequestId\":123}")]
    [InlineData("{\"kind\":\"other\",\"note\":\"Fixture\",\"itemId\":\"not a valid id with spaces\"}")]
    public async Task ClientApi_ReportRejectsInvalidInput(string body)
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApi_ReportAcceptsHeaderFormPlaybackRequestIdAndLinksToTrace()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        var id = await SeedClientPlaybackAsync(factory, "valid");
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "wont-play", note = "Fixture report", playbackRequestId = id.ToString("N") });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var report = await db.ProblemReports.SingleAsync();
        var context = JsonSerializer.Deserialize<ProblemReportContext>(report.ContextJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(id, context.PlaybackRequestId);
        Assert.Equal("ext-deezer-song-42", context.Song!.ItemId);
        Assert.Equal("deezer", context.Playback!.ProviderId);
    }

    [Fact]
    public async Task ClientApi_ReportWithOnlyExternalItemIdFillsSongContext()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Loose);
        gateway.Setup(g => g.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "1634147652"))
            .ReturnsAsync(new Song
            {
                Title = "Reported Track",
                Artist = "Reported Artist",
                Album = "Reported Album",
                PreciseDuration = TimeSpan.FromMilliseconds(215000)
            });
        using var factory = ClientPlaybackFactory(gateway);
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "wont-play", note = "Won't play", itemId = "ext-deezer-song-1634147652" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var report = await db.ProblemReports.SingleAsync();
        var context = JsonSerializer.Deserialize<ProblemReportContext>(report.ContextJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(context.PlaybackRequestId);
        Assert.NotNull(context.Song);
        Assert.Equal("ext-deezer-song-1634147652", context.Song.ItemId);
        Assert.Equal("deezer", context.Song.CatalogProviderId);
        Assert.Equal("Reported Track", context.Song.Title);
        Assert.Equal("Reported Artist", context.Song.Artist);
        Assert.Equal("Reported Album", context.Song.Album);
        Assert.Equal(215000, context.Song.DurationMilliseconds);
    }

    [Fact]
    public async Task ClientApi_ReportWhenLookupFailsOrTimesOutStillCreatesReportWithNullFields()
    {
        var gateway = new Mock<IProtocolProviderGateway>(MockBehavior.Loose);
        gateway.Setup(g => g.GetStoredSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "1634147652"))
            .ThrowsAsync(new HttpRequestException("Upstream timeout or failure"));
        gateway.Setup(g => g.GetSongAsync(It.IsAny<ProtocolExecutionContext>(), "deezer", "1634147652"))
            .ThrowsAsync(new HttpRequestException("Upstream timeout or failure"));
        using var factory = ClientPlaybackFactory(gateway);
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "wont-play", note = "Won't play", itemId = "ext-deezer-song-1634147652" });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var report = await db.ProblemReports.SingleAsync();
        var context = JsonSerializer.Deserialize<ProblemReportContext>(report.ContextJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Null(context.PlaybackRequestId);
        Assert.NotNull(context.Song);
        Assert.Equal("ext-deezer-song-1634147652", context.Song.ItemId);
        Assert.Equal("deezer", context.Song.CatalogProviderId);
        Assert.Null(context.Song.Title);
        Assert.Null(context.Song.Artist);
        Assert.Null(context.Song.Album);
        Assert.Null(context.Song.DurationMilliseconds);
    }

    [Fact]
    public async Task ClientApi_ReportRejectsUnknownProviderItemId()
    {
        using var factory = ClientPlaybackFactory();
        using var client = CreateClientApiClient(factory);
        using var request = ClientPlaybackRequest("/allstarr/v1/reports");
        request.Method = HttpMethod.Post;
        request.Content = JsonContent.Create(new { kind = "wont-play", note = "Won't play", itemId = "ext-unknownprovider-song-12345" });
        using var response = await client.SendAsync(request);
        await AssertClientApiError(response, 404, "not_found");
    }
}
