namespace allstarr.Core.Capabilities;

public enum ProviderResourceKind
{
    Unknown = 0,
    Track = 1,
    Album = 2,
    Artist = 3,
    Playlist = 4,
    Lyrics = 5,
    MusicVideo = 6
}

/// <summary>
/// An immutable provider-native identity. Account access never belongs in this value.
/// </summary>
public sealed record ProviderExternalResourceId
{
    public ProviderExternalResourceId(
        string providerId,
        ProviderResourceKind resourceKind,
        string value,
        string? catalog = null)
    {
        if (!Enum.IsDefined(resourceKind) || resourceKind == ProviderResourceKind.Unknown)
        {
            throw new ArgumentOutOfRangeException(nameof(resourceKind));
        }

        ProviderId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        ResourceKind = resourceKind;
        Value = ProviderContractValidation.RequiredText(value, nameof(value), 500);
        Catalog = catalog == null
            ? null
            : ProviderContractValidation.Catalog(catalog, nameof(catalog));
    }

    public string ProviderId { get; }

    public ProviderResourceKind ResourceKind { get; }

    public string Value { get; }

    public string? Catalog { get; }

    public void RequireOwner(string providerId, ProviderResourceKind resourceKind)
    {
        var expectedProvider = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (!ProviderId.Equals(expectedProvider, StringComparison.Ordinal) || ResourceKind != resourceKind)
        {
            throw new ArgumentException(
                "The external resource ID does not belong to the requested provider and resource kind.",
                nameof(providerId));
        }
    }

    public override string ToString() =>
        $"ProviderExternalResourceId {{ ProviderId = {ProviderId}, ResourceKind = {ResourceKind}, Catalog = {Catalog ?? "default"}, Value = \u003Copaque\u003E }}";
}
