namespace allstarr.Tests;

public sealed class WebUiSelectiveTransferContractTests
{
    [Fact]
    public void SelectiveTransfer_ExposesAccessibleCheckboxesAndReportCallout()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("renderSelectiveTransferDisclosure", script, StringComparison.Ordinal);
        Assert.Contains("handleSelectiveExport", script, StringComparison.Ordinal);
        Assert.Contains("handleSelectiveImportFile", script, StringComparison.Ordinal);
        Assert.Contains("selectiveTransferOptions", script, StringComparison.Ordinal);
        Assert.Contains("selectiveTransferBusy", script, StringComparison.Ordinal);
        Assert.Contains("selectiveTransferReport", script, StringComparison.Ordinal);
        Assert.Contains("selectiveTransferError", script, StringComparison.Ordinal);

        Assert.Contains("IncludeSettings", script, StringComparison.Ordinal);
        Assert.Contains("IncludeAccounts", script, StringComparison.Ordinal);
        Assert.Contains("IncludePlaylists", script, StringComparison.Ordinal);
        Assert.Contains("IncludeIntelligence", script, StringComparison.Ordinal);
        Assert.Contains("IncludeExtensions", script, StringComparison.Ordinal);
        Assert.Contains("ImportSettings", script, StringComparison.Ordinal);
        Assert.Contains("ImportAccounts", script, StringComparison.Ordinal);
        Assert.Contains("ImportPlaylists", script, StringComparison.Ordinal);
        Assert.Contains("ImportIntelligence", script, StringComparison.Ordinal);
        Assert.Contains("ImportExtensions", script, StringComparison.Ordinal);

        Assert.Contains("Selective granular import and export", script, StringComparison.Ordinal);
        Assert.Contains("Export selected categories", script, StringComparison.Ordinal);
        Assert.Contains("Import selected categories from archive", script, StringComparison.Ordinal);

        Assert.Contains("aria-live=\"polite\"", script, StringComparison.Ordinal);
        Assert.Contains("role=\"status\"", script, StringComparison.Ordinal);
        Assert.Contains("callout success", script, StringComparison.Ordinal);
        Assert.Contains("callout degraded", script, StringComparison.Ordinal);

        Assert.Contains(".toggle-group", css, StringComparison.Ordinal);
        Assert.Contains(".file-button", css, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectiveTransfer_HonorsGranularityEndToEnd()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "ConfigController.cs"));
        var service = File.ReadAllText(FindRepositoryFile("allstarr", "Core", "Storage", "SelectiveStateTransferService.cs"));

        Assert.Contains("ExportSelectiveState", controller, StringComparison.Ordinal);
        Assert.Contains("ImportSelectiveState", controller, StringComparison.Ordinal);
        Assert.Contains("PreviewSelectiveState", controller, StringComparison.Ordinal);
        Assert.Contains("IncludeSettings", controller, StringComparison.Ordinal);
        Assert.Contains("ImportSettings", controller, StringComparison.Ordinal);
        Assert.Contains("SelectiveTransferValidationException", controller, StringComparison.Ordinal);
        Assert.Contains("SelectiveTransferConflictException", controller, StringComparison.Ordinal);
        Assert.Contains("SelectiveTransferSchemaMismatchException", controller, StringComparison.Ordinal);
        Assert.Contains("[FromForm]", controller, StringComparison.Ordinal);
        Assert.Contains("OpenReadStream", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupJson", controller, StringComparison.Ordinal);

        Assert.Contains("ResolveIncludedCategories", service, StringComparison.Ordinal);
        Assert.Contains("CategoryDependencies", service, StringComparison.Ordinal);
        Assert.Contains("ValidateImportRequest", service, StringComparison.Ordinal);
        Assert.Contains("public enum SelectiveImportMode", service, StringComparison.Ordinal);
        Assert.Contains("IsolationLevel.RepeatableRead", service, StringComparison.Ordinal);
        Assert.Contains("MaximumExpandedBytes", service, StringComparison.Ordinal);
        Assert.Contains("ComputeSha256Async", service, StringComparison.Ordinal);
        Assert.Contains("TransferCategory.Settings", service, StringComparison.Ordinal);
        Assert.Contains("TransferCategory.Accounts", service, StringComparison.Ordinal);
        Assert.Contains("TransferCategory.Playlists", service, StringComparison.Ordinal);
        Assert.Contains("TransferCategory.Intelligence", service, StringComparison.Ordinal);
        Assert.Contains("TransferCategory.Extensions", service, StringComparison.Ordinal);
        Assert.DoesNotContain("BackupJson", service, StringComparison.Ordinal);
        Assert.DoesNotContain("stackalloc", service, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectiveTransfer_UsesSharedPrimitivesAndDisallowsInlineStyles()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        var css = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "css", "base.css"));

        Assert.Contains("class=\"toggle-row\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"inline-check\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"ghost file-button\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"actions\"", script, StringComparison.Ordinal);
        Assert.Contains("class=\"callout", script, StringComparison.Ordinal);
        Assert.Contains("class=\"content-disclosure panel settings-disclosure\"", script, StringComparison.Ordinal);

        var fileButtonSection = css.IndexOf(".file-button", StringComparison.Ordinal);
        Assert.True(fileButtonSection > 0);
        var responsiveCheck = script.IndexOf("style=\"cursor: pointer;\"", StringComparison.Ordinal);
        Assert.Equal(-1, responsiveCheck);
        var playlistCheck = script.IndexOf("style=\"display:flex;align-items:center;gap:0.5rem;cursor:pointer;\"", StringComparison.Ordinal);
        Assert.Equal(-1, playlistCheck);
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
        throw new FileNotFoundException("Repository file not found: " + string.Join("/", path));
    }
}
