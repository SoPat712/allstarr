using System.Text.Json;
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
using allstarr.Services.Common;

namespace allstarr.Core.ClientApi;

internal static class StoredItemLyrics
{
    public static async Task<ItemDetailsLyrics> ReadAsync(IApplicationCache cache, Song? song, Guid? recordingId,
        string? chosenSource, string? audioPath, EffectiveProviderPolicySnapshot policy, string lyricsEndpoint,
        ProtocolExecutionContext execution, IReadOnlyList<ProviderAccountRecord> accounts,
        IReadOnlyList<ProviderTrackIdentityRecord> identities, CancellationToken token)
    {
        if (song == null) return new(false, null, chosenSource);
        if (audioPath != null && (chosenSource is null or "kept") && policy.IsEnabled("kept"))
        {
            var kept = RichLyricsMapper.Map(await KeptLyricsReader.ReadStoredSidecarAsync(song, audioPath, token));
            if (kept != null) return new(true, kept.Format, "kept");
        }
        var lookup = MusicLyrics.ForLookup(song);
        var providers = chosenSource == null ? policy.GetProviderOrder(ProviderCapabilityKind.Lyrics) : [chosenSource];
        foreach (var provider in providers.Where(policy.IsEnabled))
        {
            token.ThrowIfCancellationRequested();
            if (provider is not ("lrclib" or "lrcred" or "unison" or "genius" or "paxsenix" or "lyricsplus" or "musixmatch" or "spicylyrics" or "spotify")) continue;
            if (provider == "lrclib")
            {
                try
                {
                    var artists = lookup.Artists.Count > 0 ? lookup.Artists : [lookup.Artist];
                    var cached = await cache.GetStringAsync("lrclib:automatic:v2:" + CacheKeyBuilder.BuildLyricsKey(
                        string.Join(", ", artists), lookup.Title, lookup.Album, lookup.Duration ?? 0));
                    var richCached = cached == null ? null : RichLyricsMapper.Map(JsonSerializer.Deserialize<LyricsInfo>(cached, ClientApiContract.JsonOptions));
                    if (richCached != null) return new(true, richCached.Format, "lrclib");
                }
                catch (Exception error) when (error is not OperationCanceledException) { }
                continue;
            }
            if (provider == "paxsenix" && recordingId == null) continue;
            var scope = provider == "paxsenix" ? $"apple:{recordingId:N}:" : "";
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
                        var accountStored = await ReadAccountCacheAsync(cache, provider, request, song, recordingId, execution, accounts, identities);
                        var richStored = Map(accountStored, provider);
                        if (richStored != null) return new(true, richStored.Format, provider);
                        continue;
                    }
                }
                catch (ArgumentException) { continue; }
            }
            token.ThrowIfCancellationRequested();
            ProviderLyricsResult? stored;
            try { stored = await cache.GetAsync<ProviderLyricsResult>(key); }
            catch (Exception error) when (error is not OperationCanceledException) { continue; }
            if (stored?.Availability != ProviderLyricsAvailabilityState.Available) continue;
            var rich = Map(stored, provider);
            if (rich != null) return new(true, rich.Format, provider);
        }
        return new(false, null, chosenSource);
    }

    private static RichLyrics? Map(ProviderLyricsResult? stored, string provider)
    {
        if (stored is not { Availability: ProviderLyricsAvailabilityState.Available }) return null;
        return RichLyricsMapper.Map(new LyricsInfo
        {
            Source = provider,
            PlainLyrics = stored.Format == ProviderLyricsFormat.PlainText ? stored.Content : null,
            SyncedLyrics = stored.Format == ProviderLyricsFormat.PlainText ? null : stored.Content,
            TimedLines = stored.TimedLines
        });
    }

    private static async Task<ProviderLyricsResult?> ReadAccountCacheAsync(IApplicationCache cache, string provider,
        ProviderLyricsRequest request, Song song, Guid? recordingId, ProtocolExecutionContext execution,
        IReadOnlyList<ProviderAccountRecord> accounts, IReadOnlyList<ProviderTrackIdentityRecord> identities)
    {
        var contexts = accounts.Where(item => item.ProviderId == provider).OrderByDescending(item => item.OwnerUserId.HasValue)
            .Select(item => new ProviderAccountContext(item.Id, item.ProviderId, item.Scope, item.Revision,
                ownerUserId: item.OwnerUserId, secretReferenceId: item.SecretReferenceId)).ToList();
        if (provider == "musixmatch")
        {
            foreach (var account in contexts.Cast<ProviderAccountContext?>().Append(null))
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
