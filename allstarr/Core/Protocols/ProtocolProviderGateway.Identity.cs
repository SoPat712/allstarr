using allstarr.Core.Capabilities;
using allstarr.Core.Matching;
using allstarr.Core.Storage;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Services.Local;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private static ProtocolExecutionContext PublicMetadataContext(CancellationToken cancellationToken) =>
        new(ProtocolKind.Jellyfin, "public", "public", null, "public-metadata", DateTimeOffset.UtcNow.AddSeconds(30), cancellationToken);

    public async Task<Song?> GetStoredSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId)
    {
        providerId = NormalizeProvider(providerId);
        var policy = await ResolveEffectivePolicyAsync(protocol);
        if (providerId != "allstarr" && policy?.IsEnabled(providerId) == false) return null;
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        // Explicit selections keep their source metadata and never consult recording aliases.
        if (providerId != "allstarr" && !IsAutomaticProvider(providerId, policy))
            return await ReadCachedSongAsync(actor, providerId, externalId) is { } selected && policy?.Includes(selected) != false
                ? selected : null;
        var itemId = $"ext-{NormalizeProvider(providerId)}-song-{externalId}";
        var songs = identities == null ? [] :
            (await identities.GetStoredSongsAsync(actor, itemId, protocol.CancellationToken))?.ToList() ?? [];
        if (keptFiles != null && await keptFiles.FindSongAsync(actor, itemId, protocol.CancellationToken) is { } retained)
            songs.Add(retained);
        if (songs.Count == 0 && await ReadCachedSongAsync(actor, providerId, externalId) is { } cached)
            songs.Add(cached);
        for (var index = 0; index < songs.Count; index++)
        {
            var source = songs[index];
            if (source.ExternalId == null || await ReadCachedSongAsync(actor, source.ExternalProvider!, source.ExternalId) is not { } recent) continue;
            recent.Id = source.Id;
            recent.CanonicalRecordingId = source.CanonicalRecordingId;
            if (!string.IsNullOrWhiteSpace(recent.AlbumId) || string.IsNullOrWhiteSpace(source.AlbumId))
                songs[index] = recent;
        }
        if (songs.Count == 0) return null;
        var allowed = songs.Where(song => (NormalizeProvider(song.ExternalProvider) == "allstarr" ||
            IsAutomaticProvider(NormalizeProvider(song.ExternalProvider), policy)) &&
            policy?.Includes(song) != false).ToArray();
        if (allowed.Length == 0) return null;
        var chosen = ChooseDisplaySource(protocol, allowed, ResolveProviderOrder(ProviderCapabilityKind.Metadata, policy));
        if (string.IsNullOrWhiteSpace(chosen.Title) || string.IsNullOrWhiteSpace(chosen.Artist))
        {
            var kept = allowed.FirstOrDefault(song => NormalizeProvider(song.ExternalProvider) == "allstarr" &&
                !string.IsNullOrWhiteSpace(song.Title) && !string.IsNullOrWhiteSpace(song.Artist));
            if (kept == null) return null;
            chosen = kept;
        }
        if (songs.FirstOrDefault(song => song.ExternalProvider == "allstarr" && song.Size is > 0) is { } keptSource)
        {
            chosen.Size = keptSource.Size;
            chosen.Container = keptSource.Container;
            chosen.Codec = keptSource.Codec;
            chosen.Bitrate = keptSource.Bitrate;
            chosen.SampleRate = keptSource.SampleRate;
            chosen.BitDepth = keptSource.BitDepth;
            chosen.Channels = keptSource.Channels;
        }
        NormalizeDisplaySource(chosen);
        return chosen;
    }

    private Song ChooseDisplaySource(ProtocolExecutionContext protocol, IReadOnlyList<Song> songs,
        IReadOnlyList<string> order)
    {
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var sources = songs.SelectMany(song => new[] { $"ext-{song.ExternalProvider}-song-{song.ExternalId}",
                $"ext-{PublicProviderId(new(NormalizeProvider(song.ExternalProvider), ProviderResourceKind.Track, song.ExternalId!))}-song-{song.ExternalId}" })
            .Append(songs[0].Id).Select(id => playbackActivity?.StreamFor(actor.EffectiveUserId, protocol.Client.DeviceId, id))
            .Where(source => source != null && source.Matches(protocol.Protocol.ToString().ToLowerInvariant(), protocol.BackendInstanceId))
            .OrderByDescending(source => source!.OpenedAt).ToArray();
        var playing = sources.FirstOrDefault();
        return songs.OrderByDescending(song => !string.IsNullOrWhiteSpace(song.AlbumId))
            .ThenByDescending(song => DisplayProviderPriority(song.ExternalProvider) != 5 && !string.IsNullOrWhiteSpace(song.Album))
            .ThenByDescending(song => playing != null && NormalizeProvider(song.ExternalProvider) == NormalizeProvider(playing.ProviderId) &&
                song.ExternalId == playing.ExternalId)
            .ThenBy(song => DisplayProviderPriority(song.ExternalProvider))
            .ThenBy(song => ProviderPriority(order, song.ExternalProvider))
            .ThenBy(song => song.ExternalProvider, StringComparer.Ordinal)
            .ThenBy(song => song.ExternalId, StringComparer.Ordinal).First();
    }

    private async Task CacheSongsAsync(ProviderExecutionContext context, IReadOnlyList<Song> songs, bool detail = false)
    {
        foreach (var song in songs.Where(item => !item.IsLocal && !string.IsNullOrWhiteSpace(item.ExternalId)))
        {
            await CacheMetadataAsync(context, ProviderResourceKind.Track, song.ExternalId!, song);
            if (detail) await CacheMetadataAsync(context, ProviderResourceKind.Track, song.ExternalId!, song, true);
        }
    }

    private static void ApplyDisplaySource(Song song, Song source)
    {
        song.Title = source.Title;
        song.Artist = source.Artist;
        song.Artists = source.Artists.ToList();
        song.ArtistId = source.ArtistId;
        song.ArtistIds = source.ArtistIds.ToList();
        song.Album = source.Album;
        song.AlbumId = source.AlbumId;
        song.AlbumArtist = source.AlbumArtist;
        song.Year = source.Year;
        song.CoverArtUrl = source.CoverArtUrl;
        song.CoverArtUrlLarge = source.CoverArtUrlLarge;
        song.ExternalProvider = source.ExternalProvider;
        song.ExternalId = source.ExternalId;
        song.Isrc = source.Isrc;
        song.Track = source.Track;
        song.DiscNumber = source.DiscNumber;
        song.TotalTracks = source.TotalTracks;
        song.Genre = source.Genre;
        song.ReleaseDate = source.ReleaseDate;
        song.ExplicitContentLyrics = source.ExplicitContentLyrics;
        song.Composer = source.Composer;
        song.Label = source.Label;
        song.Copyright = source.Copyright;
        song.Contributors = source.Contributors.ToList();
        song.Bpm = source.Bpm;
        song.SpotifyId = source.SpotifyId;
        song.Size = source.Size;
        song.Container = source.Container;
        song.Codec = source.Codec;
        song.Bitrate = source.Bitrate;
        song.SampleRate = source.SampleRate;
        song.BitDepth = source.BitDepth;
        song.Channels = source.Channels;
    }

    private static int DisplayProviderPriority(string? provider) => NormalizeProvider(provider) switch
    {
        "apple-musickit" => 0,
        "deezer" => 1,
        "qobuz" => 2,
        "tidal" or "spotiflac-tidal-web" => 3,
        "soundcloud" or "spotiflac-soundcloud" or "youtube" or "spotiflac-youtube" => 5,
        _ => 4
    };

    private static void NormalizeDisplaySource(Song song)
    {
        var provider = NormalizeProvider(song.ExternalProvider);
        song.ExternalProvider = PublicProviderId(new(provider, ProviderResourceKind.Track, song.ExternalId!));
        if (song.CanonicalRecordingId == null && song.Id == $"ext-{provider}-song-{song.ExternalId}")
            song.Id = ProtocolItemId(new(provider, ProviderResourceKind.Track, song.ExternalId!));
        string? NormalizeResource(string? id)
        {
            if (id == null) return null;
            var parsed = LocalLibraryService.ParseExternalResource(id);
            if (!parsed.isExternal) return id;
            return ProtocolItemId(new(NormalizeProvider(parsed.provider), parsed.type == "album"
                ? ProviderResourceKind.Album : ProviderResourceKind.Artist, parsed.externalId!));
        }
        song.AlbumId = NormalizeResource(song.AlbumId);
        song.ArtistId = NormalizeResource(song.ArtistId);
        song.ArtistIds = song.ArtistIds.Select(id => NormalizeResource(id) ?? string.Empty).ToList();
    }

    public async Task RememberTrackAsync(ProtocolExecutionContext protocol, string itemId)
    {
        if (identities == null) return;
        var parsed = LocalLibraryService.ParseExternalResource(itemId);
        if (!parsed.isExternal || parsed.type != "song") return;
        try
        {
            var provider = registry.ResolveProviderId(NormalizeProvider(parsed.provider!));
            var track = new ProviderExternalResourceId(provider, ProviderResourceKind.Track, parsed.externalId!);
            var routed = await PlanExactAsync<IProviderMetadataCapability>(protocol, provider,
                ProviderCapabilityKind.Metadata, "remember-track", track);
            if (routed.Candidate is { } candidate)
                await RememberPlayedTrackAsync(candidate.Context, candidate.TrackId ?? track, itemId);
        }
        catch (Exception error)
        {
            logger?.LogWarning("Could not retain the track identity ({ErrorType})", error.GetType().Name);
        }
    }

    private async Task RememberPlayedTrackAsync(ProviderExecutionContext context,
        ProviderExternalResourceId track, string requestedItemId)
    {
        if (identities == null) return;
        try
        {
            await identities.RememberSourceAsync(context, track, requestedItemId,
                context.Account != null && !IsPublicMetadataProvider(context.ProviderId)
                    ? ProviderIdentityScope.Account : ProviderIdentityScope.Catalog,
                cancellationToken: context.CancellationToken);
        }
        catch (Exception error)
        {
            // Recording bookkeeping cannot turn an already opened audio response into a failure.
            logger?.LogWarning("Could not retain the playback identity ({ErrorType})", error.GetType().Name);
        }
    }

    private async Task<Song?> GetKnownSongAsync(ProtocolExecutionContext protocol, string providerId, string externalId)
    {
        if (identities == null) return null;
        var policy = await ResolveEffectivePolicyAsync(protocol);
        if (providerId != "allstarr" && !IsAutomaticProvider(providerId, policy)) return null;
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var recording = await identities.ResolveRecordingAsync(actor, $"ext-{providerId}-song-{externalId}",
            protocol.CancellationToken);
        if (recording == null) return null;
        var plan = await router.PlanAsync<IProviderMetadataCapability>(Request(protocol, actor,
            ProviderCapabilityKind.Metadata, "protocol-recording-metadata",
            ResolveProviderOrder(ProviderCapabilityKind.Metadata, policy),
            new ProviderExternalResourceId("allstarr", ProviderResourceKind.Track, recording.Id.ToString("D"))));
        var sources = (await identities.GetStoredSongsAsync(actor, recording.PublicId, protocol.CancellationToken)) ?? [];
        var displayOrder = plan.Candidates.Where(item => item.TrackId != null).Select(candidate =>
            sources.FirstOrDefault(song => NormalizeProvider(song.ExternalProvider) == candidate.Provider.Id &&
                song.ExternalId == candidate.TrackId!.Value) ?? new Song
                {
                    Id = recording.PublicId,
                    ExternalProvider = candidate.Provider.Id,
                    ExternalId = candidate.TrackId!.Value
                }).ToList();
        var candidates = new List<allstarr.Core.Routing.ProviderRouteCandidate<IProviderMetadataCapability>>();
        while (displayOrder.Count > 0)
        {
            var chosen = ChooseDisplaySource(protocol, displayOrder, ResolveProviderOrder(ProviderCapabilityKind.Metadata, policy));
            candidates.Add(plan.Candidates.First(item => item.TrackId?.Value == chosen.ExternalId &&
                item.Provider.Id == NormalizeProvider(chosen.ExternalProvider)));
            displayOrder.Remove(chosen);
        }
        foreach (var candidate in candidates)
        {
            if (candidate.TrackId == null) continue;
            var song = await ReadCachedSongAsync(candidate.Context.Actor, candidate.Provider.Id, candidate.TrackId.Value,
                candidate.Context.Account);
            if (song == null)
            {
                var outcome = await candidate.Implementation.GetTrackAsync(candidate.Context, new(candidate.TrackId));
                if (!outcome.IsSuccess)
                {
                    if (outcome.Error!.Kind == ProviderErrorKind.NotFound) continue;
                    ThrowRouteFailure(outcome.Error);
                }
                song = await EnrichRelationshipsAsync(candidate, outcome.RequireValue());
                await CacheSongsAsync(candidate.Context, [song], true);
            }
            song.Id = recording.PublicId;
            song.CanonicalRecordingId = recording.Id;
            return policy?.Includes(song) == false ? null : song;
        }
        return null;
    }

    private async Task ApplyKnownPublicIdsAsync(ProviderExecutionContext context, IReadOnlyList<Song> songs,
        ProtocolExecutionContext? protocol = null, EffectiveProviderPolicySnapshot? policy = null)
    {
        await CacheSongsAsync(context, songs);
        if (identities == null || songs.Count == 0) return;
        if (policy == null && protocol != null)
            policy = await ResolveEffectivePolicyAsync(protocol);
        if (policy == null && effectivePolicies != null)
            policy = context.Actor.EffectiveUserId is { } userId
                ? await effectivePolicies.ResolveForUserAsync(userId, context.CancellationToken)
                : await effectivePolicies.ResolveAsync(context.CancellationToken);
        if (!IsAutomaticProvider(context.ProviderId, policy)) return;
        var lookups = songs.Select(song => new TrackIdentityLookup(context,
            new ProviderExternalResourceId(context.ProviderId, ProviderResourceKind.Track, song.ExternalId!))).ToArray();
        var resolved = await identities.ResolveManyAsync(lookups, context.CancellationToken);
        for (var index = 0; index < songs.Count; index++)
        {
            var identity = resolved[index];
            if (identity?.Verification == ProviderIdentityVerification.Verified &&
                identity.VerificationMethod is not ("automatic-suggestion" or
                    ManualTrackAuthorityPolicy.ReleasedProviderVerificationMethod or
                    ManualTrackAuthorityPolicy.ReplacedProviderVerificationMethod) &&
                !string.IsNullOrWhiteSpace(identity.PublicId))
            {
                songs[index].Id = identity.PublicId;
                songs[index].CanonicalRecordingId = identity.CanonicalRecordingId;
                if (protocol != null && await GetStoredSongAsync(protocol, context.ProviderId, songs[index].ExternalId!) is { } display)
                    ApplyDisplaySource(songs[index], display);
            }
        }
    }
}
