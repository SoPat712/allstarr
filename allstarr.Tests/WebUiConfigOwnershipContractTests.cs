using System.Text.Json;
using System.Reflection;
using allstarr.Controllers;
using allstarr.Models.Admin;

namespace allstarr.Tests;

public sealed class WebUiConfigOwnershipContractTests
{
    [Fact]
    public void SchemaFields_DefaultToLiveDurableOwnershipWithoutRestart()
    {
        var field = new AdminUiConfigField { Key = "CACHE_LYRICS_DAYS", Label = "Lyrics days" };

        Assert.Equal("durable", field.Ownership);
        Assert.False(field.ReadOnly);
        Assert.False(field.RequiresRestart);
        var json = JsonSerializer.Serialize(field);
        Assert.Contains("\"ownership\":\"durable\"", json, StringComparison.Ordinal);
        Assert.Contains("\"readOnly\":false", json, StringComparison.Ordinal);
        Assert.Contains("\"requiresRestart\":false", json, StringComparison.Ordinal);
    }

    [Fact]
    public void DeploymentFields_AreDeclaredReadOnlyWithOperatorHelp()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));
        foreach (var key in new[]
                 {
                     "BACKEND_TYPE", "REDIS_ENABLED", "LIBRARY_DOWNLOAD_PATH", "LIBRARY_KEPT_PATH",
                     "ADMIN_BIND_ANY_IP", "ADMIN_TRUSTED_SUBNETS", "DEBUG_LOG_ALL_REQUESTS"
                 })
        {
            Assert.Contains($"DeploymentField(\"{key}\"", controller, StringComparison.Ordinal);
        }
        Assert.Contains("ownership: \"deployment\"", controller, StringComparison.Ordinal);
        Assert.Contains("readOnly: true", controller, StringComparison.Ordinal);
        Assert.Contains("Edit in Compose/.env", controller, StringComparison.Ordinal);
        Assert.Contains("SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS\", \"Matching interval hours\", \"number\", \"spotifyImport.matchingIntervalHours\", min: 0", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void BuiltSchema_SeparatesDeploymentAndDurableSettings()
    {
        var method = typeof(AdminUiController).GetMethod("BuildConfigSections", BindingFlags.NonPublic | BindingFlags.Static);
        var sections = Assert.IsType<List<AdminUiConfigSection>>(method?.Invoke(null, null));
        var fields = sections.SelectMany(section => section.Fields).ToDictionary(field => field.Key);

        foreach (var key in new[]
                 {
                     "BACKEND_TYPE", "REDIS_ENABLED", "LIBRARY_DOWNLOAD_PATH", "LIBRARY_KEPT_PATH",
                     "ADMIN_BIND_ANY_IP", "ADMIN_TRUSTED_SUBNETS", "DEBUG_LOG_ALL_REQUESTS"
                 })
        {
            Assert.Equal("deployment", fields[key].Ownership);
            Assert.True(fields[key].ReadOnly);
            Assert.Contains("Compose/.env", fields[key].HelpText ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.Equal("durable", fields["CACHE_LYRICS_DAYS"].Ownership);
        Assert.False(fields["CACHE_LYRICS_DAYS"].ReadOnly);
        Assert.False(fields["CACHE_LYRICS_DAYS"].RequiresRestart);
        Assert.Equal(0, fields["SPOTIFY_IMPORT_MATCHING_INTERVAL_HOURS"].Min);
    }

    [Fact]
    public void Renderer_NeverPersistsDeploymentOwnedFields()
    {
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        Assert.Contains("if (field.readOnly || field.ownership === \"deployment\")", script, StringComparison.Ordinal);
        Assert.Contains("?disabled=${readOnly}", script, StringComparison.Ordinal);
        Assert.Contains("?readonly=${readOnly}", script, StringComparison.Ordinal);
        Assert.Contains("Deployment owned", script, StringComparison.Ordinal);
        Assert.Contains("field.requiresRestart", script, StringComparison.Ordinal);
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
