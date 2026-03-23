namespace allstarr.Services.Common;

/// <summary>
/// Represents the quality tier requested by a client for streaming.
/// Used to map client transcoding parameters to provider-specific quality levels.
/// The .env quality setting acts as a ceiling — client requests can only go equal or lower.
/// </summary>
public enum StreamQuality
{
    /// <summary>
    /// Use the quality configured in .env / appsettings (default behavior).
    /// This is the "Lossless" / "no transcoding" selection in a client.
    /// </summary>
    Original,
    
    /// <summary>
    /// High quality lossy (e.g., 320kbps AAC/MP3).
    /// Covers client selections: 320K, 256K, 192K.
    /// Maps to: SquidWTF HIGH, Deezer MP3_320, Qobuz MP3_320.
    /// </summary>
    High,
    
    /// <summary>
    /// Low quality lossy (e.g., 96-128kbps AAC/MP3).
    /// Covers client selections: 128K, 64K.
    /// Maps to: SquidWTF LOW, Deezer MP3_128, Qobuz MP3_320 (lowest available).
    /// </summary>
    Low
}

/// <summary>
/// Parses Jellyfin client transcoding query parameters to determine
/// the requested stream quality tier for external tracks.
/// 
/// Typical client quality options: Lossless, 320K, 256K, 192K, 128K, 64K
/// These are mapped to StreamQuality tiers which providers then translate
/// to their own quality levels, capped at the .env ceiling.
/// </summary>
public static class StreamQualityHelper
{
    /// <summary>
    /// Parses the request query string to determine what quality the client wants.
    /// Jellyfin clients send parameters like AudioBitRate, MaxStreamingBitrate,
    /// AudioCodec, TranscodingContainer when requesting transcoded streams.
    /// </summary>
    public static StreamQuality ParseFromQueryString(IQueryCollection query)
    {
        // Check for explicit audio bitrate (e.g., AudioBitRate=128000)
        if (query.TryGetValue("AudioBitRate", out var audioBitRateVal) &&
            int.TryParse(audioBitRateVal.FirstOrDefault(), out var audioBitRate))
        {
            return MapBitRateToQuality(audioBitRate);
        }

        // Check for MaxStreamingBitrate (e.g., MaxStreamingBitrate=140000000 for lossless)
        if (query.TryGetValue("MaxStreamingBitrate", out var maxBitrateVal) &&
            long.TryParse(maxBitrateVal.FirstOrDefault(), out var maxBitrate))
        {
            // Very high values (>= 10Mbps) indicate lossless / no transcoding
            if (maxBitrate >= 10_000_000)
            {
                return StreamQuality.Original;
            }

            return MapBitRateToQuality((int)(maxBitrate / 1000));
        }

        // Check for audioBitRate (lowercase variant used by some clients)
        if (query.TryGetValue("audioBitRate", out var audioBitRateLower) &&
            int.TryParse(audioBitRateLower.FirstOrDefault(), out var audioBitRateLowerVal))
        {
            return MapBitRateToQuality(audioBitRateLowerVal);
        }

        // Check TranscodingContainer — if client requests mp3/aac, they want lossy
        if (query.TryGetValue("TranscodingContainer", out var container))
        {
            var containerStr = container.FirstOrDefault()?.ToLowerInvariant();
            if (containerStr is "mp3" or "aac" or "m4a")
            {
                // Container specified but no bitrate — default to High (320kbps)
                return StreamQuality.High;
            }
        }

        // No transcoding parameters — use original quality from .env
        return StreamQuality.Original;
    }

    /// <summary>
    /// Maps a bitrate value (in bps) to a StreamQuality tier.
    /// Client options are typically: Lossless, 320K, 256K, 192K, 128K, 64K
    /// 
    /// >= 192kbps → High (covers 320K, 256K, 192K selections)
    /// &lt; 192kbps → Low (covers 128K, 64K selections)
    /// </summary>
    private static StreamQuality MapBitRateToQuality(int bitRate)
    {
        // >= 192kbps → High (320kbps tier)
        // Covers client selections: 320K, 256K, 192K
        if (bitRate >= 192_000)
        {
            return StreamQuality.High;
        }

        // < 192kbps → Low (96-128kbps tier)
        // Covers client selections: 128K, 64K
        return StreamQuality.Low;
    }
}
