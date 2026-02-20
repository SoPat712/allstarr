using System.Diagnostics;
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
    public void AppJs_ShouldBeDeprecated()
    {
        var filePath = Path.Combine(_wwwrootPath, "app.js");
        var content = File.ReadAllText(filePath);
        
        // Check that the file is now just a deprecation notice
        Assert.Contains("DEPRECATED", content);
        Assert.Contains("main.js", content);
    }

    [Fact]
    public void MainJs_ShouldBeComplete()
    {
        var filePath = Path.Combine(_wwwrootPath, "js", "main.js");
        var content = File.ReadAllText(filePath);
        
        // Check that critical window functions exist
        Assert.Contains("window.fetchStatus", content);
        Assert.Contains("window.fetchPlaylists", content);
        Assert.Contains("window.fetchConfig", content);
        Assert.Contains("window.fetchEndpointUsage", content);
        
        // Check that the file has proper initialization
        Assert.Contains("DOMContentLoaded", content);
        Assert.Contains("window.fetchStatus();", content);
    }

    [Fact]
    public void AppJs_ShouldHaveBalancedBraces()
    {
        // app.js is now deprecated and just contains comments
        // Skip this test or check main.js instead
        var filePath = Path.Combine(_wwwrootPath, "js", "main.js");
        var content = File.ReadAllText(filePath);
        
        var openBraces = content.Count(c => c == '{');
        var closeBraces = content.Count(c => c == '}');
        
        Assert.Equal(openBraces, closeBraces);
    }

    [Fact]
    public void AppJs_ShouldHaveBalancedParentheses()
    {
        // app.js is now deprecated and just contains comments
        // Skip this test or check main.js instead
        var filePath = Path.Combine(_wwwrootPath, "js", "main.js");
        
        // Use Node.js to validate syntax instead of counting parentheses
        // This is more reliable than regex-based string/comment removal
        string error;
        var isValid = ValidateJavaScriptSyntax(filePath, out error);
        
        Assert.True(isValid, $"JavaScript syntax validation failed: {error}");
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

    private string RemoveStringsAndComments(string content)
    {
        // Simple removal of strings and comments for brace counting
        // This is not perfect but good enough for basic validation
        var result = content;
        
        // Remove single-line comments
        result = System.Text.RegularExpressions.Regex.Replace(result, @"//.*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);
        
        // Remove multi-line comments
        result = System.Text.RegularExpressions.Regex.Replace(result, @"/\*.*?\*/", "", System.Text.RegularExpressions.RegexOptions.Singleline);
        
        // Remove strings (simple approach)
        result = System.Text.RegularExpressions.Regex.Replace(result, @"""(?:[^""\\]|\\.)*""", "");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"'(?:[^'\\]|\\.)*'", "");
        result = System.Text.RegularExpressions.Regex.Replace(result, @"`(?:[^`\\]|\\.)*`", "");
        
        return result;
    }
}
