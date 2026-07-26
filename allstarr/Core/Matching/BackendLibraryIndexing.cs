using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Matching;

public sealed record LibraryCatalogScanRequest(string LibraryScopeId, Guid? CredentialReferenceId = null, int PageSize = 200);
public sealed record LibraryCatalogScanResult(int Seen, int Indexed, int SkippedPathless, int SkippedMalformed, int Pages);

public interface IBackendLibraryCatalogScanner
{
    ProtocolKind Protocol { get; }
    Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request, CancellationToken cancellationToken);
}

public sealed class BackendLibraryCatalogScannerResolver(IEnumerable<IBackendLibraryCatalogScanner> scanners)
{
    private readonly IReadOnlyDictionary<ProtocolKind, IBackendLibraryCatalogScanner> _scanners = scanners.ToDictionary(item => item.Protocol);
    public IBackendLibraryCatalogScanner Resolve(ProtocolKind protocol) => _scanners.TryGetValue(protocol, out var scanner)
        ? scanner : throw new NotSupportedException("The backend library catalog protocol is unsupported.");
}

public abstract class JsonLibraryCatalogScanner(ILibraryIndexService index, IPlatformClock clock)
{
    protected ILibraryIndexService Index { get; } = index;
    protected IPlatformClock Clock { get; } = clock;
    protected static string? Text(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    protected static long? Number(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;
    protected static DateTimeOffset? Date(JsonElement root, string name) => DateTimeOffset.TryParse(Text(root, name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value) ? value : null;
    protected static IReadOnlyDictionary<string, string> ProviderIds(JsonElement root)
    {
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        if (root.ValueKind != JsonValueKind.Object) return values;
        foreach (var property in root.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(property.Value.GetString()))
                values[property.Name.ToLowerInvariant()] = property.Value.GetString()!;
        return values;
    }
    protected static void ValidateRequest(ProtocolExecutionContext context, LibraryCatalogScanRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(request.LibraryScopeId) || request.PageSize is < 1 or > 500 ||
            context.LibraryScopeId != null && !context.LibraryScopeId.Equals(request.LibraryScopeId, StringComparison.Ordinal))
            throw new ArgumentException("The library scan scope or page size is invalid.", nameof(request));
    }
}

public sealed class JellyfinLibraryCatalogScanner : JsonLibraryCatalogScanner, IBackendLibraryCatalogScanner
{
    public const string HttpClientName = "JellyfinLibraryCatalog";
    private readonly HttpClient _http;
    private readonly JellyfinSettings _settings;
    public JellyfinLibraryCatalogScanner(IHttpClientFactory clients, IOptions<JellyfinSettings> settings, ILibraryIndexService index, IPlatformClock clock)
        : this(clients.CreateClient(HttpClientName), settings.Value, index, clock) { }
    public JellyfinLibraryCatalogScanner(HttpClient http, JellyfinSettings settings, ILibraryIndexService index, IPlatformClock clock) : base(index, clock) => (_http, _settings) = (http, settings);
    public ProtocolKind Protocol => ProtocolKind.Jellyfin;

    public async Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(context, request);
        if (context.Protocol != ProtocolKind.Jellyfin || string.IsNullOrWhiteSpace(_settings.Url) || string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException("Jellyfin library indexing requires configured backend URL and API authentication.");
        var seen = 0; var indexed = 0; var pathless = 0; var malformed = 0; var pages = 0; var offset = 0;
        while (true)
        {
            var uri = new Uri(new Uri(_settings.Url.TrimEnd('/') + "/"),
                $"Items?Recursive=true&IncludeItemTypes=Audio&StartIndex={offset}&Limit={request.PageSize}&Fields=Path,ProviderIds,DateModified,DateCreated,Album,AlbumArtist,Artists,RunTimeTicks,ImageTags");
            using var message = new HttpRequestMessage(HttpMethod.Get, uri);
            message.Headers.TryAddWithoutValidation("X-Emby-Token", _settings.ApiKey);
            using var response = await _http.SendAsync(message, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var items = document.RootElement.TryGetProperty("Items", out var data) && data.ValueKind == JsonValueKind.Array ? data.EnumerateArray().ToArray() : [];
            pages++; seen += items.Length;
            foreach (var item in items)
            {
                var path = Text(item, "Path");
                if (string.IsNullOrWhiteSpace(path)) { pathless++; continue; }
                var id = Text(item, "Id"); var title = Text(item, "Name");
                // Jellyfin 10.11 does not populate DateModified for audio items even
                // when requested explicitly. DateCreated is stable and universally
                // returned, so use it as the source revision fallback.
                var modified = Date(item, "DateModified") ?? Date(item, "DateCreated");
                var artists = item.TryGetProperty("Artists", out var artistValues) && artistValues.ValueKind == JsonValueKind.Array
                    ? artistValues.EnumerateArray().Select(value => value.GetString()).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray() : [];
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || artists.Length == 0 || modified == null) { malformed++; continue; }
                var providers = item.TryGetProperty("ProviderIds", out var providerRoot) ? ProviderIds(providerRoot) : new Dictionary<string, string>();
                var duration = Number(item, "RunTimeTicks") is { } ticks ? ticks / TimeSpan.TicksPerMillisecond : 0;
                var imageTag = item.TryGetProperty("ImageTags", out var tags) ? Text(tags, "Primary") : null;
                try
                {
                    await Index.UpsertAsync(context, new(request.LibraryScopeId, id, path, title, string.Join(", ", artists), Text(item, "Album"),
                        Text(item, "AlbumArtist"), duration, Get(providers, "isrc"), Get(providers, "musicbrainztrack"),
                        Get(providers, "musicbrainzalbum"), Get(providers, "musicbrainzartist"), providers, null, null,
                        imageTag == null ? null : $"jellyfin-cover:{id}:{imageTag}", modified.Value), cancellationToken);
                    indexed++;
                }
                catch (ArgumentException) { malformed++; }
            }
            offset += items.Length;
            var total = Number(document.RootElement, "TotalRecordCount");
            if (items.Length == 0 || items.Length < request.PageSize || total.HasValue && offset >= total) break;
        }
        return new(seen, indexed, pathless, malformed, pages);
    }
    private static string? Get(IReadOnlyDictionary<string, string> values, string name) => values.TryGetValue(name, out var value) ? value : null;
}

public sealed class SubsonicLibraryCatalogScanner : JsonLibraryCatalogScanner, IBackendLibraryCatalogScanner
{
    public const string HttpClientName = "SubsonicLibraryCatalog";
    private readonly HttpClient _http;
    private readonly SubsonicSettings _settings;
    private readonly IBackendPlaylistAuthenticationResolver _authentication;
    public SubsonicLibraryCatalogScanner(IHttpClientFactory clients, IOptions<SubsonicSettings> settings,
        IBackendPlaylistAuthenticationResolver authentication, ILibraryIndexService index, IPlatformClock clock)
        : this(clients.CreateClient(HttpClientName), settings.Value, authentication, index, clock) { }
    public SubsonicLibraryCatalogScanner(HttpClient http, SubsonicSettings settings, IBackendPlaylistAuthenticationResolver authentication,
        ILibraryIndexService index, IPlatformClock clock) : base(index, clock) => (_http, _settings, _authentication) = (http, settings, authentication);
    public ProtocolKind Protocol => ProtocolKind.Subsonic;

    public async Task<LibraryCatalogScanResult> ScanAsync(ProtocolExecutionContext context, LibraryCatalogScanRequest request, CancellationToken cancellationToken)
    {
        ValidateRequest(context, request);
        if (context.Protocol != ProtocolKind.Subsonic || string.IsNullOrWhiteSpace(_settings.Url) || !request.CredentialReferenceId.HasValue)
            throw new InvalidOperationException("Subsonic library indexing requires a tenant-scoped encrypted credential reference.");
        var authentication = await _authentication.ResolveAsync(new(context.BackendInstanceId, context.VerifiedBackendPrincipalId,
            request.CredentialReferenceId.Value.ToString(), context.Actor!.TenantId), cancellationToken);
        var seen = 0; var indexed = 0; var pathless = 0; var malformed = 0; var pages = 0; var offset = 0;
        while (true)
        {
            var query = new List<KeyValuePair<string, string>>(authentication.FormParameters)
            { new("f", "json"), new("query", ""), new("songOffset", offset.ToString(CultureInfo.InvariantCulture)), new("songCount", request.PageSize.ToString(CultureInfo.InvariantCulture)) };
            var uri = new Uri(new Uri(_settings.Url.TrimEnd('/') + "/"), "rest/search3.view");
            using var message = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Content = new FormUrlEncodedContent(query)
            };
            foreach (var header in authentication.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
            using var response = await _http.SendAsync(message, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
            var envelope = document.RootElement.TryGetProperty("subsonic-response", out var value) ? value : document.RootElement;
            if (Text(envelope, "status") == "failed") throw new HttpRequestException("The Subsonic catalog request failed.");
            var search = envelope.TryGetProperty("searchResult3", out var result) ? result : default;
            var items = search.ValueKind == JsonValueKind.Object && search.TryGetProperty("song", out var songs) && songs.ValueKind == JsonValueKind.Array ? songs.EnumerateArray().ToArray() : [];
            pages++; seen += items.Length;
            foreach (var item in items)
            {
                var path = Text(item, "path");
                if (string.IsNullOrWhiteSpace(path)) { pathless++; continue; }
                var id = Text(item, "id"); var title = Text(item, "title"); var artist = Text(item, "artist"); var modified = Date(item, "created") ?? Date(item, "changed");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(artist) || modified == null) { malformed++; continue; }
                var providers = new SortedDictionary<string, string>(StringComparer.Ordinal);
                Add(providers, "musicbrainztrack", Text(item, "musicBrainzId")); Add(providers, "isrc", Text(item, "isrc"));
                if (item.TryGetProperty("providerIds", out var providerRoot)) foreach (var pair in ProviderIds(providerRoot)) providers[pair.Key] = pair.Value;
                try
                {
                    await Index.UpsertAsync(context, new(request.LibraryScopeId, id, path, title, artist, Text(item, "album"),
                        Text(item, "albumArtist"), (Number(item, "duration") ?? 0) * 1000, Text(item, "isrc"),
                        Text(item, "musicBrainzId"), Text(item, "releaseMusicBrainzId"), Text(item, "artistMusicBrainzId"),
                        providers, null, null, Text(item, "coverArt") is { } cover ? $"subsonic-cover:{cover}" : null, modified.Value), cancellationToken);
                    indexed++;
                }
                catch (ArgumentException) { malformed++; }
            }
            offset += items.Length;
            if (items.Length < request.PageSize) break;
        }
        return new(seen, indexed, pathless, malformed, pages);
    }
    private static void Add(IDictionary<string, string> values, string key, string? value) { if (!string.IsNullOrWhiteSpace(value)) values[key] = value; }
}

public sealed record LibraryIndexJobPayload(
    string LibraryScopeId,
    string BackendInstanceId,
    string BackendPrincipalId,
    Guid? CredentialReferenceId = null,
    int PageSize = 200);

public sealed class LibraryIndexJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    BackendLibraryCatalogScannerResolver scanners, IPlatformClock clock) : IDurableJobHandler
{
    public string JobType => "library.index";
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        LibraryIndexJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<LibraryIndexJobPayload>(); } catch (JsonException) { payload = null; }
        if (payload == null || string.IsNullOrWhiteSpace(payload.LibraryScopeId) || string.IsNullOrWhiteSpace(payload.BackendInstanceId) ||
            string.IsNullOrWhiteSpace(payload.BackendPrincipalId) || payload.PageSize is < 1 or > 500 ||
            context.Claim.TenantId == null || context.Claim.OwnerUserId == null)
            return DurableJobCompletion.Failure("library_index_payload_invalid", "The library index payload is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var identity = await db.BackendIdentities.AsNoTracking().SingleOrDefaultAsync(item => item.TenantId == context.Claim.TenantId &&
            item.UserId == context.Claim.OwnerUserId && item.BackendInstanceId == payload.BackendInstanceId &&
            item.PrincipalId == payload.BackendPrincipalId, cancellationToken);
        if (identity == null) return DurableJobCompletion.Failure("library_index_identity_unavailable", "The linked backend identity is unavailable.");
        var protocol = identity.BackendType.Equals("jellyfin", StringComparison.OrdinalIgnoreCase) ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic;
        var user = await db.Users.AsNoTracking().SingleAsync(item => item.Id == identity.UserId && item.TenantId == identity.TenantId, cancellationToken);
        var execution = new ProtocolExecutionContext(protocol, identity.BackendInstanceId, identity.PrincipalId,
            new AllstarrPrincipal(identity.TenantId, identity.UserId, identity.BackendType, identity.BackendInstanceId, identity.PrincipalId, user.DisplayName, false),
            context.Claim.CorrelationId, clock.UtcNow.AddMinutes(30), cancellationToken, libraryScopeId: payload.LibraryScopeId);
        try
        {
            var result = await scanners.Resolve(protocol).ScanAsync(
                execution,
                new(payload.LibraryScopeId, payload.CredentialReferenceId, payload.PageSize),
                cancellationToken);
            await using var summaryDb = await factory.CreateDbContextAsync(cancellationToken);
            summaryDb.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                TenantId = identity.TenantId,
                ActorUserId = identity.UserId,
                Category = "library-index",
                Action = "scan.completed",
                Outcome = "succeeded",
                CorrelationId = context.Claim.CorrelationId,
                DetailsJson = JsonSerializer.Serialize(new
                {
                    payload.LibraryScopeId,
                    payload.BackendInstanceId,
                    result.Seen,
                    result.Indexed,
                    result.SkippedPathless,
                    result.SkippedMalformed,
                    result.Pages
                }),
                CreatedAt = clock.UtcNow
            });
            await summaryDb.SaveChangesAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (HttpRequestException) { return DurableJobCompletion.Retry("library_index_backend_unavailable", "The backend catalog is temporarily unavailable."); }
        catch (InvalidOperationException) { return DurableJobCompletion.Failure("library_index_not_configured", "The backend catalog scanner is not configured."); }
    }
}

