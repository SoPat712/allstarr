namespace allstarr.Tests;

public sealed class WebUiResponsiveContractTests
{
    [Fact]
    public void NarrowLayout_ContainsWideContentAndCollapsesProviderGrids()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".table-wrap", css, StringComparison.Ordinal);
        Assert.Contains("max-width: 100%;", css, StringComparison.Ordinal);
        Assert.Contains(".grid,\n    .provider-grid", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains(".app-shell {\n        width: 100%;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderSupportStates_HaveDistinctVisualTokens()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".chip.support-supported", css, StringComparison.Ordinal);
        Assert.Contains(".chip.support-partial", css, StringComparison.Ordinal);
        Assert.Contains(".chip.support-policy_blocked", css, StringComparison.Ordinal);
        Assert.Contains(".chip.support-unavailable", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MobileMenu_ExposesKeyboardAndExpandedStateContracts()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("aria-controls=\"primary-sidebar\"", script, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=${this.navOpen ? \"true\" : \"false\"}", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\" || event.key === \" \"", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Close menu\"", script, StringComparison.Ordinal);
        Assert.Contains("this.navOpen = false;", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCards_SeparateConfigurationFromObservedHealth()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("runtimeCapabilities", script, StringComparison.Ordinal);
        Assert.Contains("Available but untested", script, StringComparison.Ordinal);
        Assert.Contains("Observed healthy", script, StringComparison.Ordinal);
        Assert.Contains("capability.configuration", script, StringComparison.Ordinal);
        Assert.Contains("capability.health", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCards_HideUnwantedMarksAndConfigurationActions()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains(
            "const providersWithoutCardMark = new Set([\"lyricsplus\", \"squidwtf\", \"lrclib\"]);",
            script,
            StringComparison.Ordinal);
        Assert.Contains("const showBrandMark = Boolean(logoUrl) || !providersWithoutCardMark.has(providerId);", script, StringComparison.Ordinal);
        Assert.Contains("${showBrandMark ? html`", script, StringComparison.Ordinal);
        Assert.Contains("!providersWithoutCardMark.has(normalizedProviderId)", script, StringComparison.Ordinal);
        Assert.Contains("const hasEditableConfig = asArray(provider.configSchema).length > 0;", script, StringComparison.Ordinal);
        Assert.Contains("${status !== \"disabled\" && hasEditableConfig ? html`", script, StringComparison.Ordinal);
        Assert.Contains("const open = hasEditableConfig &&", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LoginCopy_UsesTheSelectedBackendIdentity()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("this.authBackend = authState.backend", script, StringComparison.Ordinal);
        Assert.Contains("Sign in with your ${display(this.authBackend", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NonAdministratorShell_IsLimitedToProviderAccountSelfService()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("await this.loadSchema();", script, StringComparison.Ordinal);
        Assert.Contains("if (this.isAdministrator())", script, StringComparison.Ordinal);
        Assert.Contains("return this.authenticated && !this.isAdministrator() && route !== \"/intelligence\" ? \"/sources\" : route;", script, StringComparison.Ordinal);
        Assert.Contains("Manage credentials for your own music provider accounts.", script, StringComparison.Ordinal);
        Assert.Contains("Provider accounts are managed by an administrator.", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAccountHealthActions_AreAccountAndCapabilityScoped()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("/api/admin/providers/status", script, StringComparison.Ordinal);
        Assert.Contains("?accountId=${encodeURIComponent(accountId)}", script, StringComparison.Ordinal);
        Assert.Contains("this.testProviderAccountCapability(id, providerId, capabilityId)", script, StringComparison.Ordinal);
        Assert.Contains("String(item.providerAccountId || item.ProviderAccountId)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistLinks_AreProviderNeutralAndExposeReviewAndRunControls()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("/api/admin/playlist-links", script, StringComparison.Ordinal);
        Assert.Contains("Provider account", script, StringComparison.Ordinal);
        Assert.Contains("Navidrome / Subsonic", script, StringComparison.Ordinal);
        Assert.Contains("value=\"virtual\"", script, StringComparison.Ordinal);
        Assert.Contains("value=\"materialized\"", script, StringComparison.Ordinal);
        Assert.Contains("value=\"hybrid\"", script, StringComparison.Ordinal);
        Assert.Contains("value=\"reconcile\"", script, StringComparison.Ordinal);
        Assert.Contains("value=\"recreate\"", script, StringComparison.Ordinal);
        Assert.Contains("Pin match", script, StringComparison.Ordinal);
        Assert.Contains("Reject", script, StringComparison.Ordinal);
        Assert.Contains("Run now", script, StringComparison.Ordinal);
        Assert.Contains("Copy description", script, StringComparison.Ordinal);
        Assert.Contains("Copy artwork", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/playlist-links/backend-credentials", script, StringComparison.Ordinal);
        Assert.Contains("createPlaylistBackendCredential", script, StringComparison.Ordinal);
        Assert.Contains("rotatePlaylistBackendCredential", script, StringComparison.Ordinal);
        Assert.Contains("type=\"password\"", script, StringComparison.Ordinal);
        Assert.Contains("payload.targetCredentialReferenceId = credential.referenceId", script, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"targetCredentialReferenceId\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Target credential reference", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PlaylistLinkWorkspace_CollapsesToOneColumnOnNarrowScreens()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".playlist-link-layout", css, StringComparison.Ordinal);
        Assert.Contains(".playlist-link-form-grid", css, StringComparison.Ordinal);
        Assert.Contains(".playlist-preview", css, StringComparison.Ordinal);
        Assert.Contains("position: static;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MappingReview_UsesProviderNeutralDurableRecordsInsteadOfSpotifyCache()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("/api/admin/track-matches", script, StringComparison.Ordinal);
        Assert.Contains("externalSnapshotId", script, StringComparison.Ordinal);
        Assert.Contains("providerIdentities", script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin/spotify/mappings", script, StringComparison.Ordinal);

        var orchestration = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Playlists", "PlaylistOrchestrationService.cs"));
        Assert.DoesNotContain("SpotifyMappingService", orchestration, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            current = current.Parent;
        }

        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}
