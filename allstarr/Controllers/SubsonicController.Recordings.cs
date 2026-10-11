using System.Text.Json;
using System.Xml.Linq;
using allstarr.Core.Matching;
using allstarr.Models.Domain;
using allstarr.Services.Subsonic;

namespace allstarr.Controllers;

public partial class SubsonicController
{
    private async Task PreferKnownNativeSongsAsync(List<Song> external, List<object> native,
        SubsonicRequestParameters parameters, bool isJson)
    {
        if (_libraryIndex == null || external.Count == 0) return;
        var matches = await _libraryIndex.FindKnownRecordingTracksAsync(CurrentProtocolContext, external.Select(song =>
            new LibraryRecordingLookup(song.Id, song.CanonicalRecordingId, song.Isrc, song.ExternalProvider, song.ExternalId)).ToArray(),
            HttpContext.RequestAborted);
        foreach (var pair in matches.GroupBy(item => item.Value))
        {
            var exists = native.Any(item => item switch
            {
                XElement xml => xml.Attribute("id")?.Value == pair.Key,
                Dictionary<string, object> json => json.GetValueOrDefault("id")?.ToString() == pair.Key,
                _ => false
            });
            if (!exists)
            {
                var response = await _proxyService.RelaySafeAsync("rest/getSong", parameters.SetValue("id", pair.Key)
                    .SetValue("f", isJson ? "json" : "xml"), HttpContext.RequestAborted);
                if (!response.Success || response.Body == null) continue;
                object? item = null;
                try
                {
                    if (isJson)
                    {
                        using var document = JsonDocument.Parse(response.Body);
                        if (document.RootElement.TryGetProperty("subsonic-response", out var root) &&
                            root.TryGetProperty("status", out var status) && status.GetString() == "ok" &&
                            root.TryGetProperty("song", out var song) && song.TryGetProperty("id", out var id) && id.GetString() == pair.Key)
                            item = _responseBuilder.ConvertSubsonicJsonElement(song, true);
                    }
                    else
                    {
                        var document = XDocument.Parse(System.Text.Encoding.UTF8.GetString(response.Body));
                        var song = document.Root?.Element((document.Root.GetDefaultNamespace()) + "song");
                        if (document.Root?.Attribute("status")?.Value == "ok" && song?.Attribute("id")?.Value == pair.Key)
                            item = _responseBuilder.ConvertSubsonicXmlElement(song, "song");
                    }
                }
                catch (Exception error) when (error is JsonException or System.Xml.XmlException)
                {
                    continue;
                }
                if (item == null) continue;
                native.Add(item);
            }
            var aliases = pair.Select(item => item.Key).ToHashSet(StringComparer.Ordinal);
            external.RemoveAll(song => aliases.Contains(song.Id));
        }
    }
}
