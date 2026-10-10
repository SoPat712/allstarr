using System.Text.Json;
using allstarr.Core.Jobs;

namespace allstarr.Core.Matching;

public static class TrackRematchScopes
{
    public const string Song = "song";
    public const string Playlist = "playlist";
    public const string All = "all";
    public const string Algorithm = "algorithm";
}

public sealed record TrackRematchSongPayload(
    Guid ExternalSnapshotId,
    string Scope = TrackRematchScopes.Song);

public sealed class TrackRematchJobHandler(
    PlaylistRematchJobHandler playlist,
    TrackRematchAllJobHandler all,
    ITrackMatchRepository trackMatches) : IDurableJobHandler
{
    public const string Type = "track-match.rematch";

    public string JobType => Type;

    public Task<DurableJobCompletion> ExecuteAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        var scope = ReadScope(context.Claim.Payload);
        return scope switch
        {
            TrackRematchScopes.Playlist => playlist.ExecuteAsync(context, cancellationToken),
            TrackRematchScopes.All or TrackRematchScopes.Algorithm =>
                all.ExecuteAsync(context, cancellationToken),
            TrackRematchScopes.Song => ExecuteSongAsync(context, cancellationToken),
            _ => Task.FromResult(DurableJobCompletion.Failure(
                "track_rematch_payload_invalid",
                "The rematch request is missing a supported scope."))
        };
    }

    private async Task<DurableJobCompletion> ExecuteSongAsync(
        DurableJobExecutionContext context,
        CancellationToken cancellationToken)
    {
        TrackRematchSongPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<TrackRematchSongPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload == null ||
            payload.ExternalSnapshotId == Guid.Empty ||
            !context.Claim.OwnerUserId.HasValue)
            return DurableJobCompletion.Failure(
                "track_rematch_payload_invalid",
                "The rematch request is invalid.");

        var result = await trackMatches.RematchSnapshotAsync(
            new TrackMatchActor(context.Claim.OwnerUserId.Value, false),
            payload.ExternalSnapshotId,
            context.Claim.CorrelationId,
            cancellationToken);
        if (result.Succeeded)
            return DurableJobCompletion.Success();
        if (result.Failure is TrackMatchCommandFailure.Invalid or
            TrackMatchCommandFailure.NotFound or TrackMatchCommandFailure.Forbidden)
            return DurableJobCompletion.Failure(
                $"track_rematch_{result.Failure.ToString().ToLowerInvariant()}",
                result.Error ?? "The song could not be rematched.");
        return DurableJobCompletion.Retry(
            "track_rematch_conflict",
            result.Error ?? "The song rematch will be retried.",
            TimeSpan.FromSeconds(2));
    }

    private static string? ReadScope(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            return null;
        if (TryString(payload, "Scope", out var scope) || TryString(payload, "scope", out scope))
            return scope;
        if (Has(payload, "ConfirmationId") || Has(payload, "confirmationId"))
            return TrackRematchScopes.Playlist;
        if (Has(payload, "ExternalSnapshotId") || Has(payload, "externalSnapshotId"))
            return TrackRematchScopes.Song;
        if (Has(payload, "OperationId") || Has(payload, "operationId"))
            return TrackRematchScopes.All;
        return null;
    }

    private static bool Has(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) &&
        value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;

    private static bool TryString(JsonElement payload, string name, out string? value)
    {
        value = null;
        if (!payload.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString();
        return !string.IsNullOrWhiteSpace(value);
    }
}
