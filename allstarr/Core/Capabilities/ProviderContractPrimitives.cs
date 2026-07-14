namespace allstarr.Core.Capabilities;

public sealed record ProviderPageRequest
{
    public ProviderPageRequest(int limit = 50, string? cursor = null)
    {
        if (limit is < 1 or > 200)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Page limits must be between 1 and 200.");
        }

        Limit = limit;
        Cursor = ProviderContractValidation.OptionalText(cursor, nameof(cursor), 2000);
    }

    public int Limit { get; }

    public string? Cursor { get; }
}

public sealed record ProviderPage<T>
{
    public ProviderPage(
        string providerId,
        IEnumerable<T> items,
        string? nextCursor = null,
        bool isPartial = false,
        string? snapshotVersion = null)
    {
        ArgumentNullException.ThrowIfNull(items);
        ProviderId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        Items = ProviderContractValidation.Copy(items);
        NextCursor = ProviderContractValidation.OptionalText(nextCursor, nameof(nextCursor), 2000);
        IsPartial = isPartial;
        SnapshotVersion = ProviderContractValidation.OptionalText(
            snapshotVersion,
            nameof(snapshotVersion),
            300);
    }

    public string ProviderId { get; }

    public IReadOnlyList<T> Items { get; }

    public string? NextCursor { get; }

    public bool IsPartial { get; }

    public string? SnapshotVersion { get; }
}

public sealed record ProviderArtworkReference
{
    public ProviderArtworkReference(
        ProviderExternalResourceId? resourceId = null,
        Uri? publicUri = null,
        string? revision = null)
    {
        if (resourceId == null && publicUri == null)
        {
            throw new ArgumentException("Artwork requires a provider resource ID or public URI.");
        }

        if (publicUri != null &&
            (!publicUri.IsAbsoluteUri ||
             !publicUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException("Public artwork URIs must use HTTPS.", nameof(publicUri));
        }

        ResourceId = resourceId;
        PublicUri = publicUri;
        Revision = ProviderContractValidation.OptionalText(revision, nameof(revision), 300);
    }

    public ProviderExternalResourceId? ResourceId { get; }

    public Uri? PublicUri { get; }

    public string? Revision { get; }
}

public sealed record ProviderMediaFormat
{
    public ProviderMediaFormat(
        string mimeType,
        string container,
        string codec,
        int? bitrate = null,
        int? sampleRate = null,
        int? bitDepth = null,
        int? channels = null)
    {
        if (bitrate is <= 0 || sampleRate is <= 0 || bitDepth is <= 0 || channels is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bitrate),
                "Media numeric facts must be positive when supplied.");
        }

        MimeType = ProviderContractValidation.RequiredText(mimeType, nameof(mimeType), 100);
        Container = ProviderContractValidation.Catalog(container, nameof(container));
        Codec = ProviderContractValidation.Catalog(codec, nameof(codec));
        Bitrate = bitrate;
        SampleRate = sampleRate;
        BitDepth = bitDepth;
        Channels = channels;
    }

    public string MimeType { get; }

    public string Container { get; }

    public string Codec { get; }

    public int? Bitrate { get; }

    public int? SampleRate { get; }

    public int? BitDepth { get; }

    public int? Channels { get; }
}

public interface IProviderCapability
{
    string ProviderId { get; }

    ProviderCapabilityKind Capability { get; }
}
