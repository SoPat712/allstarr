using System.Text.Json;
using System.Text.Json.Nodes;

namespace allstarr.Services.Jellyfin;

public partial class JellyfinSessionManager
{
    internal JsonObject? GetExternalReport(JellyfinSessionKey key, bool includeStopped = false)
    {
        if (!_sessions.TryGetValue(key, out var session)) return null;
        lock (session.SyncRoot)
            return includeStopped || session.LastPlayingItemId != null ? (JsonObject?)session.ExternalReport?.DeepClone() : null;
    }

    internal bool UpdateExternalReport(JellyfinSessionKey key, JsonObject report,
        PlaybackReportReceipt? receipt = null, bool stopped = false)
    {
        if (!_sessions.TryGetValue(key, out var session)) return false;
        lock (session.SyncRoot)
        {
            var current = receipt ?? CapturePlaybackReport();
            if (!CanApplyPlaybackReceipt(session, current)) return false;
            if (stopped && session.HomeObservation is { } observed)
            {
                if (!JellyfinExternalPlaybackReport.SameOccurrence(ObservationIdentity(observed), report)) return false;
                session.HomeObservation = observed with { Receipt = current, Stopped = true };
            }
            if (session.LastPlayingItemId == null || session.ExternalReport == null ||
                !JellyfinExternalPlaybackReport.SameOccurrence(session.ExternalReport, report))
            {
                session.LastExplicitStopItemId = null;
                session.LastExplicitStopAtUtc = null;
            }
            session.ExternalReport = (JsonObject)report.DeepClone();
            session.LastPlayingItemId = stopped ? null : JellyfinExternalPlaybackReport.Text(report, "ItemId");
            session.LastPlayingPositionTicks = stopped ? null : report["PositionTicks"]?.GetValue<long>();
            session.PlaybackSequence = current.Sequence;
            session.LastActivity = DateTime.UtcNow;
            return true;
        }
    }

    internal string? ProjectExternalSessions(string body, string backendInstanceId, string? serverId = null)
    {
        using var document = JsonDocument.Parse(body);
        if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
        var changed = false;
        var rows = new List<string>();
        foreach (var row in document.RootElement.EnumerateArray())
        {
            var raw = row.GetRawText();
            if (row.ValueKind == JsonValueKind.Object &&
                row.TryGetProperty("DeviceId", out var device) && device.ValueKind == JsonValueKind.String &&
                row.TryGetProperty("UserId", out var user) && user.ValueKind == JsonValueKind.String &&
                _sessions.TryGetValue(new(backendInstanceId, user.GetString()!, device.GetString()!), out var session))
            {
                lock (session.SyncRoot)
                {
                    var report = session.ExternalReport;
                    var id = report == null ? null : JellyfinExternalPlaybackReport.Text(report, "ItemId");
                    if (id != null && session.LastActivity > DateTime.UtcNow - TimeSpan.FromMinutes(5) &&
                        row.TryGetProperty("NowPlayingItem", out var item) && item.ValueKind == JsonValueKind.Object &&
                        item.TryGetProperty("Id", out var playingId) && playingId.ValueKind == JsonValueKind.String &&
                        (playingId.GetString() == id || Guid.TryParse(playingId.GetString(), out var upstreamId) &&
                         upstreamId == Guid.Parse(JellyfinExternalPlaybackReport.UpstreamId(id))))
                    {
                        var projected = JsonNode.Parse(raw)!.AsObject();
                        if (session.LastPlayingItemId == null)
                        {
                            projected.Remove("NowPlayingItem");
                            projected["NowPlayingQueue"] = new JsonArray();
                        }
                        else
                        {
                            projected["NowPlayingItem"] = report!["Item"]?.DeepClone();
                            if (serverId != null) projected["NowPlayingItem"]!["ServerId"] = serverId;
                            var state = projected["PlayState"] as JsonObject ?? new JsonObject();
                            foreach (var field in new[] { "PositionTicks", "CanSeek", "IsPaused", "IsMuted", "VolumeLevel",
                                         "AudioStreamIndex", "SubtitleStreamIndex", "MediaSourceId", "PlayMethod", "RepeatMode",
                                         "PlaybackOrder", "LiveStreamId" })
                                if (report.ContainsKey(field)) state[field] = report[field]?.DeepClone();
                            projected["PlayState"] = state;
                            foreach (var field in new[] { "NowPlayingQueue", "PlaylistItemId", "PlaySessionId", "SessionId", "PlaybackStartTimeTicks" })
                                if (report.ContainsKey(field)) projected[field] = report[field]?.DeepClone();
                        }
                        raw = projected.ToJsonString();
                        changed = true;
                    }
                }
            }
            rows.Add(raw);
        }
        return changed ? "[" + string.Join(',', rows) + "]" : null;
    }
}
