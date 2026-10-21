using System.Collections.Concurrent;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;

namespace allstarr.Services.Subsonic;

public sealed record SubsonicPlaybackState(Song Song, string Occurrence, DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt, long PositionTicks, bool Stopped = false, bool Paused = false, double PlaybackRate = 1,
    string? SubmissionKey = null, string? StartSignalKey = null, DateTimeOffset? SubmittedAt = null,
    DateTimeOffset? StartObservedAt = null);

public sealed class SubsonicPlaybackSessions
{
    private const int MaximumSessions = 4096;
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private readonly ConcurrentDictionary<SessionKey, SubsonicPlaybackState> _sessions = new();
    private readonly object _gate = new();

    public SubsonicPlaybackState? Get(ProtocolExecutionContext context)
    {
        if (!context.CanRunUserScopedWork) return null;
        return _sessions.TryGetValue(Key(context), out var session) && session.UpdatedAt >= DateTimeOffset.UtcNow - Lifetime
            ? session : null;
    }

    public void Set(ProtocolExecutionContext context, SubsonicPlaybackState session)
    {
        if (!context.CanRunUserScopedWork) return;
        lock (_gate)
        {
            var cutoff = DateTimeOffset.UtcNow - Lifetime;
            foreach (var expired in _sessions.Where(pair => pair.Value.UpdatedAt < cutoff))
                _sessions.TryRemove(expired.Key, out _);
            _sessions[Key(context)] = session;
            foreach (var oldest in _sessions.OrderBy(pair => pair.Value.UpdatedAt).Take(Math.Max(0, _sessions.Count - MaximumSessions)))
                _sessions.TryRemove(oldest.Key, out _);
        }
    }

    public void Remove(ProtocolExecutionContext context)
    {
        if (context.CanRunUserScopedWork) _sessions.TryRemove(Key(context), out _);
    }

    private static SessionKey Key(ProtocolExecutionContext context) => new(context.BackendInstanceId,
        context.VerifiedBackendPrincipalId, context.Principal!.UserId, context.Client.ClientId, context.Client.DeviceId);

    private sealed record SessionKey(string Backend, string Principal, Guid Owner, string? Client, string? Device);
}
