using allstarr.Core.Capabilities;

namespace allstarr.Services.Common;

public enum StreamQuality
{
    Original,
    High,
    Low
}

public static class StreamQualityHelper
{
    public static ProviderAudioQuality FromSubsonicMaxBitRate(string? kilobitsPerSecond) =>
        long.TryParse(kilobitsPerSecond, out var value) && value > 0
            ? value < 192 ? ProviderAudioQuality.DataSaver : ProviderAudioQuality.Lossy
            : ProviderAudioQuality.Any;

    public static StreamQuality ParseFromQueryString(IQueryCollection query)
    {
        if (TryReadBitRate(query, "AudioBitRate", out var audioBitRate) ||
            TryReadBitRate(query, "audioBitRate", out audioBitRate))
            return MapBitRateToQuality(audioBitRate);

        if (RequestsLossyAudio(query, "AudioCodec") ||
            RequestsLossyAudio(query, "TranscodingContainer"))
            return StreamQuality.High;

        if (query.TryGetValue("MaxStreamingBitrate", out var maxBitrateVal) &&
            long.TryParse(maxBitrateVal.FirstOrDefault(), out var maxBitrate))
            return maxBitrate >= 10_000_000
                ? StreamQuality.Original
                : MapBitRateToQuality((int)maxBitrate);

        return StreamQuality.Original;
    }

    private static bool RequestsLossyAudio(IQueryCollection query, string key) =>
        query.TryGetValue(key, out var values) &&
        values.SelectMany(value => value?.Split(
                ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [])
            .Any(value => value.Equals("mp3", StringComparison.OrdinalIgnoreCase) ||
                          value.Equals("aac", StringComparison.OrdinalIgnoreCase) ||
                          value.Equals("m4a", StringComparison.OrdinalIgnoreCase));

    private static bool TryReadBitRate(IQueryCollection query, string key, out int bitRate)
    {
        bitRate = 0;
        return query.TryGetValue(key, out var value) && int.TryParse(value.FirstOrDefault(), out bitRate);
    }

    internal static StreamQuality MapBitRateToQuality(int bitRate) =>
        bitRate >= 192_000 ? StreamQuality.High : StreamQuality.Low;
}
