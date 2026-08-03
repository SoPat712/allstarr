namespace allstarr.Core.Capabilities;

public interface IProviderRegistry
{
    IReadOnlyList<ProviderDescriptor> Providers { get; }

    bool TryGet(string providerId, out ProviderDescriptor? descriptor);

    ProviderDescriptor GetRequired(string providerId);

    IReadOnlyList<ProviderDescriptor> FindByCapability(
        ProviderCapabilityKind capability,
        bool includeNonOperational = false);

    bool TryGetCapability<TCapability>(
        string providerId,
        ProviderCapabilityKind capability,
        out TCapability? implementation)
        where TCapability : class, IProviderCapability;

    TCapability GetRequiredCapability<TCapability>(
        string providerId,
        ProviderCapabilityKind capability)
        where TCapability : class, IProviderCapability;
}

public interface IDynamicProviderRegistry
{
    void RegisterOrReplaceExtension(ProviderRegistration registration);
    bool RemoveExtension(string providerId);
}

public sealed record ProviderRegistration
{
    public ProviderRegistration(
        ProviderDescriptor descriptor,
        IEnumerable<IProviderCapability>? implementations = null)
    {
        Descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Implementations = ProviderContractValidation.Copy(implementations);
    }

    public ProviderDescriptor Descriptor { get; }

    public IReadOnlyList<IProviderCapability> Implementations { get; }
}

public sealed class ProviderRegistry : IProviderRegistry, IDynamicProviderRegistry
{
    private readonly object _mutationLock = new();
    private RegistrySnapshot _snapshot;

    public ProviderRegistry(IEnumerable<ProviderRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        var validated = registrations
            .Select(ProviderRegistrationValidator.Validate)
            .OrderBy(item => item.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        var duplicate = validated
            .GroupBy(item => item.Descriptor.Id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException(
                $"Provider ID '{duplicate.Key}' is registered more than once.");
        }

        _snapshot = BuildSnapshot(validated);
    }

    public IReadOnlyList<ProviderDescriptor> Providers => Volatile.Read(ref _snapshot).SortedProviders;

    private static RegistrySnapshot BuildSnapshot(
        IEnumerable<ProviderRegistration> registrations)
    {
        var values = registrations.ToArray();
        var providers = values.Select(item => item.Descriptor).ToDictionary(item => item.Id, StringComparer.Ordinal);
        var implementations = values
            .SelectMany(registration => registration.Implementations.Select(implementation => new
            {
                Key = (registration.Descriptor.Id, implementation.Capability),
                Implementation = implementation
            }))
            .ToDictionary(item => item.Key, item => item.Implementation);

        var sortedProviders = Array.AsReadOnly(providers.Values.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray());

        return new RegistrySnapshot(
            values.ToDictionary(item => item.Descriptor.Id, StringComparer.Ordinal), providers, sortedProviders, implementations);
    }

    public void RegisterOrReplaceExtension(ProviderRegistration registration)
    {
        var validated = ProviderRegistrationValidator.Validate(registration);
        if (validated.Descriptor.Origin != ProviderOrigin.Extension)
            throw new InvalidOperationException("Dynamic provider registrations must be extension-owned.");
        lock (_mutationLock)
        {
            var snapshot = _snapshot;
            if (snapshot.Registrations.TryGetValue(validated.Descriptor.Id, out var current) &&
                current.Descriptor.Origin != ProviderOrigin.Extension)
                throw new InvalidOperationException("An extension cannot replace a built-in provider.");
            var updated = snapshot.Registrations.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            updated[validated.Descriptor.Id] = validated;
            Volatile.Write(ref _snapshot, BuildSnapshot(updated.Values));
        }
    }

    public bool RemoveExtension(string providerId)
    {
        var id = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        lock (_mutationLock)
        {
            var snapshot = _snapshot;
            if (!snapshot.Registrations.TryGetValue(id, out var current) || current.Descriptor.Origin != ProviderOrigin.Extension)
                return false;
            var updated = snapshot.Registrations.Where(item => item.Key != id)
                .ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal);
            Volatile.Write(ref _snapshot, BuildSnapshot(updated.Values));
            return true;
        }
    }

    public bool TryGet(string providerId, out ProviderDescriptor? descriptor)
    {
        var id = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        return Volatile.Read(ref _snapshot).Providers.TryGetValue(id, out descriptor);
    }

