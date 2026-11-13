using System.ComponentModel.DataAnnotations;
using allstarr.Core.ClientApiV2;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Controllers.ClientApi;

[Route("allstarr/v1/search")]
public sealed class SearchController(V2SearchStream search) : ClientApiControllerBase
{
    [HttpGet]
    [EndpointName("streamSearch")]
    [EndpointDescription("Streams external Jellyfin-shaped items with a lowercase numeric score from the merged search relevance scorer. " +
        "Every item also has source, the provider id that produced it or the library id for library items, and sourceName, its display name. Same-named library and service artists or albums are separate items. " +
        "In ranked snapshots, a library artist that matched songs or a person's answer tie to service artists is one combined item: source allstarr, sourceName Allstarr, and alsoOn, an array of {source, sourceName, id} listing the library artist first and then those service artists, which are not listed separately. " +
        "Besides the library artist, each name has at most one streaming artist: same-named artists from other services join it. In ranked snapshots that makes it a combined item the same way, its own part first in alsoOn; the default shape leaves them out. " +
        "In ranked snapshots, a service artist with a library artist's name that it is not tied to has namesakes, an array of {id, name} of those library artists. " +
        "The default shape emits {provider,items} batches and a final {done:true,late} line. " +
        "With shape=ranked, every line replaces the previous snapshot: {revision,done,providers,top,items,sections}, including library (Jellyfin) matches in top, items and sections ranked by merged search relevance scorer rules. " +
        "Revisions increase from 1; sections contains only requested songs, albums, artists and playlists, each capped at limit. " +
        "Items mixes requested types using the merged search ranking and is also capped at limit. Omitted limit defaults to 40 for ranked snapshots and 20 for provider batches. " +
        "Top selects an exact or near-exact artist, otherwise the best item, and also appears in its section; an owned exact match wins top over external lookalikes and an exact-name artist remains pinned. " +
        "Items retains top where scoring places it; clients rendering top separately skip that Id in items. " +
        "Items and sections rank by score with a five-point margin required to reorder existing items; native rows win ties. " +
        "The first snapshot waits for the preferred provider plus another answer, or 400 ms; intermediate snapshots are at least 250 ms apart. " +
        "The final snapshot flushes immediately when all answers are in, with done:true and late; earlier snapshots omit late. Provider answers are accepted for 2.75 seconds, " +
        "with up to 250 ms more for the final flush. Stable item Ids allow clients to animate changes without sorting. " +
        "Snapshot types: revision is an integer, done is a boolean, providers and late are arrays of provider-id strings, " +
        "top is an item or null, items is an item array, and sections is an object whose songs, albums, artists and playlists values are item arrays. " +
        "Missing keys mean empty: missing arrays or section keys are empty arrays, a missing sections object is empty, and a missing or null top has no item. " +
        "Items retain the existing Jellyfin item JSON plus numeric score and a stable string Id (library items keep their native Ids). All renders top followed by items, deduplicated by Id. " +
        "The searchStreamRanked capability is advertised only when the ranked snapshot shape is available; searchStreamRankedLibrary is advertised when library matches are included.")]
    [ProducesResponseType<string>(200, "application/x-ndjson")]
    public async Task<IActionResult> Get([FromQuery, Required] string q, [FromQuery, Range(1, 200)] int limit = 20,
        [FromQuery] string? types = null, [FromQuery] string? shape = null)
    {
        var query = q.Trim();
        var requested = (types ?? "Audio,MusicAlbum,MusicArtist,Playlist")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ranked = string.Equals(shape, "ranked", StringComparison.OrdinalIgnoreCase);
        if (ranked && !Request.Query.ContainsKey("limit")) limit = 40;
        if (shape != null && !ranked || query.Length is < 1 or > 200 || requested.Count == 0 ||
            requested.Any(type => !new[] { "Audio", "MusicAlbum", "MusicArtist", "Playlist" }.Contains(type, StringComparer.OrdinalIgnoreCase)))
            return BadRequest();
        return await search.StreamAsync(HttpContext, Execution, query, limit, requested, ranked);
    }
}
