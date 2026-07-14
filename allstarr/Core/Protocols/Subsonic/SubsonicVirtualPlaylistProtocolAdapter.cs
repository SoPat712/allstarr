using System.Xml.Linq;
using allstarr.Core.Playlists;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Core.Protocols.Subsonic;

public sealed class SubsonicVirtualPlaylistProtocolAdapter(IPlaylistVirtualizationService playlists)
{
    private const string Version = "1.16.1";
    private const string Namespace = "http://subsonic.org/restapi";

    public bool IsVirtualPlaylistId(string? value) =>
        PlaylistVirtualizationService.TryParseProtocolId(value, out _);

    public async Task<IActionResult?> ReadAsync(
        ProtocolExecutionContext context, string id, string format, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            return new JsonResult(new Dictionary<string, object?>
            {
                ["subsonic-response"] = new Dictionary<string, object?>
                {
                    ["status"] = "ok",
                    ["version"] = Version,
                    ["playlist"] = new Dictionary<string, object?>
                    {
                        ["id"] = playlist.ProtocolId,
                        ["name"] = playlist.Name,
                        ["comment"] = playlist.Description,
                        ["owner"] = "allstarr",
                        ["public"] = false,
                        ["songCount"] = playlist.Tracks.Count,
                        ["duration"] = playlist.Tracks.Sum(track => track.DurationMilliseconds) / 1000,
                        ["coverArt"] = playlist.ArtworkReferenceKey,
                        ["entry"] = playlist.Tracks.Select(ToJsonEntry).ToList()
                    }
                }
            });
        }

        var ns = XNamespace.Get(Namespace);
        var element = new XElement(ns + "playlist",
            new XAttribute("id", playlist.ProtocolId), new XAttribute("name", playlist.Name),
            new XAttribute("owner", "allstarr"), new XAttribute("public", false),
            new XAttribute("songCount", playlist.Tracks.Count),
            new XAttribute("duration", playlist.Tracks.Sum(track => track.DurationMilliseconds) / 1000));
        if (playlist.Description != null) element.Add(new XAttribute("comment", playlist.Description));
        if (playlist.ArtworkReferenceKey != null) element.Add(new XAttribute("coverArt", playlist.ArtworkReferenceKey));
        foreach (var track in playlist.Tracks)
            element.Add(new XElement(ns + "entry", ToXmlAttributes(track)));
        var document = new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"), new XAttribute("version", Version), element));
        return new ContentResult { Content = document.ToString(), ContentType = "application/xml" };
    }

    private static Dictionary<string, object?> ToJsonEntry(VirtualPlaylistTrack track) => new()
    {
        ["id"] = track.BackendItemId,
        ["title"] = track.Title,
        ["artist"] = track.Artist,
        ["album"] = track.Album,
        ["albumArtist"] = track.AlbumArtist,
        ["duration"] = track.DurationMilliseconds / 1000,
        ["track"] = track.SourcePosition + 1,
        ["isDir"] = false,
        ["type"] = "music",
        ["coverArt"] = track.CoverArtReference
    };

    private static IEnumerable<XAttribute> ToXmlAttributes(VirtualPlaylistTrack track)
    {
        yield return new XAttribute("id", track.BackendItemId);
        yield return new XAttribute("title", track.Title);
        yield return new XAttribute("artist", track.Artist);
        if (track.Album != null) yield return new XAttribute("album", track.Album);
        if (track.AlbumArtist != null) yield return new XAttribute("albumArtist", track.AlbumArtist);
        yield return new XAttribute("duration", track.DurationMilliseconds / 1000);
        yield return new XAttribute("track", track.SourcePosition + 1);
        yield return new XAttribute("isDir", false);
        yield return new XAttribute("type", "music");
        if (track.CoverArtReference != null) yield return new XAttribute("coverArt", track.CoverArtReference);
    }
}
