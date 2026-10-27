using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.Health;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Admin;

internal sealed record ActivityJobDisplay(
    string DisplayName,
    string Summary,
    string? TargetName,
    string Recovery,
    string ActionHref,
    string ActionLabel,
    string ActivityState,
    string Severity,
    IReadOnlyDictionary<string, string>? TechnicalDetails = null);

internal static class ActivityJobPresentation
{
    private static readonly HashSet<string> KnownHealthSteps = HealthSmokeServerProbe.StepNames.Concat(HealthSmokeProtocolProbe.StepNames).ToHashSet(StringComparer.Ordinal);
    private sealed record FavoriteContext(Guid OwnerUserId, string ItemId, string? Operation, string? Title, string? Artist);
    private sealed record HealthContext(Guid OwnerUserId, HealthSmokeRunRecord Run);

    public static async Task<IReadOnlyDictionary<Guid, ActivityJobDisplay>> ResolveAsync(
        AllstarrDbContext db, IReadOnlyCollection<DurableJobRecord> jobs,
        CancellationToken cancellationToken = default, bool administrator = false)
    {
        if (jobs.Count == 0) return new Dictionary<Guid, ActivityJobDisplay>();

        var ownerIds = jobs.Where(item => item.OwnerUserId.HasValue).Select(item => item.OwnerUserId!.Value).Distinct().ToArray();
        var jobIds = jobs.Select(item => item.Id).ToArray();
        var favoriteIds = jobs.Select(item => GuidFromPayload(item, "eventId")).OfType<Guid>().Distinct().ToArray();
        var favoriteEvents = favoriteIds.Length == 0
            ? []
            : await db.Set<FavoriteEventRecord>().AsNoTracking()
                .Where(item => favoriteIds.Contains(item.Id) && ownerIds.Contains(item.OwnerUserId))
                .ToArrayAsync(cancellationToken);
        var favoriteEventById = favoriteEvents.ToDictionary(item => item.Id);
        var favoriteOwners = favoriteEvents.Select(item => item.OwnerUserId).Distinct().ToArray();
        var favoriteItems = favoriteEvents.Select(item => item.ItemId).Distinct().ToArray();
        var favoriteStates = favoriteEvents.Length == 0
            ? []
            : await db.Set<FavoriteStateRecord>().AsNoTracking()
                .Where(item => favoriteItems.Contains(item.ItemId) && favoriteOwners.Contains(item.OwnerUserId))
                .ToArrayAsync(cancellationToken);
        var favoriteStateByOwnerItem = favoriteStates
            .GroupBy(item => (item.OwnerUserId, item.ItemId))
            .ToDictionary(group => group.Key, group => group.First());

        var playlistRuns = await db.PlaylistSyncRuns.AsNoTracking()
            .Where(item => item.JobId.HasValue && jobIds.Contains(item.JobId.Value) && ownerIds.Contains(item.OwnerUserId) &&
                db.Jobs.Any(job => job.Id == item.JobId.Value && job.OwnerUserId == item.OwnerUserId))
            .ToArrayAsync(cancellationToken);
        var playlistLinkIds = playlistRuns.Select(item => item.PlaylistLinkId).Distinct().ToArray();
        var playlistSnapshots = playlistLinkIds.Length == 0
            ? []
            : await db.PlaylistSourceSnapshots.AsNoTracking()
                .Where(item => playlistLinkIds.Contains(item.PlaylistLinkId) && item.PublishedAt.HasValue && ownerIds.Contains(item.OwnerUserId) &&
                    db.PlaylistLinks.Any(link => link.Id == item.PlaylistLinkId && link.OwnerUserId == item.OwnerUserId))
                .GroupBy(item => new { item.OwnerUserId, item.PlaylistLinkId })
                .Select(group => group.OrderByDescending(item => item.SnapshotVersion).ThenByDescending(item => item.RetrievedAt).First())
                .ToArrayAsync(cancellationToken);
        var playlistNameByJob = playlistRuns
            .Join(playlistSnapshots, run => (run.OwnerUserId, run.PlaylistLinkId), snapshot => (snapshot.OwnerUserId, snapshot.PlaylistLinkId),
                (run, snapshot) => new { JobId = run.JobId!.Value, snapshot.Name })
            .GroupBy(item => item.JobId)
            .ToDictionary(group => group.Key, group => group.Select(item => item.Name).FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));

        var healthRuns = await db.HealthSmokeRuns.AsNoTracking()
            .Where(item => jobIds.Contains(item.Id) && ownerIds.Contains(item.OwnerUserId))
            .ToArrayAsync(cancellationToken);
        var healthByJob = healthRuns.Length == 0
            ? new Dictionary<Guid, HealthContext>()
            : healthRuns.Where(run => jobs.Any(job => job.Id == run.Id && job.OwnerUserId == run.OwnerUserId))
                .ToDictionary(run => run.Id, run => new HealthContext(run.OwnerUserId, run));

