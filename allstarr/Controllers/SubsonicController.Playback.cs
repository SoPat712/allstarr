using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Playback;
using allstarr.Core.Protocols.Subsonic;
using allstarr.Models.Domain;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private bool IsExternalTrack(string itemId)
    {
        var parsed = _localLibraryService.ParseExternalId(itemId);
        return parsed.isExternal && parsed.type == "song";
    }

    private static bool AcceptedPlaybackResponse(SubsonicProxyResponse response)
    {
        if (!response.IsSuccessStatusCode || response.Body.Length == 0) return false;
        try
        {
            if (response.Body.FirstOrDefault(value => !char.IsWhiteSpace((char)value)) == '<')
            {
                var root = XDocument.Parse(Encoding.UTF8.GetString(response.Body)).Root;
                return root?.Name.LocalName == "subsonic-response" && root.Attribute("status")?.Value == "ok";
            }
            using var document = JsonDocument.Parse(response.Body);
            return document.RootElement.TryGetProperty("subsonic-response", out var envelope) &&
                   envelope.TryGetProperty("status", out var status) && status.GetString() == "ok";
        }
        catch (Exception error) when (error is JsonException or System.Xml.XmlException or InvalidOperationException)
        { return false; }
    }

    private Task<SubsonicPlaybackState?> ReadPlaybackStateAsync() =>
        Task.FromResult(_playbackSessions.Get(CurrentProtocolContext));

    private async Task<SubsonicPlaybackState> StartExternalPlaybackAsync(string itemId, string occurrence,
        DateTimeOffset now, SubsonicPlaybackState? previous, bool restart = true)
    {
        if (previous?.Song.Id == itemId && (!previous.Stopped || !restart)) return previous;
        if (restart && previous is { Stopped: true }) occurrence += ":" + Guid.CreateVersion7().ToString("N");
        var (_, provider, _, externalId) = _localLibraryService.ParseExternalId(itemId);
        Song? song = null;
        try { song = await GetProviderSongAsync(provider!, externalId!); }
        catch (Exception error) when (error is not OperationCanceledException)
        { _logger.LogWarning("Playback metadata is unavailable ({ExceptionType})", error.GetType().Name); }
        song ??= new Song { Id = itemId, Title = "External track", ExternalProvider = provider, ExternalId = externalId };
        song.Id = itemId;
        return new SubsonicPlaybackState(song, occurrence, now, now, 0);
    }

    private async Task ObserveExternalScrobbleAsync(SubsonicScrobbleSignal signal)
    {
        if (!CurrentProtocolContext.CanRunUserScopedWork) return;
        if (await SyntheticActivityPolicy.IsExcludedAsync(HttpContext, signal.ItemId))
        {
            _playbackSessions.Remove(CurrentProtocolContext);
            return;
        }
        var now = DateTimeOffset.UtcNow;
        var previous = await ReadPlaybackStateAsync();
        if (signal.Transition == PlaybackTransition.Submission && signal.HasSuppliedTime &&
            previous is { Stopped: true, SubmissionKey: not null } &&
            previous.SubmissionKey != signal.EventKey)
            previous = null;
        if (signal.Transition == PlaybackTransition.Start && signal.HasSuppliedTime && previous?.StartSignalKey is { } startKey &&
            startKey != signal.EventKey)
            previous = null;
        var state = await StartExternalPlaybackAsync(signal.ItemId, $"subsonic:{signal.EventKey}:{signal.Index}", now, previous,
            signal.Transition == PlaybackTransition.Start);
        var observedAt = signal.ObservedAt;
        if (!signal.HasSuppliedTime)
            observedAt = (signal.Transition == PlaybackTransition.Submission ? state.SubmittedAt : state.StartObservedAt) ??
                DateTimeOffset.FromUnixTimeSeconds(signal.ObservedAt.ToUnixTimeSeconds() / 30 * 30);
        var request = _scrobbleProtocolAdapter.ToSignalRequest(CurrentProtocolContext, signal) with
        {
            PlaySessionId = state.Occurrence,
            ObservedAt = observedAt
        };
        _upcomingSongs?.Observe(CurrentProtocolContext, request.Transition, request.ItemId,
            request.DeviceId, request.PlaySessionId, request.PositionTicks);
        if (!await _playbackObservations.RecordAsync(request, HttpContext.RequestAborted)) return;
        _playbackSessions.Set(CurrentProtocolContext, state with
        {
            UpdatedAt = now,
            Stopped = signal.Transition == PlaybackTransition.Submission,
            SubmissionKey = signal.Transition == PlaybackTransition.Submission ? state.SubmissionKey ?? signal.EventKey : null,
            StartSignalKey = signal.Transition == PlaybackTransition.Start && signal.HasSuppliedTime ? signal.EventKey : state.StartSignalKey,
            SubmittedAt = signal.Transition == PlaybackTransition.Submission ? observedAt : null,
            StartObservedAt = signal.Transition == PlaybackTransition.Start ? observedAt : state.StartObservedAt
        });
    }

    private async Task<IActionResult> ScrobblePlaybackAsync(SubsonicRequestParameters parameters)
    {
        var format = parameters.GetValueOrDefault("f", "xml");
        var signals = _scrobbleProtocolAdapter.Parse(parameters, DateTimeOffset.UtcNow);
        var external = signals.Where(signal => IsExternalTrack(signal.ItemId)).ToArray();
        if (external.Length > 0 && !CurrentProtocolContext.CanRunUserScopedWork)
            return _responseBuilder.CreateError(format, 50, "Playback is not available to this user");
        var endpoint = Request.Path.Value?.TrimStart('/') ?? "rest/scrobble";
        try
        {
            SubsonicProxyResponse? native = null;
            if (external.Length != signals.Count || signals.Count == 0)
            {
                var relay = external.Length == 0 ? parameters : NativeScrobbleParameters(parameters);
                native = await _proxyService.RelayRawAsync(endpoint, relay, HttpContext.RequestAborted, Request.Headers);
                if (AcceptedPlaybackResponse(native))
                {
                    foreach (var signal in signals.Where(signal => !IsExternalTrack(signal.ItemId)))
                    {
                        _upcomingSongs?.Observe(CurrentProtocolContext, signal.Transition, signal.ItemId,
                            CurrentProtocolContext.Client.DeviceId ?? CurrentProtocolContext.Client.ClientId, null, null);
                        if (!await _playbackObservations.RecordAsync(_scrobbleProtocolAdapter.ToSignalRequest(CurrentProtocolContext, signal),
                                HttpContext.RequestAborted)) break;
                        if (signal.Transition == PlaybackTransition.Start && CurrentProtocolContext.CanRunUserScopedWork)
                            _playbackSessions.Remove(CurrentProtocolContext);
                    }
                }
            }
            foreach (var signal in external) await ObserveExternalScrobbleAsync(signal);
            return native == null ? _responseBuilder.CreateOkResponse(format) :
                _relayProtocolAdapter.CreateResult(native, $"application/{format}");
        }
        catch (HttpRequestException)
        { return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server"); }
    }

    private SubsonicRequestParameters NativeScrobbleParameters(SubsonicRequestParameters parameters)
    {
        var ids = parameters.GetAllValues("id");
        var external = ids.Select((id, index) => (id, index)).Where(pair => IsExternalTrack(pair.id))
            .Select(pair => pair.index).ToHashSet();
        var counters = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var retained = parameters.Ordered.Where(parameter =>
        {
            var name = parameter.Name;
            if (!name.Equals("id", StringComparison.OrdinalIgnoreCase) && !name.Equals("time", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("submission", StringComparison.OrdinalIgnoreCase)) return true;
            var index = counters.GetValueOrDefault(name);
            counters[name] = index + 1;
            return name.Equals("submission", StringComparison.OrdinalIgnoreCase) && parameters.GetAllValues(name).Count == 1 ||
                   !external.Contains(index);
        }).ToArray();
        var body = parameters.RawBody;
        counters = parameters.QueryParameters.GroupBy(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        bool KeepBodyValue(string name)
        {
            if (!name.Equals("id", StringComparison.OrdinalIgnoreCase) && !name.Equals("time", StringComparison.OrdinalIgnoreCase) &&
                !name.Equals("submission", StringComparison.OrdinalIgnoreCase)) return true;
            var index = counters.GetValueOrDefault(name);
            counters[name] = index + 1;
            return name.Equals("submission", StringComparison.OrdinalIgnoreCase) && parameters.GetAllValues(name).Count == 1 ||
                   !external.Contains(index);
        }
        if (body != null && parameters.BodyParameters.Any(parameter => parameter.Source == SubsonicParameterSource.Form))
            body = string.Join('&', body.Split('&').Where(pair => KeepBodyValue(System.Net.WebUtility.UrlDecode(pair.Split('=', 2)[0]))));
        else if (body != null && parameters.BodyParameters.Any(parameter => parameter.Source == SubsonicParameterSource.Json))
        {
            using var document = JsonDocument.Parse(body);
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                foreach (var property in document.RootElement.EnumerateObject())
                {
                    if (property.Value.ValueKind != JsonValueKind.Array)
                    {
                        if (KeepBodyValue(property.Name)) property.WriteTo(writer);
                        continue;
                    }
                    var values = property.Value.EnumerateArray().Where(_ => KeepBodyValue(property.Name)).ToArray();
                    writer.WritePropertyName(property.Name);
                    writer.WriteStartArray();
                    foreach (var value in values) value.WriteTo(writer);
                    writer.WriteEndArray();
                }
                writer.WriteEndObject();
            }
            body = Encoding.UTF8.GetString(stream.ToArray());
        }
        return new SubsonicRequestParameters(parameters.Method, parameters.ContentType, body, retained);
    }

    [HttpGet, HttpPost]
    [Route("rest/reportPlayback")]
    [Route("rest/reportPlayback.view")]
    public async Task<IActionResult> ReportPlayback()
    {
        var parameters = await ExtractAllParameters();
        if (!IsExternalTrack(parameters.GetValueOrDefault("mediaId")))
            return await ReportNativePlaybackAsync(parameters);
        return await ReportExternalPlaybackAsync(parameters);
    }

    private async Task<IActionResult> ReportNativePlaybackAsync(SubsonicRequestParameters parameters)
    {
        var format = parameters.GetValueOrDefault("f", "xml");
        try
        {
            var native = await _proxyService.RelayRawAsync(Request.Path.Value!.TrimStart('/'), parameters,
                HttpContext.RequestAborted, Request.Headers);
            if (AcceptedPlaybackResponse(native) && CurrentProtocolContext.CanRunUserScopedWork)
            {
                var state = parameters.GetValueOrDefault("state");
                if (state == "starting") _playbackSessions.Remove(CurrentProtocolContext);
                if (state is "starting" or "playing" or "paused" or "stopped" &&
                    parameters.GetValueOrDefault("mediaType", "song") == "song" &&
                    long.TryParse(parameters.GetValueOrDefault("positionMs", "0"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out var position) && position <= TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
                    _upcomingSongs?.Observe(CurrentProtocolContext, state == "stopped" ? PlaybackTransition.Stop :
                        state == "starting" ? PlaybackTransition.Start : PlaybackTransition.Progress,
                        parameters.GetValueOrDefault("mediaId"), CurrentProtocolContext.Client.DeviceId ?? CurrentProtocolContext.Client.ClientId,
                        null, position * TimeSpan.TicksPerMillisecond, paused: state == "paused");
            }
            return _relayProtocolAdapter.CreateResult(native, $"application/{format}");
        }
        catch (HttpRequestException)
        { return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server"); }
    }

    private async Task<IActionResult> ReportExternalPlaybackAsync(SubsonicRequestParameters parameters)
    {
        var format = parameters.GetValueOrDefault("f", "xml");
        var itemId = parameters.GetValueOrDefault("mediaId");
        var stateName = parameters.GetValueOrDefault("state");
        if (parameters.GetValueOrDefault("mediaType", "song") != "song" ||
            stateName is not ("starting" or "paused" or "playing" or "stopped") ||
            !long.TryParse(parameters.GetValueOrDefault("positionMs", "0"), NumberStyles.None, CultureInfo.InvariantCulture,
                out var position) || position > TimeSpan.MaxValue.Ticks / TimeSpan.TicksPerMillisecond)
            return _responseBuilder.CreateError(format, 10, "Invalid playback report");
        if (!double.TryParse(parameters.GetValueOrDefault("playbackRate", "1"), NumberStyles.Float, CultureInfo.InvariantCulture,
                out var rate) || !double.IsFinite(rate) || rate < 0)
            return _responseBuilder.CreateError(format, 10, "Invalid playback report");
        if (!bool.TryParse(parameters.GetValueOrDefault("ignoreScrobble", "false"), out var ignore))
            return _responseBuilder.CreateError(format, 10, "Invalid playback report");
        if (!CurrentProtocolContext.CanRunUserScopedWork)
            return _responseBuilder.CreateError(format, 50, "Playback is not available to this user");
        if (await SyntheticActivityPolicy.IsExcludedAsync(HttpContext, itemId))
        {
            _playbackSessions.Remove(CurrentProtocolContext);
            return _responseBuilder.CreateOkResponse(format);
        }
        var now = DateTimeOffset.UtcNow;
        var previous = await ReadPlaybackStateAsync();
        var state = await StartExternalPlaybackAsync(itemId, $"subsonic:{now.ToUnixTimeSeconds() / 30}:0", now, previous,
            stateName == "starting");
        var transition = stateName == "stopped" ? PlaybackTransition.Stop : stateName == "starting"
            ? PlaybackTransition.Start : PlaybackTransition.Progress;
        var observedAt = transition == PlaybackTransition.Start ? state.StartedAt : now;
        _upcomingSongs?.Observe(CurrentProtocolContext, transition, itemId,
            CurrentProtocolContext.Client.DeviceId ?? CurrentProtocolContext.Client.ClientId, state.Occurrence,
            position * TimeSpan.TicksPerMillisecond, paused: stateName == "paused");
        if (!await _playbackObservations.RecordAsync(new PlaybackSignalRequest(CurrentProtocolContext, transition, itemId,
            CurrentProtocolContext.Client.DeviceId ?? CurrentProtocolContext.Client.ClientId, state.Occurrence,
            position * TimeSpan.TicksPerMillisecond, observedAt, RelayExternally: !ignore, Paused: stateName == "paused", Failed: stateName == "stopped" && previous == null), HttpContext.RequestAborted))
            return _responseBuilder.CreateOkResponse(format);
        _playbackSessions.Set(CurrentProtocolContext, state with
        {
            PositionTicks = position * TimeSpan.TicksPerMillisecond,
            UpdatedAt = now,
            Stopped = stateName == "stopped",
            Paused = stateName == "paused",
            PlaybackRate = rate
        });
        return _responseBuilder.CreateOkResponse(format);
    }

    [HttpGet, HttpPost]
    [Route("rest/getNowPlaying")]
    [Route("rest/getNowPlaying.view")]
    public async Task<IActionResult> GetNowPlaying()
    {
        var parameters = await ExtractAllParameters();
        var format = parameters.GetValueOrDefault("f", "xml");
        var state = await ReadPlaybackStateAsync();
        if (state != null && await SyntheticActivityPolicy.IsExcludedAsync(HttpContext, state.Song.Id))
        {
            _playbackSessions.Remove(CurrentProtocolContext);
            state = null;
        }
        IHeaderDictionary headers = Request.Headers;
        if (state is { Stopped: false })
            headers = new HeaderDictionary(Request.Headers.Where(header =>
                    !header.Key.Equals("If-None-Match", StringComparison.OrdinalIgnoreCase) &&
                    !header.Key.Equals("If-Modified-Since", StringComparison.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase));
        try
        {
            var native = await _proxyService.RelayRawAsync(Request.Path.Value!.TrimStart('/'), parameters,
                HttpContext.RequestAborted, headers);
            var response = state is { Stopped: false } ? MergeNowPlaying(native, state, format) : native;
            if (!ReferenceEquals(native, response)) Response.Headers.CacheControl = "no-store";
            return _relayProtocolAdapter.CreateResult(response, $"application/{format}");
        }
        catch (HttpRequestException)
        { return _responseBuilder.CreateError(format, 0, "Error connecting to Subsonic server"); }
    }

    private SubsonicProxyResponse MergeNowPlaying(SubsonicProxyResponse native, SubsonicPlaybackState state, string format)
    {
        if (!AcceptedPlaybackResponse(native)) return native;
        try
        {
            byte[] body;
            var minutes = Math.Max(0, (int)(DateTimeOffset.UtcNow - state.StartedAt).TotalMinutes);
            if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
            {
                using var document = JsonDocument.Parse(native.Body);
                var root = document.RootElement.GetProperty("subsonic-response");
                if (root.TryGetProperty("nowPlaying", out var list) && list.ValueKind == JsonValueKind.Object &&
                    list.TryGetProperty("entry", out var entries) && entries.ValueKind == JsonValueKind.Array &&
                    entries.EnumerateArray().Any(candidate => candidate.TryGetProperty("id", out var id) && id.GetString() == state.Song.Id))
                    return native;
                var entry = JsonSerializer.SerializeToNode(_responseBuilder.ConvertSongToJson(state.Song))!.AsObject();
                entry["username"] = CurrentProtocolContext.VerifiedBackendPrincipalId;
                entry["minutesAgo"] = minutes;
                entry["playerId"] = 0;
                entry["playerName"] = CurrentProtocolContext.Client.ClientId ?? "Allstarr";
                entry["coverArt"] = state.Song.Id;
                body = SubsonicPlaybackProjection.AppendJson(native.Body, JsonSerializer.SerializeToUtf8Bytes(entry));
            }
            else
            {
                var document = XDocument.Parse(Encoding.UTF8.GetString(native.Body), LoadOptions.PreserveWhitespace);
                var root = document.Root!;
                var ns = root.Name.Namespace;
                var list = root.Element(ns + "nowPlaying");
                if (list?.Elements().Any(candidate => candidate.Attribute("id")?.Value == state.Song.Id) == true) return native;
                var entry = _responseBuilder.ConvertSongToXml(state.Song, ns);
                entry.Name = ns + "entry";
                entry.SetAttributeValue("username", CurrentProtocolContext.VerifiedBackendPrincipalId);
                entry.SetAttributeValue("minutesAgo", minutes);
                entry.SetAttributeValue("playerId", 0);
                entry.SetAttributeValue("playerName", CurrentProtocolContext.Client.ClientId ?? "Allstarr");
                entry.SetAttributeValue("coverArt", state.Song.Id);
                body = SubsonicPlaybackProjection.AppendXml(native.Body, entry);
            }
            if (ReferenceEquals(body, native.Body)) return native;
            var headers = native.Headers.Where(header =>
                    !new[] { "ETag", "Last-Modified", "Content-MD5", "Digest", "Content-Encoding" }
                        .Contains(header.Key, StringComparer.OrdinalIgnoreCase))
                .ToDictionary(header => header.Key, header => header.Value, StringComparer.OrdinalIgnoreCase);
            headers["Cache-Control"] = ["no-store"];
            return native with { Body = body, Headers = headers };
        }
        catch (Exception error) when (error is JsonException or System.Xml.XmlException or InvalidOperationException)
        { return native; }
    }
}
