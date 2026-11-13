using allstarr.Core.ClientApi;
using allstarr.Core.Music;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApiV2;

public sealed class V2TrackResources(ItemLyricsReader lyrics, ItemMatchReader matches, ItemAppearancesReader appearances)
{
    public Task<V2Result> LyricsSourcesAsync(HttpContext http, ProtocolExecutionContext execution, string id, bool includeLyrics) =>
        WithSourceAsync(id, source => lyrics.SourcesAsync(http, execution, source, includeLyrics));

    public Task<V2Result> ChooseLyricsAsync(HttpContext http, ProtocolExecutionContext execution, string id, SetClientLyricsSourceRequest request) =>
        WithSourceAsync(id, source => lyrics.SetAsync(http, execution, source, request));

    public Task<V2Result> ResetLyricsAsync(HttpContext http, ProtocolExecutionContext execution, string id, ClearClientLyricsSourceRequest request) =>
        WithSourceAsync(id, source => lyrics.ClearAsync(http, execution, source, request));

    public Task<V2Result> MatchAsync(HttpContext http, ProtocolExecutionContext execution, string id) =>
        WithSourceAsync(id, source => matches.ReadAsync(http, execution, source));

    public Task<V2Result> ChooseMatchAsync(HttpContext http, ProtocolExecutionContext execution, string id, SetClientMatchRequest request) =>
        WithSourceAsync(id, source => matches.SetAsync(http, execution, source, request));

    public Task<V2Result> ResetMatchAsync(HttpContext http, ProtocolExecutionContext execution, string id, string expectedRevision) =>
        WithSourceAsync(id, source => matches.ClearAsync(http, execution, source, expectedRevision));

    public Task<V2Result> RejectMatchAsync(HttpContext http, ProtocolExecutionContext execution, string id, RejectClientMatchRequest request) =>
        WithSourceAsync(id, source => matches.RejectAsync(http, execution, source, request));

    public Task<V2Result> AppearancesAsync(HttpContext http, ProtocolExecutionContext execution, string id) =>
        WithSourceAsync(id, async source =>
        {
            var result = await appearances.ReadAsync(http, execution, source);
            V2Ids.TryDecode(id, V2IdKind.Track, out var decoded);
            return new V2TrackAppearances(decoded.Recording is { } recording ? V2Ids.Track(recording) : V2Ids.TrackSource(source),
                result.SongCount, result.Appearances.Select(item => new V2Appearance(item.Kind,
                    item.Kind == "mix" ? V2Ids.Mix(Guid.Parse(item.Id)) : V2Ids.Playlist(item.Id), item.Title, item.Subtitle,
                    item.CoverUrl, item.MatchedSongCount, item.PlaylistId == null ? null : V2Ids.Playlist(item.PlaylistId))).ToArray());
        });

    internal static async Task<V2Result> WithSourceAsync<T>(string id, Func<string, Task<T?>> read) where T : class
    {
        if (!V2Ids.TryDecode(id, V2IdKind.Track, out var decoded)) return new(400, null);
        return await ReadAsync(() => read(decoded.Recording is { } recording ? CanonicalRecordingRecord.DefaultPublicId(recording) : decoded.Source!));
    }

    internal static async Task<V2Result> ReadAsync<T>(Func<Task<T?>> read) where T : class
    {
        try
        {
            var result = await read();
            return new(result == null ? 404 : 200, result);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException) { return new(404, null); }
        catch (LyricsChoiceConflictException) { return new(409, null); }
        catch (DbUpdateConcurrencyException) { return new(409, null); }
        catch (Exception error) when (DbErrors.IsTransientConflict(error) || DbErrors.IsUniqueViolation(error)) { return new(409, null); }
        catch (ArgumentException) { return new(400, null); }
        catch (HttpRequestException) { return new(503, null); }
    }
}
