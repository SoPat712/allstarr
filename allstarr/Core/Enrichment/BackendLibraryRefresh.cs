using System.Globalization;
using System.Text.Json;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playlists.Targets;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Settings;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Enrichment;

public sealed record BackendLibraryRefreshRequest(string BackendLibraryId, Guid? CredentialReferenceId = null);
public sealed record BackendLibraryRefreshResult(bool Accepted, string? NativeScanId = null);

public interface IBackendLibraryRefresher
{
    ProtocolKind Protocol { get; }
    Task<BackendLibraryRefreshResult> RefreshAsync(ProtocolExecutionContext context,
        BackendLibraryRefreshRequest request, CancellationToken cancellationToken);
}

public sealed class BackendLibraryRefresherResolver(IEnumerable<IBackendLibraryRefresher> refreshers)
{
    private readonly IReadOnlyDictionary<ProtocolKind, IBackendLibraryRefresher> _refreshers = refreshers.ToDictionary(item => item.Protocol);
    public IBackendLibraryRefresher Resolve(ProtocolKind protocol) => _refreshers.TryGetValue(protocol, out var refresher)
        ? refresher : throw new NotSupportedException("The backend library refresh protocol is unsupported.");
}

public static class BackendRefreshValidation
{
    public static void Validate(ProtocolExecutionContext context, BackendLibraryRefreshRequest request, ProtocolKind protocol)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        if (context.Protocol != protocol || context.Principal == null || string.IsNullOrWhiteSpace(request.BackendLibraryId))
            throw new ArgumentException("The backend refresh scope is invalid.", nameof(request));
    }
}

public sealed class JellyfinLibraryRefresher : IBackendLibraryRefresher
{
    public const string HttpClientName = "JellyfinLibraryRefresh";
    private readonly HttpClient _http;
    private readonly JellyfinSettings _settings;
    public JellyfinLibraryRefresher(IHttpClientFactory clients, IOptions<JellyfinSettings> settings)
        : this(clients.CreateClient(HttpClientName), settings.Value) { }
    public JellyfinLibraryRefresher(HttpClient http, JellyfinSettings settings) => (_http, _settings) = (http, settings);
    public ProtocolKind Protocol => ProtocolKind.Jellyfin;

    public async Task<BackendLibraryRefreshResult> RefreshAsync(ProtocolExecutionContext context,
        BackendLibraryRefreshRequest request, CancellationToken cancellationToken)
    {
        BackendRefreshValidation.Validate(context, request, Protocol);
        if (string.IsNullOrWhiteSpace(_settings.Url) || string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException("Jellyfin library refresh is not configured.");
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(_settings.Url.TrimEnd('/') + "/"), "Library/Refresh"));
        message.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(_settings.ApiKey, "Allstarr", "Server", "allstarr-server", AppVersion.Version));
        using var response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        return new(true);
    }
}

public sealed class SubsonicLibraryRefresher : IBackendLibraryRefresher
{
    public const string HttpClientName = "SubsonicLibraryRefresh";
    private readonly HttpClient _http;
    private readonly SubsonicSettings _settings;
    private readonly IBackendPlaylistAuthenticationResolver _authentication;
    public SubsonicLibraryRefresher(IHttpClientFactory clients, IOptions<SubsonicSettings> settings,
        IBackendPlaylistAuthenticationResolver authentication)
        : this(clients.CreateClient(HttpClientName), settings.Value, authentication) { }
    public SubsonicLibraryRefresher(HttpClient http, SubsonicSettings settings,
        IBackendPlaylistAuthenticationResolver authentication) => (_http, _settings, _authentication) = (http, settings, authentication);
    public ProtocolKind Protocol => ProtocolKind.Subsonic;

    public async Task<BackendLibraryRefreshResult> RefreshAsync(ProtocolExecutionContext context,
        BackendLibraryRefreshRequest request, CancellationToken cancellationToken)
    {
        BackendRefreshValidation.Validate(context, request, Protocol);
        if (string.IsNullOrWhiteSpace(_settings.Url) || !request.CredentialReferenceId.HasValue)
            throw new InvalidOperationException("Subsonic library refresh requires a user-owned encrypted credential reference.");
        var authentication = await _authentication.ResolveAsync(new(context.BackendInstanceId,
            context.VerifiedBackendPrincipalId, request.CredentialReferenceId.Value.ToString()), cancellationToken);
        var form = new List<KeyValuePair<string, string>>(authentication.FormParameters) { new("f", "json") };
        using var message = new HttpRequestMessage(HttpMethod.Post,
            new Uri(new Uri(_settings.Url.TrimEnd('/') + "/"), "rest/startScan.view"))
        { Content = new FormUrlEncodedContent(form) };
        foreach (var header in authentication.Headers) message.Headers.TryAddWithoutValidation(header.Key, header.Value);
        using var response = await _http.SendAsync(message, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken));
        var envelope = document.RootElement.TryGetProperty("subsonic-response", out var root) ? root : document.RootElement;
        if (envelope.TryGetProperty("status", out var status) && status.GetString() == "failed")
            throw new HttpRequestException("The Subsonic scan request failed.");
        string? scanId = null;
        if (envelope.TryGetProperty("scanStatus", out var scan) && scan.TryGetProperty("count", out var count) && count.TryGetInt64(out var number))
            scanId = number.ToString(CultureInfo.InvariantCulture);
        return new(true, scanId);
    }
}