    public ProviderDescriptor GetRequired(string providerId) =>
        TryGet(providerId, out var descriptor)
            ? descriptor!
            : throw new KeyNotFoundException($"Provider '{providerId}' is not registered.");

    public IReadOnlyList<ProviderDescriptor> FindByCapability(
        ProviderCapabilityKind capability,
        bool includeNonOperational = false)
    {
        if (!Enum.IsDefined(capability))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        return Array.AsReadOnly(Providers
            .Where(provider => provider.Capabilities.Any(item =>
                item.Capability == capability &&
                (includeNonOperational || item.HasUsableImplementation)))
            .ToArray());
    }

    public bool TryGetCapability<TCapability>(
        string providerId,
        ProviderCapabilityKind capability,
        out TCapability? implementation)
        where TCapability : class, IProviderCapability
    {
        var id = ProviderContractValidation.ProviderId(providerId, nameof(providerId));
        if (Volatile.Read(ref _snapshot).Implementations.TryGetValue((id, capability), out var registered) &&
            registered is TCapability typed)
        {
            implementation = typed;
            return true;
        }

        implementation = null;
        return false;
    }

    public TCapability GetRequiredCapability<TCapability>(
        string providerId,
        ProviderCapabilityKind capability)
        where TCapability : class, IProviderCapability =>
        TryGetCapability<TCapability>(providerId, capability, out var implementation)
            ? implementation!
            : throw new KeyNotFoundException(
                $"Provider '{providerId}' has no registered '{capability}' implementation.");

    private sealed record RegistrySnapshot(
        IReadOnlyDictionary<string, ProviderRegistration> Registrations,
        IReadOnlyDictionary<string, ProviderDescriptor> Providers,
        IReadOnlyList<ProviderDescriptor> SortedProviders,
        IReadOnlyDictionary<(string ProviderId, ProviderCapabilityKind Capability), IProviderCapability> Implementations);
}

public static class ProviderRegistrationValidator
{
    private static readonly IReadOnlyDictionary<ProviderCapabilityKind, Type> CapabilityInterfaces =
        new Dictionary<ProviderCapabilityKind, Type>
        {
            [ProviderCapabilityKind.Metadata] = typeof(IProviderMetadataCapability),
            [ProviderCapabilityKind.Streaming] = typeof(IProviderStreamingCapability),
            [ProviderCapabilityKind.Download] = typeof(IProviderDownloadCapability),
            [ProviderCapabilityKind.Playlist] = typeof(IProviderPlaylistCapability),
            [ProviderCapabilityKind.Lyrics] = typeof(IProviderLyricsCapability),
            [ProviderCapabilityKind.Intelligence] = typeof(IProviderIntelligenceCapability),
            [ProviderCapabilityKind.Health] = typeof(IProviderHealthProbeCapability)
        };

    public static ProviderRegistration Validate(ProviderRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        var descriptor = ProviderManifestValidator.Validate(registration.Descriptor);
        if (registration.Implementations.Any(item => item == null))
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' has a null capability implementation.");
        }

        var duplicate = registration.Implementations
            .GroupBy(item => item.Capability)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate != null)
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' binds capability '{duplicate.Key}' more than once.");
        }

        foreach (var implementation in registration.Implementations)
        {
            var implementationProviderId = ProviderContractValidation.ProviderId(
                implementation.ProviderId,
                nameof(implementation.ProviderId));
            if (!implementationProviderId.Equals(descriptor.Id, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Capability implementation provider '{implementationProviderId}' does not match descriptor '{descriptor.Id}'.");
            }

            var declared = descriptor.Capabilities.SingleOrDefault(item =>
                item.Capability == implementation.Capability);
            if (declared == null || !declared.HasUsableImplementation)
            {
                throw new InvalidOperationException(
                    $"Provider '{descriptor.Id}' binds an undeclared or non-operational '{implementation.Capability}' implementation.");
            }

            if (!CapabilityInterfaces[implementation.Capability].IsInstanceOfType(implementation))
            {
                throw new InvalidOperationException(
                    $"Provider '{descriptor.Id}' capability '{implementation.Capability}' does not implement its typed contract.");
            }
        }

        foreach (var capability in descriptor.Capabilities)
        {
            var implementationCount = registration.Implementations.Count(item =>
                item.Capability == capability.Capability);
            if (capability.HasUsableImplementation != (implementationCount == 1))
            {
                throw new InvalidOperationException(
                    $"Provider '{descriptor.Id}' capability '{capability.Capability}' descriptor and implementation binding disagree.");
            }
        }

        return registration;
    }
}

