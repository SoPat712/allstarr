using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Protocols;
using allstarr.Services.Jellyfin;

namespace allstarr.Core.ClientApiV2;

public sealed record V2LibrarySize(
    [property: JsonPropertyName("tracks")] int Tracks,
    [property: JsonPropertyName("durationMs")] long DurationMs,
    [property: JsonPropertyName("bytes"), Description("The files as they are, in bytes, for the tracks whose size the media server gives.")] long Bytes,
    [property: JsonPropertyName("sizedTracks"), Description("How many of the tracks `bytes` covers.")] int SizedTracks);

/// <summary>
/// How much music the listener's own libraries hold: how many songs, their total length and the size of the files as
/// they are. An app shows it before a whole-library download; the size at another quality follows from the length.
/// </summary>
public sealed class V2LibrarySizeReader(JellyfinProxyService jellyfin, ListeningLibraries libraries, TimeProvider clock)
{
    private const int Page = 500;
    // Reading every song's file size takes the media server a few seconds, and the answer moves slowly.
    private static readonly TimeSpan Kept = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, V2LibrarySize Size)> kept = new(StringComparer.Ordinal);

    public async Task<V2Result> ReadAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        var token = execution.CancellationToken;
        var access = await libraries.ResolveAsync(execution, token);
        if (!access.Succeeded) throw new HttpRequestException("The library is unavailable.");
        var key = $"{execution.RequireActor().EffectiveUserId:N}|{string.Join(',', access.LibraryIds.Order(StringComparer.Ordinal))}";
        if (kept.TryGetValue(key, out var saved) && clock.GetUtcNow() - saved.At < Kept) return new(200, saved.Size);
        int tracks = 0, sized = 0;
        long ticks = 0, bytes = 0;
        foreach (var library in access.LibraryIds)
            for (var start = 0; ; start += Page)
            {
                var (document, status) = await jellyfin.GetJsonAsync($"Users/{Uri.EscapeDataString(execution.VerifiedBackendPrincipalId)}/Items",
                    new Dictionary<string, string>
                    {
                        ["parentId"] = library,
                        ["recursive"] = "true",
                        ["includeItemTypes"] = "Audio",
                        ["fields"] = "MediaSources",
                        ["enableUserData"] = "false",
                        ["enableImages"] = "false",
                        ["enableTotalRecordCount"] = "false",
                        ["sortBy"] = "SortName",
                        ["startIndex"] = start.ToString(CultureInfo.InvariantCulture),
                        ["limit"] = Page.ToString(CultureInfo.InvariantCulture)
                    }, http.Request.Headers, token);
                using (document)
                {
                    if (status != 200 || document == null) throw new HttpRequestException("The library is unavailable.");
                    var read = 0;
                    foreach (var item in V2AlbumEndpoints.Items(document))
                    {
                        read++;
                        tracks++;
                        if (item.TryGetProperty("RunTimeTicks", out var length) && length.TryGetInt64(out var songTicks) && songTicks > 0) ticks += songTicks;
                        if (item.TryGetProperty("MediaSources", out var sources) && sources.ValueKind == JsonValueKind.Array && sources.GetArrayLength() > 0 &&
                            sources[0].TryGetProperty("Size", out var size) && size.TryGetInt64(out var songBytes) && songBytes > 0)
                        {
                            bytes += songBytes;
                            sized++;
                        }
                    }
                    if (read < Page) break;
                }
            }
        var result = new V2LibrarySize(tracks, ticks / TimeSpan.TicksPerMillisecond, bytes, sized);
        kept[key] = (clock.GetUtcNow(), result);
        return new(200, result);
    }
}
