using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;

namespace allstarr.Core.Playlists.Targets;

public sealed class SubsonicPlaylistTarget : IBackendPlaylistTarget
{
    public const string HttpClientName = "SubsonicBackend";
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly IBackendPlaylistAuthenticationResolver _authentication;

    public SubsonicPlaylistTarget(
        IHttpClientFactory clients,
        IOptions<SubsonicSettings> settings,
        IBackendPlaylistAuthenticationResolver authentication)
        : this(
            clients.CreateClient(HttpClientName),
            new Uri(settings.Value.Url ?? throw new InvalidOperationException("Subsonic backend URL is not configured.")),
            authentication)
    {
    }

    public SubsonicPlaylistTarget(HttpClient client, Uri baseUri, IBackendPlaylistAuthenticationResolver? authentication = null)
    {
        _client = client;
        _baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/");
        _authentication = authentication ?? new NoAuthenticationResolver();
    }

    public BackendPlaylistFamily Family => BackendPlaylistFamily.Subsonic;

    public BackendPlaylistTargetCapabilities Capabilities { get; } = new(
        CanCreate: true,
        CanReadMembership: true,
        CanReconcileMembership: true,
        PreservesRequestedOrder: true,
        CanWriteName: true,
        CanWriteDescription: false,
        CanWriteArtwork: false,
        HasNativeRevision: true,
        HasStagedReplacement: true);

    public async Task<BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>> ListAsync(
        BackendPlaylistTargetContext context,
        string? query,
        int limit,
        CancellationToken cancellationToken) =>
        await ListPageAsync(context, query, 0, limit, cancellationToken);

