using allstarr.Services.Common;

namespace allstarr.Services.Jellyfin;

public sealed class JellyfinPlaybackActivitySource(JellyfinSessionManager sessionManager)
    : IPlaybackActivitySource
{
    public IReadOnlyList<PlaybackActivityState> GetActivePlaybackStates(TimeSpan maxAge)
    {
        return sessionManager.GetActivePlaybackStates(maxAge)
            .Select(state => new PlaybackActivityState(
                state.DeviceId,
                state.ItemId,
                state.PositionTicks,
                state.LastActivity))
            .ToList();
    }
}
