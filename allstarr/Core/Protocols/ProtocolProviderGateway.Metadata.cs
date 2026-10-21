using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Domain;
using allstarr.Services.Common;

namespace allstarr.Core.Protocols;

public sealed partial class ProtocolProviderGateway
{
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
        try { return JsonSerializer.Deserialize<T>(payload); }
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
        }
    }

    private async Task CacheMetadataAsync<T>(ProviderExecutionContext context, ProviderResourceKind kind,
        string externalId, T value, bool detail = false) where T : class
    {
        if (applicationCache?.IsEnabled != true) return;
        try
        {
            await applicationCache.SetStringAsync(MetadataCacheKey(context.Actor, context.ProviderId, kind, externalId, detail, context.Account),
                JsonSerializer.Serialize(value), TimeSpan.FromMinutes(5));
            if (context.Account is { } account && kind is ProviderResourceKind.Album or ProviderResourceKind.Artist)
                await applicationCache.SetStringAsync(MetadataAccountKey(context.Actor, context.ProviderId, kind, externalId, detail),
                    JsonSerializer.Serialize(new MetadataAccount(account.AccountId, account.Revision)), TimeSpan.FromMinutes(5));
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
            return payload == null ? null : JsonSerializer.Deserialize<T>(payload);
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

    private static string MetadataCacheKey(ProviderActorContext actor, string provider,
        ProviderResourceKind kind, string externalId, bool detail = false, ProviderAccountContext? account = null) => CacheKeyBuilder.BuildProtocolMetadataKey(
            NormalizeProvider(provider), JsonSerializer.Serialize(new { Actor = actor, AccountId = account?.AccountId, AccountRevision = account?.Revision }), detail ? $"{kind}:detail" : kind.ToString(), externalId);
}