        var result = new Dictionary<Guid, ActivityJobDisplay>();
        foreach (var job in jobs)
        {
            FavoriteContext? favorite = null;
            if (job.OwnerUserId is { } owner && GuidFromPayload(job, "eventId") is { } eventId &&
                favoriteEventById.TryGetValue(eventId, out var favoriteEvent) && favoriteEvent.OwnerUserId == owner &&
                favoriteStateByOwnerItem.GetValueOrDefault((owner, favoriteEvent.ItemId)) is { } state &&
                state.OwnerUserId == owner && state.ItemId == favoriteEvent.ItemId)
            {
                var title = FavoriteTitle(state.DisplayMetadataJson, state.ItemId, out var artist);
                favorite = new FavoriteContext(owner, favoriteEvent.ItemId, favoriteEvent.Operation.ToString(), title, artist);
            }
            var health = healthByJob.GetValueOrDefault(job.Id);
            var playlistName = ActivityPresentation.SafeText(playlistNameByJob.GetValueOrDefault(job.Id), 500);
            result[job.Id] = Build(job, favorite, health, playlistName, administrator);
        }
        return result;
    }

    private static ActivityJobDisplay Build(DurableJobRecord job, FavoriteContext? favorite,
        HealthContext? health, string? playlistName, bool administrator)
    {
        var displayName = DisplayName(job.Type);
        var target = favorite?.Title ?? playlistName ??
            (job.Type.StartsWith("health.", StringComparison.OrdinalIgnoreCase) ? "Server" :
             job.Type.StartsWith("library.", StringComparison.OrdinalIgnoreCase) ? "Library" :
             job.Type.Equals("storage.backup", StringComparison.OrdinalIgnoreCase) ? "Backup archive" : null);
        var summary = Summary(job, favorite, health, playlistName);
        var activityState = job.State.ToString().ToLowerInvariant();
        var severity = Severity(job.State, health);
        IReadOnlyDictionary<string, string>? technical = null;
        if (health is { } healthContext)
        {
            if (TryHealthSummary(healthContext.Run.CompletedJson, out var healthSummary))
            {
                activityState = healthSummary.Failed > 0
                    ? "failed"
                    : healthSummary.Passed == 0 ? "not_checked" : healthSummary.Skipped > 0 ? "partly_checked" : "passed";
                technical = HealthTechnical(healthSummary);
            }
            else if (job.State == DurableJobState.Succeeded)
            {
                activityState = "not_checked";
                severity = "info";
            }
        }
        if (health == null && job.Type.Equals(HealthSmokeService.JobType, StringComparison.Ordinal) && job.State == DurableJobState.Succeeded)
        {
            activityState = "not_checked";
            severity = "info";
        }
        return new(
            displayName,
            Safe(summary) ?? "Background task details are unavailable.",
            Safe(target),
            Recovery(job, health, administrator),
            $"#/activity?view=jobs&job={job.Id:N}",
            "View task",
            activityState,
            severity,
            technical);
    }

    private static string Summary(DurableJobRecord job, FavoriteContext? favorite,
        HealthContext? health, string? playlistName)
    {
        if (health is { } healthContext && TryHealthSummary(healthContext.Run.CompletedJson, out var healthSummary))
        {
            var mode = HumanMode(healthSummary.Mode);
            var result = $"Health check ({mode}): {healthSummary.Passed} passed, {healthSummary.Skipped} not checked, {healthSummary.Failed} failed.";
            if (healthSummary.FailedStep is { } failedStep)
                result += $" Failed step: {HumanStep(failedStep)}" + (healthSummary.FailureReason is { } reason ? $" because {HumanReason(reason)}." : ".");
            return result;
        }
        if (health != null || job.Type.Equals(HealthSmokeService.JobType, StringComparison.Ordinal)) return job.State switch
        {
            DurableJobState.Running => "Health check is running; its results are not ready yet.",
            DurableJobState.Failed => "Health check failed; detailed results are unavailable.",
            DurableJobState.Cancelled => "Health check was cancelled.",
            DurableJobState.RetryScheduled => "Health check is waiting for its next retry.",
            DurableJobState.Pending => "Health check is queued.",
            _ => "Health check result unavailable; detailed results were not retained."
        };
        if (favorite is { Title: { } favoriteTitle })
            return MutationSummary(job.State, $"Favorite {favorite.Operation?.ToLowerInvariant() ?? "update"}", favoriteTitle,
                favorite.Artist);
        if (playlistName is { Length: > 0 }) return MutationSummary(job.State, "Playlist sync", playlistName);
        if (job.Type.Equals("recording.keep", StringComparison.OrdinalIgnoreCase) && job.State == DurableJobState.Succeeded)
            return "Keep a song completed; song details were not retained.";
        return job.State switch
        {
            DurableJobState.Succeeded => job.LastAttemptOutcome is null
                ? "Completed; detailed result not retained."
                : $"Completed ({Safe(job.LastAttemptOutcome) ?? "success"}).",
            DurableJobState.Cancelled => "Cancelled by operator.",
            DurableJobState.RetryScheduled when string.Equals(job.LastAttemptOutcome, "deferred", StringComparison.OrdinalIgnoreCase)
                => "Waiting for the next scheduled retry.",
            DurableJobState.RetryScheduled => "Retry scheduled.",
            DurableJobState.Failed => Safe(job.LastErrorMessage) ?? "The task failed.",
            DurableJobState.Running => "Running.",
            _ => "Queued."
        };
    }

    private static string MutationSummary(DurableJobState state, string action, string target, string? artist = null)
    {
        var suffix = artist is { Length: > 0 } ? $" — {artist}" : string.Empty;
        return state switch
        {
            DurableJobState.Succeeded => $"{action} completed: {target}{suffix}",
            DurableJobState.Failed => $"{action} failed: {target}{suffix}",
            DurableJobState.Cancelled => $"{action} cancelled: {target}{suffix}",
            DurableJobState.RetryScheduled => $"{action} retry scheduled: {target}{suffix}",
            DurableJobState.Running => $"{action} running: {target}{suffix}",
            _ => $"{action} queued: {target}{suffix}"
        };
    }

    private static string HumanMode(string mode) => mode switch
    {
        "listener" => "listener checks",
        "server-api-key" => "server checks",
        _ => "configured checks"
    };

    private static string HumanStep(string name) => name switch
    {
        "backend" => "Server connection",
        "metadata" => "Song details",
        "playback" => "Playback",
        "seek" => "Seeking",
        "search" => "Search",
        "now-playing" => "Now playing",
        "scrobble" => "Scrobble",
        "heart" => "Add favorite",
        "unheart" => "Remove favorite",
        "playlist-create" => "Create playlist",
        "playlist-add" => "Add playlist song",
        "playlist-remove" => "Remove playlist song",
        "playlist-delete" => "Delete playlist",
        "download-seek" => "Seek in download",
        "download" => "Download",
        "artwork" => "Artwork",
        "lyrics" => "Lyrics",
        "cleanup" => "Cleanup",
        _ => "Check"
    };

    private static string HumanReason(string reason) => reason.ToLowerInvariant() switch
    {
        "http_401" or "unauthorized" => "backend authentication was rejected",
        "http_403" => "the server denied access",
        "http_429" => "the source rate-limited the request",
        "http_503" => "the server was temporarily unavailable",
        "transport_failure" => "the server could not be reached",
        "invalid_range" or "range_ignored" => "the server did not return the requested audio range",
        "invalid_audio" => "the server did not return usable audio",
        "timeout" or "request_timeout" => "the backend did not respond in time",
        "not_available" or "unavailable" => "the check was not available",
        _ => "the check reported an error"
    };

    private static IReadOnlyDictionary<string, string> HealthTechnical(
        (int Passed, int Skipped, int Failed, string? FailedStep, string? FailureReason, string Mode) summary)
    {
        var details = new Dictionary<string, string>
        {
            ["mode"] = summary.Mode,
            ["passed"] = summary.Passed.ToString(),
            ["notChecked"] = summary.Skipped.ToString(),
            ["failed"] = summary.Failed.ToString()
        };
        if (summary.FailedStep is { } failedStep) details["failedStep"] = failedStep;
        if (summary.FailureReason is { } reason) details["failureReason"] = reason;
        return details;
    }

    private static string Recovery(DurableJobRecord job, HealthContext? health, bool administrator)
    {
        if (job.State == DurableJobState.Running) return "Results update as this task progresses.";
        if (job.State == DurableJobState.Pending) return "Waiting for the worker to start.";
        if (health != null && !TryHealthSummary(health.Run.CompletedJson, out _))
            return administrator ? "Review the Health checks for the retained result." : "Ask an administrator to review the Health checks.";
        if (health is { } healthContext && TryHealthSummary(healthContext.Run.CompletedJson, out var healthSummary) && healthSummary.FailedStep is { } failedStep)
        {
            if (string.Equals(healthSummary.FailureReason, "http_401", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(healthSummary.FailureReason, "unauthorized", StringComparison.OrdinalIgnoreCase))
                return administrator
                    ? $"Open Settings → Setup and review the saved server connection for {HumanStep(failedStep)}."
                    : $"Ask an administrator to review the server connection in Setup for {HumanStep(failedStep)}.";
            return administrator
                ? $"Open Settings → Health and review the {HumanStep(failedStep)} check."
                : $"Ask an administrator to review the {HumanStep(failedStep)} check in Health.";
        }
        return job.State switch
        {
            DurableJobState.RetryScheduled => job.AvailableAt == default
                ? "Retry is scheduled." : $"Retry scheduled for {job.AvailableAt.ToUniversalTime():yyyy-MM-dd HH:mm:ss 'UTC'}.",
            DurableJobState.Failed => "Review the reason below. This failed task will not retry automatically.",
            DurableJobState.Cancelled => "No recovery is required; the task was cancelled.",
            DurableJobState.Succeeded => "No action needed.",
            DurableJobState.Running => "Keep this page open to follow progress.",
            _ => "Waiting to run."
        };
    }

    private static string DisplayName(string type) => type.ToLowerInvariant() switch
    {
        "library.refresh" => "Library refresh",
        "health.smoke" => "Health check",
        "health.cleanup" => "Health cleanup",
        "health.alert" => "Health alert",
        "storage.backup" => "Backup",
        "favorite.process" => "Favorite update",
        "favorite.display-backfill" => "Favorite metadata update",
        "playlist.materialize" => "Playlist sync",
        "playlist.retain-track" => "Keep playlist song",
        "playback.signal" or "playback.signal.process" => "Record a listen",
        "recording.keep" => "Keep a song",
        "recording.kept-cleanup" => "Remove unused kept copy",
        "recording.musicbrainz.enrich" => "Identify recording",
        "recording.odesli.enrich" => "Find recording links",
        "track-match.rematch" => "Refresh matches",
        "library.index" => "Refresh library index",
        _ => "Background task"
    };

    private static string Severity(DurableJobState state, HealthContext? health)
    {
        if (health is { } healthContext && TryHealthSummary(healthContext.Run.CompletedJson, out var healthSummary))
            return healthSummary.Failed > 0 ? "error" : "info";
        return state switch
        {
            DurableJobState.Failed => "error",
            DurableJobState.RetryScheduled => "warning",
            _ => "info"
        };
    }

    private static string? FavoriteTitle(string? json, string itemId, out string? artist)
    {
        artist = null;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32768) return null;
        try
        {
            var display = JsonSerializer.Deserialize<FavoriteDisplaySnapshot>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (display == null || display.ItemId != itemId || string.IsNullOrWhiteSpace(display.Title)) return null;
            artist = ActivityPresentation.SafeText(display.Artists?.FirstOrDefault(item => !string.IsNullOrWhiteSpace(item)), 500);
            return ActivityPresentation.SafeText(display.Title, 500);
        }
        catch (JsonException) { return null; }
    }

    private static bool TryHealthSummary(string? json, out (int Passed, int Skipped, int Failed, string? FailedStep, string? FailureReason, string Mode) summary)
    {
        summary = default;
        if (string.IsNullOrWhiteSpace(json) || json.Length > 131072) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("steps", out var steps) || steps.ValueKind != JsonValueKind.Array)
                return false;
            if (steps.GetArrayLength() is 0 or > 22) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var passed = 0;
            var skipped = 0;
            var failed = 0;
            string? failedStep = null;
            string? failureReason = null;
            foreach (var step in steps.EnumerateArray())
            {
                var status = Text(step, "status")?.ToLowerInvariant();
                if (step.ValueKind != JsonValueKind.Object || Text(step, "name") is not { } name || !KnownHealthSteps.Contains(name) || !seen.Add(name) ||
                    status is not ("passed" or "failed" or "skipped" or "not_checked" or "not-checked")) return false;
                if (status == "passed") passed++;
                else if (status is "skipped" or "not_checked" or "not-checked") skipped++;
                else if (status == "failed")
                {
                    failed++;
                    failedStep ??= Text(step, "name");
                    failureReason ??= Text(step, "reason");
                }
            }
            var mode = Text(root, "mode")?.ToLowerInvariant() ?? "listener";
            summary = (passed, skipped, failed, failedStep, Safe(failureReason),
                mode is "listener" ? "listener" : mode is "server-api-key" ? "server-api-key" : "unknown");
            return true;
        }
        catch (JsonException) { return false; }
    }

    private static Guid? GuidFromPayload(DurableJobRecord job, string name) => GuidFromJson(job.PayloadJson, name);

    private static Guid? GuidFromDetails(string json, string name) => GuidFromJson(json, name);

    private static Guid? GuidFromJson(string? json, string name)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 32768) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;
        }
        catch (JsonException) { return null; }
    }

    private static string? Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? Safe(string? value)
    {
        return ActivityPresentation.SafeText(value);
    }
}
