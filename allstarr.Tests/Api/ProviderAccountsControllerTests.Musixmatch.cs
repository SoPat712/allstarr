using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Secrets;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class ProviderAccountsControllerTests
{
    [Theory]
    [InlineData("{}", "10.0.0")]
    [InlineData("{\"userToken\":\"\"}", "10.0.0")]
    [InlineData("{\"appVersion\":\"10.1.1\"}", "10.1.1")]
    public async Task Musixmatch_ReplacementRetainsOmittedTokenAndAppFields(string replacement, string version)
    {
        var controller = Controller(Session(_userId));
        using var initial = JsonDocument.Parse("{\"userToken\":\"saved-fixture-token\",\"appVersion\":\"10.0.0\",\"appBuild\":\"2025120901\"}");
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "musixmatch", Secret = initial.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync();
        using var input = JsonDocument.Parse(replacement);
        Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = input.RootElement }));
        using var lease = await _secretStore.OpenAsync(account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:musixmatch:{account.Id:N}"));
        using var saved = JsonDocument.Parse(lease.Value);
        Assert.Equal("saved-fixture-token", saved.RootElement.GetProperty("userToken").GetString());
        Assert.Equal(version, saved.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal("2025120901", saved.RootElement.GetProperty("appBuild").GetString());
    }

    [Fact]
    public async Task Musixmatch_CreateEncryptsOnlyNormalizedFields()
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            userToken = "Debug Info\r\n[DeviceId]: private-device\r\n[AppBuild]: 2025120901\r\n[UserToken]: private-fixture-token\r\n[AppVersion]: 10.1.1",
            unused = "private-unused"
        }));
        using var http = new HttpClient();
        var registration = MusixmatchLyricsCapabilityAdapter.CreateRegistration(
            new MusixmatchLyricsCapabilityAdapter(http, new EncryptedProviderAccountSecretAccessor(_secretStore), _cache));
        var controller = Controller(Session(_userId), providerRegistry: new ProviderRegistry([registration]));
        var result = Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "musixmatch", Secret = input.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.SingleAsync();
        Assert.Equal("{}", account.SettingsJson);
        using var lease = await _secretStore.OpenAsync(account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:musixmatch:{account.Id:N}"));
        using var normalized = JsonDocument.Parse(lease.Value);
        Assert.Equal(3, normalized.RootElement.EnumerateObject().Count());
        Assert.Equal("private-fixture-token", normalized.RootElement.GetProperty("userToken").GetString());
        Assert.Equal("10.1.1", normalized.RootElement.GetProperty("appVersion").GetString());
        Assert.Equal("2025120901", normalized.RootElement.GetProperty("appBuild").GetString());
        Assert.DoesNotContain("private-device", lease.ReadUtf8());
        Assert.DoesNotContain("private-unused", lease.ReadUtf8());
        Assert.DoesNotContain("private-fixture-token", Encoding.UTF8.GetString((await db.SecretVersions.SingleAsync()).Ciphertext));
        Assert.DoesNotContain("private-fixture-token", JsonSerializer.Serialize(result.Value));
        var listed = Assert.IsType<OkObjectResult>(await controller.List());
        Assert.DoesNotContain("private-fixture-token", JsonSerializer.Serialize(listed.Value));
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(listed.Value));
        var summary = Assert.Single(response.RootElement.GetProperty("accounts").EnumerateArray());
        Assert.Contains("userToken", summary.GetProperty("configuredFields").EnumerateArray().Select(item => item.GetString()));
        Assert.False(summary.GetProperty("configuration").TryGetProperty("userToken", out _));
    }

    [Theory]
    [InlineData("[DeviceId]: private-marker\n[AppVersion]: 10.1.1")]
    [InlineData("private-marker is arbitrary prose")]
    [InlineData("[UserToken]: private-marker\n[AppBuild]: invalid value")]
    public async Task Musixmatch_CreateRejectsInvalidInputBeforeStorage(string value)
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { userToken = value }));
        var result = Assert.IsType<BadRequestObjectResult>(await Controller(Session(_userId)).Create(new()
        { ProviderId = "musixmatch", Secret = input.RootElement }));
        Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(result.Value));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Empty(await db.ProviderAccounts.ToArrayAsync());
        Assert.Empty(await db.SecretReferences.ToArrayAsync());
        Assert.Empty(await db.SecretVersions.ToArrayAsync());
    }

    [Fact]
    public async Task Musixmatch_ReplacementNormalizesAndRejectsInvalidWithoutChangingSecret()
    {
        var controller = Controller(Session(_userId));
        using var initial = JsonDocument.Parse("{\"userToken\":\"old-fixture-token\",\"appVersion\":\"10.0.0\"}");
        Assert.IsType<CreatedAtActionResult>(await controller.Create(new()
        { ProviderId = "musixmatch", Secret = initial.RootElement }));
        await using var db = await _factory.CreateDbContextAsync();
        var account = await db.ProviderAccounts.AsNoTracking().SingleAsync();
        using var invalid = JsonDocument.Parse("{\"userToken\":\"private-marker invalid\"}");
        var rejected = Assert.IsType<BadRequestObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = invalid.RootElement }));
        Assert.DoesNotContain("private-marker", JsonSerializer.Serialize(rejected.Value));
        Assert.Equal(1, (await db.SecretReferences.SingleAsync()).ActiveVersion);
        Assert.Equal(account.Revision, (await db.ProviderAccounts.AsNoTracking().SingleAsync()).Revision);
        using var replacement = JsonDocument.Parse(JsonSerializer.Serialize(new
        { userToken = "[AppBuild]: 2025120901\n[UserToken]: new-fixture-token\n[DeviceId]: private-device" }));
        var result = Assert.IsType<OkObjectResult>(await controller.ReplaceSecret(account.Id, new() { Secret = replacement.RootElement }));
        using var lease = await _secretStore.OpenAsync(account.SecretReferenceId!.Value,
            new SecretAccessContext(_userId, $"provider-account:musixmatch:{account.Id:N}"));
        using var normalized = JsonDocument.Parse(lease.Value);
        Assert.Equal("new-fixture-token", normalized.RootElement.GetProperty("userToken").GetString());
        Assert.Equal("2025120901", normalized.RootElement.GetProperty("appBuild").GetString());
        Assert.False(normalized.RootElement.TryGetProperty("appVersion", out _));
        Assert.DoesNotContain("private-device", lease.ReadUtf8());
        Assert.DoesNotContain("new-fixture-token", JsonSerializer.Serialize(result.Value));
        Assert.Equal(account.Revision + 1, (await db.ProviderAccounts.AsNoTracking().SingleAsync()).Revision);
        Assert.Equal(2, await db.SecretVersions.CountAsync());
    }

    [Fact]
    public async Task Musixmatch_CreateWithoutSecretPreservesUnconfiguredAccount()
    {
        Assert.IsType<CreatedAtActionResult>(await Controller(Session(_userId)).Create(new()
        { ProviderId = "musixmatch", Enabled = false }));
        await using var db = await _factory.CreateDbContextAsync();
        Assert.Null((await db.ProviderAccounts.SingleAsync()).SecretReferenceId);
        Assert.Empty(await db.SecretVersions.ToArrayAsync());
    }
}
