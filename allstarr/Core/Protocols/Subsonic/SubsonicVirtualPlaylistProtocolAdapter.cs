using System.Xml.Linq;
using allstarr.Core.Matching;
using allstarr.Core.Playlists;
using allstarr.Core.Storage;
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
}
