using System.Diagnostics;
using System.Text.RegularExpressions;
using Xunit;

namespace allstarr.Tests;

/// <summary>
/// Tests to validate JavaScript syntax in wwwroot files.
/// This prevents broken JavaScript from being committed.
/// </summary>
public class JavaScriptSyntaxTests
{
    private readonly string _wwwrootPath;

    public JavaScriptSyntaxTests()
    {
        // Get the path to the wwwroot directory
        var projectRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", ".."));
        _wwwrootPath = Path.Combine(projectRoot, "allstarr", "wwwroot");
    }

    [Fact]
    public void AppJs_ShouldHaveValidSyntax()
    {
        var filePath = Path.Combine(_wwwrootPath, "app.js");
        Assert.True(File.Exists(filePath), $"app.js not found at {filePath}");

        var isValid = ValidateJavaScriptSyntax(filePath, out var error);
        Assert.True(isValid, $"app.js has syntax errors:\n{error}");
    }

    [Fact]
    public void SpotifyMappingsJs_ShouldHaveValidSyntax()
    {
        var filePath = Path.Combine(_wwwrootPath, "spotify-mappings.js");
        Assert.True(File.Exists(filePath), $"spotify-mappings.js not found at {filePath}");

        var isValid = ValidateJavaScriptSyntax(filePath, out var error);
        Assert.True(isValid, $"spotify-mappings.js has syntax errors:\n{error}");
    }

