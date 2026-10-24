using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using allstarr.Services.Common;
using allstarr.Core.Playback;
using Microsoft.AspNetCore.WebUtilities;

namespace allstarr.Core.Health;

public sealed class HealthSmokeProtocolProbe(HttpClient client, TimeProvider time)
{
    public static readonly string[] StepNames = ["search", "playback", "seek", "now-playing", "scrobble", "heart", "unheart",
        "playlist-create", "playlist-add", "playlist-remove", "playlist-delete", "download", "lyrics", "cleanup"];

    public async Task<IReadOnlyList<HealthSmokeStep>> RunAsync(HealthSmokeProbeContext context, CancellationToken cancellationToken)
    {
        var steps = new List<HealthSmokeStep>();
        var artifacts = new HealthSmokeArtifacts(PlaylistName: $"[allstarr-test] Health {context.RunId:N}");
        long? total = null;
        double? firstByteMilliseconds = null;
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overall.CancelAfter(TimeSpan.FromMinutes(3));
        var token = overall.Token;
        try
        {
            await SaveAsync(artifacts, token);
            var searched = await StepAsync("search", async ct =>
            {
                var result = IsJellyfin(context)
                    ? await JsonAsync(context, HttpMethod.Get, "/Items", new() { ["SearchTerm"] = context.Query, ["IncludeItemTypes"] = "Audio", ["Recursive"] = "true", ["Limit"] = "50", ["userId"] = context.PrincipalId }, null, ct)
                    : await JsonAsync(context, HttpMethod.Post, "search3", new() { ["query"] = context.Query, ["songCount"] = "50", ["artistCount"] = "0", ["albumCount"] = "0" }, null, ct);
                var songs = (IsJellyfin(context) ? result?["Items"] : result?["searchResult3"]?["song"]) as JsonArray;
                var item = songs?.Select(row => row?[IsJellyfin(context) ? "Id" : "id"]?.GetValue<string>())
                    .FirstOrDefault(id => id != null && context.EligibleItem(id));
                if (item == null) throw new ProbeFailure("no_eligible_song");
                await SaveAsync(artifacts with { ItemId = item }, ct);
            });
            var itemId = artifacts.ItemId;
            foreach (var name in StepNames.Skip(1).Where(name => name != "cleanup"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (token.IsCancellationRequested)
                {
                    steps.Add(new(name, "skipped", 0, "check_timeout"));
                    continue;
                }
                if (!searched || itemId == null)
                {
                    steps.Add(new(name, "skipped", 0, "search_required"));
                    continue;
                }
                if (name is "playlist-add" or "playlist-remove" or "playlist-delete" && artifacts.PlaylistId == null)
                {
                    steps.Add(new(name, "skipped", 0, "playlist_required"));
                    continue;
                }
                await StepAsync(name, async ct =>
                {
                    var encoded = Uri.EscapeDataString(itemId);
                    switch (name)
                    {
                        case "playback":
                            var audio = await AudioAsync(context, encoded, false, 0, ct);
                            total = audio.Total;
                            firstByteMilliseconds = audio.FirstByteMilliseconds;
                            break;
                        case "seek":
                            if (total is not > 16384) throw new ProbeFailure("range_unavailable");
                            await AudioAsync(context, encoded, false, Math.Min(total.Value - 8192, total.Value * 4 / 5), ct);
                            break;
                        case "now-playing":
                            await SaveAsync(artifacts with { Playing = true }, ct);
                            await ReportAsync(context, itemId, false, ct);
                            break;
                        case "scrobble":
                            await ReportAsync(context, itemId, true, ct);
                            await SaveAsync(artifacts with { Playing = false }, ct);
                            break;
                        case "heart":
                            var detail = await DetailAsync(context, itemId, ct);
                            var original = ReadHeart(context, detail);
                            await SaveAsync(artifacts with { OriginalHeart = original }, ct);
                            await HeartAsync(context, itemId, true, ct);
                            await VerifyHeartAsync(context, itemId, true, ct);
                            break;
                        case "unheart":
                            if (artifacts.OriginalHeart == null) throw new ProbeFailure("heart_required");
                            await HeartAsync(context, itemId, false, ct);
                            await VerifyHeartAsync(context, itemId, false, ct);
                            break;
                        case "playlist-create":
                            var created = IsJellyfin(context)
                                ? await JsonAsync(context, HttpMethod.Post, "/Playlists", null,
                                    new { Name = artifacts.PlaylistName, UserId = context.PrincipalId, MediaType = "Audio", Ids = Array.Empty<string>() }, ct)
                                : await JsonAsync(context, HttpMethod.Post, "createPlaylist", new() { ["name"] = artifacts.PlaylistName! }, null, ct);
                            var id = (IsJellyfin(context) ? created?["Id"] : created?["playlist"]?["id"])?.GetValue<string>();
                            if (string.IsNullOrWhiteSpace(id) || id.Length > 500) throw new ProbeFailure("playlist_id_missing");
                            await SaveAsync(artifacts with { PlaylistId = id }, ct);
                            break;
                        case "playlist-add":
                            await PlaylistChangeAsync(context, artifacts.PlaylistId!, itemId, false, ct);
                            await VerifyPlaylistAsync(context, artifacts.PlaylistId!, itemId, true, ct);
                            break;
                        case "playlist-remove":
                            await PlaylistChangeAsync(context, artifacts.PlaylistId!, itemId, true, ct);
                            await VerifyPlaylistAsync(context, artifacts.PlaylistId!, itemId, false, ct);
                            break;
                        case "playlist-delete":
                            await DeletePlaylistAsync(context, artifacts.PlaylistId!, ct);
                            await SaveAsync(artifacts with { PlaylistId = null, PlaylistName = null }, ct);
                            break;
                        case "download":
                            await AudioAsync(context, encoded, true, 0, ct);
                            break;
                        case "lyrics":
                            var lyrics = IsJellyfin(context)
                                ? await JsonAsync(context, HttpMethod.Get, $"/Audio/{encoded}/Lyrics", null, null, ct)
                                : await JsonAsync(context, HttpMethod.Post, "getLyricsBySongId", new() { ["id"] = itemId }, null, ct);
                            if (!HasLyrics(lyrics)) throw new ProbeFailure("lyrics_missing");
                            break;
                    }
                }, name == "playback" ? context.FirstByteBudgetSeconds + 15 : name == "lyrics" ? 30 : 15);
            }
        }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var started = time.GetTimestamp();
            try
            {
                await CleanupAsync(context, artifacts, cleanup.Token);
                steps.Add(new("cleanup", "passed", Elapsed(started)));
            }
            catch (Exception error) when (error is ProbeFailure or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            { steps.Add(new("cleanup", "failed", Elapsed(started), Reason(error))); }
        }
        return steps;

        async Task SaveAsync(HealthSmokeArtifacts value, CancellationToken ct)
        {
            await context.SaveArtifacts(value, ct);
            artifacts = value;
        }

        async Task<bool> StepAsync(string name, Func<CancellationToken, Task> execute, int seconds = 15)
        {
            var started = time.GetTimestamp();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(seconds));
            try
            {
                await execute(deadline.Token);
                steps.Add(new(name, "passed", name == "playback" && firstByteMilliseconds is { } measured ? measured : Elapsed(started)));
                return true;
            }
            catch (Exception error) when (error is ProbeFailure or HttpRequestException or OperationCanceledException or System.Text.Json.JsonException)
            {
                steps.Add(new(name, "failed", Elapsed(started), Reason(error)));
                cancellationToken.ThrowIfCancellationRequested();
                return false;
            }
        }
    }

