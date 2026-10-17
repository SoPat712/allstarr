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

public sealed class ProtocolLyricsResolver(
    IProtocolProviderGateway providers,
    ILogger<ProtocolLyricsResolver> logger,
    OdesliService? odesli = null,
    IEffectiveProviderPolicyResolver? effectivePolicies = null,
    IKeptLyricsReader? keptLyrics = null,
    ILibraryIndexService? libraryIndex = null,
    IRecordingLyricsChoices? choices = null,
    ITrackIdentityService? identities = null) : IProtocolLyricsResolver
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
        LyricsInfo? plainFallback = null;
        await foreach (var candidate in ReadCandidatesAsync(protocol, song, lookupKey,
            sourceProvider, sourceExternalId, spotifyTrackId, sources, preference.SourceId, preference.RecordingId))
        {
            var lyrics = candidate.Lyrics;
            if (lyrics == null) continue;
            if (candidate.SourceId == preference.SourceId) return lyrics;
            if (!string.IsNullOrWhiteSpace(lyrics.SyncedLyrics)) return lyrics;
            plainFallback ??= lyrics;
        }
        return plainFallback;
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
        Guid? canonicalRecordingId = null)
    {
        ArgumentNullException.ThrowIfNull(protocol);
        ArgumentNullException.ThrowIfNull(song);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceKey);
        protocol.CancellationToken.ThrowIfCancellationRequested();
        var artists = song.Artists.Count > 0 ? song.Artists : [song.Artist];
        var householdOnly = sources?.HouseholdOnly == true;
        var aliasRecordingId = canonicalRecordingId;
        var aliasRecordingResolved = aliasRecordingId.HasValue;

        async Task<Guid?> ResolveAliasRecordingAsync()
        {
            if (aliasRecordingResolved) return aliasRecordingId == Guid.Empty ? null : aliasRecordingId;
            aliasRecordingResolved = true;
            if (song.IsLocal)
            {
                if (libraryIndex != null && !string.IsNullOrWhiteSpace(song.Id))
                    aliasRecordingId = (await libraryIndex.FindByBackendItemAsync(protocol, song.Id,
                        protocol.CancellationToken))?.CanonicalRecordingId;
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
                            protocol.CancellationToken))?.Id;
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
        if (effectivePolicies != null && (householdOnly || protocol.Actor?.EffectiveUserId != null))
        {
            var effectivePolicy = householdOnly
                ? await effectivePolicies.ResolveAsync(protocol.CancellationToken)
                : await effectivePolicies.ResolveForUserAsync(protocol.Actor!.EffectiveUserId!.Value, protocol.CancellationToken);
            order = effectivePolicy.ApplyProviderAvailability(
                ProviderCapabilityKind.Lyrics,
                order);
        }
        var sourceUrl = !string.IsNullOrWhiteSpace(sourceProvider) && !string.IsNullOrWhiteSpace(sourceExternalId)
            ? OdesliService.BuildTrackUrl(sourceProvider, sourceExternalId)
            : null;

        if (preferredSourceId != null && order.Contains(preferredSourceId, StringComparer.Ordinal))
            order = new[] { preferredSourceId }.Concat(order).Distinct(StringComparer.Ordinal).ToArray();
        if (sources?.OnlySourceId is { } onlySource)
            order = order.Where(item => string.Equals(item, onlySource, StringComparison.Ordinal)).ToArray();

        foreach (var providerId in order.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            protocol.CancellationToken.ThrowIfCancellationRequested();
            yield return new ProtocolLyricsCandidate(providerId, await ReadOneAsync(providerId));
        }

        async Task<LyricsInfo?> ReadOneAsync(string providerId)
        {
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
                    var externalId = ResolveExternalId(
                        providerId, resourceKey, sourceProvider, sourceExternalId, spotifyTrackId ?? song.SpotifyId);
                    if (externalId == null && sourceUrl != null && odesli != null)
                        externalId = await odesli.TranslateCachedTrackUrlAsync(
                            sourceUrl, providerId, protocol.CancellationToken);
                    ProviderLyricsResult? result;
                    if (externalId == null)
                    {
                        var recordingId = await ResolveAliasRecordingAsync();
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
                        TimedLines = result.TimedLines
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
        if (providerId.Equals(sourceProvider, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(sourceExternalId))
            return sourceExternalId;
        if (providerId == "spotify") return string.IsNullOrWhiteSpace(spotifyTrackId) ? null : spotifyTrackId;
        if (providerId is "lrclib" or "lyricsplus" or "musixmatch" or "lrcred" or "unison")
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