public static class ProviderManifestValidator
{
    private static readonly IReadOnlyDictionary<ProviderCapabilityKind, IReadOnlySet<string>> AllowedHooks =
        new Dictionary<ProviderCapabilityKind, IReadOnlySet<string>>
        {
            [ProviderCapabilityKind.Metadata] = new HashSet<string>(StringComparer.Ordinal)
            {
                "searchTracks", "getTrack", "lookupByIsrc", "searchAlbums", "getAlbum",
                "searchArtists", "getArtist"
            },
            [ProviderCapabilityKind.Streaming] = new HashSet<string>(StringComparer.Ordinal)
            {
                "getStreamLease", "probeStream"
            },
            [ProviderCapabilityKind.Download] = new HashSet<string>(StringComparer.Ordinal)
            {
                "checkAvailability", "download"
            },
            [ProviderCapabilityKind.Playlist] = new HashSet<string>(StringComparer.Ordinal)
            {
                "getUserPlaylists", "getPlaylistTracks", "searchPlaylists", "resolveArtwork",
                "mutatePlaylist"
            },
            [ProviderCapabilityKind.Lyrics] = new HashSet<string>(StringComparer.Ordinal)
            {
                "fetchLyrics"
            },
            [ProviderCapabilityKind.Intelligence] = new HashSet<string>(StringComparer.Ordinal)
            {
                "startAnalysis", "getAnalysisProgress", "getClusters", "recommend",
                "search", "findPath", "blend", "getMap", "disconnect"
            },
            [ProviderCapabilityKind.Health] = new HashSet<string>(StringComparer.Ordinal)
            {
                "probeMetadata", "probePlaylist", "probeStreaming", "probeDownload", "probeIntelligence"
            }
        };

    private static readonly IReadOnlyDictionary<ProviderCapabilityKind, IReadOnlySet<string>> RequiredHooks =
        new Dictionary<ProviderCapabilityKind, IReadOnlySet<string>>
        {
            [ProviderCapabilityKind.Metadata] = new HashSet<string>(StringComparer.Ordinal)
            {
                "searchTracks", "getTrack"
            },
            [ProviderCapabilityKind.Streaming] = new HashSet<string>(StringComparer.Ordinal)
            {
                "getStreamLease"
            },
            [ProviderCapabilityKind.Download] = new HashSet<string>(StringComparer.Ordinal)
            {
                "checkAvailability", "download"
            },
            [ProviderCapabilityKind.Playlist] = new HashSet<string>(StringComparer.Ordinal)
            {
                "getUserPlaylists", "getPlaylistTracks"
            },
            [ProviderCapabilityKind.Lyrics] = new HashSet<string>(StringComparer.Ordinal)
            {
                "fetchLyrics"
            },
            [ProviderCapabilityKind.Intelligence] = new HashSet<string>(StringComparer.Ordinal)
            {
                "recommend"
            },
            [ProviderCapabilityKind.Health] = new HashSet<string>(StringComparer.Ordinal)
        };

    public static ProviderDescriptor Validate(ProviderDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!descriptor.SdkVersion.Equals("1", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' uses unsupported SDK version '{descriptor.SdkVersion}'.");
        }

        ValidateEntryPoint(descriptor);
        if (descriptor.Capabilities.Count == 0)
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' must declare at least one SDK v1 capability.");
        }

