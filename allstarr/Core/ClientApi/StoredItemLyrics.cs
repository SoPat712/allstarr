using allstarr.Core.Caching;
using System.Text.Json;
using System.Net;
using allstarr.Core.Capabilities;
using allstarr.Core.Downloads;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.ClientApi;

internal static class StoredItemLyrics
{
    public static async Task<ItemDetailsLyrics> ReadAsync(IApplicationCache cache, Song? song, Guid? recordingId,
        string? chosenSource, string? audioPath, EffectiveProviderPolicySnapshot policy, string lyricsEndpoint,
        ProtocolExecutionContext execution, IReadOnlyList<ProviderAccountRecord> accounts,
        IReadOnlyList<ProviderTrackIdentityRecord> identities, CancellationToken token, Action<RichLyrics>? observe = null, long? recordingDurationMs = null,
        LyricsInfo? automaticLyrics = null, Action<LyricsInfo>? observeInfo = null)
    {
        if (song == null || policy.Preferences.ExplicitFilter == "CleanOnly" && !CleanRecordingPolicy.IsVerifiedClean(song))
            return new(false, null, null);
        var best = new ItemDetailsLyrics(false, null, chosenSource);
        LyricsScore? bestScore = null;
        ItemDetailsLyrics? retainedChoice = null;
        if (chosenSource == null && LyricsQuality.Evaluate(automaticLyrics, song, recordingDurationMs) is { } retainedScore &&
            RichLyricsMapper.Map(automaticLyrics) is { } retained && policy.IsAutomatic(LyricsVersions.ProviderId(retained.Source.Id)))
        {
            best = new(true, retained.Format, retained.Source.Id);
            retainedChoice = best;
            bestScore = retainedScore;
            observe?.Invoke(retained);
            observeInfo?.Invoke(automaticLyrics!);
        }
        bool ConsiderProvider(LyricsInfo? info, string provider)
        {
            if (info == null) return false;
            info.Source = provider;
            var versions = LyricsVersions.Candidates(provider, info);
            foreach (var version in versions)
            {
                if (info.Versions != null) version.Source = LyricsVersions.SourceId(provider, version.Metadata);
                if (version.Metadata?.DurationMs is { } milliseconds) version.Duration = (int)((milliseconds + 999) / 1000);
                if (RichLyricsMapper.Map(version) is { } rich) observe?.Invoke(rich);
                observeInfo?.Invoke(version);
            }
            if (chosenSource != null && LyricsVersions.ProviderId(chosenSource) == provider)
            {
                var pinned = versions.FirstOrDefault(version => version.Source == chosenSource) ??
                    (RichLyricsMapper.Map(info) != null ? info : versions.FirstOrDefault());
                if (RichLyricsMapper.Map(pinned) is not { } rich) return false;
                if (info.Versions != null && ReferenceEquals(pinned, info)) observe?.Invoke(rich);
                best = new(true, rich.Format, rich.Source.Id);
                return true;
            }
            foreach (var version in versions)
            {
                if (LyricsQuality.Evaluate(version, song, recordingDurationMs) is not { } score || !LyricsQuality.Better(score, bestScore)) continue;
                if (retainedChoice != null && !score.Reasons.Contains("duration_fits") && !score.Reasons.Contains("plain_text")) continue;
                bestScore = score;
                best = new(true, RichLyricsMapper.Map(version)!.Format, version.Source);
            }
            return false;
        }
        var lookup = MusicLyrics.ForLookup(song);
        var automatic = ProviderOrderPolicyCatalog.NormalizeOrder(ProviderCapabilityKind.Lyrics, policy.GetProviderOrder(ProviderCapabilityKind.Lyrics));
        var providers = chosenSource == null ? automatic : [LyricsVersions.ProviderId(chosenSource)];
        foreach (var provider in providers.Where(provider => chosenSource == null ? policy.IsAutomatic(provider) : policy.IsEnabled(provider))
            .Where(provider => policy.Preferences.ExplicitFilter != "CleanOnly" || provider is "backend" or "kept"))
        {
            token.ThrowIfCancellationRequested();
            if (provider == "kept" && audioPath != null)
            {
                if (ConsiderProvider(await KeptLyricsReader.ReadStoredSidecarAsync(song, audioPath, token), provider)) return best;
                continue;
            }
            if (provider is not ("lrclib" or "lrcred" or "amll" or "neteaselyrics" or "qqlyrics" or "kugoulyrics" or "lrcmux" or "unison" or "genius" or "paxsenix" or "lyricsplus" or "musixmatch" or "spicylyrics" or "spotify")) continue;
            if (provider == "lrclib")
            {
                try
                {
                    var artists = lookup.Artists.Count > 0 ? lookup.Artists : [lookup.Artist];
                    var cached = await cache.GetStringAsync("lrclib:automatic:v2:" + CacheKeyBuilder.BuildLyricsKey(
                        string.Join(", ", artists), lookup.Title, lookup.Album, lookup.Duration ?? 0));
                    var info = cached == null ? null : JsonSerializer.Deserialize<LyricsInfo>(cached, ClientApiContract.JsonOptions);
                    if (info != null) info.Duration = lookup.Duration ?? 0;
                    if (ConsiderProvider(info, provider)) return best;
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
                continue;
            }
            if (provider == "paxsenix" && recordingId == null) continue;
            var scope = provider == "paxsenix" ? $"apple:{recordingId:N}:" : provider == "genius" ? "html-decoded:v1:" :
                provider is "neteaselyrics" or "qqlyrics" or "kugoulyrics" ? "native-words:v1:" : "";
            var key = $"lyrics:v3:{provider}:" + scope + CanonicalCatalogKeys.Hash(JsonSerializer.Serialize(new
            {
                lookup.Isrc,
                TrackTitle = string.IsNullOrWhiteSpace(lookup.Title) ? null : lookup.Title.Trim(),
                ArtistNames = (lookup.Artists.Count > 0 ? lookup.Artists : [lookup.Artist])
                    .Where(artist => !string.IsNullOrWhiteSpace(artist)).Select(artist => artist.Trim()).ToArray(),
                AlbumTitle = string.IsNullOrWhiteSpace(lookup.Album) ? null : lookup.Album.Trim(),
                DurationSeconds = lookup.Duration
            }));
            if (provider is "lyricsplus" or "musixmatch" or "spicylyrics" or "spotify")
            {
                var selector = CanonicalCatalogKeys.Hash(recordingId?.ToString("N") ?? song.Id);
                var canonical = recordingId ?? new Guid(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(provider + "\n" + selector)).AsSpan(0, 16));
                var request = new ProviderLyricsRequest(canonical, new(provider, ProviderResourceKind.Track, selector), preferredFormat: ProviderLyricsFormat.LineTimed,
                    trackTitle: string.IsNullOrWhiteSpace(lookup.Title) ? null : lookup.Title.Trim(),
                    artistNames: (lookup.Artists.Count > 0 ? lookup.Artists : [lookup.Artist])
                        .Where(artist => !string.IsNullOrWhiteSpace(artist)).Select(artist => artist.Trim()).ToArray(),
                    albumTitle: string.IsNullOrWhiteSpace(lookup.Album) ? null : lookup.Album.Trim(), durationSeconds: lookup.Duration, isrc: lookup.Isrc);
                try
                {
                    if (provider == "lyricsplus") key = LyricsPlusCapabilityAdapter.StoredCacheKey(lyricsEndpoint, request);
                    else
                    {
                        var accountStored = await ReadAccountCacheAsync(cache, provider, request, song, recordingId, execution, accounts, identities, observe != null);
                        if (accountStored is { Availability: ProviderLyricsAvailabilityState.Available } &&
                            ConsiderProvider(LyricsVersions.Map(accountStored, provider, lookup.Duration), provider)) return best;
                        continue;
                    }
                }
                catch (ArgumentException) { continue; }
            }
            token.ThrowIfCancellationRequested();
            ProviderLyricsResult? stored;
            try
            {
                stored = await cache.GetAsync<ProviderLyricsResult>(key);
                if (stored == null && provider == "genius" &&
                    await cache.GetAsync<ProviderLyricsResult>(key.Replace("html-decoded:v1:", "", StringComparison.Ordinal)) is
                    { Availability: ProviderLyricsAvailabilityState.Available, Content: { } content } legacy)
                {
                    var decoded = WebUtility.HtmlDecode(content);
                    stored = new(legacy.Availability, legacy.Source, legacy.Format, decoded, "sha256:" + CanonicalCatalogKeys.Hash(decoded));
                    await cache.SetAsync(key, stored, TimeSpan.FromDays(7));
                }
            }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            if (stored?.Availability != ProviderLyricsAvailabilityState.Available) continue;
            if (ConsiderProvider(LyricsVersions.Map(stored, provider, lookup.Duration), provider)) return best;
        }
        return best;
    }

