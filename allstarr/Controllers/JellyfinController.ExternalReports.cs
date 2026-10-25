using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Services.Jellyfin;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class JellyfinController
{
    private JsonObject BuildExternalReport(JsonElement payload, string itemId, Song? song, string? deviceId)
    {
        var supplied = JsonNode.Parse(payload.GetRawText())!.AsObject();
        supplied["ItemId"] = itemId;
        var previous = string.IsNullOrWhiteSpace(deviceId) ? null : _sessionManager.GetExternalReport(PlaybackSessionKey(deviceId), includeStopped: true);
        var sameOccurrence = previous != null && JellyfinExternalPlaybackReport.SameOccurrence(previous, supplied);
        var report = sameOccurrence ? previous! : new JsonObject();
        if (!sameOccurrence && previous != null)
        {
            foreach (var field in new[] { "IsMuted", "VolumeLevel", "RepeatMode", "PlaybackOrder" })
                if (previous.ContainsKey(field)) report[field] = previous[field]?.DeepClone();
            var sessionId = JellyfinExternalPlaybackReport.Text(supplied, "SessionId");
            if (sessionId == null || sessionId == JellyfinExternalPlaybackReport.Text(previous, "SessionId"))
                foreach (var field in new[] { "SessionId", "NowPlayingQueue" })
                    if (previous.ContainsKey(field)) report[field] = previous[field]?.DeepClone();
        }
        report.Remove("EventName");
        foreach (var property in supplied)
        {
            if (property.Key is "Item" or "ItemName") continue;
            if (property.Key == "NowPlayingQueue" && property.Value == null) continue;
            report[property.Key] = property.Value?.DeepClone();
        }
        report["ItemId"] = itemId;
        if (song != null || report["Item"] == null)
            report["Item"] = JsonSerializer.SerializeToNode(BuildExternalPlaybackItem(itemId, song));
        report["PositionTicks"] = ParsePlaybackPositionTicks(payload) ?? report["PositionTicks"]?.GetValue<long>() ?? 0;
        report["IsPaused"] ??= false;
        report["IsMuted"] ??= false;
        report["CanSeek"] ??= true;
        report["PlayMethod"] ??= "DirectPlay";
        return report;
    }

    private bool IsNewExternalOccurrence(string deviceId, JsonElement payload)
    {
        var previous = _sessionManager.GetExternalReport(PlaybackSessionKey(deviceId));
        return previous != null && !JellyfinExternalPlaybackReport.SameOccurrence(previous,
            JsonNode.Parse(payload.GetRawText())!.AsObject());
    }

    private bool ObservePlaybackActivity(JsonElement payload, string? itemId, string? deviceId,
        JellyfinSessionManager.PlaybackReportReceipt receipt, bool stopped = false)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(itemId)) return false;
        var supplied = JsonNode.Parse(payload.GetRawText())!.AsObject();
        bool? paused = payload.TryGetProperty("IsPaused", out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? value.GetBoolean() : null;
        return _sessionManager.ObservePlaybackReport(PlaybackSessionKey(deviceId), receipt, itemId,
            ParsePlaybackPositionTicks(payload), paused, supplied, stopped);
    }

    private async Task<bool> ObserveExternalReportAsync(JsonObject report, PlaybackTransition transition, string? deviceId,
        JellyfinSessionManager.PlaybackReportReceipt receipt, bool stopped = false)
    {
        var signalType = transition is PlaybackTransition.Start or PlaybackTransition.InferredStart ? "start" :
            transition is PlaybackTransition.Stop or PlaybackTransition.InferredStop ? "stop" : null;
        if (signalType != null)
            await _cache.SetStringAsync(ExternalSignalCacheKey(signalType, deviceId,
                JellyfinExternalPlaybackReport.Text(report, "ItemId") + ":" + JellyfinExternalPlaybackReport.SignalSession(report)),
                "1", PlaybackSignalDedupeWindow);
        var accepted = !string.IsNullOrWhiteSpace(deviceId) &&
            _sessionManager.UpdateExternalReport(PlaybackSessionKey(deviceId), report, receipt, stopped);
        await QueuePlaybackSignalAsync(transition, JellyfinExternalPlaybackReport.Text(report, "ItemId"), deviceId,
            JellyfinExternalPlaybackReport.SignalSession(report), report["PositionTicks"]?.GetValue<long>());
        return accepted;
    }

    private string ExternalDedupeItem(string itemId, JsonElement payload, string? deviceId)
    {
        var current = JsonNode.Parse(payload.GetRawText())!.AsObject();
        var previous = string.IsNullOrWhiteSpace(deviceId) ? null : _sessionManager.GetExternalReport(PlaybackSessionKey(deviceId), includeStopped: true);
        if (previous != null && JellyfinExternalPlaybackReport.SameOccurrence(previous, current))
            foreach (var field in new[] { "PlaySessionId", "SessionId", "PlaylistItemId" })
                current[field] ??= previous[field]?.DeepClone();
        return itemId + ":" + JellyfinExternalPlaybackReport.SignalSession(current);
    }

    private string ExternalSignalCacheKey(string signalType, string? deviceId, string identity)
    {
        var context = HttpContext.RequireProtocolExecutionContext();
        return CacheKeyBuilder.BuildPlaybackSignalDedupeKey(context.BackendInstanceId,
            context.VerifiedBackendPrincipalId, signalType,
            string.IsNullOrWhiteSpace(deviceId) ? "unknown-device" : deviceId, identity);
    }

    private Task<bool> IsDuplicateExternalSignalAsync(string signalType, string? deviceId, string itemId, JsonElement payload) =>
        _cache.ExistsAsync(ExternalSignalCacheKey(signalType, deviceId, ExternalDedupeItem(itemId, payload, deviceId)));

    private bool IsStaleExternalStop(string? deviceId, string itemId, JsonElement payload)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;
        var tracked = _sessionManager.GetLastPlayingItemId(PlaybackSessionKey(deviceId));
        return tracked != null && (tracked != itemId || IsNewExternalOccurrence(deviceId, payload));
    }

    private async Task<IActionResult> ReadPlaybackSessionsAsync()
    {
        var endpoint = "Sessions" + Request.QueryString.Value;
        var upstream = await _proxyService.SendPassthroughResponseAsync(Request, endpoint, HttpContext.RequestAborted);
        if (upstream.IsSuccessStatusCode && upstream.Content.Headers.ContentType?.MediaType == "application/json")
        {
            try
            {
                var body = await upstream.Content.ReadAsStringAsync(HttpContext.RequestAborted);
                var context = HttpContext.RequireProtocolExecutionContext();
                var backend = context.BackendInstanceId;
                var projection = _sessionManager.ProjectExternalSessions(body, backend);
                if (projection != null)
                {
                    var serverId = await _proxyService.GetServerIdAsync(backend, HttpContext.RequestAborted);
                    projection = _sessionManager.ProjectExternalSessions(body, backend, serverId) ?? projection;
                }
                var factory = HttpContext.RequestServices.GetService<Microsoft.EntityFrameworkCore.IDbContextFactory<allstarr.Core.Storage.AllstarrDbContext>>();
                using var sessions = JsonDocument.Parse(projection ?? body);
                if (sessions.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var changed = false;
                    var rows = new List<string>();
                    foreach (var row in sessions.RootElement.EnumerateArray())
                    {
                        var raw = row.GetRawText();
                        if (row.ValueKind == JsonValueKind.Object && row.TryGetProperty("UserId", out var user) && user.ValueKind == JsonValueKind.String &&
                            row.TryGetProperty("NowPlayingItem", out var item) && item.ValueKind == JsonValueKind.Object &&
                            item.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.String)
                        {
                            var providerItem = SyntheticActivityPolicy.IsProviderItem(id.GetString());
                            if (!providerItem && row.TryGetProperty("DeviceId", out var device) && device.ValueKind == JsonValueKind.String &&
                                _sessionManager.GetExternalReport(new(backend, user.GetString()!, device.GetString()!), includeStopped: true) is { } report &&
                                JellyfinExternalPlaybackReport.Text(report, "ItemId") is { } tracked && SyntheticActivityPolicy.IsProviderItem(tracked))
                                providerItem = Guid.TryParse(id.GetString(), out var upstreamId) &&
                                    upstreamId == Guid.Parse(JellyfinExternalPlaybackReport.UpstreamId(tracked));
                            var synthetic = providerItem &&
                                (context.Principal is { IsSynthetic: true } principal && principal.BackendPrincipalId == user.GetString() ||
                                 factory != null && await SyntheticActivityPolicy.IsSyntheticPrincipalAsync(factory, "jellyfin", backend,
                                     user.GetString()!, HttpContext.RequestAborted));
                            if (synthetic)
                            {
                                var hidden = JsonNode.Parse(raw)!.AsObject();
                                hidden.Remove("NowPlayingItem");
                                hidden.Remove("PlayState");
                                hidden["NowPlayingQueue"] = new JsonArray();
                                raw = hidden.ToJsonString();
                                changed = true;
                            }
                        }
                        rows.Add(raw);
                    }
                    if (changed) projection = "[" + string.Join(',', rows) + "]";
                }
                if (projection != null)
                {
                    upstream.Content.Dispose();
                    upstream.Content = new StringContent(projection, Encoding.UTF8, "application/json");
                    upstream.Headers.ETag = null;
                    upstream.Headers.CacheControl = new() { Private = true, NoStore = true };
                }
            }
            catch (JsonException) { }
        }
        return new ProtocolRelayResponseResult(upstream);
    }
}
