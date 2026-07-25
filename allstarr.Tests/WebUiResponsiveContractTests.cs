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
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("aria-controls=\"primary-sidebar\"", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Open menu\"", script, StringComparison.Ordinal);
        Assert.Contains("aria-expanded=${this.navOpen ? \"true\" : \"false\"}", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Enter\" || event.key === \" \"", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Close menu\"", script, StringComparison.Ordinal);
        Assert.Contains("this.navOpen = false;", script, StringComparison.Ordinal);

        Assert.Contains(".sidebar-backdrop", css, StringComparison.Ordinal);
        Assert.Contains(".menu-trigger-lines", css, StringComparison.Ordinal);
    }

    [Fact]
    public void DesktopSidebar_CollapsesToPersistentAccessibleIconRail()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("SIDEBAR_COLLAPSED_KEY", script, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(SIDEBAR_COLLAPSED_KEY", script, StringComparison.Ordinal);
        Assert.Contains("class=\"ghost icon-button sidebar-collapse\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"brand-heading\"", script, StringComparison.Ordinal);
        Assert.Contains("this.sidebarCollapsed ? \"Expand sidebar\" : \"Collapse sidebar\"", script, StringComparison.Ordinal);
        Assert.Contains("title=${route.label} aria-label=${route.label}", script, StringComparison.Ordinal);
        Assert.Contains("@media (min-width: 961px)", css, StringComparison.Ordinal);
        Assert.Contains(".app-shell.sidebar-collapsed", css, StringComparison.Ordinal);
        Assert.Contains("--rail-width: 76px;", css, StringComparison.Ordinal);
        Assert.Contains(".sidebar-collapsed .nav-link > span", css, StringComparison.Ordinal);
        var responsive = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "responsive.css"));
        Assert.Contains(".sidebar-collapse {\n        display: none !important;", responsive, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedPlaylistDetails_ShowAllTracksAndUsePlayableSummary()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.DoesNotContain("injectedTrackPage", script, StringComparison.Ordinal);
        Assert.Contains("<small>Playable</small><strong>${playable} / ${tracks.length}</strong>", script, StringComparison.Ordinal);
        Assert.Contains("details?.totalPlayable", script, StringComparison.Ordinal);
        Assert.DoesNotContain("canReconcileLocal", script, StringComparison.Ordinal);
        Assert.Contains("filtered.map((track, index)", script, StringComparison.Ordinal);
        Assert.Contains("`All ${tracks.length} tracks`", script, StringComparison.Ordinal);
        Assert.Contains(".playlist-dialog-hero .dialog-close", css, StringComparison.Ordinal);
        Assert.Contains("align-items: start;", css, StringComparison.Ordinal);
        var responsive = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "responsive.css"));
        Assert.Contains("@media (max-width: 900px)", responsive, StringComparison.Ordinal);
        Assert.Contains(".playlist-track-table {\n        display: grid;", responsive, StringComparison.Ordinal);
    }

    [Fact]
    public void DesignSystem_UsesOrderedLayersAndFinalResponsiveOverrides()
    {
        var index = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "index.html"));
        var app = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "app.css"));
        var responsive = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "responsive.css"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("/css/foundation.css", index, StringComparison.Ordinal);
        Assert.Contains("/css/workspaces.css", index, StringComparison.Ordinal);
        Assert.Contains(".sidebar {\n        position: fixed;", responsive, StringComparison.Ordinal);
        Assert.Contains(".main-shell.has-now-playing", responsive, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"main-shell\"", script, StringComparison.Ordinal);
        Assert.Contains("data-testid=\"playlist-dialog\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Callouts_SeparateHeadingsFromSupportingMessages()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".callout {\n    display: grid;\n    gap: var(--space-1);", css, StringComparison.Ordinal);
        Assert.Contains(".callout > p,\n.callout > ul", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ViewStacks_KeepNavigationAndPanelsAtTheirIntrinsicHeight()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("const content = this.renderRoot.querySelector(\"main.content\")", script, StringComparison.Ordinal);
        Assert.Contains("if (content) content.scrollTop = 0;", script, StringComparison.Ordinal);
        Assert.Contains(".view-stack {\n    display: grid;\n    align-content: start;", css, StringComparison.Ordinal);
        Assert.Contains(".subnav {\n    gap: 0;\n    width: 100%;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SidebarAvatar_CropsAroundTheCenteredProfilePhoto()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".user-avatar img", css, StringComparison.Ordinal);
        Assert.Contains("object-fit: cover;", css, StringComparison.Ordinal);
        Assert.Contains("object-position: center;", css, StringComparison.Ordinal);
        Assert.Contains("clip-path: inset(0 round 9px);", css, StringComparison.Ordinal);
    }

    [Fact]
    public void LibraryWorkflows_UseCompactControlsModalPreviewAndScrollableShell()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("class=\"playlist-toolbar\"", script, StringComparison.Ordinal);
        Assert.Contains("data-table", script, StringComparison.Ordinal);
        Assert.Contains("Sync ${selected.size ? `${selected.size} selected` : \"all now\"}", script, StringComparison.Ordinal);
        Assert.Contains("return nothing;", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Playlist preview\"", script, StringComparison.Ordinal);
        Assert.Contains("Match review queue", script, StringComparison.Ordinal);
        Assert.Contains("Search local library", script, StringComparison.Ordinal);
        Assert.Contains("Search a playback provider", script, StringComparison.Ordinal);
        Assert.Contains(".mapping-review-modal", css, StringComparison.Ordinal);
        Assert.Contains(".playlist-preview-backdrop", css, StringComparison.Ordinal);
    }

    [Fact]
    public void MusicalTheme_UsesLayeredAccentTokensWithoutSacrificingStatusColors()
    {
        var tokens = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "tokens.css"));

        Assert.Contains("--accent-gradient:", tokens, StringComparison.Ordinal);
        Assert.Contains("--accent-action-gradient:", tokens, StringComparison.Ordinal);
        Assert.Contains("--accent-action-hover-gradient:", tokens, StringComparison.Ordinal);
        Assert.Contains("--accent-secondary:", tokens, StringComparison.Ordinal);
        Assert.Contains("--surface-glass:", tokens, StringComparison.Ordinal);
        Assert.Contains("--success:", tokens, StringComparison.Ordinal);
        Assert.Contains("--warning:", tokens, StringComparison.Ordinal);
        Assert.Contains("--error:", tokens, StringComparison.Ordinal);
        Assert.Contains(":root[data-theme=\"light\"]", tokens, StringComparison.Ordinal);

        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));
        Assert.Contains("background: var(--accent-action-gradient);", css, StringComparison.Ordinal);
        Assert.Contains("-webkit-background-clip: text;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedControlsAndSetupModal_UseCompactConsistentSpacing()
    {
        var tokens = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "tokens.css"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("--control-height: 40px;", tokens, StringComparison.Ordinal);
        Assert.Contains("--surface-control:", tokens, StringComparison.Ordinal);
        Assert.Contains("padding: 8px 14px;", css, StringComparison.Ordinal);
        Assert.Contains("backdrop-filter: blur(10px)", css, StringComparison.Ordinal);
        Assert.Contains("button.setup-choice", css, StringComparison.Ordinal);
        Assert.Contains(".setup-guide-footer button", css, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionRows_KeepCheckboxesAndLabelsAlignedInsteadOfRunningTogether()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains(".toggle-list", css, StringComparison.Ordinal);
        Assert.Contains(".toggle-row,\nfieldset.form-row > label", css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: 18px minmax(0, 1fr);", css, StringComparison.Ordinal);
        Assert.Contains(".toggle-row > span", css, StringComparison.Ordinal);
        Assert.Contains("overflow-wrap: anywhere;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SetupGuide_IsKeyboardAccessiblePersistentAndReopenable()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("SETUP_GUIDE_DISMISSED_KEY", script, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(SETUP_GUIDE_DISMISSED_KEY, \"1\")", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/onboarding/status", script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/onboarding/complete", script, StringComparison.Ordinal);
        Assert.Contains("this.onboardingStatus = await API.onboardingStatus()", script, StringComparison.Ordinal);
        Assert.Contains("this.onboardingStatus = await API.completeOnboarding()", script, StringComparison.Ordinal);
        Assert.Contains("completeSetupGuide()", script, StringComparison.Ordinal);
        Assert.Contains("openSetupGuide()", script, StringComparison.Ordinal);
        Assert.Contains("aria-label=\"Setup progress\"", script, StringComparison.Ordinal);
        Assert.Contains("aria-current=${index === this.setupStep ? \"step\" : nothing}", script, StringComparison.Ordinal);
        Assert.Contains("handleSetupGuideKeydown", script, StringComparison.Ordinal);
        Assert.Contains("Import an Allstarr 2.x .env", script, StringComparison.Ordinal);
        Assert.Contains("SETUP_GUIDE_STEP_KEY", script, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(SETUP_GUIDE_STEP_KEY, String(this.setupStep))", script, StringComparison.Ordinal);
        Assert.Contains("Refresh readiness", script, StringComparison.Ordinal);
        Assert.Contains("Connected as ${backendUser}", script, StringComparison.Ordinal);
        Assert.Contains("signed-in session is the connection test", script, StringComparison.Ordinal);
        Assert.Contains("Connected", script, StringComparison.Ordinal);
        Assert.Contains("First playlist", script, StringComparison.Ordinal);
        Assert.Contains("leaveSetupGuideFor(\"/library/playlists\")", script, StringComparison.Ordinal);
        Assert.Contains("SETUP_GUIDE_LAST_STEP = 4", script, StringComparison.Ordinal);
        Assert.Contains("Promise.all([this.loadStatus(), this.loadProviderAccounts()])", script, StringComparison.Ordinal);
        Assert.DoesNotContain("this.loadProviderHealth()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadFailures_StayVisibleOfferRetryAndReadProtocolErrorMessages()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains("loadFailures", script, StringComparison.Ordinal);
        Assert.Contains("renderLoadFailures()", script, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", script, StringComparison.Ordinal);
        Assert.Contains("retryLoadFailure(key)", script, StringComparison.Ordinal);
        Assert.Contains("recordLoadFailure(\"config\"", script, StringComparison.Ordinal);
        Assert.Contains("recordLoadFailure(\"playlistLinks\"", script, StringComparison.Ordinal);
        Assert.Contains("recordLoadFailure(\"extensionRegistries\"", script, StringComparison.Ordinal);
        Assert.Contains("data?.[\"subsonic-response\"]?.error?.message", script, StringComparison.Ordinal);
        Assert.Contains("${fallback} (HTTP ${response.status})", script, StringComparison.Ordinal);
        Assert.Contains("error.status = response.status", script, StringComparison.Ordinal);
        Assert.Contains("if (error?.status === 401)", script, StringComparison.Ordinal);
        Assert.Contains("const sessionState = await this.confirmDashboardSession()", script, StringComparison.Ordinal);
        Assert.Contains("if (sessionState === false)", script, StringComparison.Ordinal);
        Assert.Contains("this.handleExpiredSession()", script, StringComparison.Ordinal);
        Assert.Contains("sessionState === true && !authenticationRetry", script, StringComparison.Ordinal);
        Assert.Contains("await this.loadForRoute(true, true)", script, StringComparison.Ordinal);
        Assert.Contains("A failed confirmation request is not proof that the cookie expired.", script, StringComparison.Ordinal);
        Assert.Contains("this.loadFailures = {};", script, StringComparison.Ordinal);
        Assert.Contains("specificFailureRecorded", script, StringComparison.Ordinal);
        Assert.Contains("Your dashboard session expired. Sign in again to continue.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("this.recordLoadFailure(`route:${routeKey}`, `${titleCase(routeParts(routeKey)[0] || \"page\")} data`, error);\n      this.toast(error.message", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ArchitectureAndIntelligenceRoutes_AreHiddenUntilTheirUiIsReady()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));

        Assert.DoesNotContain("Route(\"architecture\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Route(\"intelligence\"", controller, StringComparison.Ordinal);
        Assert.Contains("renderArchitecture()", script, StringComparison.Ordinal);
    }

    [Fact]
    public void DashboardOverhaul_UsesStructuredReadModelsAndAFlowLayoutPlayer()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));

        Assert.Contains("/api/admin/ui/provider-summaries", script, StringComparison.Ordinal);
        Assert.Contains("API.dashboardActivity(100)", script, StringComparison.Ordinal);
        Assert.Contains("class=\"global-search\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"overview-grid\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"source-metrics\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"toast-stack operation-center\"", script, StringComparison.Ordinal);
        Assert.Contains("--progress-scale:${progress / 100}", script, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"provider-summaries\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"activity\")]", controller, StringComparison.Ordinal);
        Assert.Contains("grid-template-rows: auto minmax(0, 1fr) auto;", css, StringComparison.Ordinal);
        Assert.Contains("transform: scaleX(var(--progress-scale, 0));", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SmallScreens_UseFullHeightSetupAndCoarsePointerTouchTargets()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));
        var foundation = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "foundation.css"));

        Assert.Contains("@media (max-width: 620px)", css, StringComparison.Ordinal);
        Assert.Contains("height: 100dvh;", css, StringComparison.Ordinal);
        Assert.Contains("@media (hover: none), (pointer: coarse)", css, StringComparison.Ordinal);
        Assert.Contains("min-height: 44px;", css, StringComparison.Ordinal);
        Assert.Contains("env(safe-area-inset-bottom)", css, StringComparison.Ordinal);
        Assert.Contains("@media (prefers-reduced-motion: reduce)", foundation, StringComparison.Ordinal);
        Assert.Contains("transition-duration: 0.01ms !important;", foundation, StringComparison.Ordinal);
        Assert.Contains("animation-iteration-count: 1 !important;", foundation, StringComparison.Ordinal);
        Assert.DoesNotContain("@media (prefers-reduced-motion: reduce)", css, StringComparison.Ordinal);
    }

    [Fact]
    public void InjectedPlaylistTracks_OpenInAnAccessibleResponsiveModal()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("class=\"modal-backdrop injected-playlist-backdrop\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"panel injected-playlist-dialog redesigned-dialog\" role=\"dialog\" aria-modal=\"true\"", script, StringComparison.Ordinal);
        Assert.Contains("if (event.key !== \"Escape\") return;", script, StringComparison.Ordinal);
        Assert.Contains("else if (this.injectedTrackMenuId) this.injectedTrackMenuId = \"\";", script, StringComparison.Ordinal);
        Assert.Contains(".injected-playlist-scroll", css, StringComparison.Ordinal);
        Assert.Contains(".injected-playlist-dialog", css, StringComparison.Ordinal);
        Assert.Contains(".track-action-popover", css, StringComparison.Ordinal);
        Assert.Contains("position: fixed;", css, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCards_SeparateConfigurationFromObservedHealth()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("runtimeCapabilities", script, StringComparison.Ordinal);
        Assert.Contains("What each provider can do", script, StringComparison.Ordinal);
        Assert.Contains("Smart mixes", script, StringComparison.Ordinal);
        Assert.Contains("Technical limits and test coverage", script, StringComparison.Ordinal);
        Assert.Contains("capability.state !== \"unavailable\"", script, StringComparison.Ordinal);
        Assert.Contains("authenticateLastFmAccount", script, StringComparison.Ordinal);
        Assert.Contains("never stored by Allstarr", script, StringComparison.Ordinal);
        Assert.Contains("Last.fm no longer accepts the shared Jellyfin plugin key", script, StringComparison.Ordinal);
        Assert.Contains("Personal accounts are managed in Sources", script, StringComparison.Ordinal);
        Assert.Contains("Local songs are not being scrobbled", script, StringComparison.Ordinal);
        Assert.Contains("accountLabel(lastFmConfigured, lastFmHealth)", script, StringComparison.Ordinal);
        Assert.Contains("Save scrobbling settings", script, StringComparison.Ordinal);
        Assert.Contains("saveScrobblingSettings", script, StringComparison.Ordinal);
        Assert.Contains(">Reconnect</button>", script, StringComparison.Ordinal);
        Assert.Contains(".provider-qobuz", css, StringComparison.Ordinal);
        Assert.Contains("background: #f7f7f8", css, StringComparison.Ordinal);
        Assert.DoesNotContain("Available but untested", script, StringComparison.Ordinal);
        Assert.Contains("Not checked yet", script, StringComparison.Ordinal);
        Assert.Contains("capability.configuration", script, StringComparison.Ordinal);
        Assert.Contains("capability.health", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderCards_UseUnifiedBrandingAndKeepAccountsInSettings()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains(
            "const providersWithoutCardMark = new Set([\"lyricsplus\", \"lrclib\"]);",
            script,
            StringComparison.Ordinal);
        Assert.Contains("renderProviderLogo(providerId, \"large\")", script, StringComparison.Ordinal);
        Assert.Contains("class=\"source-metrics\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"source-card-footer\"", script, StringComparison.Ordinal);
        Assert.Contains("icon(\"plus\", 17)}<span>Connect source</span>", script, StringComparison.Ordinal);
        Assert.Contains("provider-account-dialog", script, StringComparison.Ordinal);
        Assert.Contains("[\"routing\", \"Source priority\", \"sources\"]", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Add or enable a provider account above", script, StringComparison.Ordinal);
        Assert.Contains("Source connections", script, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderAccounts_UseCompactCardsAndPlainConfigurationLanguage()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var styles = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("provider-account-grid", script, StringComparison.Ordinal);
        Assert.Contains("providerAccountModalOpen", script, StringComparison.Ordinal);
        Assert.Contains("event.target === event.currentTarget", script, StringComparison.Ordinal);
        Assert.Contains("event.key === \"Escape\"", script, StringComparison.Ordinal);
        Assert.Contains("<button @click=${() => this.toggleProviderAccountConfiguration(id)}", script, StringComparison.Ordinal);
        Assert.Contains(": \"Configure\"}</button>", script, StringComparison.Ordinal);
        Assert.Contains("Save and test", script, StringComparison.Ordinal);
        Assert.Contains("Test connection", script, StringComparison.Ordinal);
        Assert.Contains("renderNewProviderCredentialFields", script, StringComparison.Ordinal);
        Assert.Contains("this.providerAccountChoices().map((provider)", script, StringComparison.Ordinal);
        Assert.Contains("asArray(provider.accountSettings)", script, StringComparison.Ordinal);
        Assert.Contains("replace(/^shared\\s+/i, \"\")", script, StringComparison.Ordinal);
        Assert.Contains("providerAccountDisplayName(account.DisplayName || account.displayName", script, StringComparison.Ordinal);
        Assert.DoesNotContain("textarea name=\"secret\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("Replace credential", script, StringComparison.Ordinal);
        Assert.Contains("account-health-panel", styles, StringComparison.Ordinal);
        Assert.Contains("status.staged && status.daemon_running && status.wrapper_healthy", script, StringComparison.Ordinal);
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
        Assert.Contains("![\"sources\", \"settings\", \"intelligence\"].includes(zone)", script, StringComparison.Ordinal);
        Assert.Contains("if (zone === \"settings\") return this.renderSettings();", script, StringComparison.Ordinal);
        Assert.Contains("Source connections", script, StringComparison.Ordinal);
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
        Assert.DoesNotContain("No personal playlist account is ready.", script, StringComparison.Ordinal);
        Assert.Contains("Every connected provider or extension that exposes the Playlist capability", script, StringComparison.Ordinal);
        Assert.Contains("Shared playlist credentials are configured but disabled by policy.", script, StringComparison.Ordinal);
        Assert.DoesNotContain("externalPlaylistSearch", script, StringComparison.Ordinal);
        Assert.DoesNotContain("renderExternalPlaylistExplorer", script, StringComparison.Ordinal);
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
        Assert.Contains("max-height: calc(100dvh - (2 * var(--space-3)));", css, StringComparison.Ordinal);
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

    [Fact]
    public void InjectedTable_StaysScrollableWhenStatusChipWouldOverlapLastSync()
    {
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));
        var responsive = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "responsive.css"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));

        Assert.Contains(".injected-table-wrap", css, StringComparison.Ordinal);
        Assert.Contains("overflow-x: auto", css, StringComparison.Ordinal);
        Assert.Contains(".injected-table-row", css, StringComparison.Ordinal);
        Assert.Contains("min-width: min(900px", css, StringComparison.Ordinal);
        Assert.Contains("injected-table-wrap", responsive, StringComparison.Ordinal);
        Assert.Contains("class=\"injected-table-wrap\"", script, StringComparison.Ordinal);
        Assert.Contains("role=\"region\"", script, StringComparison.Ordinal);
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
