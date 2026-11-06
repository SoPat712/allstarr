using System.Text;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Intelligence;
using allstarr.Core.Storage;
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
