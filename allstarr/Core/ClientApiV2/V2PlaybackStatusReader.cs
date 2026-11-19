using allstarr.Core.ClientApi;
using allstarr.Core.Protocols;

namespace allstarr.Core.ClientApiV2;

/// <summary>
/// Reads a playback by the id resolve returned, or by the X-Allstarr-Playback-Request id a media-server stream
/// (/Audio/{id}/stream) answered with. The second is kept with the playback start, so it still answers after the
/// stream closes, as it does when an app has already pulled the whole file.
/// </summary>
public sealed class V2PlaybackStatusReader(V2ServingStreams serving, PlaybackReader started)
{
    public async Task<V2Result> ReadAsync(HttpContext http, ProtocolExecutionContext execution, string id)
    {
        // Apps that hold the id as a UUID send it back with hyphens.
        if (Guid.TryParseExact(id, "D", out var hyphenated)) id = hyphenated.ToString("N");
        var status = serving.Status(execution, id);
        if (status.Status != 404 || !Guid.TryParseExact(id, "N", out var request)) return status;
        if (await started.ReadAsync(http, execution, request) is not { } playback) return status;
        return new(200, new V2PlaybackStatus(id, Format(playback.Delivered), null)
        {
            Provider = playback.Provider is { } provider ? new V2Provider(provider.Id, provider.Name, provider.IconUrl, provider.GlyphUrl) : null,
            Cached = playback.Cached,
            RouteReason = playback.RouteReason,
            TrackId = playback.ItemId,
            StartedAt = playback.StartedAt
        });
    }

    private static V2PlaybackFormat? Format(PlaybackDelivered? delivered) =>
        delivered?.Codec is not { Length: > 0 } codec ? null : new(delivered.Format ?? codec, codec, delivered.Bitrate,
            delivered.SampleRate, delivered.BitDepth, codec is "flac" or "alac" or "ape" or "wavpack" || codec.StartsWith("pcm", StringComparison.Ordinal));
}
