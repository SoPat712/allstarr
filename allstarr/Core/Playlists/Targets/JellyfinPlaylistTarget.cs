using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using allstarr.Models.Settings;
using allstarr.Services.Jellyfin;

namespace allstarr.Core.Playlists.Targets;

public sealed class JellyfinPlaylistTarget : IBackendPlaylistTarget
{
    private readonly HttpClient _client;
    private readonly Uri _baseUri;
    private readonly IBackendPlaylistAuthenticationResolver _authentication;

    public JellyfinPlaylistTarget(IHttpClientFactory clients, IOptions<JellyfinSettings> settings)
        : this(
            clients.CreateClient(JellyfinProxyService.HttpClientName),
            new Uri(settings.Value.Url ?? throw new InvalidOperationException("Jellyfin backend URL is not configured.")),
            new JellyfinConfiguredAuthentication(settings.Value))
    {
    }

    public JellyfinPlaylistTarget(HttpClient client, Uri baseUri, IBackendPlaylistAuthenticationResolver? authentication = null)
    {
        _client = client;
        _baseUri = new Uri(baseUri.ToString().TrimEnd('/') + "/");
        _authentication = authentication ?? new NoAuthenticationResolver();
    }

    public BackendPlaylistFamily Family => BackendPlaylistFamily.Jellyfin;

