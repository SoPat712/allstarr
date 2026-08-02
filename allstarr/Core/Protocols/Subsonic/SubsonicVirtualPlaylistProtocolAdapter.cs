using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
using allstarr.Services.Subsonic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols.Subsonic;

public sealed record SubsonicPlaylistMutationRoute(bool Writable, string? TargetPlaylistId);

public interface ISubsonicPlaylistMutationResolver
{
    Task<SubsonicPlaylistMutationRoute?> ResolveAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Resolves an Allstarr playlist identifier only when it belongs to the verified
/// Subsonic actor, tenant, backend, protocol, and requested library scope.
/// </summary>
public sealed class SubsonicPlaylistMutationResolver(
    IDbContextFactory<AllstarrDbContext> contextFactory) : ISubsonicPlaylistMutationResolver
{
    public async Task<SubsonicPlaylistMutationRoute?> ResolveAsync(
        ProtocolExecutionContext context,
        string protocolId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Protocol != ProtocolKind.Subsonic ||
            !PlaylistVirtualizationService.TryParseProtocolId(protocolId, out var linkId) ||
            context.Actor?.EffectiveUserId is not { } userId)
        {
            return null;
        }

        var actor = context.Actor;
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var link = await db.PlaylistLinks.AsNoTracking().SingleOrDefaultAsync(item =>
            item.Id == linkId &&
            item.TenantId == actor.TenantId &&
            item.OwnerUserId == userId &&
            item.TargetBackendInstanceId == context.BackendInstanceId &&
            (item.TargetProtocol == "subsonic" ||
             item.TargetProtocol == "opensubsonic" ||
             item.TargetProtocol == "navidrome") &&
            item.Enabled &&
            (context.LibraryScopeId == null || item.LibraryScopeId == context.LibraryScopeId),
            cancellationToken);
        if (link == null) return null;

        var writable = link.Mode != PlaylistLinkMode.Virtual &&
                       !string.IsNullOrWhiteSpace(link.TargetPlaylistId);
        return new SubsonicPlaylistMutationRoute(
            writable,
            writable ? link.TargetPlaylistId!.Trim() : null);
    }
}

