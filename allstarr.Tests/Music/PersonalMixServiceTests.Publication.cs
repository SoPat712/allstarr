using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Music;
using allstarr.Core.Storage;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PersonalMixServiceTests
{
    [Fact]
    public async Task Mix_publication_is_healthy_when_current_songs_are_served_in_apps()
    {
        var mix = await PublishedMix();
        var publication = await Publication(mix.Id);
        Assert.Equal("healthy", publication.GetProperty("status").GetString());
        Assert.Equal(clock.UtcNow, publication.GetProperty("lastPublishedAt").GetDateTimeOffset());
        Assert.Equal(JsonValueKind.Null, publication.GetProperty("reason").ValueKind);
        Assert.Equal(mix.Songs.Select(item => item.Song.Id),
            (await service.PublishedAsync(Context(owner), default)).Single(item => item.LinkId == mix.Id).Tracks.Select(item => item.BackendItemId));
        var list = JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(await Controller(owner, true).List()).Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(publication.GetRawText(), list.EnumerateArray().Single(item => item.GetProperty("id").GetGuid() == mix.Id).GetProperty("publication").GetRawText());
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await service.FeedbackAsync(owner, mix.Id, new(mix.Songs[0].Song.Id, "notForMe"), default);
        var current = await service.GetAsync(owner, mix.Id);
        Assert.Equal(mix.Songs.Count - 1, current.Songs.Count);
        Assert.Equal(current.Songs.Select(item => item.Song.Id),
            (await service.PublishedAsync(Context(owner), default)).Single(item => item.LinkId == mix.Id).Tracks.Select(item => item.BackendItemId));
        publication = await Publication(mix.Id);
        Assert.Equal("healthy", publication.GetProperty("status").GetString());
        Assert.Equal(clock.UtcNow, publication.GetProperty("lastPublishedAt").GetDateTimeOffset());
    }

    [Theory]
    [InlineData(DurableJobState.Pending)]
    [InlineData(DurableJobState.Running)]
    [InlineData(DurableJobState.RetryScheduled)]
    public async Task Mix_publication_is_updating_while_its_refresh_is_queued_or_running(DurableJobState state)
    {
        var mix = await PublishedMix();
        await PublicationJob(mix.Id, state);
        Assert.Equal("updating", (await Publication(mix.Id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Mix_publication_reports_a_plain_reason_after_a_failed_refresh_and_recovers()
    {
        var mix = await PublishedMix();
        clock.UtcNow = clock.UtcNow.AddMinutes(16);
        await PublicationJob(mix.Id, DurableJobState.Failed);
        var publication = await Publication(mix.Id);
        Assert.Equal("needsLook", publication.GetProperty("status").GetString());
        Assert.Contains("couldn't be refreshed", publication.GetProperty("reason").GetString());
        Assert.DoesNotContain("private error", publication.GetProperty("reason").GetString());
        clock.UtcNow = clock.UtcNow.AddMinutes(1);
        await service.GenerateAsync(owner, mix.Id);
        Assert.Equal("healthy", (await Publication(mix.Id)).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mix_publication_needs_a_look_when_the_virtual_playlist_is_unavailable(bool disabled)
    {
        var mix = await PublishedMix();
        if (disabled) publish = false;
        else
        {
            await using var db = factory.CreateDbContext();
            var row = await db.ExternalMetadataSnapshots.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstAsync(item => item.ResourceKind == "personal-mixes");
            var document = JsonNode.Parse(row.PayloadJson)!;
            document["Mixes"]![mix.Kind]!["Generation"] = 0;
            row.PayloadJson = document.ToJsonString();
            row.Id = Guid.NewGuid();
            row.SnapshotVersion++;
            db.ExternalMetadataSnapshots.Add(row);
            await db.SaveChangesAsync();
        }
        var publication = await Publication(mix.Id);
        Assert.Equal("needsLook", publication.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, publication.GetProperty("lastPublishedAt").ValueKind);
        Assert.False(string.IsNullOrWhiteSpace(publication.GetProperty("reason").GetString()));
        Assert.DoesNotContain(await service.PublishedAsync(Context(owner), default), item => item.LinkId == mix.Id);
    }

    [Fact]
    public async Task Mix_publication_becomes_stale_at_its_refresh_time_and_active_work_takes_precedence()
    {
        var mix = await PublishedMix();
        clock.UtcNow = DateTimeOffset.Parse("2026-10-30T04:59:59Z");
        Assert.Equal("healthy", (await Publication(mix.Id)).GetProperty("status").GetString());
        clock.UtcNow = clock.UtcNow.AddSeconds(1);
        var publication = await Publication(mix.Id);
        Assert.Equal("needsLook", publication.GetProperty("status").GetString());
        Assert.Contains("refresh time", publication.GetProperty("reason").GetString());
        await PublicationJob(Guid.Empty, DurableJobState.Pending);
        Assert.Equal("updating", (await Publication(mix.Id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Mix_publication_ignores_other_listeners_other_mixes_and_previews()
    {
        var mix = await PublishedMix();
        await PublicationJob(mix.Id, DurableJobState.Failed, other);
        await PublicationJob(Guid.NewGuid(), DurableJobState.Pending);
        await PublicationJob(mix.Id, DurableJobState.Running, dryRun: true);
        Assert.Equal("healthy", (await Publication(mix.Id)).GetProperty("status").GetString());
        await service.SetInAppsAsync(owner, mix.Id, false, default);
        Assert.Equal(JsonValueKind.Null, (await Publication(mix.Id)).ValueKind);
    }

    [Fact]
    public async Task Mix_publication_reports_songs_without_a_resolvable_route()
    {
        var mix = await PublishedMix();
        await using var db = factory.CreateDbContext();
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == owner);
        var unresolved = mix with { Added = true, Songs = [mix.Songs[0] with { Song = new() { Id = "missing", IsLocal = true } }] };
        var publication = (await service.PublicationStatusAsync(user, [unresolved], true, default))[mix.Id];
        Assert.Equal("needsLook", publication.Status);
        Assert.Contains("Some songs are no longer available", publication.Reason);
        var encoded = mix with { Added = true, Songs = [mix.Songs[0] with { Song = new() { Id = mix.Songs[0].Song.Id } }] };
        Assert.Equal("healthy", (await service.PublicationStatusAsync(user, [encoded], true, default))[mix.Id].Status);
    }

    [Fact]
    public async Task Saved_custom_mix_publication_has_no_scheduled_expiry_and_readiness_is_separate()
    {
        var mix = await PublishedMix();
        await using var db = factory.CreateDbContext();
        var row = await db.ExternalMetadataSnapshots.AsNoTracking().OrderByDescending(item => item.SnapshotVersion).FirstAsync(item => item.ResourceKind == "personal-mixes");
        var document = JsonNode.Parse(row.PayloadJson)!;
        document["Mixes"]![mix.Kind]!["Kind"] = "custom";
        document["Mixes"]![mix.Kind]!["Readiness"]!["Ready"] = false;
        row.PayloadJson = document.ToJsonString();
        row.Id = Guid.NewGuid();
        row.SnapshotVersion++;
        db.ExternalMetadataSnapshots.Add(row);
        await db.SaveChangesAsync();
        clock.UtcNow = clock.UtcNow.AddMonths(1);
        await PublicationJob(Guid.Empty, DurableJobState.Pending);
        Assert.Equal("healthy", (await Publication(mix.Id)).GetProperty("status").GetString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Service_mix_publication_follows_virtual_or_stored_delivery(bool served)
    {
        protocol = "jellyfin";
        await using var db = factory.CreateDbContext();
        var user = await db.Users.SingleAsync(item => item.Id == owner);
        user.BackendType = protocol;
        await db.SaveChangesAsync();
        var link = await ExternalLink();
        var saved = await db.PlaylistLinks.SingleAsync(item => item.Id == link.Id);
        saved.Mode = served ? PlaylistLinkMode.Virtual : PlaylistLinkMode.Hybrid;
        saved.TargetPlaylistId = served ? null : "saved-playlist";
        var snapshot = await db.PlaylistSourceSnapshots.SingleAsync(item => item.PlaylistLinkId == link.Id);
        var run = new PlaylistSyncRunRecord
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            PlaylistLinkId = link.Id,
            PlaylistSourceSnapshotId = snapshot.Id,
            Generation = 1,
            IdempotencyKey = Guid.NewGuid().ToString(),
            RuleVersion = "1",
            State = PlaylistSyncState.Succeeded,
            VerificationCode = "verified",
            VerifiedAt = clock.UtcNow,
            StartedAt = clock.UtcNow,
            CompletedAt = clock.UtcNow
        };
        db.PlaylistSyncRuns.Add(run);
        await db.SaveChangesAsync();
        var card = new PersonalMix(link.Id, "external", "Service mix", "", 1, clock.UtcNow, new(true, "Ready"), true, []);
        var status = (await service.PublicationStatusAsync(user, [card], true, default))[link.Id];
        Assert.Equal("healthy", status.Status);
        Assert.Equal(clock.UtcNow, status.LastPublishedAt);
        Assert.Null(status.Reason);
        run.State = PlaylistSyncState.Failed;
        await db.SaveChangesAsync();
        status = (await service.PublicationStatusAsync(user, [card], true, default))[link.Id];
        Assert.Equal("needsLook", status.Status);
        Assert.StartsWith(served ? "Served by Allstarr." : "Saved in Jellyfin.", status.Reason);
        Assert.Contains("couldn't be updated", status.Reason);
        run.State = PlaylistSyncState.Succeeded;
        run.VerificationCode = "order";
        await db.SaveChangesAsync();
        status = (await service.PublicationStatusAsync(user, [card], true, default))[link.Id];
        Assert.Equal(served ? "healthy" : "needsLook", status.Status);
        run.VerificationCode = "verified";
        saved.TargetPlaylistId = null;
        await db.SaveChangesAsync();
        status = (await service.PublicationStatusAsync(user, [card], true, default))[link.Id];
        Assert.Equal(served ? "healthy" : "needsLook", status.Status);
        if (!served) Assert.Contains("isn't available", status.Reason);
        db.Jobs.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = owner,
            ScopeKey = Guid.NewGuid().ToString(),
            IdempotencyKey = Guid.NewGuid().ToString(),
            Type = allstarr.Core.Jobs.DurableScheduleEngine.PlaylistSyncJobType,
            State = DurableJobState.Pending,
            PayloadJson = JsonSerializer.Serialize(new { PlaylistLinkId = link.Id }),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        });
        await db.SaveChangesAsync();
        status = (await service.PublicationStatusAsync(user, [card], true, default))[link.Id];
        Assert.Equal("updating", status.Status);
        Assert.Null(status.Reason);
    }

    private async Task<PersonalMix> PublishedMix()
    {
        publish = true;
        await Seed();
        var mix = (await service.GenerateAsync(owner, timeZoneId: "UTC")).Single(item => item.Kind == "on-repeat");
        await service.SetInAppsAsync(owner, mix.Id, true, default);
        return mix;
    }

    private async Task<JsonElement> Publication(Guid id)
    {
        var result = Assert.IsType<OkObjectResult>(await Controller(owner, true).Get(id, default));
        return JsonSerializer.SerializeToElement(result.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web))
            .GetProperty("mix").GetProperty("publication");
    }

    private async Task PublicationJob(Guid id, DurableJobState state, Guid? listener = null, bool dryRun = false)
    {
        await using var db = factory.CreateDbContext();
        db.Jobs.Add(new()
        {
            Id = Guid.NewGuid(),
            OwnerUserId = listener ?? owner,
            Type = PersonalMixService.JobType,
            ScopeKey = Guid.NewGuid().ToString(),
            IdempotencyKey = Guid.NewGuid().ToString(),
            State = state,
            PayloadJson = dryRun ? JsonSerializer.Serialize(new MixRefreshRequest(id, true)) : JsonSerializer.Serialize(id),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow,
            CompletedAt = state == DurableJobState.Failed ? clock.UtcNow : null,
            LastErrorMessage = "private error"
        });
        await db.SaveChangesAsync();
    }
}
