using System.Text.Json.Serialization;

namespace allstarr.Core.Capabilities;

public enum ProviderLyricsFormat
{
    PlainText,
    LineTimed,
    WordTimed
}

public enum ProviderLyricsAvailabilityState
{
    Available,
    Unavailable,
    Restricted
}

public sealed record ProviderLyricsRequest
{
    public ProviderLyricsRequest(
        Guid canonicalRecordingId,
        ProviderExternalResourceId providerTrackId,
        bool availabilityOnly = false,
        ProviderLyricsFormat? preferredFormat = null,
        string? trackTitle = null,
        IReadOnlyList<string>? artistNames = null,
        string? albumTitle = null,
        int? durationSeconds = null,
        string? isrc = null)
    {
        ArgumentNullException.ThrowIfNull(providerTrackId);
        if (canonicalRecordingId == Guid.Empty)
        {
            throw new ArgumentException("A canonical recording ID is required.", nameof(canonicalRecordingId));
        }

        providerTrackId.RequireOwner(providerTrackId.ProviderId, ProviderResourceKind.Track);
        if (preferredFormat.HasValue && !Enum.IsDefined(preferredFormat.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(preferredFormat));
        }

        CanonicalRecordingId = canonicalRecordingId;
        ProviderTrackId = providerTrackId;
        AvailabilityOnly = availabilityOnly;
        PreferredFormat = preferredFormat;
        TrackTitle = ProviderContractValidation.OptionalText(trackTitle, nameof(trackTitle), 1_000);
        ArtistNames = Array.AsReadOnly((artistNames ?? [])
            .Select(name => ProviderContractValidation.RequiredText(name, nameof(artistNames), 1_000))
            .ToArray());
        AlbumTitle = ProviderContractValidation.OptionalText(albumTitle, nameof(albumTitle), 1_000);
        if (durationSeconds is < 0 or > 24 * 60 * 60)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }
        DurationSeconds = durationSeconds;
        Isrc = ProviderContractValidation.OptionalText(isrc, nameof(isrc), 100);
    }

    public Guid CanonicalRecordingId { get; }

    public ProviderExternalResourceId ProviderTrackId { get; }

    public bool AvailabilityOnly { get; }

    public ProviderLyricsFormat? PreferredFormat { get; }

    public string? TrackTitle { get; }

    public IReadOnlyList<string> ArtistNames { get; }

    public string? AlbumTitle { get; }

    public int? DurationSeconds { get; }

    public string? Isrc { get; }
}

public sealed record ProviderLyricsTimedSegment
{
    public ProviderLyricsTimedSegment(string text, TimeSpan start, TimeSpan? duration = null,
        bool? isBackground = null, bool? synthetic = null)
    {
        LyricsTimingLimits.ValidateText(text);
        LyricsTimingLimits.ValidateTime(start, duration);
        Text = text;
        Start = start;
        Duration = duration;
        IsBackground = isBackground;
        Synthetic = synthetic;
    }
    public string Text { get; }
    public TimeSpan Start { get; }
    public TimeSpan? Duration { get; }
    public bool? IsBackground { get; }
    public bool? Synthetic { get; }
}

public sealed record ProviderLyricsTimedLine
{
    public ProviderLyricsTimedLine(string text, TimeSpan start, TimeSpan? duration = null,
        IReadOnlyList<ProviderLyricsTimedSegment>? segments = null)
    {
        LyricsTimingLimits.ValidateText(text);
        LyricsTimingLimits.ValidateTime(start, duration);
        Text = text;
        Start = start;
        Duration = duration;
        if (segments is { Count: > LyricsTimingLimits.MaximumElements } || segments?.Any(item => item == null) == true)
            throw new ArgumentException("Lyric segments exceed the supported bounds.", nameof(segments));
        Segments = segments is { Count: > 0 } ? Array.AsReadOnly(segments.ToArray()) : null;
    }
    public string Text { get; }
    public TimeSpan Start { get; }
    public TimeSpan? Duration { get; }
    public IReadOnlyList<ProviderLyricsTimedSegment>? Segments { get; }
}

public sealed record ProviderLyricsContributor
{
    public ProviderLyricsContributor(string username, string? url = null)
    {
        Username = ProviderContractValidation.RequiredText(username, nameof(username), 200);
        Url = IsPublicProfileUrl(url) ? url : null;
    }

