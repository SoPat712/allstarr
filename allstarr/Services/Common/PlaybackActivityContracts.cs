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
    string? Device = null);

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

    Task<PlaybackTrackMetadata?> ResolveAsync(string itemId, ProtocolExecutionContext context, CancellationToken cancellationToken) =>
        ResolveAsync(itemId, cancellationToken);

    Task<PlaybackArtwork?> ResolveArtworkAsync(string itemId, ProtocolExecutionContext context, CancellationToken cancellationToken) =>
        ResolveArtworkAsync(itemId, cancellationToken);
}

public interface IPlaybackDeliveryActivitySource
{
    bool WasDelivered(Guid? userId, string itemId, string deviceId);
    PlaybackStreamSource? StreamFor(Guid? userId, string? deviceId, string itemId) => null;
}

public sealed record PlaybackStreamSource(
    string ProviderId,
    string ExternalId,
    Guid? AccountId,
    string Protocol,
    string BackendInstanceId,
    bool Cached,
    DateTimeOffset OpenedAt,
    string SelectionReason)
{
    private readonly long? size;
    private readonly ProviderMediaFormat? media;
    internal PlaybackFileFacts? FileFacts { get; init; }
    public long? Size { get => FileFacts != null ? FileFacts.Size : size; init => size = value; }
    public ProviderMediaFormat? Media { get => FileFacts != null ? FileFacts.Media : media; init => media = value; }
    public long? AccountRevision { get; init; }
    public Guid? ManagedFileId { get; init; }
    public string SeekRung { get; init; } = "sequential";
    internal object? ObservationToken { get; init; }

    public bool Matches(string protocol, string backendInstanceId) =>
        Protocol == protocol && BackendInstanceId == backendInstanceId;
}

public sealed class PlaybackDeliveryActivityStore : IPlaybackDeliveryActivitySource, IDisposable
{
    private static readonly TimeSpan Retention = TimeSpan.FromHours(1);
    private readonly object sourceGate = new();
    private readonly Microsoft.Extensions.Caching.Memory.MemoryCache _delivered = new(
        new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions
        {
            SizeLimit = 4_096
        });

    public void MarkDelivered(Guid userId, string itemId, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(itemId) && !string.IsNullOrWhiteSpace(deviceId))
        {
            Microsoft.Extensions.Caching.Memory.CacheExtensions.Set(
                _delivered,
                (userId, deviceId, StreamItemKey(itemId), "delivered"),
                true,
                new Microsoft.Extensions.Caching.Memory.MemoryCacheEntryOptions
                {
                    AbsoluteExpirationRelativeToNow = Retention,
                    Size = 1
                });
        }
    }

    public bool WasDelivered(Guid? userId, string itemId, string deviceId) =>
        userId is { } user &&
        _delivered.TryGetValue((user, deviceId, StreamItemKey(itemId), "delivered"), out _);

    public void StreamOpened(ProtocolExecutionContext context, string itemId,
        ProviderAudioQuality quality, ProtocolProviderStream stream)
    {
        if (context.Actor?.EffectiveUserId is not { } userId ||
            string.IsNullOrWhiteSpace(context.Client.DeviceId) || stream.ServingExternalId == null) return;
        stream.FileFacts.Size = stream.Response.StatusCode == System.Net.HttpStatusCode.PartialContent
            ? stream.Response.Content.Headers.ContentRange?.Length
            : stream.Response.Content.Headers.ContentLength;
        stream.FileFacts.Media = stream.Lease.Media;
        var source = new PlaybackStreamSource(stream.ServingProviderId, stream.ServingExternalId,
            stream.ServingAccountId, context.Protocol.ToString().ToLowerInvariant(),
            context.BackendInstanceId, stream.IsCached, DateTimeOffset.UtcNow,
            stream.SelectionReason)
        {
            FileFacts = stream.FileFacts,
            AccountRevision = stream.ServingAccountRevision,
            ManagedFileId = stream.ServingManagedFileId,
            ObservationToken = stream.ObservationToken,
            SeekRung = stream.IsCached
                ? stream.Lease.LeaseId == "managed-progressive-cache"
                    ? stream.Lease.SupportsByteRanges ? "progressive-cache" : "sequential-cache"
                    : "byte-range"
                : stream.Response.StatusCode == System.Net.HttpStatusCode.PartialContent &&
                    stream.Response.Content.Headers.ContentRange is { HasLength: true } actual &&
                    StreamByteRanges.HasValidContentRange(actual, stream.Response.Content.Headers.ContentLength)
                        ? "byte-range" : "sequential"
        };
        var key = (userId, context.Client.DeviceId, StreamItemKey(itemId));
        lock (sourceGate)
        {
            Set(key, source);
            Set((key, context.Protocol, context.BackendInstanceId, quality), source);
        }
    }

    public void SeekRungSelected(ProtocolExecutionContext context, string itemId,
        ProviderAudioQuality quality, ProtocolProviderStream stream, string rung)
    {
        if (context.Actor?.EffectiveUserId is not { } userId || string.IsNullOrWhiteSpace(context.Client.DeviceId)) return;
        var key = (userId, context.Client.DeviceId, StreamItemKey(itemId));
        var exactKey = (key, context.Protocol, context.BackendInstanceId, quality);
        lock (sourceGate)
        {
            var source = _delivered.Get<PlaybackStreamSource>(exactKey);
            if (source == null || !ReferenceEquals(source.ObservationToken, stream.ObservationToken) ||
                source.ProviderId != stream.ServingProviderId || source.ExternalId != stream.ServingExternalId ||
                source.AccountId != stream.ServingAccountId || source.AccountRevision != stream.ServingAccountRevision ||
                source.ManagedFileId != stream.ServingManagedFileId) return;
            // A duration hint must not replace proof of an available byte reader.
            if (source.SeekRung is "byte-range" or "progressive-cache") return;
            var updated = source with { SeekRung = rung };
            Set(exactKey, updated);
            if (_delivered.Get<PlaybackStreamSource>(key) == source) Set(key, updated);
        }
    }

    public PlaybackStreamSource? StreamFor(ProtocolExecutionContext context, string itemId,
        ProviderAudioQuality quality) => context.Actor?.EffectiveUserId is { } userId
        ? _delivered.Get<PlaybackStreamSource>(((userId, context.Client.DeviceId, StreamItemKey(itemId)),
            context.Protocol, context.BackendInstanceId, quality))
        : null;

    public PlaybackStreamSource? StreamFor(Guid? userId, string? deviceId, string itemId) =>
        userId is { } user && !string.IsNullOrWhiteSpace(deviceId)
            ? _delivered.Get<PlaybackStreamSource>((user, deviceId, StreamItemKey(itemId)))
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
