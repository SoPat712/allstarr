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
                     "BACKEND_TYPE", "LIBRARY_DOWNLOAD_PATH", "LIBRARY_KEPT_PATH",
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
                     "BACKEND_TYPE", "LIBRARY_DOWNLOAD_PATH", "LIBRARY_KEPT_PATH",
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

    [Fact]
    public void ProviderSecrets_UseEncryptedAccountRotationInsteadOfRuntimeSettings()
    {
        var controller = File.ReadAllText(FindRepositoryFile("allstarr", "Controllers", "AdminUiController.cs"));
        var script = File.ReadAllText(FindRepositoryFile("allstarr", "wwwroot", "js", "webui.js"));
        Assert.DoesNotContain("Field(\"SPOTIFY_API_SESSION_COOKIE\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Field(\"DEEZER_ARL\"", controller, StringComparison.Ordinal);
        Assert.DoesNotContain("Field(\"QOBUZ_USER_AUTH_TOKEN\"", controller, StringComparison.Ordinal);
        Assert.Contains("replaceProviderAccountSecret", script, StringComparison.Ordinal);
        Assert.Contains("setProviderAccountEnabled", script, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeConfiguration_NeverReadsOrWritesDotEnvAsLiveState()
    {
        var files = new[]
        {
            FindRepositoryFile("allstarr", "Services", "Admin", "AdminHelperService.cs"),
            FindRepositoryFile("allstarr", "Services", "Spotify", "SpotifySessionCookieService.cs"),
            FindRepositoryFile("allstarr", "Services", "Common", "ExtensionManager.cs"),
            FindRepositoryFile("allstarr", "Controllers", "ConfigController.cs"),
            FindRepositoryFile("allstarr", "Controllers", "JellyfinAdminController.cs"),
            FindRepositoryFile("allstarr", "Controllers", "SpotifyAdminController.cs"),
            FindRepositoryFile("allstarr", "Controllers", "ScrobblingAdminController.cs")
        };
        var source = string.Join('\n', files.Select(File.ReadAllText));

        Assert.DoesNotContain("UpdateEnvConfigAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadPlaylistsFromEnvFileAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("ReadJsonFileAsync", source, StringComparison.Ordinal);
        Assert.DoesNotContain("WriteJsonFileAsync", source, StringComparison.Ordinal);
        foreach (var path in files.Take(2))
        {
            Assert.DoesNotContain("WriteAllText", File.ReadAllText(path), StringComparison.Ordinal);
        }

        var migration = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Configuration", "LegacyEnvMigrationService.cs"));
        Assert.Contains("LegacyEnvImportRecord", migration, StringComparison.Ordinal);
        Assert.Contains("AppliedAt", migration, StringComparison.Ordinal);
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
