namespace allstarr.Tests;

public sealed class PlaylistSourceDiscoveryContractTests
{
    [Fact]
    public void Discovery_IsCapabilityDrivenAndReportsAccountScope()
    {
        var controller = Read("allstarr/Controllers/PlaylistLinksController.cs");

        Assert.Contains("FindByCapability(ProviderCapabilityKind.Playlist", controller, StringComparison.Ordinal);
        Assert.Contains("capability.AllowedAccountScopes.Contains(item.Scope)", controller, StringComparison.Ordinal);
        Assert.Contains("accessLabel = account.Scope switch", controller, StringComparison.Ordinal);
        Assert.Contains("blockedAccounts", controller, StringComparison.Ordinal);
        Assert.Contains("shared-playlist-credentials-disabled", controller, StringComparison.Ordinal);
        Assert.Contains("item.CreatedByUserId ?? item.OwnerUserId", controller, StringComparison.Ordinal);
        Assert.Contains("creatorNames.GetValueOrDefault(creatorId)", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("supportedProviders.Contains(\"spotify\")", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("supportedProviders.Contains(\"apple-musickit\")", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Wizard_ExplainsUsableAndPolicyBlockedSources()
    {
        var script = Read("allstarr/wwwroot/js/webui.js");

        Assert.Contains("playlistSourceBlockedAccounts", script, StringComparison.Ordinal);
        Assert.Contains("playlistSourceProviders", script, StringComparison.Ordinal);
        Assert.Contains("provider or extension that exposes the Playlist capability", script, StringComparison.Ordinal);
        Assert.Contains("Deployment-shared account", Read("allstarr/Controllers/PlaylistLinksController.cs"), StringComparison.Ordinal);
        Assert.DoesNotContain("Connect Spotify or Apple MusicKit in Settings", script, StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledExtensionPlaylistCapabilities_EnterTheSameDiscoveryRegistry()
    {
        var coordinator = Read("allstarr/Core/Extensions/ExtensionRuntimeCoordinator.cs");
        var adapters = Read("allstarr/Core/Extensions/ExtensionCapabilityAdapters.cs");
        var controller = Read("allstarr/Controllers/PlaylistLinksController.cs");

        Assert.Contains(
            "ProviderCapabilityKind.Playlist => new ExtensionPlaylistCapabilityAdapter",
            coordinator,
            StringComparison.Ordinal);
        Assert.Contains(
            "ExtensionPlaylistCapabilityAdapter : ExtensionCapabilityAdapterBase, IProviderPlaylistCapability",
            adapters,
            StringComparison.Ordinal);
        Assert.Contains(
            "FindByCapability(ProviderCapabilityKind.Playlist, includeNonOperational: false)",
            controller,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "item.ProviderId == \"spotify\"",
            controller,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "item.ProviderId == \"apple-musickit\"",
            controller,
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Read(string relativePath)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", relativePath));
        return File.ReadAllText(path);
    }
}
