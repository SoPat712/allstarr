using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver
        {
            Modifiers =
            {
                info =>
                {
                    if (info.Type != typeof(Song)) return;
                    info.Properties.Remove(info.Properties.First(property => property.Name == nameof(Song.PreciseDuration)));
                    var duration = info.CreateJsonPropertyInfo(typeof(TimeSpan?), nameof(Song.PreciseDuration));
                    duration.Get = value => ((Song)value).PreciseDuration;
                    duration.Set = (value, duration) => ((Song)value).PreciseDuration = (TimeSpan?)duration;
                    info.Properties.Add(duration);
                }
            }
        }
    };
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> metadataWork = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> foregroundMetadata = new(StringComparer.Ordinal);
    private static readonly TimeSpan MetadataFreshFor = TimeSpan.FromHours(24);
    private static readonly TimeSpan MetadataRetention = TimeSpan.FromDays(7);

    private async Task<T?> FullMetadataAsync<T>(ProviderExecutionContext source, ProviderResourceKind kind,
        string externalId, Func<ProviderExecutionContext, Task<T?>> fetch, bool prefetch = false) where T : class
    {
        var key = MetadataCacheKey(source.Actor, source.ProviderId, kind, externalId, true, source.Account);
        var cached = await ReadCachedMetadataAsync<T>(source.Actor, source.ProviderId, kind, externalId, true, source.Account);
        var clock = timeProvider ?? TimeProvider.System;
        var stamp = applicationCache?.IsEnabled == true ? await applicationCache.GetStringAsync(key + ":fetched") : null;
        if (cached != null && DateTimeOffset.TryParse(stamp, out var at) && clock.GetUtcNow() - at < MetadataFreshFor)
            return cached;
        var work = metadataWork.GetOrAdd(key, _ => new(() => Task.Run(FetchAsync)));
        if (cached != null)
        {
            _ = work.Value;
            return cached;
        }
        if (!prefetch) foregroundMetadata.AddOrUpdate(source.ProviderId, 1, (_, count) => count + 1);
        try
        {
            var payload = await work.Value.WaitAsync(source.CancellationToken);
            return payload == null ? null : JsonSerializer.Deserialize<T>(payload, MetadataJson);
        }
        finally
        {
            if (!prefetch) foregroundMetadata.AddOrUpdate(source.ProviderId, 0, (_, count) => count - 1);
        }

        async Task<string?> FetchAsync()
        {
            try
            {
                if (cached == null && await ReadCachedMetadataAsync<T>(source.Actor, source.ProviderId, kind, externalId, true, source.Account) is { } ready &&
                    DateTimeOffset.TryParse(await applicationCache!.GetStringAsync(key + ":fetched"), out var refreshed) && clock.GetUtcNow() - refreshed < MetadataFreshFor)
                    return JsonSerializer.Serialize(ready, MetadataJson);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
                var execution = new ProviderExecutionContext(source.Actor, source.ProviderId, source.Account, source.Policy,
                    source.OperationId, source.CorrelationId, clock.GetUtcNow().AddSeconds(30), timeout.Token)
                { IsReadOnly = source.IsReadOnly };
                var value = await fetch(execution).WaitAsync(timeout.Token);
                if (value == null) return null;
                await CacheMetadataAsync(execution, kind, externalId, value);
                await CacheMetadataAsync(execution, kind, externalId, value, true);
                return JsonSerializer.Serialize(value, MetadataJson);
            }
            catch (Exception error) when (cached != null)
            {
                logger?.LogDebug("Metadata refresh was unavailable ({ErrorType})", error.GetType().Name);
                return null;
            }
            finally { metadataWork.TryRemove(key, out _); }
        }
    }

    private ProtocolExecutionContext BackgroundMetadataContext(ProtocolExecutionContext protocol) =>
        new(protocol.Protocol, protocol.BackendInstanceId, protocol.VerifiedBackendPrincipalId, protocol.Principal,
            protocol.CorrelationId, (timeProvider ?? TimeProvider.System).GetUtcNow().AddSeconds(30), CancellationToken.None, protocol.Client)
        { Policy = protocol.Policy, IsReadOnly = protocol.IsReadOnly };

    private async Task RefreshSongDetailAsync(ProtocolExecutionContext protocol, string provider, string externalId)
    {
        if (applicationCache?.IsEnabled != true || provider == "allstarr") return;
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        var accountPayload = await applicationCache.GetStringAsync(MetadataAccountKey(actor, provider, ProviderResourceKind.Track, externalId, true));
        var account = accountPayload == null ? null : JsonSerializer.Deserialize<MetadataAccount>(accountPayload);
        var key = MetadataCacheKey(actor, provider, ProviderResourceKind.Track, externalId, true);
        if (account != null)
            key = CacheKeyBuilder.BuildProtocolMetadataKey(provider,
                JsonSerializer.Serialize(new { Actor = actor, AccountId = (Guid?)account.Id, AccountRevision = (long?)account.Revision }), "Track:detail", externalId);
        var stamp = await applicationCache.GetStringAsync(key + ":fetched");
        if (!DateTimeOffset.TryParse(stamp, out var at) || (timeProvider ?? TimeProvider.System).GetUtcNow() - at < MetadataFreshFor) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var background = BackgroundMetadataContext(protocol);
                var routed = await PlanExactAsync<IProviderMetadataCapability>(background, provider, ProviderCapabilityKind.Metadata, "protocol-metadata-get-track");
                if (routed.Candidate is not { } candidate) return;
                await FullMetadataAsync<Song>(candidate.Context, ProviderResourceKind.Track, externalId, async execution =>
                {
                    var outcome = await candidate.Implementation.GetTrackAsync(execution, new(new(provider, ProviderResourceKind.Track, externalId)));
                    return outcome.IsSuccess ? await EnrichRelationshipsAsync(candidate with { Context = execution }, outcome.RequireValue()) : null;
                });
            }
            catch (Exception error) { logger?.LogDebug("Song refresh was unavailable ({ErrorType})", error.GetType().Name); }
        });
    }

    internal async Task<ProtocolCachedSongFacts?> ReadCachedSongFactsAsync(ProviderActorContext actor, string providerId,
        string externalId, ProviderAccountContext? account = null)
    {
        var provider = NormalizeProvider(providerId);
        if (actor.Kind is not (ProviderActorKind.User or ProviderActorKind.Administrator) ||
            actor.EffectiveUserId == null || actor.BackendPrincipal == null || account != null &&
            (!account.Enabled || NormalizeProvider(account.ProviderId) != provider ||
             account.Scope == ProviderAccountScope.Personal && account.OwnerUserId != actor.EffectiveUserId)) return null;
        var song = await ReadCachedSongAsync(actor, provider, externalId, account);
        if (song == null || song.IsLocal || NormalizeProvider(song.ExternalProvider) != provider || song.ExternalId != externalId) return null;
        return new(song.Title, song.Artist, song.Album,
            song.Duration is > 0 and <= 604800 ? song.Duration.Value * 1000L : null, song.CanonicalRecordingId);
    }

    public async Task<Album?> GetStoredAlbumAsync(ProtocolExecutionContext protocol, string providerId, string externalId)
    {
        var album = await ReadStoredMetadataAsync<Album>(protocol, providerId, ProviderResourceKind.Album, externalId);
        if (album == null || string.IsNullOrWhiteSpace(album.Id)) return null;
        var policy = await ResolveEffectivePolicyAsync(protocol);
        album.Songs = album.Songs.Where(song => policy?.Includes(song) != false).ToList();
        return album;
    }

    public async Task<Artist?> GetStoredArtistAsync(ProtocolExecutionContext protocol, string providerId, string externalId)
    {
        var artist = await ReadStoredMetadataAsync<Artist>(protocol, providerId, ProviderResourceKind.Artist, externalId);
        return string.IsNullOrWhiteSpace(artist?.Id) ? null : artist;
    }

    private async Task<T?> ReadStoredMetadataAsync<T>(ProtocolExecutionContext protocol, string providerId,
        ProviderResourceKind kind, string externalId, bool detail = false) where T : class
    {
        var provider = NormalizeProvider(providerId);
        if ((await ResolveEffectivePolicyAsync(protocol))?.IsEnabled(provider) == false) return null;
        var actor = protocol.Actor ?? new ProviderActorContext(ProviderActorKind.PublicRead, null);
        if (await ReadCachedMetadataAsync<T>(actor, provider, kind, externalId, detail) is { } cached) return cached;
        if (await ReadAccountMetadataAsync<T>(protocol, actor, provider, kind, externalId, detail) is { } accountMetadata) return accountMetadata;
        if (detail) return null;
        var payload = identities == null ? null : await identities.GetStoredMetadataAsync(
            actor, new(provider, kind, externalId), protocol.CancellationToken);
        if (payload == null) return null;
        try { return JsonSerializer.Deserialize<T>(payload, MetadataJson); }
        catch (JsonException) { return null; }
    }

    private async Task CacheResourcesAsync(ProviderExecutionContext context, IReadOnlyList<Album> albums,
        IReadOnlyList<Artist> artists, bool detail = false)
    {
        foreach (var album in albums.Where(item => !item.IsLocal && !string.IsNullOrWhiteSpace(item.ExternalId)))
        {
            await CacheMetadataAsync(context, ProviderResourceKind.Album, album.ExternalId!, album);
            if (detail) await CacheMetadataAsync(context, ProviderResourceKind.Album, album.ExternalId!, album, true);
        }
        foreach (var artist in artists.Where(item => !item.IsLocal && !string.IsNullOrWhiteSpace(item.ExternalId)))
        {
            await CacheMetadataAsync(context, ProviderResourceKind.Artist, artist.ExternalId!, artist);
            if (detail) await CacheMetadataAsync(context, ProviderResourceKind.Artist, artist.ExternalId!, artist, true);
            if (sharedTags != null && artist.Genres.Count > 0 && context.Actor.EffectiveUserId is { } owner)
            {
                try { await sharedTags.CaptureArtistAsync(owner, artist, context.ProviderId, context.CancellationToken); }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger?.LogDebug("Could not retain artist genres ({ErrorType})", error.GetType().Name); }
            }
        }
    }

    private async Task CacheMetadataAsync<T>(ProviderExecutionContext context, ProviderResourceKind kind,
        string externalId, T value, bool detail = false) where T : class
    {
        if (applicationCache?.IsEnabled != true) return;
        try
        {
            await applicationCache.SetStringAsync(MetadataCacheKey(context.Actor, context.ProviderId, kind, externalId, detail, context.Account),
                JsonSerializer.Serialize(value, MetadataJson), detail ? MetadataRetention : MetadataFreshFor);
            if (detail)
                await applicationCache.SetStringAsync(MetadataCacheKey(context.Actor, context.ProviderId, kind, externalId, true, context.Account) + ":fetched",
                    (timeProvider ?? TimeProvider.System).GetUtcNow().ToString("O"), MetadataRetention);
            if (context.Account is { } account)
                await applicationCache.SetStringAsync(MetadataAccountKey(context.Actor, context.ProviderId, kind, externalId, detail),
                    JsonSerializer.Serialize(new MetadataAccount(account.AccountId, account.Revision)), detail ? MetadataRetention : MetadataFreshFor);
        }
        catch (Exception error)
        {
            logger?.LogDebug("Could not cache resource metadata ({ErrorType})", error.GetType().Name);
        }
    }

    private async Task<T?> ReadAccountMetadataAsync<T>(ProtocolExecutionContext protocol, ProviderActorContext actor,
        string provider, ProviderResourceKind kind, string externalId, bool detail) where T : class
    {
        if (applicationCache?.IsEnabled != true) return null;
        try
        {
            var payload = await applicationCache.GetStringAsync(MetadataAccountKey(actor, provider, kind, externalId, detail));
            var source = payload == null ? null : JsonSerializer.Deserialize<MetadataAccount>(payload);
            if (source == null) return null;
            var routed = await PlanExactAsync<IProviderMetadataCapability>(protocol, provider,
                ProviderCapabilityKind.Metadata, "protocol-metadata-read-account");
            if (routed.Candidate?.Context.Account is not { } account || account.AccountId != source.Id ||
                account.Revision != source.Revision) return null;
            return await ReadCachedMetadataAsync<T>(actor, provider, kind, externalId, detail, account);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            logger?.LogDebug("Could not read account metadata ({ErrorType})", error.GetType().Name);
            return null;
        }
    }

    private static string MetadataAccountKey(ProviderActorContext actor, string provider, ProviderResourceKind kind,
        string externalId, bool detail) => MetadataCacheKey(actor, provider, kind, externalId, detail) + ":account";

    private sealed record MetadataAccount(Guid Id, long Revision);

    private async Task<T?> ReadCachedMetadataAsync<T>(ProviderActorContext actor, string provider,
        ProviderResourceKind kind, string externalId, bool detail = false, ProviderAccountContext? account = null) where T : class
    {
        if (applicationCache?.IsEnabled != true) return null;
        try
        {
            var payload = await applicationCache.GetStringAsync(MetadataCacheKey(actor, provider, kind, externalId, detail, account));
            return payload == null ? null : JsonSerializer.Deserialize<T>(payload, MetadataJson);
        }
        catch (Exception error)
        {
            logger?.LogDebug("Could not read cached resource metadata ({ErrorType})", error.GetType().Name);
            return null;
        }
    }

    private async Task<Song?> ReadCachedSongAsync(ProviderActorContext actor, string provider, string externalId,
        ProviderAccountContext? account = null) =>
        await ReadCachedMetadataAsync<Song>(actor, provider, ProviderResourceKind.Track, externalId, true, account) ??
        await ReadCachedMetadataAsync<Song>(actor, provider, ProviderResourceKind.Track, externalId, false, account);

    internal static string MetadataCacheKey(ProviderActorContext actor, string provider,
        ProviderResourceKind kind, string externalId, bool detail = false, ProviderAccountContext? account = null) => CacheKeyBuilder.BuildProtocolMetadataKey(
            NormalizeProvider(provider), JsonSerializer.Serialize(new { Actor = actor, AccountId = account?.AccountId, AccountRevision = account?.Revision }), detail ? $"{kind}:detail" : kind.ToString(), externalId);
}

internal sealed record ProtocolCachedSongFacts(string? Title, string? Artist, string? Album,
    long? DurationMilliseconds, Guid? CanonicalRecordingId);
