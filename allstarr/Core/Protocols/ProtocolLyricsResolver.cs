using allstarr.Core.ClientApi;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Storage;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using allstarr.Services.Common;
using allstarr.Core.Settings;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Music;

namespace allstarr.Core.Protocols;

public sealed record ProtocolLyricsSources(
    Func<CancellationToken, Task<LyricsInfo?>>? Backend = null,
    Func<CancellationToken, Task<LyricsInfo?>>? Kept = null,
    bool HouseholdOnly = false)
{
    /// <summary>Reads only this source, still subject to the listener's availability policy.</summary>
    public string? OnlySourceId { get; init; }
    public bool ForPermanentSidecar { get; init; }
    public bool ReadOnly { get; init; }
}

public sealed record ProtocolLyricsCandidate(string SourceId, LyricsInfo? Lyrics)
{
    public string State { get; init; } = Lyrics == null ? "unavailable" : "ready";
    public bool IsDefaultVersion { get; init; } = true;
    internal LyricsScore? Assessment { get; init; }
    internal bool AutomaticEligible { get; init; } = true;
}

public interface IProtocolLyricsResolver
{
    Task<RichLyrics?> GetRichLyricsAsync(ProtocolExecutionContext context, Song song, string resourceKey,
        ProtocolLyricsSources? sources = null);

    Task<IReadOnlyList<ProtocolLyricsCandidate>> FindAllAsync(ProtocolExecutionContext protocol, Song song,
        string resourceKey, ProtocolLyricsSources? sources = null);

    Task<LyricsInfo?> FindAsync(ProtocolExecutionContext protocol, Song song, string resourceKey,
        ProtocolLyricsSources sources);

    Task<LyricsInfo?> FindAsync(
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? sourceProvider = null,
        string? sourceExternalId = null,
        string? spotifyTrackId = null);
}

