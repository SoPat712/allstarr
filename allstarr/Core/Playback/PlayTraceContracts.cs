using System.Text.RegularExpressions;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Operations;
using allstarr.Models.Domain;

namespace allstarr.Core.Playback;

public sealed record PlayTraceMetadata(string? Title = null, string? Artist = null, string? Album = null,
    long? DurationMilliseconds = null, string? Artwork = null, bool? IsExplicit = null);
public sealed record PlayTraceRequest(string Protocol, string? ItemId, string? Client, string? Device, PlayTraceMetadata Presented);
public sealed record PlayTraceResolution(Guid? CanonicalRecordingId = null, Guid? ExternalSnapshotId = null,
    Guid? DecisionId = null, string? State = null, double? Score = null, string AuthorityScope = "automatic",
    string? Provider = null, string? Route = null, string? AccountLabel = null, bool? Cached = null,
    string? Quality = null, Guid? AuthorityId = null, long? AuthorityRevision = null, string? AuthorityKind = null, string? ExternalId = null);
public sealed record PlayTraceDelivery(long? MaxBitrate, int? MaxSampleRate, int? MaxChannels, int? MaxBitDepth,
    bool Converted, string? Container, string? Codec, long StartTimeTicks,
    IReadOnlyList<string> Containers, IReadOnlyList<string> Codecs, allstarr.Core.Capabilities.ProviderMediaFormat? Output = null);
public sealed record PlayTraceTiming(double ElapsedMs, double? HeadersMs, double? FirstByteMs,
    IReadOnlyList<PlaybackStartPhaseTiming> Phases, int OmittedPhases);
public sealed record PlayTraceData(Guid OwnerUserId, PlayTraceRequest Request, PlayTraceResolution Resolution,
    TrackMediaFacts? Received = null, PlayTraceDelivery? Delivery = null);
public sealed record PlayTraceDetail(Guid Id, DateTimeOffset StartedAt, string Outcome, PlayTraceRequest Request,
    PlayTraceResolution Resolution, TrackMediaFacts? Received, PlayTraceDelivery? Delivery,
    IReadOnlyList<string> Differences, PlayTraceTiming Timing, Guid? ListenerId = null);
public sealed record PlayTraceVersionRequest(string TargetType, Guid? LibraryTrackId = null, string? ExternalProvider = null,
    string? ExternalId = null, string AuthorityScope = "personal", ManualAuthorityRevision? ExpectedAuthority = null);
public sealed record PlayTraceForgetRequest(string AuthorityScope = "personal", ManualAuthorityRevision? ExpectedAuthority = null);
public sealed record PlayTraceReportRequest(Guid RequestId, string Note);

public static partial class PlayTraceText
{
    public static string? Clean(string? value, int limit = 200) => ProblemReportText.Clean(value == null ? null :
        Identity().Replace(Address().Replace(value[..Math.Min(value.Length, 8192)], "<redacted-address>"), "<redacted-identity>"), limit);
    internal static string? Reference(string? value) => ProblemReportText.IsReference(value) &&
        !Address().IsMatch(value!) ? value : null;
    internal static PlayTraceMetadata Metadata(Song song) => new(Clean(song.Title), Clean(song.Artist), Clean(song.Album),
        song.PreciseDuration?.TotalMilliseconds is { } duration && duration is > 0 and < 86400000 ? (long)duration :
            song.Duration is > 0 and < 86400 ? song.Duration * 1000L : null,
        string.IsNullOrWhiteSpace(song.CoverArtUrl) ? null : "available",
        song.ExplicitContentLyrics switch { 0 or 3 => false, 1 => true, _ => null });
    internal static TrackMediaFacts? Facts(TrackMediaFacts? facts) => facts == null ? null : facts with
    {
        Media = new(Clean(facts.Media.MimeType) ?? "application/octet-stream", Reference(facts.Media.Container) ?? "unknown",
            Reference(facts.Media.Codec) ?? "unknown", facts.Media.Bitrate, facts.Media.SampleRate, facts.Media.BitDepth, facts.Media.Channels),
        DurationSeconds = facts.DurationSeconds is > 0 and < 86400 ? facts.DurationSeconds : null,
        Tags = facts.Tags == null ? null : new(Clean(facts.Tags.Title), Clean(facts.Tags.Artist), Clean(facts.Tags.Album),
            Reference(facts.Tags.Isrc), facts.Tags.IsExplicit)
    };
    [GeneratedRegex(@"\b(?:\d{1,3}\.){3}\d{1,3}\b|(?<![a-z0-9])(?:[a-f0-9]{0,4}:){2,}[a-f0-9:.]*(?:%[a-z0-9]+)?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Address();
    [GeneratedRegex(@"\b[a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12}\b|\b[a-f0-9]{32}\b|[a-z0-9._%+-]+@[a-z0-9.-]+\.[a-z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Identity();
}

public static class PlayTraceDifferences
{
    public static IReadOnlyList<string> Compare(PlayTraceMetadata presented, TrackMediaFacts? received)
    {
        var flags = new List<string>();
        if (presented.DurationMilliseconds is > 0 && received?.DurationSeconds is { } seconds && seconds > 0 &&
            Math.Abs(presented.DurationMilliseconds.Value - seconds * 1000) > Math.Max(5000, presented.DurationMilliseconds.Value * .05))
            flags.Add("duration-mismatch");
        static string Normalize(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        static bool Different(string? expected, string? actual) => !string.IsNullOrWhiteSpace(expected) && !string.IsNullOrWhiteSpace(actual) &&
            Normalize(expected) != Normalize(actual);
        if (Different(presented.Title, received?.Tags?.Title)) flags.Add("title-mismatch");
        if (Different(presented.Artist, received?.Tags?.Artist)) flags.Add("artist-mismatch");
        if (presented.IsExplicit.HasValue && received?.Tags?.IsExplicit is { } explicitReceived && presented.IsExplicit != explicitReceived)
            flags.Add("explicit-clean-mismatch");
        return flags;
    }
}