public sealed class SubsonicVirtualPlaylistProtocolAdapter(
    IPlaylistVirtualizationService playlists,
    ISubsonicPlaylistMutationResolver mutationResolver)
{
    private const string Version = "1.16.1";
    private const string Namespace = "http://subsonic.org/restapi";

    public bool IsVirtualPlaylistId(string? value) =>
        PlaylistVirtualizationService.TryParseProtocolId(value, out _);

    public Task<SubsonicPlaylistMutationRoute?> ResolveMutationAsync(
        ProtocolExecutionContext context,
        string id,
        CancellationToken cancellationToken) =>
        mutationResolver.ResolveAsync(context, id, cancellationToken);

    public async Task<SubsonicProxyResponse> ListAsync(
        ProtocolExecutionContext context,
        string format,
        SubsonicProxyResponse nativeResponse,
        CancellationToken cancellationToken)
    {
        if (!nativeResponse.IsSuccessStatusCode || !TryGetFormat(nativeResponse, format, out var nativeFormat))
            return nativeResponse;
        if (!IsSuccessfulNativeResponse(nativeResponse.Body, nativeFormat))
            return nativeResponse;

        var visible = await playlists.ListAsync(context, cancellationToken);
        if (visible.Count == 0) return nativeResponse;

        var merged = nativeFormat == NativeFormat.Json
            ? MergeJson(nativeResponse.Body, visible)
            : MergeXml(nativeResponse.Body, visible);
        return merged == null ? nativeResponse : nativeResponse with { Body = merged };
    }

    public async Task<IActionResult?> ReadAsync(
        ProtocolExecutionContext context, string id, string format, CancellationToken cancellationToken)
    {
        var playlist = await playlists.ReadAsync(context, id, cancellationToken);
        if (playlist == null) return null;
        var tracks = playlist.Tracks.ToArray();
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
                        ["songCount"] = tracks.Length,
                        ["duration"] = tracks.All(track => track.DurationMilliseconds.HasValue)
                            ? tracks.Sum(track => track.DurationMilliseconds) / 1000
                            : null,
                        ["coverArt"] = playlist.ArtworkReferenceKey,
                        ["entry"] = tracks.Select(ToJsonEntry).ToList()
                    }
                }
            });
        }

        var ns = XNamespace.Get(Namespace);
        var element = new XElement(ns + "playlist",
            new XAttribute("id", playlist.ProtocolId), new XAttribute("name", playlist.Name),
            new XAttribute("owner", "allstarr"), new XAttribute("public", false),
            new XAttribute("songCount", tracks.Length));
        if (tracks.All(track => track.DurationMilliseconds.HasValue))
            element.Add(new XAttribute("duration", tracks.Sum(track => track.DurationMilliseconds)!.Value / 1000));
        if (playlist.Description != null) element.Add(new XAttribute("comment", playlist.Description));
        if (playlist.ArtworkReferenceKey != null) element.Add(new XAttribute("coverArt", playlist.ArtworkReferenceKey));
        foreach (var track in tracks)
            element.Add(new XElement(ns + "entry", ToXmlAttributes(track)));
        var document = new XDocument(new XElement(ns + "subsonic-response",
            new XAttribute("status", "ok"), new XAttribute("version", Version), element));
        return new ContentResult { Content = document.ToString(), ContentType = "application/xml" };
    }

    private static Dictionary<string, object?> ToJsonEntry(VirtualPlaylistTrack track)
    {
        if (track.NativeEntryJson != null)
        {
            using var document = JsonDocument.Parse(track.NativeEntryJson);
            return document.RootElement.EnumerateObject()
                .ToDictionary(item => item.Name, item => (object?)item.Value.Clone(), StringComparer.Ordinal);
        }
        var result = new Dictionary<string, object?>
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
        AddSourceIdentity(result, track);
        return result;
    }

    private static IEnumerable<XAttribute> ToXmlAttributes(VirtualPlaylistTrack track)
    {
        if (track.NativeEntryJson != null)
        {
            using var document = JsonDocument.Parse(track.NativeEntryJson);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null) continue;
                yield return new XAttribute(property.Name, property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText());
            }
            yield break;
        }
        yield return new XAttribute("id", track.BackendItemId);
        yield return new XAttribute("title", track.Title);
        yield return new XAttribute("artist", track.Artist);
        if (track.Album != null) yield return new XAttribute("album", track.Album);
        if (track.AlbumArtist != null) yield return new XAttribute("albumArtist", track.AlbumArtist);
        if (track.DurationMilliseconds.HasValue)
            yield return new XAttribute("duration", track.DurationMilliseconds.Value / 1000);
        yield return new XAttribute("track", track.SourcePosition + 1);
        yield return new XAttribute("isDir", false);
        yield return new XAttribute("type", "music");
        if (track.CoverArtReference != null) yield return new XAttribute("coverArt", track.CoverArtReference);
        if (track.SourceIdentity is { } identity)
        {
            yield return new XAttribute("allstarrSource", identity.ProviderId);
            yield return new XAttribute("allstarrSourceHash", identity.ExternalIdHash);
            yield return new XAttribute("allstarrSourceRevision", identity.SourceRevision);
            if (identity.ExternalId != null)
                yield return new XAttribute("allstarrSourceId", identity.ExternalId);
        }
        if (track.SourceMetadata?.Isrc != null)
            yield return new XAttribute("isrc", track.SourceMetadata.Isrc);
    }

    private static void AddSourceIdentity(
        IDictionary<string, object?> result,
        VirtualPlaylistTrack track)
    {
        if (track.SourceIdentity is not { } identity) return;
        result["allstarrSource"] = identity.ProviderId;
        result["allstarrSourceHash"] = identity.ExternalIdHash;
        result["allstarrSourceRevision"] = identity.SourceRevision;
        if (identity.ExternalId != null) result["allstarrSourceId"] = identity.ExternalId;
        if (track.SourceMetadata?.Isrc != null) result["isrc"] = track.SourceMetadata.Isrc;
    }

    private static bool TryGetFormat(
        SubsonicProxyResponse response,
        string format,
        out NativeFormat nativeFormat)
    {
        nativeFormat = default;
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase) ||
            response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
        {
            nativeFormat = NativeFormat.Json;
            return true;
        }

        if (format.Equals("xml", StringComparison.OrdinalIgnoreCase) ||
            response.ContentType?.Contains("xml", StringComparison.OrdinalIgnoreCase) == true)
        {
            nativeFormat = NativeFormat.Xml;
            return true;
        }

        return false;
    }

    private static byte[]? MergeJson(
        byte[] body,
        IReadOnlyList<VirtualPlaylistReadModel> visible)
    {
        try
        {
            var document = JsonNode.Parse(Encoding.UTF8.GetString(body))?.AsObject();
            var response = document?["subsonic-response"]?.AsObject();
            if (response?["status"]?.GetValue<string>() != "ok") return null;
            var playlists = response["playlists"] as JsonObject;
            if (playlists == null)
            {
                playlists = new JsonObject();
                response["playlists"] = playlists;
            }
            var native = playlists["playlist"] switch
            {
                JsonArray array => array,
                JsonObject single => new JsonArray(single.DeepClone()),
                null => [],
                _ => null
            };
            if (native == null) return null;
            playlists["playlist"] = native;

            var nativeIds = native
                .OfType<JsonObject>()
                .Select(item => item["id"]?.GetValue<string>())
                .Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var playlist in visible)
            {
                if (!nativeIds.Add(playlist.ProtocolId)) continue;
                native.Add(ToJsonPlaylist(playlist));
                added++;
            }

            return added == 0 ? null : Encoding.UTF8.GetBytes(document!.ToJsonString());
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static bool IsSuccessfulNativeResponse(byte[] body, NativeFormat format)
    {
        try
        {
            return format == NativeFormat.Json
                ? JsonNode.Parse(Encoding.UTF8.GetString(body))?["subsonic-response"]?["status"]?.GetValue<string>() == "ok"
                : XDocument.Parse(Encoding.UTF8.GetString(body)).Root?.Attribute("status")?.Value == "ok";
        }
        catch (JsonException)
        {
            return false;
        }
        catch (XmlException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[]? MergeXml(
        byte[] body,
        IReadOnlyList<VirtualPlaylistReadModel> visible)
    {
        try
        {
            var document = XDocument.Parse(Encoding.UTF8.GetString(body), LoadOptions.PreserveWhitespace);
            var root = document.Root;
            if (root?.Attribute("status")?.Value != "ok") return null;
            var ns = root.Name.Namespace;
            var playlists = root.Element(ns + "playlists");
            if (playlists == null)
            {
                playlists = new XElement(ns + "playlists");
                root.Add(playlists);
            }

            var nativeIds = playlists.Elements(ns + "playlist")
                .Select(item => item.Attribute("id")?.Value)
                .Where(id => id != null)
                .ToHashSet(StringComparer.Ordinal);
            var added = 0;
            foreach (var playlist in visible)
            {
                if (!nativeIds.Add(playlist.ProtocolId)) continue;
                playlists.Add(ToXmlPlaylist(playlist, ns));
                added++;
            }

            return added == 0
                ? null
                : Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting));
        }
        catch (XmlException)
        {
            return null;
        }
    }

    private static JsonObject ToJsonPlaylist(VirtualPlaylistReadModel playlist)
    {
        var result = new JsonObject
        {
            ["id"] = playlist.ProtocolId,
            ["name"] = playlist.Name,
            ["owner"] = "allstarr",
            ["public"] = false,
            ["songCount"] = playlist.Tracks.Count
        };
        AddOptionalPlaylistFields(result, playlist);
        return result;
    }

    private static XElement ToXmlPlaylist(VirtualPlaylistReadModel playlist, XNamespace ns)
    {
        var result = new XElement(ns + "playlist",
            new XAttribute("id", playlist.ProtocolId),
            new XAttribute("name", playlist.Name),
            new XAttribute("owner", "allstarr"),
            new XAttribute("public", "false"),
            new XAttribute("songCount", playlist.Tracks.Count));
        if (playlist.Description != null)
            result.Add(new XAttribute("comment", playlist.Description));
        if (TryGetDurationSeconds(playlist, out var duration))
            result.Add(new XAttribute("duration", duration));
        if (playlist.ArtworkReferenceKey != null)
            result.Add(new XAttribute("coverArt", playlist.ArtworkReferenceKey));
        return result;
    }

    private static void AddOptionalPlaylistFields(
        JsonObject result,
        VirtualPlaylistReadModel playlist)
    {
        if (playlist.Description != null) result["comment"] = playlist.Description;
        if (TryGetDurationSeconds(playlist, out var duration)) result["duration"] = duration;
        if (playlist.ArtworkReferenceKey != null) result["coverArt"] = playlist.ArtworkReferenceKey;
    }

    private static bool TryGetDurationSeconds(
        VirtualPlaylistReadModel playlist,
        out long duration)
    {
        duration = 0;
        if (playlist.Tracks.Any(track => !track.DurationMilliseconds.HasValue)) return false;
        try
        {
            duration = checked(playlist.Tracks.Sum(track => track.DurationMilliseconds!.Value) / 1000);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private enum NativeFormat
    {
        Json,
        Xml
    }
}
