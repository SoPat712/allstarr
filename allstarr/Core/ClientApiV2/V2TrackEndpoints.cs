using allstarr.Core.Protocols;

namespace allstarr.Core.ClientApiV2;

/// <summary>A route's answer before it is written: a status and, for success, the body.</summary>
public readonly record struct V2Result(int Status, object? Body);

/// <summary>The track routes, shared by their controller and the batch call so both answer the same way.</summary>
public sealed class V2TrackEndpoints(V2TrackReader reader, V2TrackViewReader views)
{
    public static readonly IReadOnlySet<string> Includes = new HashSet<string>(StringComparer.Ordinal) { "album" };

    public async Task<V2Result> ListAsync(HttpContext http, ProtocolExecutionContext execution, string? ids, string? include)
    {
        if (!V2Query.TryIds(ids, out var requested) || !V2Query.TryNames(include, Includes, out var included)) return new(400, null);
        return new(200, await reader.ReadAsync(http, execution, requested, included.Contains("album")));
    }

    public async Task<V2Result> GetAsync(HttpContext http, ProtocolExecutionContext execution, string id, string? include, string? view)
    {
        if (!V2Query.TryNames(include, Includes, out var included) || !V2Query.TryNames(view, V2TrackViewReader.Names, out var requested))
            return new(400, null);
        var (result, sources) = await reader.ReadWithSourcesAsync(http, execution, [id], included.Contains("album"));
        if (result.Tracks.Count == 0) return new(404, null);
        var track = result.Tracks[0];
        var response = new V2TrackResponse(track) { Albums = result.Albums };
        if (requested.Count == 0) return new(200, response);
        var (read, status) = await views.ReadAsync(http, execution, sources[track.Id], requested);
        return new(200, response with { Views = read, ViewStatus = status });
    }
}
