using allstarr.Core.Capabilities;

namespace allstarr.Core.ClientApi;

public sealed record LyricsSearchResponse(IReadOnlyList<LyricsSearchResult> Results, bool Truncated);
public sealed record LyricsSearchResult(string ItemId, string Title, string Artist, string? Album, string? AlbumId,
    bool IsExternal, LyricsSearchLine Line, IReadOnlyList<LyricsSearchHighlight> Highlights, double Score, RichLyricsSource Source);
public sealed record LyricsSearchLine(string Text, long? StartMs, long? EndMs, string? Before, string? After);
public sealed record LyricsSearchHighlight(int Start, int Length);
