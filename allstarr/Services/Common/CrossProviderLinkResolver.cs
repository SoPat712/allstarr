using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Services.Common;

public sealed record CrossProviderLinkCandidate(
    string ProviderId, string ExternalId, bool Verified, double Confidence, string Method)
{
    public ProviderExternalResourceId Id => new(ProviderId, ProviderResourceKind.Track, ExternalId);
}

public interface ICrossProviderLinkResolver
{
    Task<IReadOnlyList<CrossProviderLinkCandidate>> ResolveAsync(ExternalTrackMatchSnapshot recording,
        IReadOnlyList<ProviderExternalResourceId> verifiedIdentities, ProviderActorContext actor,
        string correlationId, CancellationToken cancellationToken);
}

public sealed class CrossProviderLinkResolver(
    IProviderRegistry registry, IEffectiveProviderPolicyResolver policies, TrackMatchDecisionEngine scorer,
    IApplicationCache cache, IOptions<OdesliSettings> settings, IPlatformClock clock,
    OdesliService? odesli = null, ProviderAccountResolver? accounts = null,
    ApplicationCacheRequestCoalescer? coalescer = null) : ICrossProviderLinkResolver
{
    private readonly ApplicationCacheRequestCoalescer _coalescer = coalescer ?? new(new ApplicationCacheActivityMetrics());

    public async Task<IReadOnlyList<CrossProviderLinkCandidate>> ResolveAsync(ExternalTrackMatchSnapshot recording,
        IReadOnlyList<ProviderExternalResourceId> verifiedIdentities, ProviderActorContext actor,
        string correlationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Value.Enabled || actor.EffectiveUserId is not { } userId) return [];
        var policy = await policies.ResolveForUserAsync(userId, cancellationToken);
        var disabled = policy.DisabledProviders.Select(Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId).ToHashSet(StringComparer.Ordinal);
        var order = policy.GetProviderOrder(ProviderCapabilityKind.Metadata)
            .Select(Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId)
            .Where(id => !disabled.Contains(id)).Distinct(StringComparer.Ordinal).Take(32).ToArray();
        var known = verifiedIdentities.Select(item => Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(item.ProviderId))
            .Append(Core.Providers.AppleMusicKit.AppleMusicClient.NormalizeProviderId(recording.ProviderId)).ToHashSet(StringComparer.Ordinal);
        var results = new List<CrossProviderLinkCandidate>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        IReadOnlyDictionary<string, string>? optionalLinks = null;
        foreach (var providerId in order.Where(id => !known.Contains(id)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) break;
            var descriptor = registry.Providers.FirstOrDefault(item => item.Id == providerId)?.Capabilities
                .FirstOrDefault(item => item.Capability == ProviderCapabilityKind.Metadata);
            if (descriptor == null || !descriptor.HasUsableImplementation || descriptor.AccountRequirement == ProviderAccountRequirement.Required && accounts == null) continue;
            if (!registry.TryGetCapability<IProviderMetadataCapability>(providerId, ProviderCapabilityKind.Metadata, out var metadata) || metadata == null) continue;
            try
            {
                ProviderAccountContext? account = null;
                if (accounts != null && descriptor.AccountRequirement != ProviderAccountRequirement.None)
                {
                    var principal = actor.BackendPrincipal;
                    var resolved = await accounts.ResolveAsync(new(new(userId, principal?.BackendType ?? "allstarr",
                        principal?.BackendInstanceId ?? "cross-links", principal?.PrincipalId ?? userId.ToString("N"),
                        "", false), providerId, "metadata", AllowedScopes: descriptor.AllowedAccountScopes), timeout.Token);
                    if (resolved != null)
                    {
                        var saved = resolved.Account;
                        account = new(saved.Id, saved.ProviderId, saved.Scope, saved.Revision, saved.Enabled,
                            saved.OwnerUserId, resolved.Reason, saved.SecretReferenceId);
                    }
                }
                if (descriptor.AccountRequirement == ProviderAccountRequirement.Required && account == null) continue;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                deadline.CancelAfter(TimeSpan.FromSeconds(10));
                var context = new ProviderExecutionContext(actor, providerId, account,
                    new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                        ProviderExplicitContentPolicy.Allow, false, true, false, order),
                    "cross-provider-evidence", correlationId, clock.UtcNow.AddSeconds(10), deadline.Token);
                var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    recording.Title,
                    recording.Artist,
                    recording.Album,
                    recording.AlbumArtist,
                    recording.DurationMilliseconds,
                    recording.Isrc,
                    recording.IsExplicit,
                    userId,
                    account = account?.AccountId,
                    revision = account?.Revision,
                    version = TrackMatchDecisionEngine.AlgorithmVersion
                }))));
                var key = $"cross-links:v1:{providerId}:{digest}";
                var cached = await cache.GetAsync<List<CrossProviderLinkCandidate>>(key);
                if (cached != null) { results.AddRange(cached); continue; }
                var found = await _coalescer.RunAsync(key, async () =>
                {
                    var tracks = new List<ProviderTrackMetadata>();
                    var transient = false;
                    if (!string.IsNullOrWhiteSpace(recording.Isrc))
                    {
                        var exact = await metadata.LookupByIsrcAsync(context, new(recording.Isrc)).WaitAsync(deadline.Token);
                        transient |= IsTransient(exact.Error);
                        if (exact.IsSuccess) tracks.Add(exact.RequireValue());
                        else if (exact.Error?.Kind is ProviderErrorKind.NotSupported or ProviderErrorKind.CapabilityUnavailable)
                        {
                            var search = await metadata.SearchTracksAsync(context, new(recording.Isrc, new(10))).WaitAsync(deadline.Token);
                            transient |= IsTransient(search.Error);
                            if (search.IsSuccess) tracks.AddRange(search.RequireValue().Items);
                        }
                    }
                    var exactMatch = tracks.FirstOrDefault(track => track.Id.ProviderId == providerId &&
                        FuzzyMatcher.IdentifiersEqual(recording.Isrc, track.Isrc));
                    if (exactMatch != null)
                    {
                        var exactResult = new List<CrossProviderLinkCandidate> { new(providerId, exactMatch.Id.Value, true, 0.99, "isrc-lookup") };
                        await cache.SetAsync(key, exactResult, TimeSpan.FromDays(7));
                        return exactResult;
                    }
                    if (!string.IsNullOrWhiteSpace(recording.Title) && !string.IsNullOrWhiteSpace(recording.Artist))
                    {
                        var query = (recording.Title + " " + recording.Artist);
                        if (query.Length > 500) query = query[..500];
                        var search = await metadata.SearchTracksAsync(context, new(query, new(10))).WaitAsync(deadline.Token);
                        transient |= IsTransient(search.Error);
                        if (search.IsSuccess) tracks.AddRange(search.RequireValue().Items);
                    }
                    // Optional links are candidates too: their target metadata must pass the same match rules.
                    if (tracks.Count == 0 && odesli != null && !string.IsNullOrWhiteSpace(settings.Value.ApiKey))
                    {
                        var url = verifiedIdentities.Select(id => OdesliService.BuildTrackUrl(id.ProviderId, id.Value)).FirstOrDefault(url => url != null);
                        if (url != null)
                        {
                            try { optionalLinks ??= await odesli.GetTrackLinksAsync(url, deadline.Token); }
                            catch (OdesliLookupException) { transient = true; }
                            if (optionalLinks?.TryGetValue(OdesliService.NormalizeProviderId(providerId), out var id) == true)
                            {
                                var lookup = await metadata.GetTrackAsync(context, new(new(providerId, ProviderResourceKind.Track, id))).WaitAsync(deadline.Token);
                                transient |= IsTransient(lookup.Error);
                                if (lookup.IsSuccess) tracks.Add(lookup.RequireValue());
                            }
                        }
                    }
                    var found = Score(recording, tracks.Where(track => track.Id.ProviderId == providerId)
                        .DistinctBy(track => track.Id).Take(20).ToArray(), userId);
                    if (!transient) await cache.SetAsync(key, found, found.Count == 0 ? TimeSpan.FromMinutes(15) : TimeSpan.FromDays(1));
                    return found;
                }, deadline.Token);
                results.AddRange(found);
            }
            catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                // Optional metadata cannot prevent other configured sources from resolving links.
            }
        }
        return results;
    }

    private List<CrossProviderLinkCandidate> Score(ExternalTrackMatchSnapshot source, ProviderTrackMetadata[] tracks, Guid userId)
    {
        if (tracks.Length == 0) return [];
        if (string.IsNullOrWhiteSpace(source.Title) || string.IsNullOrWhiteSpace(source.Artist)) return [];
        var candidates = tracks.Select(track => new LocalTrackMatchCandidate(Guid.NewGuid(), null, "cross-links", null,
            track.Id.Value, null, track.Title, string.Join(", ", track.Artists.Select(artist => artist.Name)),
            track.AlbumTitle, track.AlbumArtist, (long?)track.Duration?.TotalMilliseconds, track.Isrc, null, track.IsExplicit,
            new Dictionary<string, string> { [track.Id.ProviderId] = track.Id.Value }, IsLocal: false)).ToArray();
        var decision = scorer.Decide(new(userId, "cross-links", null, 1, 1, new HashSet<string>()), source, candidates);
        return decision.Candidates.Select(score =>
        {
            var index = Array.FindIndex(candidates, candidate => candidate.LibraryTrackId == score.LibraryTrackId);
            var track = tracks[index];
            var verified = decision.State == TrackMatchReviewState.Accepted && decision.SelectedLibraryTrackId == score.LibraryTrackId;
            return new CrossProviderLinkCandidate(track.Id.ProviderId, track.Id.Value, verified, score.Confidence,
                verified && FuzzyMatcher.IdentifiersEqual(source.Isrc, track.Isrc) ? "isrc-lookup" : "cross-service-match");
        }).ToList();
    }

    private static bool IsTransient(ProviderError? error) => error?.Kind is ProviderErrorKind.RateLimited or
        ProviderErrorKind.TransientFailure or ProviderErrorKind.Canceled;
}
