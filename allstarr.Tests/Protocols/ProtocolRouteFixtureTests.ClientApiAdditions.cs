using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using allstarr.Core.Enrichment;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_AdditionRetryHeaderIsAllowedByCors()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var request = new HttpRequestMessage(HttpMethod.Options, "/allstarr/v1/library/additions");
        request.Headers.Add("Origin", "https://client.example");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "Content-Type,Idempotency-Key");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Contains("idempotency-key", string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ClientApi_AdditionPostBindsTheRetryHeaderAndReturnsTheSavedAddition()
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        var (owner, _) = await SeedClientMeAsync(factory, false);
        var id = Guid.CreateVersion7();
        const string itemId = "ext-spotify-song-track";
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            db.PromotionRequests.Add(new()
            {
                Id = id,
                RequesterUserId = owner,
                RecordingId = Guid.NewGuid(),
                IdentityKey = "retry",
                RequestJson = JsonSerializer.Serialize(new PromoteSongRequest(Guid.NewGuid(), Guid.Empty, Guid.Empty, "music", "retry")
                { Unit = "song", RequestedItemId = itemId }),
                Unit = "song",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = owner,
                Action = "library-addition.accepted",
                CorrelationId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("tap"))),
                SubjectId = id.ToString("D"),
                DetailsJson = JsonSerializer.Serialize(new allstarr.Core.ClientApi.AddClientLibraryItem(itemId, "song")),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "tap");
        using var response = await client.PostAsJsonAsync("/allstarr/v1/library/additions", new { itemId, unit = "song" });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(id, body.GetProperty("id").GetGuid());
        Assert.Equal(itemId, body.GetProperty("requestedItemId").GetString());
        Assert.False(body.TryGetProperty("libraryItemId", out _));
        using var changed = await client.PostAsJsonAsync("/allstarr/v1/library/additions", new { itemId, unit = "album" });
        await AssertClientApiError(changed, 409, "conflict");
    }

    [Fact]
    public async Task ClientApi_PreferencesArePersonalPersistAndValidate()
    {
        using var factory = ClientMeFactory(administrator: true);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var initial = await client.GetAsync("/allstarr/v1/preferences");
        Assert.Equal("keep", (await initial.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heartAction").GetString());
        foreach (var action in new[] { "heart", "addToLibrary", "keep" })
        {
            using var updated = await client.PatchAsJsonAsync("/allstarr/v1/preferences", new { heartAction = action });
            Assert.Equal(HttpStatusCode.OK, updated.StatusCode);
            using var restored = await client.GetAsync("/allstarr/v1/preferences");
            Assert.Equal(action, (await restored.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heartAction").GetString());
        }
        using var own = await client.PatchAsJsonAsync("/allstarr/v1/preferences", new { heartAction = "heart" });
        client.DefaultRequestHeaders.Remove("X-Emby-Token");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "other-token");
        using var other = await client.GetAsync("/allstarr/v1/preferences");
        Assert.Equal("keep", (await other.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("heartAction").GetString());
        using var invalid = await client.PatchAsJsonAsync("/allstarr/v1/preferences", new { heartAction = "download" });
        await AssertClientApiError(invalid, 400, "invalid_request");
        await using var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        Assert.Equal("Library:HeartAction", (await db.RuntimeSettings.Where(item => item.OwnerUserId != null).SingleAsync()).Key);
    }

    [Theory]
    [InlineData("preferences")]
    [InlineData("library/additions")]
    public async Task ClientApi_AdditionsAndPreferencesRequireListenerAndMusicPort(string path)
    {
        using var factory = ClientMeFactory();
        using var client = CreateClientApiClient(factory);
        using var anonymous = await client.GetAsync("/allstarr/v1/" + path);
        await AssertClientApiError(anonymous, 401, "unauthorized");
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var dashboard = await client.GetAsync("http://localhost:5275/allstarr/v1/" + path);
        await AssertClientApiError(dashboard, 404, "not_found");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClientApi_AdditionCapabilitiesUseStoredApprovalRoles(bool administrator)
    {
        using var factory = ClientMeFactory(administrator);
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body.GetProperty("features").GetProperty("libraryAdditions").GetBoolean());
        Assert.True(body.GetProperty("features").GetProperty("preferences").GetBoolean());
        var additions = body.GetProperty("libraryAdditions");
        Assert.Equal(administrator, additions.GetProperty("canAddDirectly").GetBoolean());
        Assert.Equal(!administrator, additions.GetProperty("requiresApproval").GetBoolean());
        Assert.Equal("album", additions.GetProperty("defaultUnit").GetString());
    }

    [Fact]
    public async Task ClientApi_AdministratorSeesOnlyOwnAdditionRequests()
    {
        using var factory = ClientMeFactory(administrator: true);
        using var client = CreateClientApiClient(factory);
        var (owner, other) = await SeedClientMeAsync(factory, true);
        await using (var db = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync())
        {
            foreach (var listener in new[] { owner, other })
                db.PromotionRequests.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    RequesterUserId = listener,
                    RecordingId = Guid.NewGuid(),
                    IdentityKey = listener.ToString(),
                    RequestJson = "{}",
                    SongsJson = "[]",
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");
        using var response = await client.GetAsync("/allstarr/v1/library/additions");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var item = Assert.Single((await response.Content.ReadFromJsonAsync<JsonElement>()).EnumerateArray());
        Assert.Equal("waitingForApproval", item.GetProperty("state").GetString());
        using var foreign = await factory.Services.GetRequiredService<IDbContextFactory<AllstarrDbContext>>().CreateDbContextAsync();
        var id = await foreign.PromotionRequests.Where(row => row.RequesterUserId == other).Select(row => row.Id).SingleAsync();
        using var cancelled = await client.PostAsync($"/allstarr/v1/library/additions/{id}/cancel", null);
        await AssertClientApiError(cancelled, 404, "not_found");
    }
}
