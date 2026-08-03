using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Intelligence;
using allstarr.Core.Protocols;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private static readonly IReadOnlySet<string> SonicRelayParameters =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "u", "p", "t", "s", "apiKey", "v", "c", "f"
        };

    [HttpGet, HttpPost]
    [Route("rest/getSonicSimilarTracks")]
    [Route("rest/getSonicSimilarTracks.view")]
    public async Task<IActionResult> GetSonicSimilarTracks()
    {
        var parameters = await ExtractAllParameters();
        var format = SonicFormat(parameters);
        var id = parameters.GetValueOrDefault("id").Trim();
        if (id.Length == 0 || !SonicCount(parameters, 10, out var count))
            return _responseBuilder.CreateError(format, 10, "A valid song and count are required");

        try
        {
            var scope = await ResolveSonicScopeAsync(id);
            if (scope == null) return SonicUnavailable(format);
            var tracks = await _audioMuse!.FindSimilarAsync(
                scope, [id], count, HttpContext.RequestAborted);
            return await CreateSonicResponseAsync(parameters, format, tracks, strict: false);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new StatusCodeResult(499);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "OpenSubsonic sonic similarity failed safely ({ExceptionType})",
                exception.GetType().Name);
            return SonicUnavailable(format);
        }
    }

    [HttpGet, HttpPost]
    [Route("rest/findSonicPath")]
    [Route("rest/findSonicPath.view")]
    public async Task<IActionResult> FindSonicPath()
    {
        var parameters = await ExtractAllParameters();
        var format = SonicFormat(parameters);
        var start = parameters.GetValueOrDefault("startSongId").Trim();
        var end = parameters.GetValueOrDefault("endSongId").Trim();
        if (start.Length == 0 || end.Length == 0 || start == end ||
            !SonicCount(parameters, 25, out var count))
            return _responseBuilder.CreateError(format, 10, "Two different songs and a valid count are required");

        try
        {
            var scope = await ResolveSonicScopeAsync(start);
            var endScope = await ResolveSonicScopeAsync(end);
            if (scope == null || endScope != scope) return SonicUnavailable(format);
            var path = await _audioMuse!.FindPathAsync(
                scope, start, end, count, HttpContext.RequestAborted);
            return await CreateSonicResponseAsync(parameters, format, path.Tracks, strict: true);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            return new StatusCodeResult(499);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "OpenSubsonic sonic path failed safely ({ExceptionType})",
                exception.GetType().Name);
            return SonicUnavailable(format);
        }
    }

    [HttpGet, HttpPost]
    [Route("rest/getOpenSubsonicExtensions")]
    [Route("rest/getOpenSubsonicExtensions.view")]
    public async Task<IActionResult> GetOpenSubsonicExtensions()
    {
        var parameters = await _requestParser.ExtractAllParametersAsync(Request);
        var format = SonicFormat(parameters);
        if (_audioMuse?.IsAvailable != true)
        {
            var upstream = await _proxyService.RelayRawAsync(
                "rest/getOpenSubsonicExtensions.view",
                parameters.SetValue("f", format),
                HttpContext.RequestAborted);
            return _relayProtocolAdapter.CreateResult(upstream, $"application/{format}");
        }

        SubsonicProxyResponse? availableExtensions = null;
        try
        {
            availableExtensions = await _proxyService.RelayRawAsync(
                "rest/getOpenSubsonicExtensions.view",
                parameters.SetValue("f", format),
                HttpContext.RequestAborted);
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                "Could not read backend OpenSubsonic extensions ({ExceptionType})",
                exception.GetType().Name);
        }
        return MergeSonicExtension(availableExtensions, format);
    }

    private async Task<IntelligenceScope?> ResolveSonicScopeAsync(string itemId)
    {
        if (_audioMuse?.IsAvailable != true) return null;
        return await SonicProtocolScope.ResolveAsync(
            CurrentProtocolContext,
            itemId,
            _libraryScopes,
            _intelligencePolicies,
            HttpContext.RequestAborted);
    }

    private async Task<IActionResult> CreateSonicResponseAsync(
        SubsonicRequestParameters parameters,
        string format,
        IReadOnlyList<RecommendationSourceItem> tracks,
        bool strict)
    {
        var requested = tracks.Where(track =>
                !string.IsNullOrWhiteSpace(track.Identity?.BackendItemId))
            .Take(200).ToArray();
        var hydrated = new List<SonicEntry>(requested.Length);
        foreach (var batch in requested.Chunk(5))
        {
            var loaded = await Task.WhenAll(batch.Select(async (track, index) =>
            {
                var native = await ReadSonicEntryAsync(
                    parameters, format, track.Identity!.BackendItemId!);
                var score = hydrated.Count == 0 && index == 0 && strict
                    ? 1d
                    : Math.Clamp(track.Score, 0d, 1d);
                return native == null ? null : new SonicEntry(native, score);
            }));
            hydrated.AddRange(loaded.OfType<SonicEntry>());
        }

        if (strict && hydrated.Count != requested.Length)
            return _responseBuilder.CreateError(format, 70, "A song in the sonic path is no longer available");
        return SonicResponse(format, hydrated);
    }

    private async Task<NativeSonicEntry?> ReadSonicEntryAsync(
        SubsonicRequestParameters parameters,
        string format,
        string id)
    {
        var request = parameters.Select(SonicRelayParameters)
            .SetValue("f", format)
            .SetValue("id", id);
        var response = await _proxyService.RelayRawAsync(
            "rest/getSong.view", request, HttpContext.RequestAborted);
        if (!response.IsSuccessStatusCode || response.Body.Length == 0) return null;

        if (format == "json")
        {
            var node = JsonNode.Parse(response.Body)?["subsonic-response"]?["song"] as JsonObject;
            return node == null ? null : new NativeSonicEntry((JsonObject)node.DeepClone(), null);
        }

        var document = XDocument.Parse(Encoding.UTF8.GetString(response.Body));
        var song = document.Root?.Elements().FirstOrDefault(element => element.Name.LocalName == "song");
        return song == null ? null : new NativeSonicEntry(null, new XElement(song));
    }

    private IActionResult MergeSonicExtension(SubsonicProxyResponse? upstream, string format)
    {
        try
        {
            if (upstream is { IsSuccessStatusCode: true, Body.Length: > 0 })
            {
                if (format == "json")
                {
                    var jsonDocument = JsonNode.Parse(upstream.Body)!.AsObject();
                    var jsonRoot = jsonDocument["subsonic-response"]!.AsObject();
                    var extensions = jsonRoot["openSubsonicExtensions"] as JsonArray ?? [];
                    jsonRoot["openSubsonicExtensions"] = extensions;
                    if (!extensions.OfType<JsonObject>().Any(item =>
                            item["name"]?.GetValue<string>() == "sonicSimilarity"))
                        extensions.Add(new JsonObject
                        {
                            ["name"] = "sonicSimilarity",
                            ["versions"] = new JsonArray(1)
                        });
                    return JsonContent(jsonDocument);
                }

                var document = XDocument.Parse(Encoding.UTF8.GetString(upstream.Body));
                var root = document.Root!;
                if (!root.Elements().Any(element =>
                        element.Name.LocalName == "openSubsonicExtensions" &&
                        element.Attribute("name")?.Value == "sonicSimilarity"))
                    root.Add(SonicExtensionXml(root.Name.Namespace));
                return XmlContent(document);
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or XmlException)
        {
            _logger.LogWarning(
                "Could not merge backend OpenSubsonic extensions ({ExceptionType})",
                exception.GetType().Name);
        }

        return format == "json"
            ? JsonContent(new JsonObject
            {
                ["subsonic-response"] = new JsonObject
                {
                    ["status"] = "ok",
                    ["version"] = "1.16.1",
                    ["openSubsonic"] = true,
                    ["openSubsonicExtensions"] = new JsonArray(new JsonObject
                    {
                        ["name"] = "sonicSimilarity",
                        ["versions"] = new JsonArray(1)
                    })
                }
            })
            : XmlContent(new XDocument(new XElement(
                XNamespace.Get("http://subsonic.org/restapi") + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", "1.16.1"),
                SonicExtensionXml(XNamespace.Get("http://subsonic.org/restapi")))));
    }

    private static IActionResult SonicResponse(string format, IReadOnlyList<SonicEntry> entries)
    {
        if (format == "json")
        {
            var matches = new JsonArray(entries.Select(entry => (JsonNode)new JsonObject
            {
                ["entry"] = entry.Native.Json,
                ["similarity"] = entry.Score
            }).ToArray());
            return JsonContent(new JsonObject
            {
                ["subsonic-response"] = new JsonObject
                {
                    ["status"] = "ok",
                    ["version"] = "1.16.1",
                    ["openSubsonic"] = true,
                    ["sonicMatch"] = matches
                }
            });
        }

        XNamespace ns = "http://subsonic.org/restapi";
        return XmlContent(new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"),
            new XAttribute("version", "1.16.1"),
            entries.Select(entry =>
            {
                var native = new XElement(entry.Native.Xml!);
                native.Name = ns + "entry";
                return new XElement(ns + "sonicMatch",
                    new XAttribute("similarity", entry.Score.ToString("0.########", CultureInfo.InvariantCulture)),
                    native);
            }))));
    }

    private static XElement SonicExtensionXml(XNamespace ns) => new(ns + "openSubsonicExtensions",
        new XAttribute("name", "sonicSimilarity"),
        new XElement(ns + "versions", 1));

    private static ContentResult JsonContent(JsonObject value) => new()
    {
        Content = value.ToJsonString(),
        ContentType = "application/json",
        StatusCode = StatusCodes.Status200OK
    };

    private static ContentResult XmlContent(XDocument value) => new()
    {
        Content = value.ToString(),
        ContentType = "application/xml",
        StatusCode = StatusCodes.Status200OK
    };

    private IActionResult SonicUnavailable(string format) =>
        _responseBuilder.CreateError(format, 70, "Sonic similarity is not available for this library");

    private static string SonicFormat(SubsonicRequestParameters parameters) =>
        parameters.GetValueOrDefault("f", "xml").Equals("json", StringComparison.OrdinalIgnoreCase)
            ? "json"
            : "xml";

    private static bool SonicCount(
        SubsonicRequestParameters parameters,
        int fallback,
        out int count)
    {
        var value = parameters.GetValueOrDefault("count");
        count = value.Length == 0 ? fallback : int.TryParse(value, out var parsed) ? parsed : 0;
        return count is >= 1 and <= 200;
    }

    private sealed record NativeSonicEntry(JsonObject? Json, XElement? Xml);
    private sealed record SonicEntry(NativeSonicEntry Native, double Score);
}