    public BackendPlaylistTargetCapabilities Capabilities { get; } = new(
        CanCreate: true,
        CanReadMembership: true,
        CanReconcileMembership: true,
        PreservesRequestedOrder: true,
        CanWriteName: true,
        CanWriteDescription: true,
        CanWriteArtwork: true,
        HasNativeRevision: false,
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
        var search = string.IsNullOrWhiteSpace(query)
            ? string.Empty
            : $"&SearchTerm={Escape(query.Trim())}";
        var path = $"Users/{Escape(context.VerifiedPrincipalId)}/Items?IncludeItemTypes=Playlist&Recursive=true&Fields=Overview,ChildCount,PrimaryImageTag&StartIndex={offset}&Limit={limit}{search}";
        var response = await SendAsync(context, HttpMethod.Get, path, null, cancellationToken);
        if (!response.IsSuccess)
            return ConvertFailure<IReadOnlyList<BackendPlaylistSummary>>(response);

        using var document = JsonDocument.Parse(response.Body!);
        var values = new List<BackendPlaylistSummary>();
        foreach (var item in document.RootElement.GetPropertyOrDefault("Items").EnumerateArrayOrEmpty())
        {
            var id = item.StringOrNull("Id");
            var name = item.StringOrNull("Name");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name)) continue;
            int? trackCount = item.GetPropertyOrDefault("ChildCount").TryGetInt32(out var count) ? count : null;
            var imageTag = item.StringOrNull("PrimaryImageTag");
            values.Add(new BackendPlaylistSummary(
                id,
                name,
                trackCount,
                item.StringOrNull("Overview"),
                string.IsNullOrWhiteSpace(imageTag) ? null : $"jellyfin:{id}:{imageTag}"));
        }

        return new(BackendPlaylistTargetStatus.Success, values, response.Status);
    }

    public async Task<BackendPlaylistTargetResult<BackendPlaylistArtwork>> ReadArtworkAsync(
        BackendPlaylistTargetContext context,
        string backendPlaylistId,
        string? artworkReference,
        CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            context,
            HttpMethod.Get,
            $"Items/{Escape(backendPlaylistId)}/Images/Primary",
            null,
            cancellationToken);
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
        var path = $"Users/{Escape(context.VerifiedPrincipalId)}/Items?IncludeItemTypes=Playlist&Recursive=true&SearchTerm={Escape(name)}";
        var response = await SendAsync(context, HttpMethod.Get, path, null, cancellationToken);
        if (!response.IsSuccess) return ConvertFailure<BackendPlaylistSnapshot?>(response);

        using var document = JsonDocument.Parse(response.Body!);
        foreach (var item in document.RootElement.GetPropertyOrDefault("Items").EnumerateArrayOrEmpty())
        {
            if (item.StringOrNull("Name")?.Equals(name, StringComparison.OrdinalIgnoreCase) != true) continue;
            var id = item.StringOrNull("Id");
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
        var metadataResponse = await SendAsync(context, HttpMethod.Get, $"Users/{Escape(context.VerifiedPrincipalId)}/Items/{Escape(backendPlaylistId)}", null, cancellationToken);
        if (!metadataResponse.IsSuccess) return ConvertFailure<BackendPlaylistSnapshot>(metadataResponse);
        var membersResponse = await SendAsync(context, HttpMethod.Get,
            $"Playlists/{Escape(backendPlaylistId)}/Items?UserId={Escape(context.VerifiedPrincipalId)}&Fields=RunTimeTicks",
            null, cancellationToken);
        if (!membersResponse.IsSuccess) return ConvertFailure<BackendPlaylistSnapshot>(membersResponse);

        using var metadata = JsonDocument.Parse(metadataResponse.Body!);
        using var members = JsonDocument.Parse(membersResponse.Body!);
        var root = metadata.RootElement;
        var memberList = members.RootElement.GetPropertyOrDefault("Items").EnumerateArrayOrEmpty()
            .Select(item => new BackendPlaylistMember(
                item.StringOrNull("Id") ?? throw new JsonException("Jellyfin playlist member has no Id."),
                item.StringOrNull("PlaylistItemId"),
                MillisecondsFromTicks(item.Int64OrNull("RunTimeTicks"))))
            .ToArray();
        var name = root.StringOrNull("Name") ?? backendPlaylistId;
        var description = root.StringOrNull("Overview");
        var artwork = root.GetPropertyOrDefault("ImageTags").StringOrNull("Primary");
        var reportedCount = members.RootElement.Int64OrNull("TotalRecordCount") is { } count &&
                            count is >= 0 and <= int.MaxValue
            ? (int)count
            : memberList.Length;
        var duration = MillisecondsFromTicks(root.Int64OrNull("RunTimeTicks")) ??
                       (memberList.Length == 0
                           ? 0
                           : memberList.All(item => item.DurationMilliseconds.HasValue)
                               ? memberList.Sum(item => item.DurationMilliseconds!.Value)
                               : null);
        var fingerprint = BackendPlaylistSnapshot.ComputeFingerprint(backendPlaylistId, name, memberList, description, artwork);
        return new(BackendPlaylistTargetStatus.Success,
            new(backendPlaylistId, name, memberList, fingerprint, null, description, artwork,
                reportedCount, duration),
            membersResponse.Status);
    }

    public async Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> WriteAsync(
        BackendPlaylistTargetContext context,
        BackendPlaylistWriteRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            if (request.Mode == BackendPlaylistWriteMode.Recreate)
            {
                var created = request.RecoveryPlaylistId == null
                    ? await CreateAsync(context, request, cancellationToken)
                    : await WriteAsync(context, new BackendPlaylistWriteRequest(
                        BackendPlaylistWriteMode.Reconcile,
                        request.Metadata,
                        request.OrderedBackendItemIds,
                        request.IdempotencyKey,
                        request.RecoveryPlaylistId,
                        syncOwnedBackendItemIds: request.OrderedBackendItemIds,
                        removeStaleSyncOwnedItems: true), cancellationToken);
                if (!created.IsSuccess) return created;
                return created with
                {
                    Value = created.Value! with
                    {
                        ReplacementRequiresCleanup = request.BackendPlaylistId != null,
                        ReplacedPlaylistId = request.BackendPlaylistId
                    },
                    RecoveryPlaylistId = created.Value!.Snapshot.BackendPlaylistId
                };
            }

            if (request.BackendPlaylistId == null)
                return await CreateAsync(context, request, cancellationToken);

            var currentResult = await ReadAsync(context, request.BackendPlaylistId, cancellationToken);
            if (!currentResult.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt, BackendPlaylistSnapshot>(currentResult);
            var current = currentResult.Value!;
            if (Conflicts(request, current))
                return new(BackendPlaylistTargetStatus.Conflict, UpstreamStatus: currentResult.UpstreamStatus, ErrorCode: "target-revision-conflict");

            var desired = BuildFinalOrder(current, request);
            var metadataMatches = current.Name == request.Metadata.Name && current.Description == request.Metadata.Description;
            if (current.Members.Select(member => member.BackendItemId).SequenceEqual(desired) && metadataMatches && request.Metadata.Artwork == null)
                return Success(current, changed: false, []);

            var staleOwned = request.SyncOwnedBackendItemIds.ToHashSet(StringComparer.Ordinal);
            var staleEntries = current.Members
                .Where(member => request.RemoveStaleSyncOwnedItems &&
                                 staleOwned.Contains(member.BackendItemId) &&
                                 !request.OrderedBackendItemIds.Contains(member.BackendItemId, StringComparer.Ordinal))
                .Select(member => member.EntryId ?? member.BackendItemId)
                .ToArray();
            if (staleEntries.Length > 0)
            {
                var remove = await SendAsync(context, HttpMethod.Delete,
                    $"Playlists/{Escape(current.BackendPlaylistId)}/Items?EntryIds={Csv(staleEntries)}",
                    null, cancellationToken);
                if (!remove.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(remove, current.BackendPlaylistId);
            }

            var present = current.Members.Select(member => member.BackendItemId).ToHashSet(StringComparer.Ordinal);
            var missing = desired.Where(id => !present.Contains(id)).ToArray();
            if (missing.Length > 0)
            {
                var add = await SendAsync(context, HttpMethod.Post,
                    $"Playlists/{Escape(current.BackendPlaylistId)}/Items?Ids={Csv(missing)}&UserId={Escape(context.VerifiedPrincipalId)}",
                    JsonContent.Create(new { }), cancellationToken);
                if (!add.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(add, current.BackendPlaylistId);
            }

            var afterMembership = await ReadAsync(context, current.BackendPlaylistId, cancellationToken);
            if (!afterMembership.IsSuccess)
                return ConvertFailure<BackendPlaylistWriteReceipt, BackendPlaylistSnapshot>(afterMembership, current.BackendPlaylistId);
            var working = afterMembership.Value!.Members.ToList();
            for (var index = 0; index < desired.Count; index++)
            {
                var oldIndex = working.FindIndex(member => member.BackendItemId == desired[index]);
                if (oldIndex == index) continue;
                var member = working[oldIndex];
                var move = await SendAsync(context, HttpMethod.Post,
                    $"Playlists/{Escape(current.BackendPlaylistId)}/Items/{Escape(member.EntryId ?? member.BackendItemId)}/Move/{index}",
                    JsonContent.Create(new { }), cancellationToken);
                if (!move.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(move, current.BackendPlaylistId);
                working.RemoveAt(oldIndex);
                working.Insert(index, member);
            }

            var metadata = await WriteMetadataAsync(context, current.BackendPlaylistId, request.Metadata, cancellationToken);
            if (!metadata.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(metadata, current.BackendPlaylistId);
            var final = await ReadAsync(context, current.BackendPlaylistId, cancellationToken);
            return final.IsSuccess
                ? Success(final.Value!, changed: true, [])
                : ConvertFailure<BackendPlaylistWriteReceipt, BackendPlaylistSnapshot>(final, current.BackendPlaylistId);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new(BackendPlaylistTargetStatus.Cancelled, ErrorCode: "cancelled", RecoveryPlaylistId: request.BackendPlaylistId);
        }
    }

    private async Task<BackendPlaylistTargetResult<BackendPlaylistWriteReceipt>> CreateAsync(
        BackendPlaylistTargetContext context,
        BackendPlaylistWriteRequest request,
        CancellationToken cancellationToken)
    {
        var create = await SendAsync(context, HttpMethod.Post,
            $"Playlists?Name={Escape(request.Metadata.Name)}&Ids={Csv(request.OrderedBackendItemIds)}&UserId={Escape(context.VerifiedPrincipalId)}",
            JsonContent.Create(new { }), cancellationToken);
        if (!create.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(create);
        using var document = JsonDocument.Parse(create.Body!);
        var id = document.RootElement.StringOrNull("Id") ?? throw new JsonException("Jellyfin create-playlist response has no Id.");
        var metadata = await WriteMetadataAsync(context, id, request.Metadata, cancellationToken);
        if (!metadata.IsSuccess) return ConvertFailure<BackendPlaylistWriteReceipt>(metadata, id);
        var snapshot = await ReadAsync(context, id, cancellationToken);
        return snapshot.IsSuccess
            ? Success(snapshot.Value!, changed: true, [])
            : ConvertFailure<BackendPlaylistWriteReceipt, BackendPlaylistSnapshot>(snapshot, id);
    }

    private async Task<HttpResult> WriteMetadataAsync(
        BackendPlaylistTargetContext context,
        string id,
        BackendPlaylistMetadata metadata,
        CancellationToken cancellationToken)
    {
        var update = await SendAsync(context, HttpMethod.Post, $"Items/{Escape(id)}",
            JsonContent.Create(new { metadata.Name, Overview = metadata.Description }), cancellationToken);
        if (!update.IsSuccess || metadata.Artwork == null) return update;
        using var artwork = new ByteArrayContent(metadata.Artwork);
        artwork.Headers.ContentType = new(metadata.ArtworkContentType ?? "image/jpeg");
        return await SendAsync(context, HttpMethod.Post, $"Items/{Escape(id)}/Images/Primary", artwork, cancellationToken);
    }

    private async Task<HttpResult> SendAsync(
        BackendPlaylistTargetContext context,
        HttpMethod method,
        string path,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(_baseUri, path)) { Content = content };
        var authentication = await _authentication.ResolveAsync(context, cancellationToken);
        foreach (var header in authentication.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        try
        {
            using var response = await _client.SendAsync(request, cancellationToken);
            var body = response.Content.Headers.ContentLength == 0
                ? []
                : await response.Content.ReadAsByteArrayAsync(cancellationToken);
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

    private static bool Conflicts(BackendPlaylistWriteRequest request, BackendPlaylistSnapshot current) =>
        request.ExpectedFingerprint != null && request.ExpectedFingerprint != current.Fingerprint ||
        request.ExpectedRevision != null && request.ExpectedRevision != current.NativeRevision;

    private static long? MillisecondsFromTicks(long? ticks) =>
        ticks > 0 ? ticks / TimeSpan.TicksPerMillisecond : null;

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

    private static BackendPlaylistTargetResult<BackendPlaylistWriteReceipt> Success(
        BackendPlaylistSnapshot snapshot, bool changed, IReadOnlyList<string> unsupported) =>
        new(BackendPlaylistTargetStatus.Success, new(snapshot, changed, unsupported));

    private static BackendPlaylistTargetResult<T> ConvertFailure<T>(
        HttpResult result, string? recoveryId = null) =>
        new(MapStatus(result.Status), UpstreamStatus: result.Status, ErrorCode: result.ErrorCode ?? $"upstream-{(int)result.Status}", RecoveryPlaylistId: recoveryId);

    private static BackendPlaylistTargetResult<T> ConvertFailure<T, TSource>(
        BackendPlaylistTargetResult<TSource> result, string? recoveryId = null) =>
        new(result.Status, UpstreamStatus: result.UpstreamStatus, ErrorCode: result.ErrorCode, RecoveryPlaylistId: recoveryId ?? result.RecoveryPlaylistId);

    private static BackendPlaylistTargetStatus MapStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => BackendPlaylistTargetStatus.NotFound,
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => BackendPlaylistTargetStatus.Unauthorized,
        HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => BackendPlaylistTargetStatus.Conflict,
        _ => BackendPlaylistTargetStatus.BackendFailure
    };

    private static string Escape(string value) => Uri.EscapeDataString(value);
    private static string Csv(IEnumerable<string> values) => Escape(string.Join(',', values));
    private sealed record HttpResult(HttpStatusCode Status, byte[]? Body, string? ErrorCode = null, string? ContentType = null)
    {
        public bool IsSuccess => (int)Status is >= 200 and < 300;
    }

    private sealed class NoAuthenticationResolver : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken) =>
            ValueTask.FromResult(BackendPlaylistAuthentication.None);
    }

    private sealed class JellyfinConfiguredAuthentication(JellyfinSettings settings) : IBackendPlaylistAuthenticationResolver
    {
        public ValueTask<BackendPlaylistAuthentication> ResolveAsync(BackendPlaylistTargetContext context, CancellationToken cancellationToken)
        {
            var authorization = $"MediaBrowser Client=\"{settings.ClientName}\", Device=\"{settings.DeviceName}\", " +
                                $"DeviceId=\"{settings.DeviceId}\", Version=\"{settings.ClientVersion}\", Token=\"{settings.ApiKey}\"";
            return ValueTask.FromResult(new BackendPlaylistAuthentication(
                new Dictionary<string, string> { ["X-Emby-Authorization"] = authorization }, []));
        }
    }
}

internal static class PlaylistTargetJsonExtensions
{
    public static JsonElement GetPropertyOrDefault(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) ? value : default;

    public static string? StringOrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    public static long? Int64OrNull(this JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt64(out var number)
            ? number
            : null;

    public static IEnumerable<JsonElement> EnumerateArrayOrEmpty(this JsonElement element) =>
        element.ValueKind == JsonValueKind.Array ? element.EnumerateArray().ToArray() : [];
}
