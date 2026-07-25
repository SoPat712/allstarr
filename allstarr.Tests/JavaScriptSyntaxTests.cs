using System.Diagnostics;

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
        var projectRoot = Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), "..", "..", "..", ".."));
        _wwwrootPath = Path.Combine(projectRoot, "allstarr", "wwwroot");
    }

    [Fact]
    public void WwwrootJavaScriptFiles_ShouldHaveValidSyntax()
    {
        var files = Directory
            .EnumerateFiles(_wwwrootPath, "*.js", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        Assert.NotEmpty(files);

        foreach (var filePath in files)
        {
            var isValid = ValidateJavaScriptSyntax(filePath, out var error);
            Assert.True(isValid, $"{Path.GetRelativePath(_wwwrootPath, filePath)} has syntax errors:\n{error}");
        }
    }

    [Fact]
    public void IndexHtml_ShouldLoadLitWebUiEntryPoint()
    {
        var indexPath = Path.Combine(_wwwrootPath, "index.html");
        var content = File.ReadAllText(indexPath);

        Assert.Contains("<allstarr-app>", content);
        Assert.Contains("/js/webui.js", content);
        Assert.Contains("/css/foundation.css", content);
        Assert.Contains("/css/workspaces.css", content);
    }

    [Fact]
    public void WebUi_ShouldUseSchemaDrivenLitShell()
    {
        var webuiPath = Path.Combine(_wwwrootPath, "js", "webui.js");
        var content = File.ReadAllText(webuiPath);

        Assert.Contains("/js/lit-3.3.3.js", content);
        Assert.DoesNotContain("from \"https://", content);
        Assert.DoesNotContain("from \"http://", content);
        Assert.DoesNotContain("import(\"https://", content);
        Assert.DoesNotContain("import(\"http://", content);
        Assert.Contains("/api/admin/ui/schema", content);
        Assert.Contains("customElements.define(\"allstarr-app\"", content);
        Assert.Contains("EventSource(\"/api/admin/downloads/activity\")", content);
        Assert.Contains("/api/admin/extensions/registries", content);
        Assert.Contains("Discovered Apple download capabilities", content);
    }

    [Fact]
    public void WebUiRuntime_ShouldBeLocalAndLicenseAttributed()
    {
        var runtimePath = Path.Combine(_wwwrootPath, "js", "lit-3.3.3.js");
        var licensePath = Path.Combine(_wwwrootPath, "vendor", "LICENSE.lit");

        Assert.True(File.Exists(runtimePath));
        Assert.True(File.Exists(licensePath));
        var runtime = File.ReadAllText(runtimePath);
        Assert.Contains("SPDX-License-Identifier: BSD-3-Clause", runtime);
        Assert.DoesNotContain("https://", runtime);
        Assert.DoesNotContain("/npm/", runtime);
    }

    [Fact]
    public void SpotifyMappingsHtml_ShouldRedirectToIntegratedRoute()
    {
        var mappingsPath = Path.Combine(_wwwrootPath, "spotify-mappings.html");
        var content = File.ReadAllText(mappingsPath);

        Assert.Contains("/#/library/mappings", content);
    }

    private static bool ValidateJavaScriptSyntax(string filePath, out string error)
    {
        error = string.Empty;

        try
        {
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