public sealed record BackendLibraryRefreshJobPayload(string BackendLibraryId, string BackendInstanceId,
    string BackendPrincipalId, Guid? CredentialReferenceId = null);

public sealed class BackendLibraryRefreshOrchestrator(DurableJobQueue queue)
{
    public Task<DurableJobEnqueueResult> EnqueueAsync(Guid ownerUserId,
        BackendLibraryRefreshJobPayload payload, string lineageIdempotencyKey, string correlationId,
        CancellationToken cancellationToken = default) => queue.EnqueueAsync(new DurableJobEnqueueRequest<BackendLibraryRefreshJobPayload>(
            "library.refresh", $"refresh:{payload.BackendInstanceId}:{payload.BackendLibraryId}:{lineageIdempotencyKey}", payload,
            ownerUserId, CorrelationId: correlationId), cancellationToken);
}

public sealed class BackendLibraryRefreshJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    BackendLibraryRefresherResolver refreshers, IPlatformClock clock,
    IBackendLibraryAccessResolver libraryAccess) : IDurableJobHandler
{
    public string JobType => "library.refresh";
    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        BackendLibraryRefreshJobPayload? payload;
        try { payload = context.Claim.Payload.Deserialize<BackendLibraryRefreshJobPayload>(); } catch (JsonException) { payload = null; }
        if (payload == null || string.IsNullOrWhiteSpace(payload.BackendLibraryId) || string.IsNullOrWhiteSpace(payload.BackendInstanceId) ||
            string.IsNullOrWhiteSpace(payload.BackendPrincipalId) || context.Claim.OwnerUserId == null)
            return DurableJobCompletion.Failure("library_refresh_payload_invalid", "The backend library refresh payload is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == context.Claim.OwnerUserId && item.Enabled &&
            item.BackendInstanceId == payload.BackendInstanceId && item.BackendPrincipalId == payload.BackendPrincipalId, cancellationToken);
        if (user == null || user.BackendType is not ("jellyfin" or "subsonic"))
            return DurableJobCompletion.Failure("library_refresh_identity_unavailable", "The linked backend identity is unavailable.");
        var protocol = user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic;
        var execution = new ProtocolExecutionContext(protocol, user.BackendInstanceId, user.BackendPrincipalId,
            new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId,
                user.BackendPrincipalId, user.DisplayName, user.IsAdmin), context.Claim.CorrelationId, clock.UtcNow.AddMinutes(10),
            cancellationToken);
        if (!(await libraryAccess.ResolveAsync(execution, cancellationToken)).Allows(payload.BackendLibraryId))
            return DurableJobCompletion.Failure("library_refresh_access_unavailable", "The backend library is unavailable to this user.");
        try
        {
            var result = await refreshers.Resolve(protocol).RefreshAsync(execution,
                new(payload.BackendLibraryId, payload.CredentialReferenceId), cancellationToken);
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = user.Id,
                Category = "library-refresh",
                Action = "scan.requested",
                Outcome = result.Accepted ? "succeeded" : "failed",
                CorrelationId = context.Claim.CorrelationId,
                DetailsJson = JsonSerializer.Serialize(new { payload.BackendLibraryId, payload.BackendInstanceId, protocol, result.NativeScanId }),
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(cancellationToken);
            return result.Accepted ? DurableJobCompletion.Success() : DurableJobCompletion.Failure("library_refresh_rejected", "The backend rejected its library refresh request.");
        }
        catch (HttpRequestException) { return DurableJobCompletion.Retry("library_refresh_backend_unavailable", "The backend library refresh is temporarily unavailable."); }
        catch (InvalidOperationException) { return DurableJobCompletion.Failure("library_refresh_not_configured", "The backend library refresh is not configured."); }
    }
}

public static class MetadataEnrichmentRegistration
{
    public static IServiceCollection AddMetadataEnrichment(this IServiceCollection services)
    {
        services.AddSingleton<IMetadataEnrichmentPlanner, MetadataEnrichmentPlanner>();
        services.AddSingleton<IManagedTagFileMutator, TagLibManagedTagFileMutator>();
        services.AddSingleton<IManagedMetadataWriter, TagLibManagedMetadataWriter>();
        services.AddSingleton<ManagedMetadataPlanApplicator>();
        services.AddSingleton<DurableMetadataEnrichmentService>();
        services.AddHttpClient(JellyfinLibraryRefresher.HttpClientName);
        services.AddHttpClient(SubsonicLibraryRefresher.HttpClientName);
        services.AddSingleton<IBackendLibraryRefresher, JellyfinLibraryRefresher>();
        services.AddSingleton<IBackendLibraryRefresher, SubsonicLibraryRefresher>();
        services.AddSingleton<BackendLibraryRefresherResolver>();
        services.AddSingleton<BackendLibraryRefreshOrchestrator>();
        services.AddSingleton<IDurableJobHandler, BackendLibraryRefreshJobHandler>();
        return services;
    }
}
