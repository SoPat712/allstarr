using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using allstarr.Models.Scrobbling;
using allstarr.Models.Settings;

namespace allstarr.Services.Scrobbling;

/// <summary>
/// Orchestrates scrobbling across multiple services (Last.fm, ListenBrainz, etc.).
/// Manages playback sessions and determines when to scrobble based on listening rules.
/// </summary>
public class ScrobblingOrchestrator
{
    private readonly IEnumerable<IScrobblingService> _scrobblingServices;
    private readonly ScrobblingSettings _settings;
    private readonly ILogger<ScrobblingOrchestrator> _logger;
    private readonly ConcurrentDictionary<string, PlaybackSession> _sessions = new();
    private readonly Timer _cleanupTimer;
    
    public ScrobblingOrchestrator(
        IEnumerable<IScrobblingService> scrobblingServices,
        IOptions<ScrobblingSettings> settings,
        ILogger<ScrobblingOrchestrator> logger)
    {
        _scrobblingServices = scrobblingServices;
        _settings = settings.Value;
        _logger = logger;
        
        // Clean up stale sessions every 5 minutes
        _cleanupTimer = new Timer(CleanupStaleSessions, null, TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5));
        
        var enabledServices = _scrobblingServices.Where(s => s.IsEnabled).Select(s => s.ServiceName).ToList();
        if (enabledServices.Any())
        {
            _logger.LogInformation("🎵 Scrobbling orchestrator initialized with services: {Services}", 
                string.Join(", ", enabledServices));
        }
        else
        {
            _logger.LogInformation("Scrobbling orchestrator initialized (no services enabled)");
        }
    }
    
    /// <summary>
    /// Handles playback start - sends "Now Playing" to all enabled services.
    /// </summary>
    public async Task OnPlaybackStartAsync(string deviceId, ScrobbleTrack track)
    {
        if (!_settings.Enabled)
            return;

        var existingSession = FindSession(deviceId, track.Artist, track.Title);
        if (existingSession != null)
        {
            existingSession.LastActivity = DateTime.UtcNow;
            _logger.LogDebug(
                "Ignoring duplicate playback start for active session: {Artist} - {Track} (device: {DeviceId}, session: {SessionId})",
                track.Artist,
                track.Title,
                deviceId,
                existingSession.SessionId);
            return;
        }
        
        var sessionId = $"{deviceId}:{track.Artist}:{track.Title}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
        
        var session = new PlaybackSession
        {
            SessionId = sessionId,
            DeviceId = deviceId,
            Track = track with { Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
            StartTime = DateTime.UtcNow,
            LastPositionSeconds = 0,
            LastActivity = DateTime.UtcNow
        };
        
        _sessions[sessionId] = session;
        
        _logger.LogDebug("🎵 Playback started: {Artist} - {Track} (session: {SessionId})", 
            track.Artist, track.Title, sessionId);
        
        // Send "Now Playing" to all enabled services
        await SendNowPlayingAsync(session);
    }
    
    /// <summary>
    /// Handles playback progress - checks if track should be scrobbled.
    /// </summary>
    public async Task OnPlaybackProgressAsync(string deviceId, ScrobbleTrack track, int positionSeconds)
    {
        if (!_settings.Enabled)
            return;
        
        // Find the session for this track.
        // If we never saw a start event (client skipped it or metadata failed earlier),
        // recover by creating a session from the first progress event.
        var session = FindSession(deviceId, track.Artist, track.Title);
        
        if (session == null)
        {
            var inferredStartTime = DateTimeOffset.UtcNow.AddSeconds(-Math.Max(positionSeconds, 0)).ToUnixTimeSeconds();
            var recoveredTrack = track with { Timestamp = inferredStartTime };

            var sessionId = $"{deviceId}:{track.Artist}:{track.Title}:{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}";
            session = new PlaybackSession
            {
                SessionId = sessionId,
                DeviceId = deviceId,
                Track = recoveredTrack,
                StartTime = DateTime.UtcNow,
                LastPositionSeconds = 0,
                LastActivity = DateTime.UtcNow
            };

            _sessions[sessionId] = session;

            _logger.LogInformation(
                "Recovered missing scrobble session from progress: {Artist} - {Track} (position: {Position}s)",
                track.Artist,
                track.Title,
                positionSeconds);

            await SendNowPlayingAsync(session);
        }
        
        session.LastPositionSeconds = positionSeconds;
        session.LastActivity = DateTime.UtcNow;
        
        // Check if we should scrobble (and haven't already)
        if (!session.Scrobbled && session.ShouldScrobble())
        {
            _logger.LogDebug("✓ Scrobble threshold reached for: {Artist} - {Track} (position: {Position}s)", 
                track.Artist, track.Title, positionSeconds);
            await ScrobbleAsync(session);
        }
    }
    
    /// <summary>
    /// Handles playback stop - final chance to scrobble if threshold was met.
    /// </summary>
    public async Task OnPlaybackStopAsync(string deviceId, string artist, string title, int positionSeconds)
    {
        if (!_settings.Enabled)
            return;
        
        // Find and remove the session
        var session = FindSession(deviceId, artist, title);
        
        if (session == null)
        {
            _logger.LogDebug("No active session found for stop: {Artist} - {Track}", artist, title);
            return;
        }
        
        session.LastPositionSeconds = positionSeconds;
        
        // Final check if we should scrobble (and haven't already)
        if (!session.Scrobbled && session.ShouldScrobble())
        {
            _logger.LogDebug("✓ Scrobbling on stop: {Artist} - {Track} (position: {Position}s)", 
                artist, title, positionSeconds);
            await ScrobbleAsync(session);
        }
        else if (session.Scrobbled)
        {
            _logger.LogDebug("Track already scrobbled during playback: {Artist} - {Track}", artist, title);
        }
        else
        {
            _logger.LogDebug("Track not scrobbled (threshold not met): {Artist} - {Track} (position: {Position}s, duration: {Duration}s)", 
                artist, title, positionSeconds, session.Track.DurationSeconds);
        }
        
        // Remove session
        _sessions.TryRemove(session.SessionId, out _);
    }
    
    /// <summary>
    /// Sends "Now Playing" to all enabled services with retry logic.
    /// </summary>
    private async Task SendNowPlayingAsync(PlaybackSession session)
    {
        if (session.NowPlayingSent)
            return;
        
        var tasks = _scrobblingServices
            .Where(s => s.IsEnabled)
            .Select(async service =>
            {
                const int maxRetries = 3;
                var retryDelays = new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };
                
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        var result = await service.UpdateNowPlayingAsync(session.Track);
                        if (result.Success)
                        {
                            _logger.LogInformation("✓ Now Playing sent to {Service}: {Artist} - {Track}", 
                                service.ServiceName, session.Track.Artist, session.Track.Title);
                            return; // Success, exit retry loop
                        }
                        else if (result.Ignored)
                        {
                            return; // Ignored, don't retry
                        }
                        else if (result.ShouldRetry && attempt < maxRetries - 1)
                        {
                            _logger.LogWarning("⚠️ Now Playing failed for {Service}: {Error} - Retrying in {Delay}s (attempt {Attempt}/{Max})", 
                                service.ServiceName, result.ErrorMessage, retryDelays[attempt].TotalSeconds, attempt + 1, maxRetries);
                            await Task.Delay(retryDelays[attempt]);
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Now Playing failed for {Service}: {Error}", 
                                service.ServiceName, result.ErrorMessage);
                            return; // Don't retry or max retries reached
                        }
                    }
                    catch (Exception ex)
                    {
                        if (attempt < maxRetries - 1)
                        {
                            _logger.LogWarning(ex, "❌ Error sending Now Playing to {Service} - Retrying in {Delay}s (attempt {Attempt}/{Max})", 
                                service.ServiceName, retryDelays[attempt].TotalSeconds, attempt + 1, maxRetries);
                            await Task.Delay(retryDelays[attempt]);
                        }
                        else
                        {
                            _logger.LogError(ex, "❌ Error sending Now Playing to {Service} after {Max} attempts", 
                                service.ServiceName, maxRetries);
                        }
                    }
                }
            });
        
        await Task.WhenAll(tasks);
        session.NowPlayingSent = true;
    }
    
    /// <summary>
    /// Scrobbles a track to all enabled services with retry logic.
    /// Only retries on failure - prevents double scrobbling.
    /// </summary>
    private async Task ScrobbleAsync(PlaybackSession session)
    {
        if (session.Scrobbled)
        {
            _logger.LogDebug("Track already scrobbled, skipping: {Artist} - {Track}", 
                session.Track.Artist, session.Track.Title);
            return;
        }
        
        _logger.LogDebug("Scrobbling track to {Count} enabled services: {Artist} - {Track}", 
            _scrobblingServices.Count(s => s.IsEnabled), session.Track.Artist, session.Track.Title);
        
        var tasks = _scrobblingServices
            .Where(s => s.IsEnabled)
            .Select(async service =>
            {
                const int maxRetries = 3;
                var retryDelays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) };
                
                for (int attempt = 0; attempt < maxRetries; attempt++)
                {
                    try
                    {
                        var result = await service.ScrobbleAsync(session.Track);
                        if (result.Success && !result.Ignored)
                        {
                            _logger.LogInformation("✓ Scrobbled to {Service}: {Artist} - {Track}", 
                                service.ServiceName, session.Track.Artist, session.Track.Title);
                            return true; // Success, exit retry loop - prevents double scrobbling
                        }
                        else if (result.Ignored)
                        {
                            _logger.LogDebug("⊘ Scrobble skipped by {Service}: {Reason}", 
                                service.ServiceName, result.IgnoredReason);
                            return true; // Ignored, don't retry
                        }
                        else if (result.ShouldRetry && attempt < maxRetries - 1)
                        {
                            _logger.LogWarning("❌ Scrobble failed for {Service}: {Error} - Retrying in {Delay}s (attempt {Attempt}/{Max})", 
                                service.ServiceName, result.ErrorMessage, retryDelays[attempt].TotalSeconds, attempt + 1, maxRetries);
                            await Task.Delay(retryDelays[attempt]);
                        }
                        else
                        {
                            _logger.LogError("❌ Scrobble failed for {Service}: {Error} - No more retries", 
                                service.ServiceName, result.ErrorMessage);
                            return false; // Don't retry or max retries reached
                        }
                    }
                    catch (Exception ex)
                    {
                        if (attempt < maxRetries - 1)
                        {
                            _logger.LogWarning(ex, "❌ Error scrobbling to {Service} - Retrying in {Delay}s (attempt {Attempt}/{Max})", 
                                service.ServiceName, retryDelays[attempt].TotalSeconds, attempt + 1, maxRetries);
                            await Task.Delay(retryDelays[attempt]);
                        }
                        else
                        {
                            _logger.LogError(ex, "❌ Error scrobbling to {Service} after {Max} attempts", 
                                service.ServiceName, maxRetries);
                            return false;
                        }
                    }
                }

                return false;
            });
        
        var outcomes = await Task.WhenAll(tasks);
        if (outcomes.Any(s => s))
        {
            session.Scrobbled = true;
            _logger.LogDebug("Marked session as scrobbled: {SessionId}", session.SessionId);
        }
        else
        {
            _logger.LogWarning(
                "Scrobble failed for all enabled services: {Artist} - {Track}. Will retry on next progress/stop.",
                session.Track.Artist,
                session.Track.Title);
        }
    }

    private PlaybackSession? FindSession(string deviceId, string artist, string title)
    {
        return _sessions.Values
            .Where(s =>
                s.DeviceId == deviceId &&
                string.Equals(s.Track.Artist, artist, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(s.Track.Title, title, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(s => s.StartTime)
            .FirstOrDefault();
    }
    
    /// <summary>
    /// Cleans up stale sessions (inactive for more than 10 minutes).
    /// </summary>
    private void CleanupStaleSessions(object? state)
    {
        var now = DateTime.UtcNow;
        var staleThreshold = TimeSpan.FromMinutes(10);
        
        var staleSessions = _sessions.Where(kvp => now - kvp.Value.LastActivity > staleThreshold).ToList();
        
        foreach (var stale in staleSessions)
        {
            _logger.LogDebug("🧹 Removing stale scrobbling session: {SessionId}", stale.Key);
            _sessions.TryRemove(stale.Key, out _);
        }
        
        if (staleSessions.Any())
        {
            _logger.LogDebug("Cleaned up {Count} stale scrobbling sessions", staleSessions.Count);
        }
    }
    
    /// <summary>
    /// Gets information about active scrobbling sessions (for debugging).
    /// </summary>
    public object GetSessionsInfo()
    {
        var now = DateTime.UtcNow;
        var sessions = _sessions.Values.Select(s => new
        {
            SessionId = s.SessionId,
            DeviceId = s.DeviceId,
            Artist = s.Track.Artist,
            Track = s.Track.Title,
            Duration = s.Track.DurationSeconds,
            Position = s.LastPositionSeconds,
            StartTime = s.StartTime,
            ElapsedMinutes = Math.Round((now - s.StartTime).TotalMinutes, 1),
            NowPlayingSent = s.NowPlayingSent,
            Scrobbled = s.Scrobbled,
            ShouldScrobble = s.ShouldScrobble()
        }).ToList();
        
        return new
        {
            TotalSessions = sessions.Count,
            ScrobbledSessions = sessions.Count(s => s.Scrobbled),
            PendingSessions = sessions.Count(s => !s.Scrobbled),
            Sessions = sessions.OrderByDescending(s => s.StartTime)
        };
    }
}