    public async Task CleanupAsync(HealthSmokeProbeContext context, HealthSmokeArtifacts artifacts, CancellationToken token)
    {
        if (artifacts.ItemId != null && !SyntheticActivityPolicy.IsProviderItem(artifacts.ItemId) ||
            (artifacts.PlaylistId != null || artifacts.PlaylistName != null) &&
            artifacts.PlaylistName != $"[allstarr-test] Health {context.RunId:N}")
            throw new ProbeFailure("cleanup_scope_invalid");
        var failed = false;
        if (artifacts.ItemId != null && artifacts.OriginalHeart is { } original)
        {
            try { await HeartAsync(context, artifacts.ItemId, original, token); await VerifyHeartAsync(context, artifacts.ItemId, original, token); }
            catch (Exception error) when (error is ProbeFailure or HttpRequestException) { failed = true; }
        }
        if (artifacts.ItemId != null && artifacts.Playing)
        {
            try { await ReportAsync(context, artifacts.ItemId, true, token); }
            catch (Exception error) when (error is ProbeFailure or HttpRequestException) { failed = true; }
        }
        var playlistId = artifacts.PlaylistId;
        if (playlistId == null && artifacts.PlaylistName != null)
        {
            var found = IsJellyfin(context)
                ? await JsonAsync(context, HttpMethod.Get, "/Items", new() { ["IncludeItemTypes"] = "Playlist", ["SearchTerm"] = artifacts.PlaylistName, ["userId"] = context.PrincipalId }, null, token)
                : await JsonAsync(context, HttpMethod.Post, "getPlaylists", new() { ["query"] = artifacts.PlaylistName }, null, token);
            if (IsJellyfin(context) ? found?["Items"] is not JsonArray : found?["playlists"] is not JsonObject ||
                found["playlists"]?["playlist"] is { } listed && listed is not JsonArray)
                throw new ProbeFailure("invalid_response");
            var playlists = (IsJellyfin(context) ? found?["Items"] : found?["playlists"]?["playlist"]) as JsonArray;
            var matches = playlists?.Where(item => item?[IsJellyfin(context) ? "Name" : "name"]?.GetValue<string>() == artifacts.PlaylistName).ToArray() ?? [];
            if (matches.Length > 1) throw new ProbeFailure("cleanup_scope_invalid");
            playlistId = matches.SingleOrDefault()?[IsJellyfin(context) ? "Id" : "id"]?.GetValue<string>();
        }
        if (playlistId != null)
        {
            try { await DeletePlaylistAsync(context, playlistId, token); }
            catch (ProbeFailure error) when (error.Code is "http_404" or "protocol_error_70") { }
            catch (Exception error) when (error is ProbeFailure or HttpRequestException) { failed = true; }
        }
        if (failed) throw new ProbeFailure("cleanup_incomplete");
        await context.SaveArtifacts(new(), token);
    }

