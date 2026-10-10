using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;

namespace allstarr.Core.Matching;

public static class CanonicalCatalogKeys
{
    private const string DefaultCatalog = "default";

    public static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    public static string DefaultPublicId(Guid recordingId) =>
        CanonicalRecordingRecord.DefaultPublicId(recordingId);

    public static string ProviderTrackNamespace(
        string providerId,
        ProviderResourceKind resourceKind,
        string? catalog,
        ProviderIdentityScope scope,
        Guid? providerAccountId)
    {
        providerId = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (resourceKind != ProviderResourceKind.Track)
        {
            throw new ArgumentOutOfRangeException(
                nameof(resourceKind),
                "Canonical recording aliases require a track resource.");
        }

        catalog = catalog == null
            ? DefaultCatalog
            : ProviderContractValidation.Catalog(catalog, nameof(catalog));
        if (scope is not ProviderIdentityScope.Catalog and not ProviderIdentityScope.Account ||
            scope == ProviderIdentityScope.Catalog && providerAccountId.HasValue ||
            scope == ProviderIdentityScope.Account && !providerAccountId.HasValue)
        {
            throw new ArgumentException(
                "Provider alias scope and account must describe one exact identity boundary.",
                nameof(scope));
        }

        var descriptor = string.Join(
            '\n',
            providerId,
            resourceKind.ToString(),
            catalog,
            scope.ToString(),
            providerAccountId?.ToString("D") ?? string.Empty);
        return $"provider:{Hash(descriptor)}";
    }

    public static string NativeTrackNamespace(string protocol, string backendInstanceId)
    {
        protocol = ProviderContractValidation.Catalog(protocol, nameof(protocol));
        backendInstanceId = ProviderContractValidation.RequiredText(
            backendInstanceId,
            nameof(backendInstanceId),
            200);
        return $"native:{Hash($"{protocol}\n{backendInstanceId}")}";
    }
}
