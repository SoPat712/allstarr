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
        int? durationSeconds = null)
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
    }

    public Guid CanonicalRecordingId { get; }

    public ProviderExternalResourceId ProviderTrackId { get; }

    public bool AvailabilityOnly { get; }

    public ProviderLyricsFormat? PreferredFormat { get; }

    public string? TrackTitle { get; }

    public IReadOnlyList<string> ArtistNames { get; }

    public string? AlbumTitle { get; }

    public int? DurationSeconds { get; }
}

public sealed record ProviderLyricsResult
{
    public ProviderLyricsResult(
        ProviderLyricsAvailabilityState availability,
        string source,
        ProviderLyricsFormat? format = null,
        string? content = null,
        string? revision = null)
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
            (format.HasValue || content != null))
        {
            throw new ArgumentException("Unavailable or restricted lyrics cannot include content.", nameof(content));
        }

        if (content != null && !format.HasValue)
        {
            throw new ArgumentException("Lyrics content requires an explicit format.", nameof(format));
        }

        Availability = availability;
        Source = ProviderContractValidation.RequiredText(source, nameof(source), 200);
        Format = format;
        Content = ProviderContractValidation.OptionalContent(content, nameof(content), 2_000_000);
        Revision = ProviderContractValidation.OptionalText(revision, nameof(revision), 300);
    }

    public ProviderLyricsAvailabilityState Availability { get; }

    public string Source { get; }

    public ProviderLyricsFormat? Format { get; }

    public string? Content { get; }

    public string? Revision { get; }
}

public interface IProviderLyricsCapability : IProviderCapability
{
    Task<ProviderOutcome<ProviderLyricsResult>> FetchLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request);
}