/// <summary>
/// Keeps the durable audio index warm for linked Jellyfin users. The scanner itself
/// requests IncludeItemTypes=Audio, so video and administrative resources never enter
/// the music identity graph.
/// </summary>
public sealed class LibraryIndexMaintenanceService(
    IDbContextFactory<AllstarrDbContext> factory,
    DurableJobQueue jobs,
    DurableStorageState storageState,
    ILogger<LibraryIndexMaintenanceService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(1);
    private static readonly TimeSpan MaximumIndexAge = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (storageState.GetSnapshot().Readiness == DurableStorageReadiness.Ready)
                    await EnqueueStaleIndexesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Durable Jellyfin audio index maintenance failed; it will retry");
            }

            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    internal async Task<int> EnqueueStaleIndexesAsync(CancellationToken cancellationToken)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var identities = await db.BackendIdentities.AsNoTracking()
            .Where(identity => identity.BackendType == "jellyfin")
            .OrderBy(identity => identity.TenantId).ThenBy(identity => identity.UserId)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var enqueued = 0;

        foreach (var identity in identities)
        {
            var lastIndexedAt = await db.LibraryTracks.AsNoTracking()
                .Where(track => track.TenantId == identity.TenantId && track.OwnerUserId == identity.UserId &&
                    track.BackendInstanceId == identity.BackendInstanceId && track.LibraryScopeId == "music")
                .MaxAsync(track => (DateTimeOffset?)track.IndexedAt, cancellationToken);
            if (lastIndexedAt.HasValue && now - lastIndexedAt.Value < MaximumIndexAge) continue;

            // An empty index may be the result of a transient backend/schema problem. Give it
            // a fresh hourly idempotency bucket so a corrected deployment can recover promptly;
            // populated indexes retain the quieter twelve-hour refresh cadence.
            var generationWindow = lastIndexedAt.HasValue ? MaximumIndexAge : CheckInterval;
            var generation = now.UtcTicks / generationWindow.Ticks;
            var result = await jobs.EnqueueAsync(new DurableJobEnqueueRequest<LibraryIndexJobPayload>(
                "library.index",
                $"library-index:auto:{identity.TenantId:N}:{identity.UserId:N}:{identity.BackendInstanceId}:music:{generation}",
                new("music", identity.BackendInstanceId, identity.PrincipalId, null, 200),
                identity.TenantId,
                identity.UserId,
                LibraryScopeId: "music",
                CorrelationId: $"library-index-auto-{identity.Id:N}-{generation}"), cancellationToken);
            if (result.Created) enqueued++;
        }

        if (enqueued > 0) logger.LogInformation("Enqueued {Count} stale Jellyfin audio index jobs", enqueued);
        return enqueued;
    }
}

public static class BackendLibraryIndexingRegistration
{
    public static IServiceCollection AddBackendLibraryIndexing(this IServiceCollection services)
    {
        services.AddHttpClient(JellyfinLibraryCatalogScanner.HttpClientName);
        services.AddHttpClient(SubsonicLibraryCatalogScanner.HttpClientName);
        services.AddSingleton<IBackendLibraryCatalogScanner, JellyfinLibraryCatalogScanner>();
        services.AddSingleton<IBackendLibraryCatalogScanner, SubsonicLibraryCatalogScanner>();
        services.AddSingleton<BackendLibraryCatalogScannerResolver>();
        services.AddSingleton<IDurableJobHandler, LibraryIndexJobHandler>();
        services.AddHostedService<LibraryIndexMaintenanceService>();
        return services;
    }
}