    [Fact]
    public void ModularJs_UtilsShouldHaveValidSyntax()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "utils.js");
        Assert.True(File.Exists(filePath), $"js/utils.js not found at {filePath}");

        var isValid = ValidateJavaScriptSyntax(filePath, out var error);
        Assert.True(isValid, $"js/utils.js has syntax errors:\n{error}");
    }

    [Fact]
    public void ModularJs_ApiShouldHaveValidSyntax()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "api.js");
        Assert.True(File.Exists(filePath), $"js/api.js not found at {filePath}");

        var isValid = ValidateJavaScriptSyntax(filePath, out var error);
        Assert.True(isValid, $"js/api.js has syntax errors:\n{error}");
    }

    [Fact]
    public void ModularJs_MainShouldHaveValidSyntax()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "main.js");
        Assert.True(File.Exists(filePath), $"js/main.js not found at {filePath}");

        var isValid = ValidateJavaScriptSyntax(filePath, out var error);
        Assert.True(isValid, $"js/main.js has syntax errors:\n{error}");
    }

    [Fact]
    public void ModularJs_ExtractedModulesShouldHaveValidSyntax()
    {
        var moduleFiles = new[]
        {
            "settings-editor.js",
            "auth-session.js",
            "dashboard-data.js",
            "operations.js",
            "playlist-admin.js",
            "scrobbling-admin.js"
        };

        foreach (var moduleFile in moduleFiles)
        {
            var filePath = Path.Combine(_wwwrootPath, "js", moduleFile);
            Assert.True(File.Exists(filePath), $"js/{moduleFile} not found at {filePath}");

            var isValid = ValidateJavaScriptSyntax(filePath, out var error);
            Assert.True(isValid, $"js/{moduleFile} has syntax errors:\n{error}");
        }
    }

    [Fact]
    public void AppJs_ShouldBeDeprecated()
    {
        var filePath = Path.Combine(_wwwrootPath, "app.js");
        var content = File.ReadAllText(filePath);

        Assert.Contains("DEPRECATED", content);
        Assert.Contains("main.js", content);
    }

    [Fact]
    public void MainJs_ShouldBeComplete()
    {
        var mainPath = Path.Combine(_wwwrootPath, "js", "main.js");
        var dashboardPath = Path.Combine(_wwwrootPath, "js", "dashboard-data.js");
        var settingsPath = Path.Combine(_wwwrootPath, "js", "settings-editor.js");
        var authPath = Path.Combine(_wwwrootPath, "js", "auth-session.js");
        var operationsPath = Path.Combine(_wwwrootPath, "js", "operations.js");
        var playlistPath = Path.Combine(_wwwrootPath, "js", "playlist-admin.js");
        var scrobblingPath = Path.Combine(_wwwrootPath, "js", "scrobbling-admin.js");

        Assert.True(File.Exists(mainPath), $"js/main.js not found at {mainPath}");
        Assert.True(File.Exists(dashboardPath), $"js/dashboard-data.js not found at {dashboardPath}");
        Assert.True(File.Exists(settingsPath), $"js/settings-editor.js not found at {settingsPath}");
        Assert.True(File.Exists(authPath), $"js/auth-session.js not found at {authPath}");
        Assert.True(File.Exists(operationsPath), $"js/operations.js not found at {operationsPath}");
        Assert.True(File.Exists(playlistPath), $"js/playlist-admin.js not found at {playlistPath}");
        Assert.True(File.Exists(scrobblingPath), $"js/scrobbling-admin.js not found at {scrobblingPath}");

        var mainContent = File.ReadAllText(mainPath);
        var dashboardContent = File.ReadAllText(dashboardPath);
        var settingsContent = File.ReadAllText(settingsPath);
        var authContent = File.ReadAllText(authPath);
        var operationsContent = File.ReadAllText(operationsPath);
        var playlistContent = File.ReadAllText(playlistPath);
        var scrobblingContent = File.ReadAllText(scrobblingPath);

        Assert.Contains("DOMContentLoaded", mainContent);
        Assert.Contains("authSession.bootstrapAuth()", mainContent);
        Assert.Contains("initDashboardData", mainContent);

        Assert.Contains("window.fetchStatus", dashboardContent);
        Assert.Contains("window.fetchPlaylists", dashboardContent);
        Assert.Contains("window.fetchConfig", dashboardContent);
        Assert.Contains("window.fetchEndpointUsage", dashboardContent);

        Assert.Contains("window.openEditSetting", settingsContent);
        Assert.Contains("window.saveEditSetting", settingsContent);
        Assert.Contains("window.logoutAdminSession", authContent);
        Assert.Contains("window.restartContainer", operationsContent);
        Assert.Contains("window.linkPlaylist", playlistContent);
        Assert.Contains("window.loadScrobblingConfig", scrobblingContent);
    }

    [Fact]
    public void AppJs_ShouldHaveBalancedBraces()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "main.js");
        var content = File.ReadAllText(filePath);

        var openBraces = content.Count(c => c == '{');
        var closeBraces = content.Count(c => c == '}');

        Assert.Equal(openBraces, closeBraces);
    }

    [Fact]
    public void AppJs_ShouldHaveBalancedParentheses()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "main.js");

        // Validate syntax with Node.js instead of counting parentheses.
        string error;
        var isValid = ValidateJavaScriptSyntax(filePath, out error);

        Assert.True(isValid, $"JavaScript syntax validation failed: {error}");
    }

    [Fact]
    public void ApiJs_ShouldCentralizeFetchHandling()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "api.js");
        var content = File.ReadAllText(filePath);

        Assert.Contains("async function requestJson", content);
        Assert.Contains("async function requestBlob", content);
        Assert.Contains("async function requestOptionalJson", content);

        var fetchCallCount = Regex.Matches(content, @"\bfetch\(").Count;
        Assert.Equal(3, fetchCallCount);
    }

    [Fact]
    public void ScrobblingAdmin_ShouldUseApiWrappersInsteadOfDirectFetch()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "scrobbling-admin.js");
        var content = File.ReadAllText(filePath);

        Assert.DoesNotContain("fetch(", content);
        Assert.Contains("API.fetchScrobblingStatus()", content);
        Assert.Contains("API.updateLocalTracksScrobbling", content);
        Assert.Contains("API.authenticateLastFm()", content);
        Assert.Contains("API.validateListenBrainzToken", content);
    }

    private bool ValidateJavaScriptSyntax(string filePath, out string error)
    {
        error = string.Empty;

        try
        {
            // Use Node.js to check syntax
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "node",
                    Arguments = $"--check \"{filePath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            var stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                error = stderr;
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"Failed to run Node.js syntax check: {ex.Message}\n" +
                    "Make sure Node.js is installed and available in PATH.";
            return false;
        }
    }

}