    [JsonPropertyName("username")]
    public string Username { get; }
    [JsonPropertyName("url"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; }

    public static bool IsPublicProfileUrl(string? value) => value is { Length: <= 2_000 } &&
        !value.Any(char.IsControl) && !value.Contains('%') && !value.Contains('\\') && Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == Uri.UriSchemeHttps && uri.Host == "spicylyrics.org" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0 &&
        uri.AbsolutePath.StartsWith("/uid/", StringComparison.Ordinal) &&
        uri.AbsolutePath[5..] is { Length: > 0 } id && id.All(char.IsAsciiLetterOrDigit);
}

public sealed record ProviderLyricsAttribution
{
    public ProviderLyricsAttribution(string service, string catalog,
        ProviderLyricsContributor? uploader = null, ProviderLyricsContributor? maker = null)
    {
        Service = ProviderContractValidation.ProviderId(service, nameof(service));
        Catalog = ProviderContractValidation.Catalog(catalog, nameof(catalog));
        Uploader = uploader;
        Maker = maker;
    }

    [JsonPropertyName("service")]
    public string Service { get; }
    [JsonPropertyName("catalog")]
    public string Catalog { get; }
    [JsonPropertyName("uploader"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderLyricsContributor? Uploader { get; }
    [JsonPropertyName("maker"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderLyricsContributor? Maker { get; }
}

public sealed record ProviderLyricsResult
{
    public ProviderLyricsResult(
        ProviderLyricsAvailabilityState availability,
        string source,
        ProviderLyricsFormat? format = null,
        string? content = null,
        string? revision = null,
        IReadOnlyList<ProviderLyricsTimedLine>? timedLines = null,
        ProviderLyricsAttribution? attribution = null)
    {
        if (!Enum.IsDefined(availability))
        {
            throw new ArgumentOutOfRangeException(nameof(availability));
        }

        if (format.HasValue && !Enum.IsDefined(format.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(format));
        }

        if (availability != ProviderLyricsAvailabilityState.Available &&
            (format.HasValue || content != null || attribution != null))
        {
            throw new ArgumentException("Unavailable or restricted lyrics cannot include content.", nameof(content));
        }

        if (content != null && !format.HasValue)
        {
            throw new ArgumentException("Lyrics content requires an explicit format.", nameof(format));
        }

        Attribution = attribution;
        Availability = availability;
        Source = ProviderContractValidation.RequiredText(source, nameof(source), 200);
        Format = format;
        Content = ProviderContractValidation.OptionalContent(content, nameof(content), 2_000_000);
        Revision = ProviderContractValidation.OptionalText(revision, nameof(revision), 300);
        if (timedLines is { Count: > 0 })
        {
            if (availability != ProviderLyricsAvailabilityState.Available || string.IsNullOrWhiteSpace(Content))
                throw new ArgumentException("Timing content requires available lyrics text.", nameof(timedLines));
            if (timedLines.Count > LyricsTimingLimits.MaximumElements || timedLines.Any(line => line == null))
                throw new ArgumentException("Lyric lines exceed the supported bounds.", nameof(timedLines));
            var characters = Content.Length;
            var segments = 0;
            foreach (var line in timedLines)
            {
                characters = checked(characters + line.Text.Length);
                if (characters > LyricsTimingLimits.MaximumCharacters)
                    throw new ArgumentException("Lyric timing content is too large.", nameof(timedLines));
                foreach (var segment in line.Segments ?? [])
                {
                    characters = checked(characters + segment.Text.Length);
                    if (++segments > LyricsTimingLimits.MaximumElements || characters > LyricsTimingLimits.MaximumCharacters)
                        throw new ArgumentException("Lyric timing content is too large.", nameof(timedLines));
                }
            }
            TimedLines = Array.AsReadOnly(timedLines.ToArray());
        }
    }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProviderLyricsAttribution? Attribution { get; }

    public ProviderLyricsAvailabilityState Availability { get; }

    public string Source { get; }

    public ProviderLyricsFormat? Format { get; }

    public string? Content { get; }

    public string? Revision { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<ProviderLyricsTimedLine>? TimedLines { get; }
}

internal static class LyricsTimingLimits
{
    public const int MaximumElements = 50_000;
    public const int MaximumCharacters = 2_000_000;
    private static readonly TimeSpan MaximumTime = TimeSpan.FromHours(24);

    public static void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaximumCharacters || text.Contains('\r') || text.Contains('\n'))
            throw new ArgumentException("A lyric timing entry must contain one bounded line of text.", nameof(text));
    }

    public static void ValidateTime(TimeSpan start, TimeSpan? duration)
    {
        if (start < TimeSpan.Zero || start > MaximumTime)
            throw new ArgumentOutOfRangeException(nameof(start));
        if (duration is { } value && (value < TimeSpan.Zero || value > MaximumTime || start > MaximumTime - value))
            throw new ArgumentOutOfRangeException(nameof(duration));
    }
}

public interface IProviderLyricsCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request);
}