    private static async Task<ProviderLyricsResult?> ReadAccountCacheAsync(IApplicationCache cache, string provider,
        ProviderLyricsRequest request, Song song, Guid? recordingId, ProtocolExecutionContext execution,
        IReadOnlyList<ProviderAccountRecord> accounts, IReadOnlyList<ProviderTrackIdentityRecord> identities, bool selectedOnly)
    {
        var contexts = accounts.Where(item => item.ProviderId == provider).OrderByDescending(item => item.OwnerUserId.HasValue)
            .Select(item => new ProviderAccountContext(item.Id, item.ProviderId, item.Scope, item.Revision,
                ownerUserId: item.OwnerUserId, secretReferenceId: item.SecretReferenceId)).ToList();
        if (provider == "musixmatch")
        {
            foreach (var account in contexts.Cast<ProviderAccountContext?>().Concat(selectedOnly && contexts.Count > 0 ? [] : new ProviderAccountContext?[] { null }))
            {
                execution.CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (await cache.GetAsync<ProviderLyricsResult>(MusixmatchLyricsCapabilityAdapter.BuildCacheKey(account, request)) is
                        { Availability: ProviderLyricsAvailabilityState.Available } stored) return stored;
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
            return null;
        }
        var selectors = identities.Where(item => item.CanonicalRecordingId == recordingId && item.ProviderId == "spotify")
            .Select(item => item.ExternalId).Prepend(song.SpotifyId ?? (song.ExternalProvider == "spotify" ? song.ExternalId : null))
            .Select(item => SpotifyTrackReference.TryNormalize(item, out var id) ? id : null)
            .OfType<string>().Distinct(StringComparer.Ordinal).ToArray();
        var knownSelector = song.SpotifyId ?? (song.ExternalProvider == "spotify" ? song.ExternalId : null);
        if (selectedOnly && !SpotifyTrackReference.TryNormalize(knownSelector, out _) && selectors.Length != 1) return null;
        foreach (var selector in selectors)
            foreach (var account in contexts)
            {
                execution.CancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (provider == "spotify")
                    {
                        if (account.SecretReferenceId == null) continue;
                        var selected = new ProviderLyricsRequest(request.CanonicalRecordingId, new(provider, ProviderResourceKind.Track, selector),
                            preferredFormat: request.PreferredFormat);
                        if (await cache.GetAsync<ProviderLyricsResult>(SpotifyPlaylistCapabilityAdapter.LyricsCacheKey(account, selected)) is
                            { Availability: ProviderLyricsAvailabilityState.Available } stored) return stored;
                        continue;
                    }
                    var key = SpicyLyricsCapabilityAdapter.StoredCacheKey(execution.RequireActor(), account, provider, selector!);
                    var entry = await cache.GetAsync<SpicyLyricsCapabilityAdapter.CachedLyrics>(key);
                    var now = DateTimeOffset.UtcNow;
                    if (entry != null && entry.FetchedAtUtc <= now && entry.ExpiresAtUtc > now && entry.ExpiresAtUtc > entry.FetchedAtUtc &&
                        entry.ExpiresAtUtc - entry.FetchedAtUtc <= TimeSpan.FromDays(30)) return entry.Result;
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
            }
        return null;
    }
}