        if (descriptor.Capabilities.Count > Enum.GetValues<ProviderCapabilityKind>().Length ||
            descriptor.Capabilities.Any(item => item == null))
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' has an invalid SDK v1 capability list.");
        }

        if (descriptor.Settings.Count > 100 || descriptor.Settings.Any(item => item == null))
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' has an invalid or unbounded settings schema.");
        }

        var duplicateCapability = descriptor.Capabilities
            .GroupBy(item => item.Capability)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateCapability != null)
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' declares capability '{duplicateCapability.Key}' more than once.");
        }

        foreach (var capability in descriptor.Capabilities)
        {
            ValidateCapability(descriptor.Id, descriptor.Origin, capability);
        }

        var hasHealthHooks = descriptor.Capabilities.Any(item =>
            item.Capability == ProviderCapabilityKind.Health && item.Hooks.Count > 0);
        if (descriptor.HealthProbe != hasHealthHooks)
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' healthProbe must agree with its validated health hooks.");
        }

        ValidateSettingsAndPermissions(descriptor);
        return descriptor;
    }

    public static IReadOnlySet<string> GetAllowedHooks(ProviderCapabilityKind capability)
    {
        if (!AllowedHooks.TryGetValue(capability, out var hooks))
        {
            throw new ArgumentOutOfRangeException(nameof(capability));
        }

        return new HashSet<string>(hooks, StringComparer.Ordinal);
    }

    private static void ValidateEntryPoint(ProviderDescriptor descriptor)
    {
        if (descriptor.Origin == ProviderOrigin.BuiltIn)
        {
            if (descriptor.EntryPoint != null)
            {
                throw new InvalidOperationException(
                    $"Built-in provider '{descriptor.Id}' cannot declare an extension entry point.");
            }

            return;
        }

        if (descriptor.EntryPoint == null)
        {
            throw new InvalidOperationException(
                $"Extension provider '{descriptor.Id}' requires a package-relative entry point.");
        }

        var normalized = descriptor.EntryPoint.Replace('\\', '/');
        if (Path.IsPathRooted(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            normalized.Split('/').Any(segment => segment is "" or "." or "..") ||
            normalized.Contains(':'))
        {
            throw new InvalidOperationException(
                $"Extension provider '{descriptor.Id}' entry point must remain inside its package.");
        }
    }

    private static void ValidateCapability(
        string providerId,
        ProviderOrigin origin,
        ProviderCapabilityDescriptor descriptor)
    {
        if (descriptor.Capability == ProviderCapabilityKind.Playlist &&
            descriptor.AccountRequirement != ProviderAccountRequirement.Required)
        {
            throw new InvalidOperationException(
                $"Playlist capability on provider '{providerId}' requires an explicit provider account.");
        }

        var allowedHooks = GetAllowedHooks(descriptor.Capability);
        var unexpected = descriptor.Hooks.FirstOrDefault(hook => !allowedHooks.Contains(hook));
        if (unexpected != null)
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' declares hook '{unexpected}' for the wrong capability.");
        }

        if (origin != ProviderOrigin.BuiltIn &&
            descriptor.Hooks.Contains("mutatePlaylist", StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{providerId}' cannot declare the host-only playlist mutation hook.");
        }

        if (descriptor.HasUsableImplementation && descriptor.Hooks.Count == 0)
        {
            throw new InvalidOperationException(
                $"Supported capability '{descriptor.Capability}' on provider '{providerId}' requires a validated hook.");
        }


        var missingRequired = RequiredHooks[descriptor.Capability]
            .Where(hook => !descriptor.Hooks.Contains(hook, StringComparer.Ordinal))
            .ToArray();
        if (descriptor.HasUsableImplementation && missingRequired.Length > 0)
        {
            throw new InvalidOperationException(
                $"Supported capability '{descriptor.Capability}' on provider '{providerId}' is missing required hooks: {string.Join(",", missingRequired)}.");
        }

        if (!descriptor.HasUsableImplementation && descriptor.Hooks.Count != 0)
        {
            throw new InvalidOperationException(
                $"Non-operational capability '{descriptor.Capability}' on provider '{providerId}' cannot expose hooks.");
        }
    }

    private static void ValidateSettingsAndPermissions(ProviderDescriptor descriptor)
    {
        var duplicateSetting = descriptor.Settings
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSetting != null)
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' declares setting '{duplicateSetting.Key}' more than once.");
        }

        var secretSettings = descriptor.Settings
            .Where(item => item.ValueKind == ProviderSettingValueKind.Secret)
            .Select(item => item.Key)
            .OrderBy(item => item, StringComparer.Ordinal)
            .ToArray();
        if (!secretSettings.SequenceEqual(
                descriptor.Permissions.SecretSettingKeys,
                StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"Provider '{descriptor.Id}' secret permissions must exactly match its secret settings.");
        }
    }
}
