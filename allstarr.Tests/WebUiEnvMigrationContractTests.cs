namespace allstarr.Tests;

public sealed class WebUiEnvMigrationContractTests
{
    private readonly string _script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
    private readonly string _css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

    [Fact]
    public void Wizard_UsesPreviewTokenThenExplicitConfirmedApply()
    {
        Assert.Contains("/api/admin/config/migration/preview", _script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/config/migration/apply", _script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/config/migration/status", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("\"/api/config/migration/", _script, StringComparison.Ordinal);
        Assert.Contains("source instanceof Blob", _script, StringComparison.Ordinal);
        Assert.Contains("data.append(\"file\", file", _script, StringComparison.Ordinal);
        Assert.Contains("{ method: \"POST\", body: data }", _script, StringComparison.Ordinal);
        Assert.Contains("jsonBody({ previewToken, revision, confirmed: true })", _script, StringComparison.Ordinal);
        Assert.Contains("name=\"confirmMigration\" type=\"checkbox\" required", _script, StringComparison.Ordinal);
        Assert.Contains("Existing durable settings stay unchanged", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("imported values can replace current settings", _script, StringComparison.Ordinal);
        Assert.Contains("Apply migration", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("API.importEnv(file)", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/admin/import-env", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void FetchHelpers_MatchTheControllerTransportAndRevisionContract()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "ConfigController.cs"));
        var service = File.ReadAllText(FindRepositoryFile("allstarr", "Core", "Configuration", "LegacyEnvMigrationService.cs"));

        Assert.Contains("[FromForm] IFormFile? file", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpGet(\"config/migration/status\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"config/migration/preview\")]", controller, StringComparison.Ordinal);
        Assert.Contains("[HttpPost(\"config/migration/apply\")]", controller, StringComparison.Ordinal);
        Assert.Contains("RequestSizeLimit(LegacyEnvParser.MaxBytes * 2L)", controller, StringComparison.Ordinal);
        Assert.Contains("public string? PreviewToken", controller, StringComparison.Ordinal);
        Assert.Contains("public string? Revision", controller, StringComparison.Ordinal);
        Assert.Contains("string PreviewToken,", service, StringComparison.Ordinal);
        Assert.Contains("string SourceSha256,", service, StringComparison.Ordinal);
        Assert.Contains("string ParserVersion,", service, StringComparison.Ordinal);
        Assert.Contains("int SourceLine,", service, StringComparison.Ordinal);
        Assert.Contains("string Revision,", service, StringComparison.Ordinal);
        Assert.Contains("data.append(\"file\"", _script, StringComparison.Ordinal);
        Assert.Contains("jsonBody({ previewToken, revision, confirmed: true })", _script, StringComparison.Ordinal);
    }


    [Fact]
    public void FirstRunGuide_OffersLegacyImportWithoutRepeatingAfterCompletion()
    {
        Assert.Contains("canOfferEnvMigration", _script, StringComparison.Ordinal);
        Assert.Contains("status.eligible ?? status.Eligible ?? status.firstRun ?? status.FirstRun", _script, StringComparison.Ordinal);
        Assert.Contains("!Boolean(status.completed ?? status.Completed)", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"dialog\" aria-modal=\"true\"", _script, StringComparison.Ordinal);
        Assert.Contains("Import an Allstarr 2.x .env", _script, StringComparison.Ordinal);
        Assert.Contains("Skip for now", _script, StringComparison.Ordinal);
        Assert.Contains("Upgrading from Allstarr 2.x?", _script, StringComparison.Ordinal);
        Assert.Contains("Import your old <code>.env</code> through a safe preview", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("found an eligible legacy migration", _script, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("event.key === \"Escape\"", _script, StringComparison.Ordinal);
        Assert.Contains("event.key !== \"Tab\"", _script, StringComparison.Ordinal);
        Assert.Contains("(activeDialog.querySelector(\"[autofocus]\") || activeDialog).focus()", _script, StringComparison.Ordinal);
        Assert.Contains("this.handleDialogKeydown(event, close)", _script, StringComparison.Ordinal);
        Assert.Contains("localStorage.setItem(SETUP_GUIDE_DISMISSED_KEY, \"1\")", _script, StringComparison.Ordinal);
        Assert.Contains("localStorage.getItem(SETUP_GUIDE_DISMISSED_KEY) !== \"1\"", _script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/onboarding/status", _script, StringComparison.Ordinal);
        Assert.Contains("/api/admin/onboarding/complete", _script, StringComparison.Ordinal);
        Assert.Contains("const completed = Boolean(this.onboardingStatus?.completed", _script, StringComparison.Ordinal);
        Assert.Contains("this.completeSetupGuide()", _script, StringComparison.Ordinal);
        Assert.Contains("This tenant already completed its legacy environment import", _script, StringComparison.Ordinal);
        Assert.Contains("migrationCompleted && migration.state !== \"success\"", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("sessionStorage.setItem(MIGRATION_PROMPT_DISMISSED_KEY", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void FirstRunGuide_RevealsAndFocusesTheMigrationSectionAfterRouting()
    {
        Assert.Contains("revealRouteTarget(path, selector)", _script, StringComparison.Ordinal);
        Assert.Contains("await new Promise((resolve) => window.requestAnimationFrame(resolve))", _script, StringComparison.Ordinal);
        Assert.Contains("target.scrollIntoView({ behavior: reducedMotion ? \"auto\" : \"smooth\", block: \"start\" })", _script, StringComparison.Ordinal);
        Assert.Contains("target.focus({ preventScroll: true })", _script, StringComparison.Ordinal);
        Assert.Contains("void this.revealRouteTarget(\"/settings\", \"#env-migration-title\")", _script, StringComparison.Ordinal);
        Assert.Contains("#env-migration-title", _css, StringComparison.Ordinal);
        Assert.Contains("scroll-margin-top:", _css, StringComparison.Ordinal);
    }

    [Fact]
    public void OutcomeCategories_DoNotOverclaimPlaylistOrAccountMigration()
    {
        foreach (var label in new[]
                 {
                     "Imported durable settings", "Disabled shared accounts", "Deployment checklist",
                     "Your personal accounts", "Conflicts", "Unknown keys", "Playlist ownership handoffs"
                 })
        {
            Assert.Contains(label, _script, StringComparison.Ordinal);
        }

        Assert.Contains("requires_target_selection", _script, StringComparison.Ordinal);
        Assert.Contains("Rows marked for durable import are applied automatically", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("LEGACY_ENV_MIGRATION", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void Wizard_AcceptsFileOrPasteAndShowsAllLifecycleStates()
    {
        Assert.Contains("id=\"legacy-env-file\" type=\"file\" @change=", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"legacy-env-file\" type=\"file\" accept=", _script, StringComparison.Ordinal);
        Assert.Contains("The picker shows all files", _script, StringComparison.Ordinal);
        Assert.Contains("this.previewEnvMigration(file, file.name)", _script, StringComparison.Ordinal);
        Assert.DoesNotContain("file.text()", _script, StringComparison.Ordinal);
        Assert.Contains("Paste legacy .env contents", _script, StringComparison.Ordinal);
        Assert.Contains("previewing", _script, StringComparison.Ordinal);
        Assert.Contains("applying", _script, StringComparison.Ordinal);
        Assert.Contains("Migration completed", _script, StringComparison.Ordinal);
        Assert.Contains("Migration could not continue", _script, StringComparison.Ordinal);
        Assert.Contains("1 MB or smaller", _script, StringComparison.Ordinal);
        Assert.Contains("Imported durable settings are active immediately", _script, StringComparison.Ordinal);
        Assert.Contains("Deployment-owned values were not copied to the server", _script, StringComparison.Ordinal);
        Assert.Contains("then recreate the Allstarr container", _script, StringComparison.Ordinal);
        Assert.Contains("No container restart is required for this migration", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void OptionalRuntimeRows_ShowRedactedPostImportDeploymentGuidance()
    {
        Assert.Contains("migrationOptionalRuntimeServices()", _script, StringComparison.Ordinal);
        Assert.Contains("SPOTIFY_LYRICS_API_URL", _script, StringComparison.Ordinal);
        Assert.Contains("APPLE_DOWNLOAD_URL", _script, StringComparison.Ordinal);
        Assert.Contains("APPLE_MUSIC_AIO_URL", _script, StringComparison.Ordinal);
        Assert.Contains("Optional services still need setup", _script, StringComparison.Ordinal);
        Assert.Contains("No URL, login, token, or session value is shown here.", _script, StringComparison.Ordinal);
        Assert.Contains("docs/operations/spotify-lyrics-sidecar.md", _script, StringComparison.Ordinal);
        Assert.Contains("docs/operations/apple-download-provider.md", _script, StringComparison.Ordinal);
        Assert.Contains("./allstarr.sh enable spotify-lyrics", _script, StringComparison.Ordinal);
        Assert.Contains("./allstarr.sh install-apple", _script, StringComparison.Ordinal);
        Assert.Contains("./allstarr.sh status", _script, StringComparison.Ordinal);
        Assert.Contains("target=\"_blank\" rel=\"noopener noreferrer\"", _script, StringComparison.Ordinal);
        Assert.Contains("this.renderMigrationOptionalRuntimeGuidance()", _script, StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_AllowsExplicitSharingRedactionAndGroupsChangesWithWarnings()
    {
        Assert.Contains("migrationEntryIsSensitive", _script, StringComparison.Ordinal);
        Assert.Contains("return \"[redacted]\"", _script, StringComparison.Ordinal);
        Assert.Contains("password|secret|token|cookie|api[_-]?key", _script, StringComparison.Ordinal);
        Assert.Contains("migrationCategories", _script, StringComparison.Ordinal);
        Assert.Contains("Redact for sharing", _script, StringComparison.Ordinal);
        Assert.Contains("this.redactionMode && this.migrationEntryIsSensitive", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"alert\"", _script, StringComparison.Ordinal);
        Assert.Contains("entry.warning || entry.Warning || entry.reason", _script, StringComparison.Ordinal);
        Assert.Contains("Local scrobbling can duplicate plays", File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Configuration", "LegacyEnvMigrationService.cs")), StringComparison.Ordinal);
    }

    [Fact]
    public void Preview_ShowsSourceFingerprintParserAndItemLines()
    {
        Assert.Contains("preview.sourceSha256 || preview.SourceSha256", _script, StringComparison.Ordinal);
        Assert.Contains("preview.parserVersion || preview.ParserVersion", _script, StringComparison.Ordinal);
        Assert.Contains("Migration preview provenance", _script, StringComparison.Ordinal);
        Assert.Contains("Source SHA-256", _script, StringComparison.Ordinal);
        Assert.Contains("Parser version", _script, StringComparison.Ordinal);
        Assert.Contains("entry.sourceLine ?? entry.SourceLine", _script, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\">Line</th>", _script, StringComparison.Ordinal);
        Assert.Contains(".env-migration-provenance", _css, StringComparison.Ordinal);
    }

    [Fact]
    public void Wizard_IsKeyboardResponsiveAndAnnouncesProgress()
    {
        Assert.Contains("aria-busy=${busy ? \"true\" : \"false\"}", _script, StringComparison.Ordinal);
        Assert.Contains("role=\"status\" aria-live=\"polite\"", _script, StringComparison.Ordinal);
        Assert.Contains(".env-migration-source", _css, StringComparison.Ordinal);
        Assert.Contains("grid-template-columns: minmax(0, 1fr);", _css, StringComparison.Ordinal);
        Assert.Contains(".env-migration-progress progress", _css, StringComparison.Ordinal);
        Assert.Contains(".modal-backdrop", _css, StringComparison.Ordinal);
        Assert.Contains("width: min(780px, 100%);", _css, StringComparison.Ordinal);
    }

    private static string FindRepositoryFile(params string[] path)
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current != null)
        {
            var candidate = Path.Combine([current.FullName, .. path]);
            if (File.Exists(candidate)) return candidate;
            current = current.Parent;
        }
        throw new FileNotFoundException($"Could not locate {Path.Combine(path)}");
    }
}