public sealed partial class ProtocolLyricsResolver(
    IProtocolProviderGateway providers,
    ILogger<ProtocolLyricsResolver> logger,
    ICrossProviderLinkResolver? crossLinks = null,
    IEffectiveProviderPolicyResolver? effectivePolicies = null,
    IKeptLyricsReader? keptLyrics = null,
    ILibraryIndexService? libraryIndex = null,
    IRecordingLyricsChoices? choices = null,
    ITrackIdentityService? identities = null,
    TimeProvider? timeProvider = null, IHostApplicationLifetime? lifetime = null,
    allstarr.Core.ClientEvents.ClientLyricsUpdates? clientUpdates = null,
    LyricsSearchIndex? searchIndex = null, LyricsSearchChanges? searchChanges = null,
    LyricsLookupOptions? lookupOptions = null) : IProtocolLyricsResolver, IHostedService
{
    public async Task<RichLyrics?> GetRichLyricsAsync(ProtocolExecutionContext context, Song song, string resourceKey,
        ProtocolLyricsSources? sources = null) => RichLyricsMapper.Map(await FindAsync(
            context, MusicLyrics.ForLookup(song), resourceKey, sources ?? new ProtocolLyricsSources()));

    public async Task<IReadOnlyList<ProtocolLyricsCandidate>> FindAllAsync(ProtocolExecutionContext protocol, Song song,
        string resourceKey, ProtocolLyricsSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        var responseDeadline = LyricsClock.GetUtcNow().Add(LookupOptions.ResponseBudget);
        if (sources?.ReadOnly == true) protocol = protocol with { IsReadOnly = true };
        var preference = await ResolveChoiceAsync(protocol, song, sources?.HouseholdOnly == true, song.ExternalProvider, song.ExternalId);
        var results = new List<ProtocolLyricsCandidate>();
        await foreach (var candidate in ReadCandidatesAsync(protocol, song, preference.RecordingId?.ToString("N") ?? song.CanonicalRecordingId?.ToString("N") ?? resourceKey,
            song.ExternalProvider, song.ExternalId, song.SpotifyId, sources, preference.SourceId, preference.RecordingId,
            responseDeadline: responseDeadline, recordingDurationMs: preference.DurationMs))
        {
            results.Add(candidate);
            if (results.Count == 20) break;
        }
        return Assess(results, song, preference.DurationMs);
    }

    internal static IReadOnlyList<ProtocolLyricsCandidate> Assess(IEnumerable<ProtocolLyricsCandidate> candidates, Song song,
        long? recordingDurationMs = null) => candidates.Select(candidate => candidate with
        { Assessment = candidate.AutomaticEligible ? candidate.Assessment ?? LyricsQuality.Evaluate(candidate.Lyrics, song, recordingDurationMs) : null }).ToArray();

    public Task<LyricsInfo?> FindAsync(ProtocolExecutionContext protocol, Song song, string resourceKey,
        ProtocolLyricsSources sources) => FindCoreAsync(protocol, song, resourceKey,
        song.ExternalProvider, song.ExternalId, song.SpotifyId, sources);

    public Task<LyricsInfo?> FindAsync(
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? sourceProvider = null,
        string? sourceExternalId = null,
        string? spotifyTrackId = null) => FindCoreAsync(protocol, song, resourceKey,
        sourceProvider, sourceExternalId, spotifyTrackId, null);

    private async Task<LyricsInfo?> FindCoreAsync(
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId,
        ProtocolLyricsSources? sources)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        protocol.CancellationToken.ThrowIfCancellationRequested();
        var responseDeadline = LyricsClock.GetUtcNow().Add(LookupOptions.ResponseBudget);
        var preference = await ResolveChoiceAsync(protocol, song, sources?.HouseholdOnly == true,
            sourceProvider, sourceExternalId);
        var lookupKey = preference.RecordingId?.ToString("N") ?? resourceKey;

        await foreach (var candidate in ReadCandidatesAsync(protocol, song, lookupKey,
            sourceProvider, sourceExternalId, spotifyTrackId, sources, preference.SourceId, preference.RecordingId,
            automaticLookup: true, responseDeadline: responseDeadline, recordingDurationMs: preference.DurationMs))
        {
            var lyrics = candidate.Lyrics;
            if (lyrics == null) continue;
            if (sources?.HouseholdOnly != true && providers is not IProtocolLyricsExecutionGateway)
                await IndexAsync(protocol, song, lyrics, preference.RecordingId);
            Observe(lyrics);
            return lyrics;
        }
        Observe(null);
        return null;

        void Observe(LyricsInfo? value)
        {
            if (providers is not IProtocolLyricsExecutionGateway && protocol.Actor?.EffectiveUserId is { } owner && !protocol.IsPreparation &&
                sources?.HouseholdOnly != true && sources?.ForPermanentSidecar != true)
                clientUpdates?.Observe(owner, string.IsNullOrWhiteSpace(protocol.RequestedItemId) ? song.Id : protocol.RequestedItemId, value);
        }
    }

    private async Task IndexAsync(ProtocolExecutionContext context, Song song, LyricsInfo? info, Guid? recordingId = null)
    {
        if (searchIndex == null || context.Protocol != ProtocolKind.Jellyfin || RichLyricsMapper.Map(info) is not { } rich) return;
        try
        {
            recordingId ??= (await ResolveChoiceAsync(context, song, false, song.ExternalProvider, song.ExternalId)).RecordingId;
            await searchIndex.StoreAsync(context, song, recordingId, rich, expectedProvenance: info?.CacheProvenance);
            if (context.Principal is { } principal && LyricsSearchIndex.ItemKey(song) is { } itemId)
                searchChanges?.SongChanged(principal.UserId, itemId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Lyrics search indexing was deferred ({ErrorType})", exception.GetType().Name);
        }
    }

    private async Task<(Guid? RecordingId, string? SourceId, long? DurationMs)> ResolveChoiceAsync(ProtocolExecutionContext protocol,
        Song song, bool householdOnly, string? sourceProvider, string? sourceExternalId)
    {
        if (protocol.Actor == null) return (song.IsLocal ? null : song.CanonicalRecordingId, null, null);
        try
        {
            Guid? recordingId;
            CanonicalRecordingIdentity? recording = null;
            if (song.IsLocal)
                recordingId = libraryIndex == null || string.IsNullOrWhiteSpace(song.Id) ? null :
                    (await libraryIndex.FindByBackendItemAsync(protocol, song.Id, protocol.CancellationToken))?.CanonicalRecordingId;
            else
            {
                recordingId = song.CanonicalRecordingId;
                if (recordingId == null && identities != null)
                {
                    var publicId = !string.IsNullOrWhiteSpace(song.Id) ? song.Id :
                        !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId)
                            ? $"ext-{sourceProvider}-song-{sourceExternalId}" : null;
                    if (publicId != null)
                    {
                        recording = await identities.ResolveRecordingAsync(protocol.Actor, publicId, protocol.CancellationToken);
                        recordingId = recording?.Id;
                    }
                }
            }
            if (recordingId == null || recordingId == Guid.Empty) return (null, null, null);
            var layers = choices == null ? null : await choices.GetAsync(protocol, recordingId.Value);
            if (recording == null && identities != null)
            {
                try { recording = await identities.ResolveRecordingAsync(protocol.Actor, recordingId.Value.ToString("D"), protocol.CancellationToken); }
                catch (Exception exception) when (exception is not OperationCanceledException)
                { logger.LogWarning("Recording lyrics duration is unavailable; using the item duration"); }
            }
            return (recordingId, (householdOnly ? layers?.Household : layers?.Effective)?.SourceId, recording?.DurationMilliseconds);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Recording lyrics preferences are unavailable; using the configured source order");
            return (null, null, null);
        }
    }

    private async IAsyncEnumerable<ProtocolLyricsCandidate> ReadCandidatesAsync(
        ProtocolExecutionContext protocol,
        Song song,
        string resourceKey,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId,
        ProtocolLyricsSources? sources,
        string? preferredSourceId = null,
        Guid? canonicalRecordingId = null,
        bool automaticLookup = false,
        DateTimeOffset? responseDeadline = null, long? recordingDurationMs = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        protocol.CancellationToken.ThrowIfCancellationRequested();
        var callerToken = protocol.CancellationToken;
        var artists = song.Artists.Count > 0 ? song.Artists : [song.Artist];
        var householdOnly = sources?.HouseholdOnly == true;
        var executionSnapshot = new LyricsExecutionSnapshot();

        Task<Guid?> ResolveAliasRecordingAsync(CancellationToken token) =>
            executionSnapshot.GetOrCreateAsync("recording", ResolveAliasRecordingCoreAsync, token);

        async Task<Guid?> ResolveAliasRecordingCoreAsync(CancellationToken token)
        {
            var aliasRecordingId = canonicalRecordingId;
            if (aliasRecordingId.HasValue) return aliasRecordingId == Guid.Empty ? null : aliasRecordingId;
            if (song.IsLocal)
            {
                if (libraryIndex != null && !string.IsNullOrWhiteSpace(song.Id))
                    aliasRecordingId = (await libraryIndex.FindByBackendItemAsync(protocol, song.Id,
                        token))?.CanonicalRecordingId;
            }
            else
            {
                aliasRecordingId = song.CanonicalRecordingId;
                if (aliasRecordingId == null && identities != null && protocol.Actor != null)
                {
                    var publicId = !string.IsNullOrWhiteSpace(song.Id) ? song.Id :
                        !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId)
                            ? $"ext-{sourceProvider}-song-{sourceExternalId}" : null;
                    if (publicId != null)
                        aliasRecordingId = (await identities.ResolveRecordingAsync(protocol.Actor, publicId,
                            token))?.Id;
                }
            }
            return aliasRecordingId == Guid.Empty ? null : aliasRecordingId;
        }

        var localSources = new Dictionary<string, Func<CancellationToken, Task<LyricsInfo?>>>(StringComparer.Ordinal);
        if (!householdOnly && sources?.Backend != null) localSources.Add("backend", sources.Backend);
        if (!householdOnly && sources?.Kept != null) localSources.Add("kept", sources.Kept);
        else if (keptLyrics != null)
            localSources.Add("kept", async token =>
            {
                var identities = new List<KeptLyricsIdentity>();
                if (song.IsLocal)
                {
                    if (libraryIndex == null || string.IsNullOrWhiteSpace(song.Id)) return null;
                    var native = await libraryIndex.FindByBackendItemAsync(protocol, song.Id, token);
                    if (native == null) return null;
                    identities.AddRange(native.ProviderTrackIds.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new KeptLyricsIdentity(pair.Key, pair.Value)));
                }
                else
                {
                    var provider = sourceProvider ?? song.ExternalProvider;
                    var externalId = sourceExternalId ?? song.ExternalId;
                    if (!string.IsNullOrWhiteSpace(provider) && !string.IsNullOrWhiteSpace(externalId))
                        identities.Add(new(provider, externalId));
                    else
                    {
                        var parsed = allstarr.Services.Local.LocalLibraryService.ParseExternalResource(song.Id);
                        if (parsed.isExternal && parsed.type == "song" && parsed.provider != "allstarr")
                            identities.Add(new(parsed.provider!, parsed.externalId!));
                    }
                }
                return identities.Count == 0 ? null : householdOnly
                    ? await keptLyrics.ReadHouseholdAsync(protocol, song, identities)
                    : await keptLyrics.ReadAsync(protocol, song, identities);
            });
        var preferredProvider = preferredSourceId == null ? null : LyricsVersions.ProviderId(preferredSourceId);
        var configuredOrder = providers.GetProviderOrder(ProviderCapabilityKind.Lyrics);
        IReadOnlyList<string> order = new[] { "backend", "kept" }
            .Where(id => localSources.ContainsKey(id) && !configuredOrder.Contains(id, StringComparer.Ordinal))
            .Concat(configuredOrder).Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string> enabledOrder = order;
        IReadOnlyList<string> automaticOrder = order;
        var cleanOnly = protocol.Policy?.Preferences.ExplicitFilter == "CleanOnly";
        if (effectivePolicies != null)
        {
            var effectivePolicy = householdOnly || protocol.Actor?.EffectiveUserId == null
                ? await effectivePolicies.ResolveAsync(protocol.CancellationToken)
                : await effectivePolicies.ResolveForUserAsync(protocol.Actor!.EffectiveUserId!.Value, protocol.CancellationToken);
            cleanOnly = effectivePolicy.Preferences.ExplicitFilter == "CleanOnly";
            var enabled = effectivePolicy.ApplyProviderAvailability(ProviderCapabilityKind.Lyrics, order);
            enabledOrder = enabled;
            automaticOrder = effectivePolicy.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics, order);
            order = automaticLookup && sources?.OnlySourceId == null ? automaticOrder : enabled;
            if (automaticLookup && preferredSourceId != null && enabled.Contains(preferredProvider!, StringComparer.Ordinal))
                order = new[] { preferredProvider! }.Concat(order).Distinct(StringComparer.Ordinal).ToArray();
        }
        if (cleanOnly)
        {
            if (!CleanRecordingPolicy.IsVerifiedClean(song)) yield break;
            order = order.Where(id => id is "backend" or "kept").ToArray();
            enabledOrder = order;
        }
        IReadOnlyList<CrossProviderLinkCandidate>? resolvedLinks = null;

        if (automaticLookup && preferredSourceId != null && order.Contains(preferredProvider!, StringComparer.Ordinal))
            order = new[] { preferredProvider! }.Concat(order).Distinct(StringComparer.Ordinal).ToArray();
        if (sources?.OnlySourceId is { } onlySource)
            order = order.Where(item => string.Equals(item, LyricsVersions.ProviderId(onlySource), StringComparison.Ordinal)).ToArray();

        if (sources?.ForPermanentSidecar == true)
        {
            order = order.Where(id => id != "spicylyrics").ToArray();
            enabledOrder = enabledOrder.Where(id => id != "spicylyrics").ToArray();
        }

        var localResults = new Dictionary<string, LyricsInfo?>(StringComparer.Ordinal);
        ProtocolLyricsCandidate? bestLocal = null;
        var bestLocalRank = int.MaxValue;
        if (sources?.OnlySourceId == null && sources?.ForPermanentSidecar != true &&
            providers is IProtocolLyricsExecutionGateway executionGateway && protocol.Actor != null)
        {
            var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(new
            {
                protocol.BackendInstanceId,
                protocol.VerifiedBackendPrincipalId,
                Owner = protocol.Actor.EffectiveUserId,
                resourceKey,
                song.Title,
                song.Artists,
                song.Artist,
                song.Album,
                song.Duration,
                song.PreciseDuration,
                recordingDurationMs,
                song.Isrc,
                sourceProvider,
                sourceExternalId,
                spotifyTrackId,
                order,
                preferredSourceId,
                householdOnly,
                protocol.IsReadOnly,
                protocol.IsPreparation,
                protocol.RequestedItemId,
                Permanent = sources?.ForPermanentSidecar == true,
                Local = localSources.Keys.ToArray()
            }))));
            var lookup = EnterAutomatic(key, protocol, song, householdOnly, preferredSourceId, recordingDurationMs, (owned, deadline) =>
            {
                protocol = owned;
                return order.Where(id => localSources.ContainsKey(id) || id is not ("backend" or "kept"))
                    .Select(id => localSources.TryGetValue(id, out var local)
                    ? new AutomaticSource(id, null, local, automaticOrder.Contains(id, StringComparer.Ordinal))
                    : new AutomaticSource(id, async token =>
                    {
                        var spec = await PrepareSpecAsync(id, deadline, false, token);
                        var read = spec == null ? null : await executionGateway.PrepareAsync(spec, token);
                        if (read != null) return read;
                        spec = await PrepareSpecAsync(id, deadline, true, token);
                        return spec == null ? null : await executionGateway.PrepareAsync(spec, token);
                    }, null, automaticOrder.Contains(id, StringComparer.Ordinal))).ToArray();
            });
            if (lookup != null)
                foreach (var candidate in await WaitAutomaticAsync(lookup, !automaticLookup, callerToken,
                    responseDeadline ?? LyricsClock.GetUtcNow().Add(LookupOptions.ResponseBudget)))
                    yield return candidate with { AutomaticEligible = automaticOrder.Contains(LyricsVersions.ProviderId(candidate.SourceId), StringComparer.Ordinal) };
            yield break;
        }
        if (automaticLookup && sources?.OnlySourceId == null)
        {
            if (preferredSourceId != null && enabledOrder.Contains(preferredProvider!, StringComparer.Ordinal))
            {
                var pinned = await ReadOneAsync(preferredProvider!);
                if (pinned != null)
                {
                    var versions = LyricsVersions.Candidates(preferredProvider!, pinned);
                    var selected = versions.FirstOrDefault(version => version.Source == preferredSourceId) ??
                        (RichLyricsMapper.Map(pinned) == null ? versions.FirstOrDefault() : pinned);
                    if (selected != null)
                    {
                        yield return new(selected.Source ?? preferredProvider!, selected);
                        yield break;
                    }
                }
                order = order.Where(id => id != preferredProvider).ToArray();
            }
            for (var rank = 0; rank < order.Count; rank++)
            {
                var id = order[rank];
                if (!localSources.ContainsKey(id)) continue;
                var answer = await ReadOneAsync(id);
                localResults[id] = answer;
                var candidate = BestVersion(id, answer, song, recordingDurationMs);
                if (candidate?.Assessment == null || candidate.Assessment.Score <= (bestLocal?.Assessment?.Score ?? -1)) continue;
                bestLocal = candidate;
                bestLocalRank = rank;
            }
        }

        async Task<string?> ResolveSpicySelectorAsync(bool allowDiscovery, CancellationToken token)
        {
            var known = ResolveExternalId("spicylyrics", resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId);
            if (known != null || !allowDiscovery || protocol.Actor == null) return known;
            var recording = await ResolveAliasRecordingAsync(token);
            if (recording.HasValue && identities != null)
            {
                var stored = await identities.GetStoredSongsAsync(protocol.Actor, recording.Value.ToString("D"), token);
                token.ThrowIfCancellationRequested();
                var selectors = stored.Where(item => item.ExternalProvider == "spotify")
                    .Select(item => SpotifyTrackReference.TryNormalize(item.ExternalId, out var id) ? id : null)
                    .Where(id => id != null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
                if (selectors.Length == 1) return selectors[0];
                if (selectors.Length > 1) return null;
            }
            var linked = (resolvedLinks ?? []).Where(item => item.Verified && OdesliService.NormalizeProviderId(item.ProviderId) == "spotify")
                .Select(item => SpotifyTrackReference.TryNormalize(item.ExternalId, out var id) ? id : null)
                .Where(id => id != null).Distinct(StringComparer.Ordinal).Take(2).ToArray();
            return linked.Length == 1 ? linked[0] : null;
        }

        async Task<LyricsReadSpec?> PrepareSpecAsync(string providerId, DateTimeOffset deadline, bool allowDiscovery, CancellationToken token)
        {
            var externalId = providerId == "spicylyrics" ? await ResolveSpicySelectorAsync(allowDiscovery, token) :
                ResolveExternalId(providerId, resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId);
            if (providerId == "spicylyrics" && externalId == null) return null;
            if (allowDiscovery && externalId == null && crossLinks != null && protocol.Actor != null &&
                !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId))
            {
                resolvedLinks = await executionSnapshot.GetOrCreateAsync("links", sharedToken => crossLinks.ResolveAsync(
                    new(resourceKey, sourceProvider, sourceExternalId, song.Title, string.Join(", ", artists), song.Album,
                        null, (long?)song.Duration * 1000, song.Isrc, null, null),
                    [], protocol.Actor, protocol.CorrelationId, sharedToken), token);
                externalId = resolvedLinks.FirstOrDefault(item => item.Verified &&
                    OdesliService.NormalizeProviderId(item.ProviderId) == OdesliService.NormalizeProviderId(providerId))?.ExternalId;
            }
            var useRecordingSelector = externalId == null;
            var recording = externalId == null ? await ResolveAliasRecordingAsync(token) : canonicalRecordingId;
            if (externalId == null && !recording.HasValue) return null;
            externalId ??= recording!.Value.ToString("D");
            var target = new ProviderExternalResourceId(providerId, ProviderResourceKind.Track, externalId);
            var source = useRecordingSelector && recording.HasValue
                ? new ProviderExternalResourceId("allstarr", ProviderResourceKind.Track, recording.Value.ToString("D")) : target;
            var canonical = recording ?? new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{providerId}\n{externalId}")).AsSpan(0, 16));
            return new(protocol.Actor!, providerId, source, new(canonical, target, preferredFormat: ProviderLyricsFormat.LineTimed,
                trackTitle: string.IsNullOrWhiteSpace(song.Title) ? null : song.Title.Trim(), artistNames: artists.Where(artist => !string.IsNullOrWhiteSpace(artist)).Select(artist => artist.Trim()).ToArray(), albumTitle: string.IsNullOrWhiteSpace(song.Album) ? null : song.Album.Trim(), durationSeconds: song.Duration, isrc: song.Isrc),
                householdOnly ? [ProviderAccountScope.Shared] : [ProviderAccountScope.Personal, ProviderAccountScope.Shared], protocol.CorrelationId, deadline, householdOnly)
            { ManualSelection = !automaticLookup || providerId == preferredProvider, Snapshot = executionSnapshot };
        }

        var best = bestLocal;
        var bestRank = bestLocalRank;
        for (var rank = 0; rank < order.Count; rank++)
        {
            var providerId = order[rank];
            protocol.CancellationToken.ThrowIfCancellationRequested();
            if (automaticLookup && !LyricsQuality.CanImprove(providerId, rank, best?.Assessment?.Score ?? -1, bestRank, song, recordingDurationMs)) continue;
            var lyrics = localResults.TryGetValue(providerId, out var local) ? local : await ReadOneAsync(providerId);
            if (!automaticLookup)
            {
                if (lyrics == null) { yield return new(providerId, null) { AutomaticEligible = automaticOrder.Contains(providerId, StringComparer.Ordinal) }; continue; }
                foreach (var version in LyricsVersions.Candidates(providerId, lyrics))
                {
                    var id = lyrics.Versions == null ? providerId : LyricsVersions.SourceId(providerId, version.Metadata);
                    if (sources?.OnlySourceId is { } selected && selected != providerId && selected != id) continue;
                    yield return new(id, version)
                    {
                        AutomaticEligible = automaticOrder.Contains(providerId, StringComparer.Ordinal),
                        IsDefaultVersion = RichLyricsMapper.Map(lyrics) != null &&
                        (lyrics.Versions == null || id == LyricsVersions.SourceId(providerId, lyrics.Metadata))
                    };
                }
                continue;
            }
            var candidate = BestVersion(providerId, lyrics, song, recordingDurationMs);
            var score = candidate?.Assessment?.Score ?? -1;
            if (score > (best?.Assessment?.Score ?? -1) || score >= 0 && score == best?.Assessment?.Score && rank < bestRank)
            {
                best = candidate;
                bestRank = rank;
            }
        }
        if (automaticLookup && best != null)
        {
            if (best.Lyrics != null) best.Lyrics.Source = best.SourceId;
            yield return best;
        }

        async Task<LyricsInfo?> ReadOneAsync(string providerId)
        {
            if (sources?.ForPermanentSidecar == true && providerId == "spicylyrics") return null;
            try
            {
                LyricsInfo? lyrics;
                if (localSources.TryGetValue(providerId, out var readLocal))
                {
                    lyrics = await readLocal(protocol.CancellationToken);
                    if (lyrics != null) lyrics.Source = providerId;
                    if (sources?.ReadOnly != true && !householdOnly) await IndexAsync(protocol, song, lyrics, canonicalRecordingId);
                }
                else
                {
                    if (providerId is "backend" or "kept") return null;
                    var externalId = providerId == "spicylyrics" ? await ResolveSpicySelectorAsync(true, protocol.CancellationToken) : ResolveExternalId(
                        providerId, resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId);
                    if (providerId == "spicylyrics" && externalId == null) return null;
                    if (externalId == null && crossLinks != null && protocol.Actor != null &&
                        !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId))
                    {
                        resolvedLinks ??= await crossLinks.ResolveAsync(new(resourceKey, sourceProvider, sourceExternalId,
                                song.Title, string.Join(", ", artists), song.Album, null, (long?)song.Duration * 1000,
                                song.Isrc, null, null), [], protocol.Actor, protocol.CorrelationId, protocol.CancellationToken);
                        externalId = resolvedLinks.FirstOrDefault(item => item.Verified &&
                            OdesliService.NormalizeProviderId(item.ProviderId) == OdesliService.NormalizeProviderId(providerId))?.ExternalId;
                    }
                    ProviderLyricsResult? result;
                    if (externalId == null)
                    {
                        var recordingId = await ResolveAliasRecordingAsync(protocol.CancellationToken);
                        if (!recordingId.HasValue) return null;
                        result = await providers.GetRecordingLyricsAsync(protocol, providerId, recordingId.Value,
                            ProviderLyricsFormat.LineTimed, song.Title, artists, song.Album, song.Duration, householdOnly, song.Isrc);
                    }
                    else result = householdOnly ? await providers.GetHouseholdLyricsAsync(
                        protocol, providerId, externalId, ProviderLyricsFormat.LineTimed,
                        song.Title, artists, song.Album, song.Duration, song.Isrc) : await providers.GetLyricsAsync(
                        protocol,
                        providerId,
                        externalId,
                        ProviderLyricsFormat.LineTimed,
                        song.Title,
                        artists,
                        song.Album,
                        song.Duration,
                        song.Isrc);
                    protocol.CancellationToken.ThrowIfCancellationRequested();
                    if (result == null || string.IsNullOrWhiteSpace(result.Content) && result.Versions?.Any(version => !string.IsNullOrWhiteSpace(version.Content)) != true)
                    {
                        if (sources?.ReadOnly != true && !householdOnly && searchIndex != null)
                            await searchIndex.ReconcileVersionsAsync(protocol, song, providerId, new HashSet<string>(), unavailable: true);
                        return null;
                    }
                    lyrics = LyricsVersions.Map(result, providerId, song.Duration);
                    lyrics.TrackName = song.Title;
                    lyrics.ArtistName = string.Join(", ", artists);
                    lyrics.AlbumName = song.Album;
                    lyrics.Duration = song.Duration ?? 0;
                    if (sources?.ReadOnly != true && !householdOnly)
                    {
                        await IndexAsync(protocol, song, lyrics, canonicalRecordingId);
                        if (lyrics.Versions != null)
                        {
                            foreach (var version in LyricsVersions.Candidates(providerId, lyrics))
                                await IndexAsync(protocol, song, version, canonicalRecordingId);
                            if (searchIndex != null)
                                await searchIndex.ReconcileVersionsAsync(protocol, song, providerId,
                                    LyricsVersions.Candidates(providerId, lyrics).Select(version => version.Source!).ToHashSet(StringComparer.Ordinal));
                        }
                        else if (searchIndex != null)
                            await searchIndex.ReconcileVersionsAsync(protocol, song, providerId, new HashSet<string>());
                    }
                }
                protocol.CancellationToken.ThrowIfCancellationRequested();
                if (lyrics == null || string.IsNullOrWhiteSpace(lyrics.SyncedLyrics) && string.IsNullOrWhiteSpace(lyrics.PlainLyrics) && lyrics.Versions?.Count is not > 0)
                    return null;
                return lyrics;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning("Lyrics source {Provider} failed; trying the next configured source", providerId);
            }
            return null;
        }
    }

    private static string? ResolveExternalId(
        string providerId,
        string resourceKey,
        string? sourceProvider,
        string? sourceExternalId,
        string? spotifyTrackId)
    {
        providerId = providerId.Trim().ToLowerInvariant();
        if (providerId == "spicylyrics")
        {
            if (SpotifyTrackReference.TryNormalize(spotifyTrackId, out var known)) return known;
            return (string.Equals(sourceProvider, "spotify", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(sourceProvider, "spicylyrics", StringComparison.OrdinalIgnoreCase)) && SpotifyTrackReference.TryNormalize(sourceExternalId, out var selected)
                ? selected : null;
        }
        if (providerId == "amll")
        {
            var prefix = sourceProvider switch
            {
                "spotify" or "spicylyrics" => "spotify", "applemusic" or "apple-musickit" or "apple-download" => "apple",
                "netease" or "neteaselyrics" => "netease", "qq" or "qqlyrics" => "qq", _ => null
            };
            if (prefix == "spotify" && SpotifyTrackReference.TryNormalize(sourceExternalId, out var sourceSpotify)) return "spotify:" + sourceSpotify;
            if (prefix is "apple" or "netease" or "qq" && !string.IsNullOrWhiteSpace(sourceExternalId)) return prefix + ":" + sourceExternalId;
            if (SpotifyTrackReference.TryNormalize(spotifyTrackId, out var knownSpotify)) return "spotify:" + knownSpotify;
        }
        if (providerId.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sourceExternalId))
            return sourceExternalId;
        if (providerId == "spotify") return string.IsNullOrWhiteSpace(spotifyTrackId) ? null : spotifyTrackId;
        if (providerId is "lrclib" or "lyricsplus" or "musixmatch" or "lrcred" or "amll" or "neteaselyrics" or "qqlyrics" or "kugoulyrics" or "lrcmux" or "unison" or "paxsenix" or "genius")
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(resourceKey))).ToLowerInvariant();
        if (providerId.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase)) return sourceExternalId;
        var sourceIsApple = sourceProvider is not null &&
                            (sourceProvider.Equals("applemusic", StringComparison.OrdinalIgnoreCase) ||
                             sourceProvider.Equals("apple-download", StringComparison.OrdinalIgnoreCase) ||
                             sourceProvider.Equals("spotiflac-apple-music", StringComparison.OrdinalIgnoreCase));
        return sourceIsApple && providerId is "apple-download" or "spotiflac-apple-music"
            ? sourceExternalId
            : null;
    }
}
