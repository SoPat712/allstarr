using System.Text.Json;
using allstarr.Core.ClientApi;

namespace allstarr.Core.ClientEvents;

public interface IClientEventPublisher
{
    void Publish(Guid userId, ClientEvent value, string? deviceId = null);
}

public sealed record ClientEvent(string Type, string Key, string Json)
{
    public static ClientEvent Keep(string itemId, string state, double? progress = null) =>
        Create("keep.updated", itemId, progress is { } value && double.IsFinite(value)
            ? new { itemId, state, progress = Math.Clamp(value, 0, 1) } : (object)new { itemId, state });
    public static ClientEvent Match(string itemId, string state) => Create("match.updated", itemId, new { itemId, state });
    public static ClientEvent Lyrics(string itemId, object revision) => Create("lyrics.updated", itemId, new { itemId, revision });
    public static ClientEvent Mix(Guid mixId) => Create("mix.updated", mixId.ToString("D"), new { mixId });
    public static ClientEvent Releases(int count) => Create("releases.updated", "inbox", new { count });
    public static ClientEvent Playback(string itemId) => Create("playback.updated", itemId, new { itemId });
    private static ClientEvent Create(string type, string key, object data) => new(type, key,
        JsonSerializer.Serialize(data, ClientApiContract.JsonOptions));
}

public sealed class ClientEventOptions
{
    public int Capacity { get; init; } = 64;
    public int ConnectionsPerUser { get; init; } = 8;
    public int MaximumConnections { get; init; } = 256;
    public int EventsPerSecond { get; init; } = 10;
    public TimeSpan CoalesceWindow { get; init; } = TimeSpan.FromMilliseconds(100);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(25);
}
