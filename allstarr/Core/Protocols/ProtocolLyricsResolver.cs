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
}

public sealed record ProtocolLyricsCandidate(string SourceId, LyricsInfo? Lyrics);

public interface IProtocolLyricsResolver
{
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
    TimeProvider? timeProvider = null, IHostApplicationLifetime? lifetime = null) : IProtocolLyricsResolver, IHostedService
{
    public async Task<IReadOnlyList<ProtocolLyricsCandidate>> FindAllAsync(ProtocolExecutionContext protocol, Song song,
        string resourceKey, ProtocolLyricsSources? sources = null)
    {
        ArgumentNullException.ThrowIfNull(song);
        var results = new List<ProtocolLyricsCandidate>();
        await foreach (var candidate in ReadCandidatesAsync(protocol, song, song.CanonicalRecordingId?.ToString("N") ?? resourceKey,
            song.ExternalProvider, song.ExternalId, song.SpotifyId, sources))
            results.Add(candidate);
        return results;
    }

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
        var preference = await ResolveChoiceAsync(protocol, song, sources?.HouseholdOnly == true,
            sourceProvider, sourceExternalId);
        var lookupKey = preference.RecordingId?.ToString("N") ?? resourceKey;

        await foreach (var candidate in ReadCandidatesAsync(protocol, song, lookupKey,
            sourceProvider, sourceExternalId, spotifyTrackId, sources, preference.SourceId, preference.RecordingId, skipPaxsenixAfterPlainLyrics: true, automaticLookup: true))
        {
            var lyrics = candidate.Lyrics;
            if (lyrics == null) continue;
            return lyrics;
        }
        return null;
    }

    private async Task<(Guid? RecordingId, string? SourceId)> ResolveChoiceAsync(ProtocolExecutionContext protocol,
        Song song, bool householdOnly, string? sourceProvider, string? sourceExternalId)
    {
        if (choices == null || protocol.Actor == null) return (song.IsLocal ? null : song.CanonicalRecordingId, null);
        try
        {
            Guid? recordingId;
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
                        recordingId = (await identities.ResolveRecordingAsync(protocol.Actor, publicId, protocol.CancellationToken))?.Id;
                }
            }
            if (recordingId == null || recordingId == Guid.Empty) return (null, null);
            var layers = await choices.GetAsync(protocol, recordingId.Value);
            return (recordingId, (householdOnly ? layers.Household : layers.Effective)?.SourceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning("Recording lyrics preferences are unavailable; using the configured source order");
            return (null, null);
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
        bool skipPaxsenixAfterPlainLyrics = false,
        bool automaticLookup = false)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        protocol.CancellationToken.ThrowIfCancellationRequested();
        var artists = song.Artists.Count > 0 ? song.Artists : [song.Artist];
        var householdOnly = sources?.HouseholdOnly == true;
        var aliasRecordingId = canonicalRecordingId;
        var aliasRecordingResolved = aliasRecordingId.HasValue;

        async Task<Guid?> ResolveAliasRecordingAsync(CancellationToken token)
        {
            if (aliasRecordingResolved) return aliasRecordingId == Guid.Empty ? null : aliasRecordingId;
            aliasRecordingResolved = true;
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
        IReadOnlyList<string> order = providers.GetProviderOrder(ProviderCapabilityKind.Lyrics).Concat(localSources.Keys)
            .Distinct(StringComparer.Ordinal).ToArray();
        IReadOnlyList<string> enabledOrder = order;
        if (effectivePolicies != null)
        {
            var effectivePolicy = householdOnly || protocol.Actor?.EffectiveUserId == null
                ? await effectivePolicies.ResolveAsync(protocol.CancellationToken)
                : await effectivePolicies.ResolveForUserAsync(protocol.Actor!.EffectiveUserId!.Value, protocol.CancellationToken);
            var enabled = effectivePolicy.ApplyProviderAvailability(ProviderCapabilityKind.Lyrics, order);
            enabledOrder = enabled;
            order = automaticLookup && sources?.OnlySourceId == null
                ? effectivePolicy.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics, order)
                : enabled;
            if (preferredSourceId != null && enabled.Contains(preferredSourceId, StringComparer.Ordinal))
                order = new[] { preferredSourceId }.Concat(order).Distinct(StringComparer.Ordinal).ToArray();
        }
        IReadOnlyList<CrossProviderLinkCandidate>? resolvedLinks = null;

        if (preferredSourceId != null && order.Contains(preferredSourceId, StringComparer.Ordinal))
            order = new[] { preferredSourceId }.Concat(order).Distinct(StringComparer.Ordinal).ToArray();
        if (sources?.OnlySourceId is { } onlySource)
            order = order.Where(item => string.Equals(item, onlySource, StringComparison.Ordinal)).ToArray();

        if (sources?.ForPermanentSidecar == true)
        {
            order = order.Where(id => id != "spicylyrics").ToArray();
            enabledOrder = enabledOrder.Where(id => id != "spicylyrics").ToArray();
        }

        if (automaticLookup && sources?.OnlySourceId == null)
        {
            var preflight = new[] { preferredSourceId, "kept" }.Where(id => id != null).Distinct(StringComparer.Ordinal);
            foreach (var id in preflight)
            {
                if (!enabledOrder.Contains(id!, StringComparer.Ordinal) || id == "kept" && id != preferredSourceId &&
                    !order.Contains(id, StringComparer.Ordinal)) continue;
                var answer = await ReadOneAsync(id!);
                order = order.Where(item => item != id).ToArray();
                if (answer != null) { yield return new(id!, answer); yield break; }
            }
            if (providers is IProtocolLyricsExecutionGateway executionGateway && protocol.Actor != null)
            {
                var lookup = EnterAutomatic(protocol);
                if (lookup == null)
                {
                    foreach (var id in order.Where(localSources.ContainsKey))
                    {
                        var answer = await ReadOneAsync(id);
                        if (answer != null) { yield return new(id, answer); yield break; }
                    }
                    yield break;
                }
                try
                {
                    var prepared = new List<AutomaticSource>();
                    foreach (var id in order)
                    {
                        if (localSources.ContainsKey(id)) prepared.Add(new(id, null, false, false, () => ReadOneAsync(id)));
                        else if (id is not ("backend" or "kept"))
                        {
                            var needsDiscovery = ResolveExternalId(id, resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId) == null &&
                                (id == "spicylyrics" || !canonicalRecordingId.HasValue && !song.CanonicalRecordingId.HasValue);
                            var canDiscover = (id == "spicylyrics" ? identities != null :
                                crossLinks != null && !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId)) &&
                                ResolveExternalId(id, resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId) == null;
                            prepared.Add(new(id, async (discovery, token) =>
                            {
                                var spec = await PrepareSpecAsync(id, lookup.Deadline, discovery, token);
                                return spec == null ? null : await executionGateway.PrepareAsync(spec, token);
                            }, needsDiscovery, canDiscover, null));
                        }
                    }
                    var answer = await RunAutomaticAsync(executionGateway, prepared, lookup, protocol.CancellationToken);
                    if (answer != null) yield return answer;
                }
                finally { DrainAutomatic(lookup); }
                yield break;
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
                resolvedLinks ??= await crossLinks.ResolveAsync(new(resourceKey, sourceProvider, sourceExternalId,
                    song.Title, string.Join(", ", artists), song.Album, null, (long?)song.Duration * 1000, song.Isrc, null, null),
                    [], protocol.Actor, protocol.CorrelationId, token);
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
                householdOnly ? [ProviderAccountScope.Shared] : [ProviderAccountScope.Personal, ProviderAccountScope.Shared], protocol.CorrelationId, deadline, householdOnly);
        }

        var hasPlainLyrics = false;
        foreach (var providerId in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            protocol.CancellationToken.ThrowIfCancellationRequested();
            if (skipPaxsenixAfterPlainLyrics && hasPlainLyrics && providerId == "paxsenix") continue;
            var lyrics = await ReadOneAsync(providerId);
            hasPlainLyrics |= !string.IsNullOrWhiteSpace(lyrics?.PlainLyrics);
            yield return new ProtocolLyricsCandidate(providerId, lyrics);
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
                    if (string.IsNullOrWhiteSpace(result?.Content)) return null;
                    lyrics = new LyricsInfo
                    {
                        TrackName = song.Title,
                        ArtistName = string.Join(", ", artists),
                        AlbumName = song.Album,
                        Duration = song.Duration ?? 0,
                        PlainLyrics = result.Format == ProviderLyricsFormat.PlainText ? result.Content : null,
                        SyncedLyrics = result.Format == ProviderLyricsFormat.PlainText ? null : result.Content,
                        Source = providerId,
                        Revision = result.Revision,
                        TimedLines = result.TimedLines,
                        Attribution = result.Attribution
                    };
                }
                protocol.CancellationToken.ThrowIfCancellationRequested();
                if (lyrics == null || string.IsNullOrWhiteSpace(lyrics.SyncedLyrics) && string.IsNullOrWhiteSpace(lyrics.PlainLyrics))
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
        if (providerId.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sourceExternalId))
            return sourceExternalId;
        if (providerId == "spotify") return string.IsNullOrWhiteSpace(spotifyTrackId) ? null : spotifyTrackId;
        if (providerId is "lrclib" or "lyricsplus" or "musixmatch" or "lrcred" or "unison" or "paxsenix" or "genius")
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