    public async Task<BackendPlaylistTargetResult<IReadOnlyList<BackendPlaylistSummary>>> ListPageAsync(
        BackendPlaylistTargetContext context,
        string? query,
        int offset,
        int limit,
        CancellationToken cancellationToken)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 200);
        var response = await CallAsync(context, "getPlaylists", [], cancellationToken);
        if (!response.IsSuccess)
            return ConvertFailure<IReadOnlyList<BackendPlaylistSummary>>(response);
        using var document = JsonDocument.Parse(response.Body!);
        if (!TryResponseRoot(document.RootElement, out var root, out var protocolFailure))
            return ConvertFailure<IReadOnlyList<BackendPlaylistSummary>>(protocolFailure!);

        var normalizedQuery = query?.Trim();
        var values = new List<BackendPlaylistSummary>();
        var skipped = 0;
        foreach (var playlist in root.GetPropertyOrDefault("playlists").GetPropertyOrDefault("playlist").EnumerateArrayOrEmpty())
        {
            var id = playlist.StringOrNull("id");
            var name = playlist.StringOrNull("name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            if (!string.IsNullOrWhiteSpace(normalizedQuery) &&
                !name.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)) continue;
            if (skipped++ < offset) continue;
            int? trackCount = playlist.GetPropertyOrDefault("songCount").TryGetInt32(out var count) ? count : null;
            values.Add(new BackendPlaylistSummary(
                id,
                name,
                trackCount,
                playlist.StringOrNull("comment"),
                playlist.StringOrNull("coverArt")));
            if (values.Count == limit) break;
        }

        return new(BackendPlaylistTargetStatus.Success, values, response.Status);
    }

    public async Task<BackendPlaylistTargetResult<BackendPlaylistArtwork>> ReadArtworkAsync(
        BackendPlaylistTargetContext context,
        string backendPlaylistId,
        string? artworkReference,
        CancellationToken cancellationToken)
    {
        var coverArtId = string.IsNullOrWhiteSpace(artworkReference) ? backendPlaylistId : artworkReference;
        var response = await CallAsync(context, "getCoverArt", [Pair("id", coverArtId)], cancellationToken);
        if (!response.IsSuccess || response.Body is not { Length: > 0 })
            return ConvertFailure<BackendPlaylistArtwork>(response);
        var contentType = response.ContentType is "image/png" or "image/webp" ? response.ContentType : "image/jpeg";
        return new(BackendPlaylistTargetStatus.Success, new BackendPlaylistArtwork(response.Body, contentType), response.Status);
    }

    public async Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot?>> FindByNameAsync(
        BackendPlaylistTargetContext context,
        string name,
        CancellationToken cancellationToken)
    {
        var response = await CallAsync(context, "getPlaylists", [], cancellationToken);
        if (!response.IsSuccess) return ConvertFailure<BackendPlaylistSnapshot?>(response);
        using var document = JsonDocument.Parse(response.Body!);
        if (!TryResponseRoot(document.RootElement, out var root, out var protocolFailure))
            return ConvertFailure<BackendPlaylistSnapshot?>(protocolFailure!);
        var playlists = root.GetPropertyOrDefault("playlists").GetPropertyOrDefault("playlist").EnumerateArrayOrEmpty();
        foreach (var playlist in playlists)
        {
            if (playlist.StringOrNull("name")?.Equals(name, StringComparison.OrdinalIgnoreCase) != true) continue;
            var id = playlist.StringOrNull("id");
            if (id == null) continue;
            var read = await ReadAsync(context, id, cancellationToken);
            return read.IsSuccess
                ? new(BackendPlaylistTargetStatus.Success, read.Value, read.UpstreamStatus)
                : ConvertFailure<BackendPlaylistSnapshot?, BackendPlaylistSnapshot>(read);
        }
        return new(BackendPlaylistTargetStatus.Success, null, response.Status);
    }

    public async Task<BackendPlaylistTargetResult<BackendPlaylistSnapshot>> ReadAsync(
        BackendPlaylistTargetContext context,
        string backendPlaylistId,
        CancellationToken cancellationToken)
    {
        var response = await CallAsync(context, "getPlaylist", [Pair("id", backendPlaylistId)], cancellationToken);
        if (!response.IsSuccess) return ConvertFailure<BackendPlaylistSnapshot>(response);
        using var document = JsonDocument.Parse(response.Body!);
        if (!TryResponseRoot(document.RootElement, out var root, out var protocolFailure))
            return ConvertFailure<BackendPlaylistSnapshot>(protocolFailure!);
        var playlist = root.GetPropertyOrDefault("playlist");
        if (playlist.ValueKind != JsonValueKind.Object)
            return new(BackendPlaylistTargetStatus.NotFound, UpstreamStatus: response.Status, ErrorCode: "playlist-not-found");
        var id = playlist.StringOrNull("id") ?? backendPlaylistId;
        var name = playlist.StringOrNull("name") ?? id;
        var description = playlist.StringOrNull("comment");
        var artwork = playlist.StringOrNull("coverArt");
        var revision = playlist.StringOrNull("changed");
        var members = playlist.GetPropertyOrDefault("entry").EnumerateArrayOrEmpty()
            .Select(entry => new BackendPlaylistMember(
                entry.StringOrNull("id") ?? throw new JsonException("Subsonic playlist entry has no id."),
                durationMilliseconds: MillisecondsFromSeconds(entry.Int64OrNull("duration"))))
            .ToArray();
        var reportedCount = playlist.Int64OrNull("songCount") is { } count &&
                            count is >= 0 and <= int.MaxValue
            ? (int)count
            : members.Length;
        var duration = MillisecondsFromSeconds(playlist.Int64OrNull("duration")) ??
                       (members.Length == 0
                           ? 0
                           : members.All(item => item.DurationMilliseconds.HasValue)
                               ? members.Sum(item => item.DurationMilliseconds!.Value)
                               : null);
        var fingerprint = BackendPlaylistSnapshot.ComputeFingerprint(id, name, members, description, artwork);
        return new(BackendPlaylistTargetStatus.Success,
            new(id, name, members, fingerprint, revision, description, artwork,
                reportedCount, duration),
            response.Status);
    }

    public async Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(
        BackendPlaylistTargetContext context,
        BackendPlaylistWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var unsupported = UnsupportedFields(request.Metadata);
            if (request.Mode == BackendPlaylistWriteMode.Recreate || request.BackendPlaylistId == null)
            {
                if (request.Mode == BackendPlaylistWriteMode.Recreate && request.RecoveryPlaylistId != null)
                {
                    var resumed = await WriteAsync(context, new BackendPlaylistWriteRequest(
                        BackendPlaylistWriteMode.Reconcile,
                        request.Metadata,
                        request.OrderedBackendItemIds,
                        request.IdempotencyKey,
                        request.RecoveryPlaylistId,
                        syncOwnedBackendItemIds: request.OrderedBackendItemIds,
                        removeStaleSyncOwnedItems: true), cancellationToken);
                    if (!resumed.IsSuccess) return resumed;
                    return resumed with
                    {
                        Value = resumed.Value! with
                        {
                            ReplacementRequiresCleanup = request.BackendPlaylistId != null,
                            ReplacedPlaylistId = request.BackendPlaylistId
                        },
                        RecoveryPlaylistId = request.RecoveryPlaylistId
                    };
                }
                var create = await CallAsync(context, "createPlaylist",
                    [Pair("name", request.Metadata.Name), .. request.OrderedBackendItemIds.Select(id => Pair("songId", id))],
                    cancellationToken);
                if (!create.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(create);
                if (!TryProtocolSuccess(create, out var failure)) return ConvertFailure<BackendPlaylistWriteReceipt>(failure!);
                var found = await FindByNameAsync(context, request.Metadata.Name, cancellationToken);
                if (!found.IsSuccess || found.Value == null)
                    return new(BackendPlaylistTargetStatus.BackendFailure, UpstreamStatus: found.UpstreamStatus, ErrorCode: "created-playlist-not-readable");
                return new(BackendPlaylistTargetStatus.Success,
                    new(found.Value, true, unsupported,
                        ReplacementRequiresCleanup: request.Mode == BackendPlaylistWriteMode.Recreate && request.BackendPlaylistId != null,
                        ReplacedPlaylistId: request.BackendPlaylistId),
                    create.Status,
                    RecoveryPlaylistId: found.Value.BackendPlaylistId);
            }

            var currentResult = await ReadAsync(context, request.BackendPlaylistId, cancellationToken);
            if (!currentResult.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt, BackendPlaylistSnapshot>(currentResult);
            var current = currentResult.Value!;
            if (Conflicts(request, current))
                return new(BackendPlaylistTargetStatus.Conflict, UpstreamStatus: currentResult.UpstreamStatus, ErrorCode: "target-revision-conflict");
            var desired = BuildFinalOrder(current, request);
            if (current.Members.Select(member => member.BackendItemId).SequenceEqual(desired))
                return new(BackendPlaylistTargetStatus.Success, new(current, false, unsupported), currentResult.UpstreamStatus);

            // OpenSubsonic createPlaylist with playlistId replaces the ordered membership in one operation.
            // This is deterministic on retry and avoids index-based duplicate/removal drift.
            var replace = await CallAsync(context, "createPlaylist",
                [Pair("playlistId", current.BackendPlaylistId), .. desired.Select(id => Pair("songId", id))],
                cancellationToken);
            if (!replace.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(replace, current.BackendPlaylistId);
            if (!TryProtocolSuccess(replace, out var protocolFailure))
                return ConvertFailure<BackendPlaylistWriteReceipt>(protocolFailure!, current.BackendPlaylistId);
            var final = await ReadAsync(context, current.BackendPlaylistId, cancellationToken);
            return final.IsSuccess
                ? new(BackendPlaylistTargetStatus.Success, new(final.Value!, true, unsupported), replace.Status)
                : ConvertFailure<BackendPlaylistWriteReceipt, BackendPlaylistSnapshot>(final, current.BackendPlaylistId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(BackendPlaylistTargetStatus.Cancelled, ErrorCode: "cancelled", RecoveryPlaylistId: request.BackendPlaylistId);
        }
    }

    private async Task<HttpResult> CallAsync(
        BackendPlaylistTargetContext context,
        string endpoint,
        IReadOnlyList<KeyValuePair<string, string>> operationParameters,
        CancellationToken cancellationToken)
    {
        var authentication = await _authentication.ResolveAsync(context, cancellationToken);
        var parameters = authentication.FormParameters
            .Where(pair => !pair.Key.Equals("f", StringComparison.OrdinalIgnoreCase))
            .Concat([Pair("f", "json")])
            .Concat(operationParameters)
            .ToArray();
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, $"rest/{endpoint}.view"))
        {
            Content = new FormUrlEncodedContent(parameters)
        };
        foreach (var header in authentication.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            return new(response.StatusCode, body, ContentType: response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return new(HttpStatusCode.ServiceUnavailable, null, "transport-failure");
        }
    }

    private static bool TryProtocolSuccess(HttpResult result, out HttpResult? failure)
    {
        using var document = JsonDocument.Parse(result.Body!);
        return TryResponseRoot(document.RootElement, out _, out failure);
    }

    private static bool TryResponseRoot(JsonElement document, out JsonElement root, out HttpResult? failure)
    {
        root = document.GetPropertyOrDefault("subsonic-response");
        if (root.ValueKind == JsonValueKind.Object && root.StringOrNull("status") == "ok")
        {
            failure = null;
            return true;
        }
        var code = root.GetPropertyOrDefault("error").GetPropertyOrDefault("code").ToString();
        failure = new(HttpStatusCode.OK, null, string.IsNullOrWhiteSpace(code) ? "subsonic-failed" : $"subsonic-{code}");
        return false;
    }

    private static IReadOnlyList<string> UnsupportedFields(BackendPlaylistMetadata metadata)
    {
        var fields = new List<string>();
        if (metadata.Description != null) fields.Add("description");
        if (metadata.Artwork != null) fields.Add("artwork");
        return fields;
    }

    private static bool Conflicts(BackendPlaylistWriteRequest request, BackendPlaylistSnapshot current) =>
        request.ExpectedFingerprint != null && request.ExpectedFingerprint != current.Fingerprint ||
        request.ExpectedRevision != null && request.ExpectedRevision != current.NativeRevision;

    private static long? MillisecondsFromSeconds(long? seconds) =>
        seconds > 0 && seconds <= long.MaxValue / 1000 ? seconds * 1000 : null;

    private static IReadOnlyList<string> BuildFinalOrder(
        BackendPlaylistSnapshot current,
        BackendPlaylistWriteRequest request)
    {
        var requested = request.OrderedBackendItemIds.ToHashSet(StringComparer.Ordinal);
        var syncOwned = request.SyncOwnedBackendItemIds.ToHashSet(StringComparer.Ordinal);
        var preserved = current.Members.Select(member => member.BackendItemId)
            .Where(id => !requested.Contains(id))
            .Where(id => !request.RemoveStaleSyncOwnedItems || !syncOwned.Contains(id));
        return request.OrderedBackendItemIds.Concat(preserved).Distinct(StringComparer.Ordinal).ToArray();
    }

    private static BackendPlaylistTargetResult<T> ConvertFailure<T>(HttpResult result, string? recoveryId = null) =>
        new(MapStatus(result.Status, result.ErrorCode), UpstreamStatus: result.Status, ErrorCode: result.ErrorCode ?? $"upstream-{(int)result.Status}", RecoveryPlaylistId: recoveryId);

    private static BackendPlaylistTargetResult<T> ConvertFailure<T, TSource>(BackendPlaylistTargetResult<TSource> result, string? recoveryId = null) =>
        new(result.Status, UpstreamStatus: result.UpstreamStatus, ErrorCode: result.ErrorCode, RecoveryPlaylistId: recoveryId ?? result.RecoveryPlaylistId);

    private static BackendPlaylistTargetStatus MapStatus(HttpStatusCode status, string? errorCode) => status switch
    {
        HttpStatusCode.NotFound => BackendPlaylistTargetStatus.NotFound,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => BackendPlaylistTargetStatus.Unauthorized,
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => BackendPlaylistTargetStatus.Conflict,
        HttpStatusCode.OK when errorCode != null => BackendPlaylistTargetStatus.BackendFailure,
        _ => BackendPlaylistTargetStatus.BackendFailure
    };

    private static KeyValuePair<string, string> Pair(string key, string value) => new(key, value);
    private sealed record HttpResult(HttpStatusCode Status, byte[]? Body, string? ErrorCode = null, string? ContentType = null)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;
    }

    private sealed class NoAuthenticationResolver : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(BackendPlaylistAuthentication.None);
    }
}
