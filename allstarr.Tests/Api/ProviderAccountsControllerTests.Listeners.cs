using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class ProviderAccountsControllerTests
{
    [Fact]
    public async Task AdministratorList_ReturnsStoredListenerActivityAndFiveRecentPlays()
    {
        var now = DateTimeOffset.UtcNow;
        var latestActivity = now.AddMinutes(-5);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var user = await db.Users.SingleAsync(item => item.Id == _userId);
            user.IsAdmin = true;
            user.IsTrustedListener = true;
            user.LastSeenAt = now.AddDays(-1);
            for (var index = 1; index <= 6; index++)
                db.ListeningEvents.Add(ListenerPlay(_userId, now.AddHours(-index), $"Song {index}"));
            db.ListeningEvents.Add(ListenerPlay(_userId, now.AddDays(-8), "Older song"));
            var skipped = ListenerPlay(_userId, now.AddHours(-7), "Long skipped song");
            skipped.State = ListeningEventState.Skipped;
            skipped.ListenedAt = null;
            skipped.PositionTicks = TimeSpan.FromSeconds(30).Ticks;
            db.ListeningEvents.Add(skipped);
            var shortPlay = ListenerPlay(_userId, now.AddHours(-8), "Short song");
            shortPlay.State = ListeningEventState.Abandoned;
            shortPlay.ListenedAt = null;
            shortPlay.PositionTicks = TimeSpan.FromSeconds(29).Ticks;
            db.ListeningEvents.Add(shortPlay);
            var playing = ListenerPlay(_userId, latestActivity, "Playing song");
            playing.State = ListeningEventState.Playing;
            playing.ListenedAt = null;
            db.ListeningEvents.Add(playing);
            var otherBackend = ListenerPlay(_userId, now.AddMinutes(-1), "Other backend song");
            otherBackend.BackendInstanceId = "other-backend";
            db.ListeningEvents.Add(otherBackend);
            var otherProtocol = ListenerPlay(_userId, now.AddMinutes(-1), "Other protocol song");
            otherProtocol.Protocol = "subsonic";
            db.ListeningEvents.Add(otherProtocol);
            db.ListeningEvents.Add(ListenerPlay(_userId, now.AddDays(1), "Future song"));
            db.ListeningEvents.Add(ListenerPlay(_otherUserId, now.AddMinutes(-2), "Other listener song"));
            await db.SaveChangesAsync();
        }

        var result = Assert.IsType<OkObjectResult>(await Controller(Session(_userId, administrator: true)).List());
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        var listeners = payload.RootElement.GetProperty("audienceUsers").EnumerateArray().ToArray();
        var listener = Assert.Single(listeners, item => item.GetProperty("Id").GetGuid() == _userId);
        Assert.Equal("User one", listener.GetProperty("DisplayName").GetString());
        Assert.True(listener.GetProperty("isAdministrator").GetBoolean());
        Assert.True(listener.GetProperty("isTrustedListener").GetBoolean());
        Assert.Equal(latestActivity, listener.GetProperty("lastActiveAt").GetDateTimeOffset());
        Assert.Equal(7, listener.GetProperty("playsThisWeek").GetInt32());
        Assert.False(listener.GetProperty("playlistEditingAllowed").GetBoolean());
        var recent = listener.GetProperty("recentPlays").EnumerateArray().ToArray();
        Assert.Equal(5, recent.Length);
        Assert.Equal(Enumerable.Range(1, 5).Select(index => $"Song {index}"),
            recent.Select(item => item.GetProperty("title").GetString()));
        Assert.All(recent, item => Assert.Equal("Stored artist", item.GetProperty("artist").GetString()));
        Assert.Equal(now.AddHours(-1), recent[0].GetProperty("playedAt").GetDateTimeOffset());
        Assert.Equal(1, Assert.Single(listeners, item => item.GetProperty("Id").GetGuid() == _otherUserId)
            .GetProperty("playsThisWeek").GetInt32());
    }

    [Fact]
    public async Task AdministratorList_ReturnsEmptyHistoryAndStoredIdentityActivity()
    {
        var lastSeen = DateTimeOffset.UtcNow.AddHours(-3);
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _otherUserId)).LastSeenAt = lastSeen;
            var disabled = User(Guid.CreateVersion7(), "Disabled listener");
            disabled.Enabled = false;
            db.Users.Add(disabled);
            await db.SaveChangesAsync();
        }

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await Controller(Session(_userId, administrator: true)).List()).Value));
        var listeners = payload.RootElement.GetProperty("audienceUsers").EnumerateArray().ToArray();
        Assert.Equal(2, listeners.Length);
        var unknown = Assert.Single(listeners, item => item.GetProperty("Id").GetGuid() == _userId);
        Assert.False(unknown.GetProperty("isAdministrator").GetBoolean());
        Assert.All(listeners, item => Assert.False(item.GetProperty("isTrustedListener").GetBoolean()));
        Assert.Equal(JsonValueKind.Null, unknown.GetProperty("lastActiveAt").ValueKind);
        Assert.Equal(0, unknown.GetProperty("playsThisWeek").GetInt32());
        Assert.Empty(unknown.GetProperty("recentPlays").EnumerateArray());
        Assert.Equal(lastSeen, Assert.Single(listeners, item => item.GetProperty("Id").GetGuid() == _otherUserId)
            .GetProperty("lastActiveAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData("jellyfin", BackendCredentialScope.JellyfinPurpose)]
    [InlineData("subsonic", BackendCredentialScope.SubsonicPurpose)]
    public async Task AdministratorList_UsesOnlyActiveMatchingPlaylistConsent(string backend, string purpose)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _userId)).BackendType = backend;
            await db.SaveChangesAsync();
        }
        var wrongPurpose = purpose == BackendCredentialScope.JellyfinPurpose
            ? BackendCredentialScope.SubsonicPurpose : BackendCredentialScope.JellyfinPurpose;
        await _secretStore.StoreAsync(_userId, wrongPurpose, Encoding.UTF8.GetBytes("private-fixture"));
        await _secretStore.StoreAsync(_otherUserId, "provider-account:deezer:fixture", Encoding.UTF8.GetBytes("private-fixture"));
        var grant = await _secretStore.StoreAsync(_userId, purpose, Encoding.UTF8.GetBytes("private-fixture"));

        using (var payload = JsonDocument.Parse(JsonSerializer.Serialize(
                   Assert.IsType<OkObjectResult>(await Controller(Session(_userId, administrator: true)).List()).Value)))
        {
            var listeners = payload.RootElement.GetProperty("audienceUsers").EnumerateArray().ToArray();
            Assert.True(Assert.Single(listeners, item => item.GetProperty("Id").GetGuid() == _userId)
                .GetProperty("playlistEditingAllowed").GetBoolean());
            Assert.False(Assert.Single(listeners, item => item.GetProperty("Id").GetGuid() == _otherUserId)
                .GetProperty("playlistEditingAllowed").GetBoolean());
            Assert.DoesNotContain("private-fixture", payload.RootElement.GetRawText());
        }

        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.SecretReferences.SingleAsync(item => item.Id == grant.Id)).RevokedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        using var revoked = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await Controller(Session(_userId, administrator: true)).List()).Value));
        Assert.False(Assert.Single(revoked.RootElement.GetProperty("audienceUsers").EnumerateArray(),
            item => item.GetProperty("Id").GetGuid() == _userId).GetProperty("playlistEditingAllowed").GetBoolean());
    }

    [Fact]
    public async Task ListenerList_DoesNotReturnAudienceActivity()
    {
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(
            Assert.IsType<OkObjectResult>(await Controller(Session(_userId)).List()).Value));
        Assert.Empty(payload.RootElement.GetProperty("audienceUsers").EnumerateArray());
    }

    [Fact]
    public async Task ListenerTrust_DefaultsToFalseForNewUsers()
    {
        Assert.False(new UserRecord().IsTrustedListener);
        await using var db = await _factory.CreateDbContextAsync();
        Assert.All(await db.Users.AsNoTracking().ToListAsync(), user => Assert.False(user.IsTrustedListener));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Administrator_CanSetAndUnsetListenerTrust(bool trusted)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _userId)).IsAdmin = true;
            (await db.Users.SingleAsync(item => item.Id == _otherUserId)).IsTrustedListener = !trusted;
            await db.SaveChangesAsync();
        }

        var result = Assert.IsType<OkObjectResult>(await Controller(ListenerTrustSession())
            .SetListenerTrust(_otherUserId, new() { IsTrustedListener = trusted }));
        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(_otherUserId, payload.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(trusted, payload.RootElement.GetProperty("isTrustedListener").GetBoolean());
        await using var persisted = await _factory.CreateDbContextAsync();
        Assert.Equal(trusted, (await persisted.Users.AsNoTracking()
            .SingleAsync(item => item.Id == _otherUserId)).IsTrustedListener);
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Listener_CannotChangeTheirOwnOrAnotherListenersTrust(bool self, bool trusted)
    {
        var target = self ? _userId : _otherUserId;
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == target)).IsTrustedListener = !trusted;
            await db.SaveChangesAsync();
        }

        AssertForbidden(await Controller(ListenerTrustSession(administrator: false))
            .SetListenerTrust(target, new() { IsTrustedListener = trusted }));
        await using var persisted = await _factory.CreateDbContextAsync();
        Assert.Equal(!trusted, (await persisted.Users.AsNoTracking()
            .SingleAsync(item => item.Id == target)).IsTrustedListener);
    }

    [Theory]
    [InlineData("not-administrator")]
    [InlineData("session-not-administrator")]
    [InlineData("disabled")]
    [InlineData("missing")]
    [InlineData("backend-type")]
    [InlineData("backend-instance")]
    [InlineData("backend-principal")]
    [InlineData("expired")]
    [InlineData("unlinked")]
    public async Task ListenerTrust_RequiresMatchingEnabledStoredAdministrator(string invalidIdentity)
    {
        await using (var db = await _factory.CreateDbContextAsync())
        {
            var administrator = await db.Users.SingleAsync(item => item.Id == _userId);
            administrator.IsAdmin = invalidIdentity != "not-administrator";
            if (invalidIdentity == "disabled") administrator.Enabled = false;
            if (invalidIdentity == "missing") db.Users.Remove(administrator);
            if (invalidIdentity == "backend-type") administrator.BackendType = "subsonic";
            if (invalidIdentity == "backend-instance") administrator.BackendInstanceId = "another-instance";
            if (invalidIdentity == "backend-principal") administrator.BackendPrincipalId = "another-principal";
            await db.SaveChangesAsync();
        }
        var session = ListenerTrustSession(administrator: invalidIdentity != "session-not-administrator",
            linked: invalidIdentity != "unlinked");
        if (invalidIdentity == "expired") session.ExpiresAtUtc = DateTime.UtcNow.AddMinutes(-1);

        AssertForbidden(await Controller(session)
            .SetListenerTrust(_otherUserId, new() { IsTrustedListener = true }));
        await using var persisted = await _factory.CreateDbContextAsync();
        Assert.False((await persisted.Users.AsNoTracking()
            .SingleAsync(item => item.Id == _otherUserId)).IsTrustedListener);
    }

    [Fact]
    public async Task ListenerTrust_RequiresAuthentication()
    {
        var controller = Controller(ListenerTrustSession());
        controller.HttpContext.Items.Remove(AdminAuthSessionService.HttpContextSessionItemKey);

        Assert.IsType<UnauthorizedObjectResult>(await controller
            .SetListenerTrust(_otherUserId, new() { IsTrustedListener = true }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ListenerTrust_ReturnsNotFoundForMissingOrDisabledListener(bool disabled)
    {
        var target = disabled ? _otherUserId : Guid.CreateVersion7();
        await using (var db = await _factory.CreateDbContextAsync())
        {
            (await db.Users.SingleAsync(item => item.Id == _userId)).IsAdmin = true;
            if (disabled) (await db.Users.SingleAsync(item => item.Id == target)).Enabled = false;
            await db.SaveChangesAsync();
        }

        Assert.IsType<NotFoundResult>(await Controller(ListenerTrustSession())
            .SetListenerTrust(target, new() { IsTrustedListener = true }));
        if (disabled)
        {
            await using var persisted = await _factory.CreateDbContextAsync();
            Assert.False((await persisted.Users.AsNoTracking()
                .SingleAsync(item => item.Id == target)).IsTrustedListener);
        }
    }

    private AdminAuthSession ListenerTrustSession(bool administrator = true, bool linked = true) => new()
    {
        SessionId = Guid.NewGuid().ToString("N"),
        UserId = _userId.ToString(),
        UserName = "fixture",
        IsAdministrator = administrator,
        BackendType = "Jellyfin",
        BackendInstanceId = "fixture",
        AllstarrUserId = linked ? _userId : null,
        JellyfinAccessToken = "protected-in-real-session-store",
        ExpiresAtUtc = DateTime.UtcNow.AddHours(1),
        LastSeenUtc = DateTime.UtcNow
    };

    private static ListeningEventRecord ListenerPlay(Guid owner, DateTimeOffset at, string title) => new()
    {
        Id = Guid.CreateVersion7(),
        OwnerUserId = owner,
        Protocol = "jellyfin",
        BackendInstanceId = "fixture",
        OccurrenceKey = Guid.NewGuid().ToString("N"),
        State = ListeningEventState.Completed,
        StartedAt = at,
        ListenedAt = at,
        UpdatedAt = at,
        TrackReference = title,
        Title = title,
        Artist = "Stored artist"
    };
}
