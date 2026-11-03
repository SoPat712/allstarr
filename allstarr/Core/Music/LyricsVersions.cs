using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

internal static class LyricsVersions
{
    public static string ProviderId(string sourceId) => sourceId.IndexOf(':') is var separator && separator >= 0 ? sourceId[..separator] : sourceId;

    public static string Validate(string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        ProviderContractValidation.ProviderId(ProviderId(sourceId), nameof(sourceId));
        if (sourceId.Length > 100 || sourceId.Any(char.IsControl) || sourceId.EndsWith(':'))
            throw new ArgumentException("A bounded lyrics source ID is required.", nameof(sourceId));
        return sourceId;
    }

    public static string SourceId(string provider, ProviderLyricsMetadata? metadata)
    {
        var key = metadata?.VersionKey;
        if (string.IsNullOrWhiteSpace(key))
        {
            static string Normalize(string? value) => string.Join(' ', (value ?? "").Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            var identity = JsonSerializer.Serialize(new[] { Normalize(metadata?.Title), Normalize(metadata?.Artist),
                Normalize(metadata?.Album), metadata?.DurationMs?.ToString(CultureInfo.InvariantCulture) ?? "" });
            key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..16];
        }
        return provider + ":" + key;
    }

    public static IReadOnlyList<LyricsInfo> Candidates(string provider, LyricsInfo? lyrics)
    {
        if (lyrics == null) return [];
        if (lyrics.Versions == null) return [lyrics];
        return lyrics.Versions.Where(version => RichLyricsMapper.Map(version) != null)
            .DistinctBy(version => SourceId(provider, version.Metadata)).Take(5).ToArray();
    }

    public static LyricsInfo Map(ProviderLyricsResult result, string provider, int? duration, string? provenance = null) => new()
    {
        TrackName = result.Metadata?.Title ?? "",
        ArtistName = result.Metadata?.Artist ?? "",
        AlbumName = result.Metadata?.Album ?? "",
        Duration = result.Metadata?.DurationMs is { } milliseconds ? (int)Math.Clamp((milliseconds + 999) / 1000, 0, 86400) : duration ?? 0,
        PlainLyrics = result.Format == ProviderLyricsFormat.PlainText ? result.Content : null,
        SyncedLyrics = result.Format == ProviderLyricsFormat.PlainText ? null : result.Content,
        Source = provider,
        Revision = result.Revision,
        TimedLines = result.TimedLines,
        CacheProvenance = provenance ?? result.CacheProvenance,
        Attribution = result.Attribution,
        Metadata = result.Metadata,
        Versions = result.Versions?.Where(version => version.Availability == ProviderLyricsAvailabilityState.Available && !string.IsNullOrWhiteSpace(version.Content))
            .Select(version => Map(version with { Versions = null }, SourceId(provider, version.Metadata), duration, provenance ?? result.CacheProvenance)).Take(5).ToArray()
    };
}
