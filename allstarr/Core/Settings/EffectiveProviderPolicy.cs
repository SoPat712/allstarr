using System.Collections.Immutable;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Settings;

public sealed record ProviderOrderPolicyDefinition(
    ProviderCapabilityKind Capability,
    string SettingKey,
    string BootstrapKey,
    string DefaultValue);

public static class ProviderOrderPolicyCatalog
{
    public static ImmutableArray<ProviderOrderPolicyDefinition> Definitions { get; } =
    [
        new(ProviderCapabilityKind.Metadata, "Providers:MetadataOrder", "MULTI_PROVIDER_METADATA_ORDER", "apple-download,deezer,qobuz"),
        new(ProviderCapabilityKind.Download, "Providers:DownloadOrder", "MULTI_PROVIDER_DOWNLOAD_ORDER", "apple-download,deezer,qobuz"),
        new(ProviderCapabilityKind.Streaming, "Providers:StreamingOrder", "MULTI_PROVIDER_STREAMING_ORDER", "apple-download,deezer,qobuz"),
        new(ProviderCapabilityKind.Playlist, "Providers:PlaylistOrder", "MULTI_PROVIDER_PLAYLIST_ORDER", "spotify,apple-download,deezer,qobuz"),
        new(ProviderCapabilityKind.Lyrics, "Providers:LyricsOrder", "MULTI_PROVIDER_LYRICS_ORDER", "spotify,apple-download,lrclib")
    ];

    public static ProviderOrderPolicyDefinition? Find(ProviderCapabilityKind capability) =>
        Definitions.FirstOrDefault(item => item.Capability == capability);
}

public sealed record EffectiveProviderPolicySnapshot(
    Guid TenantId,
    ImmutableDictionary<ProviderCapabilityKind, ImmutableArray<string>> ProviderOrders,
    ImmutableHashSet<string> DisabledProviders,
    string AudioQuality,
    double LocalPreferenceWindow)
{
    public IReadOnlyList<string> GetProviderOrder(ProviderCapabilityKind capability) =>
        ProviderOrders.TryGetValue(capability, out var order) ? order : [];

    public IReadOnlyList<string> ApplyProviderAvailability(
        ProviderCapabilityKind capability,
        IEnumerable<string> availableProviders)
    {
        var available = availableProviders
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim().ToLowerInvariant())
            .Where(item => !DisabledProviders.Contains(item))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var availableSet = available.ToHashSet(StringComparer.Ordinal);
        return GetProviderOrder(capability)
            .Where(availableSet.Contains)
            .Concat(available.Order(StringComparer.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}

public interface IEffectiveProviderPolicyResolver
{
    Task<EffectiveProviderPolicySnapshot> ResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default);
}

public sealed class EffectiveProviderPolicyResolver(IDurableRuntimeSettings settings)
    : IEffectiveProviderPolicyResolver
{
    private static readonly string[] Keys =
    [
        .. ProviderOrderPolicyCatalog.Definitions.Select(item => item.SettingKey),
        "Providers:Disabled",
        AudioQualityPolicy.SettingKey,
        "Matching:LocalPreferencePercent"
    ];

    public async Task<EffectiveProviderPolicySnapshot> ResolveAsync(
        Guid tenantId,
        CancellationToken cancellationToken = default)
    {
        if (tenantId == Guid.Empty)
            throw new ArgumentException("A tenant is required.", nameof(tenantId));

        var values = await settings.GetManyAsync(tenantId, Keys, cancellationToken);
        var orders = ProviderOrderPolicyCatalog.Definitions.ToImmutableDictionary(
            item => item.Capability,
            item => ((string[])values[item.SettingKey].Value).ToImmutableArray());
        var disabled = ((string[])values["Providers:Disabled"].Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

        return new EffectiveProviderPolicySnapshot(
            tenantId,
            orders,
            disabled,
            (string)values[AudioQualityPolicy.SettingKey].Value,
            (int)values["Matching:LocalPreferencePercent"].Value / 100d);
    }
}
