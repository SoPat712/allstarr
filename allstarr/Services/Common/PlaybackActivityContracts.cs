using allstarr.Core.Capabilities;
using allstarr.Core.Protocols;
using Microsoft.Extensions.Caching.Memory;

namespace allstarr.Services.Common;

public sealed record PlaybackActivityState(
    string DeviceId,
    string ItemId,
    long PositionTicks,
    DateTime LastActivity,
    Guid? UserId = null,
    string? BackendUserId = null,
    string? UserName = null,
    string? Client = null,
    string? Device = null,
    Guid? TenantId = null);

public sealed record PlaybackTrackMetadata(
    string Title,
    string Artist,
    string? Album,
    string? CoverArtUrl,
    int? DurationSeconds = null,
    string? AlbumArtist = null,
    string? RecordingMusicBrainzId = null,
    int? TrackNumber = null);

public sealed record PlaybackArtwork(byte[] Content, string ContentType);

public interface IPlaybackActivitySource
{
    IReadOnlyList<PlaybackActivityState> GetActivePlaybackStates(TimeSpan maxAge);
}

public interface IPlaybackMetadataResolver
{
    Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, CancellationToken cancellationToken);

    Task<PlaybackArtwork?> ResolveArtworkAsync(string itemId, CancellationToken cancellationToken);
}

public interface IPlaybackDeliveryActivitySource
{
    bool WasDelivered(string itemId, string deviceId);
    PlaybackStreamSource? StreamFor(Guid? tenantId, Guid? userId, string? deviceId, string itemId) => null;
}

public sealed record PlaybackStreamSource(
    string ProviderId,
    string ExternalId,
    Guid? AccountId,
    string Protocol,
    string BackendInstanceId,
    string? LibraryScopeId,
    bool Cached,
    DateTimeOffset OpenedAt,
    string SelectionReason)
{
    public bool Matches(string protocol, string backendInstanceId, string? libraryScopeId) =>
        Protocol == protocol && BackendInstanceId == backendInstanceId &&
        (LibraryScopeId == null || LibraryScopeId == libraryScopeId);
}

public sealed class PlaybackDeliveryActivityStore : IPlaybackDeliveryActivitySource, IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _delivered = new(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
        {
            SizeLimit = 4_096
        });

    public void MarkDelivered(string itemId, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(
                _delivered,
                $"{deviceId}\n{itemId}",
                true,
                new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = Retention,
                    Size = 1
                });
        }
    }

    public bool WasDelivered(string itemId, string deviceId) =>
        _delivered.TryGetValue($"{deviceId}\n{itemId}", out _);

    public void StreamOpened(ProtocolExecutionContext context, string itemId,
        ProviderAudioQuality quality, ProtocolProviderStream stream)
    {
        if (context.Actor?.EffectiveUserId is not { } userId ||
            string.IsNullOrWhiteSpace(context.Client.DeviceId) || stream.ServingExternalId == null) return;
        var source = new PlaybackStreamSource(stream.ServingProviderId, stream.ServingExternalId,
            stream.ServingAccountId, context.Protocol.ToString().ToLowerInvariant(),
            context.BackendInstanceId, context.LibraryScopeId, stream.IsCached, DateTimeOffset.UtcNow,
            stream.SelectionReason);
        var key = (context.Actor.TenantId, userId, context.Client.DeviceId, StreamItemKey(itemId));
        Set(key, source);
        Set((key, context.Protocol, context.BackendInstanceId, context.LibraryScopeId, quality), source);
    }

    public PlaybackStreamSource? StreamFor(ProtocolExecutionContext context, string itemId,
        ProviderAudioQuality quality) => context.Actor?.EffectiveUserId is { } userId
        ? _delivered.Get<PlaybackStreamSource>(((context.Actor.TenantId, userId, context.Client.DeviceId, StreamItemKey(itemId)),
            context.Protocol, context.BackendInstanceId, context.LibraryScopeId, quality))
        : null;

    public PlaybackStreamSource? StreamFor(Guid? tenantId, Guid? userId, string? deviceId, string itemId) =>
        tenantId is { } tenant && userId is { } user && !string.IsNullOrWhiteSpace(deviceId)
            ? _delivered.Get<PlaybackStreamSource>((tenant, user, deviceId, StreamItemKey(itemId)))
            : null;

    private static string StreamItemKey(string itemId)
    {
        var identity = ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId);
        if (identity == null) return itemId;
        var provider = identity.Value.Provider.ToLowerInvariant();
        if (provider == "applemusic") provider = "apple-download";
        return $"ext-{provider}-song-{identity.Value.ExternalId}";
    }

    private void Set(object key, PlaybackStreamSource source) =>
        _delivered.Set(key, source, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = Retention,
            Size = 1
        });

    public void Dispose() => _delivered.Dispose();
}
