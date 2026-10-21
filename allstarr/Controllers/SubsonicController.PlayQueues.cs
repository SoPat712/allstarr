using allstarr.Core.Protocols.Subsonic;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private readonly SubsonicPlayQueueStore? _playQueues;

    [HttpGet, HttpPost]
    [Route("rest/getPlayQueue")]
    [Route("rest/getPlayQueue.view")]
    public Task<IActionResult> GetPlayQueue() => ReadPlayQueueAsync(false);

    [HttpGet, HttpPost]
    [Route("rest/getPlayQueueByIndex")]
    [Route("rest/getPlayQueueByIndex.view")]
    public Task<IActionResult> GetPlayQueueByIndex() => ReadPlayQueueAsync(true);

    [HttpGet, HttpPost]
    [Route("rest/savePlayQueue")]
    [Route("rest/savePlayQueue.view")]
    public Task<IActionResult> SavePlayQueue() => SavePlayQueueAsync(false);

    [HttpGet, HttpPost]
    [Route("rest/savePlayQueueByIndex")]
    [Route("rest/savePlayQueueByIndex.view")]
    public Task<IActionResult> SavePlayQueueByIndex() => SavePlayQueueAsync(true);

    private async Task<IActionResult> ReadPlayQueueAsync(bool byIndex)
    {
        var parameters = await ExtractAllParameters();
        var format = SonicFormat(parameters);
        var context = CurrentProtocolContext;
        var saved = _playQueues == null ? null : await _playQueues.ReadAsync(context, HttpContext.RequestAborted);
        if (saved != null)
            return SubsonicPlayQueueProtocolAdapter.CreateResponse(saved, format, byIndex, _responseBuilder);

        var native = await _proxyService.RelayRawAsync(byIndex ? "rest/getPlayQueue.view" : Request.Path.Value!.TrimStart('/'),
            parameters, HttpContext.RequestAborted, Request.Headers);
        if (byIndex && SubsonicPlayQueueProtocolAdapter.NativeQueue(native, format, context.VerifiedBackendPrincipalId) is { } queue)
            return SubsonicPlayQueueProtocolAdapter.CreateResponse(queue, format, true, _responseBuilder);
        return _relayProtocolAdapter.CreateResult(native, $"application/{format}");
    }

    private async Task<IActionResult> SavePlayQueueAsync(bool byIndex)
    {
        var parameters = await ExtractAllParameters();
        var format = SonicFormat(parameters);
        SubsonicQueueSave request;
        try
        {
            request = SubsonicPlayQueueProtocolAdapter.Parse(parameters, byIndex);
        }
        catch (ArgumentException exception)
        {
            return _responseBuilder.CreateError(format, 10, exception.Message);
        }
        if (_playQueues == null)
            return _responseBuilder.CreateError(format, 0, "Queue storage is unavailable.");

        var context = CurrentProtocolContext;
        var previous = await _playQueues.ReadAsync(context, HttpContext.RequestAborted);
        if (!byIndex && previous == null && request.Ids.All(id => !_localLibraryService.ParseSongId(id).isExternal))
            return await RelayMutation("rest/savePlayQueue", parameters);

        var entries = new Dictionary<string, SubsonicQueueEntry>(StringComparer.Ordinal);
        foreach (var id in request.Ids.Distinct(StringComparer.Ordinal))
        {
            var (external, provider, externalId) = _localLibraryService.ParseSongId(id);
            var nativeId = id;
            if (external && _libraryIndex != null &&
                await _libraryIndex.ResolveKnownTrackAsync(context, id, HttpContext.RequestAborted) is { } knownNative)
            {
                nativeId = knownNative;
                external = false;
            }
            if (external)
            {
                var song = await GetProviderSongAsync(provider!, externalId!);
                if (song == null) return _responseBuilder.CreateError(format, 70, "A song in the queue is unavailable.");
                entries[id] = new(song.Id, song, null, null);
            }
            else
            {
                var json = await ReadQueueNativeSongAsync(parameters, nativeId, "json");
                var xml = await ReadQueueNativeSongAsync(parameters, nativeId, "xml");
                if (json == null || xml == null)
                    return _responseBuilder.CreateError(format, 70, "A song in the queue is unavailable.");
                entries[id] = new(nativeId, null, json, xml);
            }
        }

        var saved = new SubsonicSavedQueue(
            request.Ids.Select(id => entries[id]).ToArray(), request.CurrentIndex, request.Position,
            context.VerifiedBackendPrincipalId, DateTimeOffset.UtcNow, context.Client.ClientId ?? "");
        await _playQueues.SaveAsync(context, saved, HttpContext.RequestAborted);
        _upcomingSongs?.RememberQueue(context,
            saved.CurrentIndex is { } index ? saved.Entries[index].Id : null,
            saved.CurrentIndex is { } current ? saved.Entries.Skip(current + 1).Take(2).Select(entry => entry.Id).ToArray() : []);
        return _responseBuilder.CreateOkResponse(format);
    }

    private async Task<string?> ReadQueueNativeSongAsync(SubsonicRequestParameters parameters, string id, string format)
    {
        var selected = parameters.Select(SonicRelayParameters).SetValue("id", id).SetValue("f", format);
        var response = await _proxyService.RelayRawAsync("rest/getSong.view", selected, HttpContext.RequestAborted);
        return SubsonicPlayQueueProtocolAdapter.NativeSong(response, format);
    }
}