    private async Task<(long? Total, double FirstByteMilliseconds)> AudioAsync(HealthSmokeProbeContext context, string item, bool download, long start, CancellationToken token)
    {
        var started = time.GetTimestamp();
        using var firstByteDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        firstByteDeadline.CancelAfter(TimeSpan.FromSeconds(context.FirstByteBudgetSeconds));
        var path = IsJellyfin(context) ? download ? $"/Items/{item}/Download" : $"/Audio/{item}/stream?static=true" : download ? "download" : "stream";
        using var request = Request(context, HttpMethod.Get, path, IsJellyfin(context) ? null : new() { ["id"] = Uri.UnescapeDataString(item), ["format"] = "raw" });
        request.Headers.Range = new RangeHeaderValue(start, start + 8191);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, firstByteDeadline.Token);
        CheckStatus(response);
        var range = response.Content.Headers.ContentRange;
        if (start > 0 && (response.StatusCode != HttpStatusCode.PartialContent || range?.From != start || range.Length is not > 0))
            throw new ProbeFailure("invalid_range");
        var type = response.Content.Headers.ContentType?.MediaType;
        if (type == null || !(type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) || type == "application/octet-stream"))
            throw new ProbeFailure("invalid_audio");
        await using var stream = await response.Content.ReadAsStreamAsync(firstByteDeadline.Token);
        var prefix = new byte[8192];
        var length = await stream.ReadAsync(prefix, firstByteDeadline.Token);
        var firstByteMilliseconds = Elapsed(started);
        firstByteDeadline.CancelAfter(Timeout.InfiniteTimeSpan);
        using var prefixDeadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        prefixDeadline.CancelAfter(TimeSpan.FromSeconds(15));
        while (length < prefix.Length)
        {
            var read = await stream.ReadAsync(prefix.AsMemory(length), prefixDeadline.Token);
            if (read == 0) break;
            length += read;
        }
        var text = Encoding.UTF8.GetString(prefix, 0, Math.Min(length, 128)).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (length == 0 || text.StartsWith('<') || text.StartsWith('{') || text.StartsWith('[')) throw new ProbeFailure("invalid_audio");
        if (length < prefix.Length) throw new ProbeFailure("truncated_audio");
        return (range?.Length ?? response.Content.Headers.ContentLength, firstByteMilliseconds);
    }

    private Task<JsonNode?> DetailAsync(HealthSmokeProbeContext context, string item, CancellationToken token) => IsJellyfin(context)
        ? JsonAsync(context, HttpMethod.Get, $"/Users/{Uri.EscapeDataString(context.PrincipalId)}/Items/{Uri.EscapeDataString(item)}", null, null, token)
        : JsonAsync(context, HttpMethod.Post, "getSong", new() { ["id"] = item }, null, token);

    private Task<JsonNode?> HeartAsync(HealthSmokeProbeContext context, string item, bool hearted, CancellationToken token) => IsJellyfin(context)
        ? JsonAsync(context, hearted ? HttpMethod.Post : HttpMethod.Delete, $"/Users/{Uri.EscapeDataString(context.PrincipalId)}/FavoriteItems/{Uri.EscapeDataString(item)}", null, null, token)
        : JsonAsync(context, HttpMethod.Post, hearted ? "star" : "unstar", new() { ["id"] = item }, null, token);

    private async Task VerifyHeartAsync(HealthSmokeProbeContext context, string item, bool expected, CancellationToken token)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var detail = await DetailAsync(context, item, token);
            var actual = ReadHeart(context, detail);
            if (actual == expected) return;
            await Task.Delay(TimeSpan.FromMilliseconds(200), time, token);
        }
        throw new ProbeFailure("favorite_state_mismatch");
    }

    private static bool ReadHeart(HealthSmokeProbeContext context, JsonNode? value)
    {
        if (IsJellyfin(context))
        {
            if (value?["UserData"]?["IsFavorite"] is JsonValue favorite && favorite.TryGetValue<bool>(out var hearted)) return hearted;
        }
        else if (value?["song"] is JsonObject song) return song["starred"] != null;
        throw new ProbeFailure("invalid_response");
    }

    private Task<JsonNode?> ReportAsync(HealthSmokeProbeContext context, string item, bool completed, CancellationToken token) => IsJellyfin(context)
        ? JsonAsync(context, HttpMethod.Post, completed ? "/Sessions/Playing/Stopped" : "/Sessions/Playing", null,
            new { ItemId = item, PlaySessionId = context.RunId.ToString("N"), PositionTicks = completed ? TimeSpan.FromMinutes(3).Ticks : 0, CanSeek = true, IsPaused = false }, token)
        : JsonAsync(context, HttpMethod.Post, "scrobble", new() { ["id"] = item, ["submission"] = completed ? "true" : "false", ["time"] = time.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture) }, null, token);

    private async Task<JsonNode?> PlaylistAsync(HealthSmokeProbeContext context, string id, CancellationToken token)
    {
        var value = IsJellyfin(context)
            ? await JsonAsync(context, HttpMethod.Get, $"/Playlists/{Uri.EscapeDataString(id)}/Items", new() { ["UserId"] = context.PrincipalId, ["Limit"] = "10" }, null, token)
            : await JsonAsync(context, HttpMethod.Post, "getPlaylist", new() { ["id"] = id }, null, token);
        if (IsJellyfin(context) ? value?["Items"] is not JsonArray : value?["playlist"] is not JsonObject ||
            value["playlist"]?["entry"] is { } entries && entries is not JsonArray)
            throw new ProbeFailure("invalid_response");
        return value;
    }

    private async Task PlaylistChangeAsync(HealthSmokeProbeContext context, string id, string item, bool remove, CancellationToken token)
    {
        if (IsJellyfin(context))
        {
            var entries = remove ? (await PlaylistAsync(context, id, token))?["Items"] as JsonArray : null;
            var entry = entries?.FirstOrDefault(row => row?["Id"]?.GetValue<string>() == item)?["PlaylistItemId"]?.GetValue<string>();
            if (remove && entry == null) throw new ProbeFailure("playlist_entry_missing");
            await JsonAsync(context, remove ? HttpMethod.Delete : HttpMethod.Post, $"/Playlists/{Uri.EscapeDataString(id)}/Items",
                new() { [remove ? "EntryIds" : "Ids"] = remove ? entry! : item, ["UserId"] = context.PrincipalId }, null, token);
        }
        else await JsonAsync(context, HttpMethod.Post, "updatePlaylist", new() { ["playlistId"] = id, [remove ? "songIndexToRemove" : "songIdToAdd"] = remove ? "0" : item }, null, token);
    }

    private async Task VerifyPlaylistAsync(HealthSmokeProbeContext context, string id, string item, bool expected, CancellationToken token)
    {
        var value = await PlaylistAsync(context, id, token);
        var rows = (IsJellyfin(context) ? value?["Items"] : value?["playlist"]?["entry"]) as JsonArray;
        if ((rows?.Any(row => row?[IsJellyfin(context) ? "Id" : "id"]?.GetValue<string>() == item) == true) != expected)
            throw new ProbeFailure("playlist_state_mismatch");
    }

    private Task<JsonNode?> DeletePlaylistAsync(HealthSmokeProbeContext context, string id, CancellationToken token) => IsJellyfin(context)
        ? JsonAsync(context, HttpMethod.Delete, $"/Items/{Uri.EscapeDataString(id)}", null, null, token)
        : JsonAsync(context, HttpMethod.Post, "deletePlaylist", new() { ["id"] = id }, null, token);

    private async Task<JsonNode?> JsonAsync(HealthSmokeProbeContext context, HttpMethod method, string path,
        Dictionary<string, string>? values, object? body, CancellationToken token)
    {
        using var request = Request(context, method, path, values);
        if (body != null) request.Content = JsonContent.Create(body);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        CheckStatus(response);
        if (response.StatusCode == HttpStatusCode.NoContent || response.Content.Headers.ContentLength == 0) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, token);
            if (read == 0) break;
            if (bytes.Length + read > 2 * 1024 * 1024) throw new ProbeFailure("response_too_large");
            bytes.Write(buffer, 0, read);
        }
        if (bytes.Length == 0) return null;
        var json = JsonNode.Parse(bytes.ToArray());
        if (IsJellyfin(context)) return json;
        var result = json?["subsonic-response"];
        if (result?["status"]?.GetValue<string>() != "ok")
            throw new ProbeFailure(result?["error"]?["code"]?.ToString() == "70" ? "protocol_error_70" : "protocol_error");
        return result;
    }

    private static HttpRequestMessage Request(HealthSmokeProbeContext context, HttpMethod method, string path, Dictionary<string, string>? values)
    {
        values ??= [];
        var jellyfin = IsJellyfin(context);
        if (!jellyfin)
        {
            path = "/rest/" + path + ".view";
            values["u"] = context.Credentials.UserName;
            values["p"] = context.Credentials.Password;
            values["v"] = "1.16.1";
            values["c"] = "allstarr-health";
            values["f"] = "json";
            method = HttpMethod.Post;
        }
        var request = new HttpRequestMessage(method, jellyfin ? QueryHelpers.AddQueryString(path, values!) : path);
        if (jellyfin) request.Headers.TryAddWithoutValidation("Authorization", AuthHeaderHelper.CreateAuthHeader(context.Credentials.AccessToken, "AllstarrHealth", "Health check", "health-" + context.RunId.ToString("N"), "1"));
        else request.Content = new FormUrlEncodedContent(values);
        return request;
    }

    private static bool HasLyrics(JsonNode? value) => value switch
    {
        JsonArray array => array.Any(HasLyrics),
        JsonObject obj => obj.Any(pair => pair.Key is "Text" or "value" && pair.Value is JsonValue text && text.TryGetValue<string>(out var line) && !string.IsNullOrWhiteSpace(line) || pair.Key is "Lyrics" or "lyricsList" or "structuredLyrics" or "line" && HasLyrics(pair.Value)),
        _ => false
    };

    private double Elapsed(long started) => Math.Round(time.GetElapsedTime(started).TotalMilliseconds, 1);
    private static bool IsJellyfin(HealthSmokeProbeContext context) => context.Protocol.Equals("Jellyfin", StringComparison.OrdinalIgnoreCase);
    private static void CheckStatus(HttpResponseMessage response)
    { if (!response.IsSuccessStatusCode) throw new ProbeFailure("http_" + (int)response.StatusCode); }
    private static string Reason(Exception error) => error is ProbeFailure failure ? failure.Code
        : error is OperationCanceledException ? "timeout" : error is System.Text.Json.JsonException ? "invalid_response" : "transport_failure";
    private sealed class ProbeFailure(string code) : Exception { public string Code { get; } = code; }
}
