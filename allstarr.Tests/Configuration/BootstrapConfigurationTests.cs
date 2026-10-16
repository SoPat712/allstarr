using allstarr.Services.Common;
using Microsoft.Extensions.Configuration;

namespace allstarr.Tests;

[Collection(nameof(EnvironmentVariableCollection))]
public sealed class BootstrapConfigurationTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "allstarr-bootstrap", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Bootstrap_ReadsLegacyReachabilityAndBackendButLeavesProductValuesForExplicitImport()
    {
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, ".env");
        const string source = """
            BACKEND_TYPE=Subsonic
            SUBSONIC_URL=http://backend-fixture:4533
            Admin__BindAnyIp=true
            Admin__TrustedSubnets=192.0.2.0/24
            Admin__ContainerGateway=auto
            Admin__Containerized=true
            Storage__DataDirectory=/isolated/data
            CACHE_LYRICS_DAYS=80
            AUDIO_QUALITY=lossy
            JELLYFIN_API_KEY=private-bootstrap-fixture
            DEEZER_ARL=private-source-fixture
            """;
        File.WriteAllText(path, source);
        var settings = new ConfigurationManager();
        settings.AddInMemoryCollection(new Dictionary<string, string?> { ["Cache:LyricsDays"] = "14", ["Audio:Quality"] = "lossless" });
        RuntimeEnvConfiguration.AddBootstrapOverrides(settings, path);
        Assert.Equal("Subsonic", settings["Backend:Type"]);
        Assert.Equal("http://backend-fixture:4533", settings["Subsonic:Url"]);
        Assert.Equal("true", settings["Admin:BindAnyIp"]);
        Assert.Equal("192.0.2.0/24", settings["Admin:TrustedSubnets"]);
        Assert.Equal("/isolated/data", settings["Storage:DataDirectory"]);
        Assert.Equal("auto", settings["Admin:ContainerGateway"]);
        Assert.Equal("true", settings["Admin:Containerized"]);
        Assert.Equal("14", settings["Cache:LyricsDays"]);
        Assert.Equal("lossless", settings["Audio:Quality"]);
        Assert.Null(settings["Jellyfin:ApiKey"]);
        Assert.Null(settings["Deezer:Arl"]);
        Assert.Equal(source, File.ReadAllText(path));
    }

    [Theory]
    [InlineData(null, "Cache__LyricsDays")]
    [InlineData("ASPNETCORE_", "ASPNETCORE_Cache__LyricsDays")]
    public void Bootstrap_RemovesTheFrameworkProductEnvironmentProviderWhilePreservingDefaults(string? prefix, string variable)
    {
        var prior = Environment.GetEnvironmentVariable(variable);
        try
        {
            Environment.SetEnvironmentVariable(variable, "99");
            var settings = new ConfigurationManager();
            settings.AddInMemoryCollection(new Dictionary<string, string?> { ["Cache:LyricsDays"] = "14" });
            settings.AddEnvironmentVariables(prefix);
            RuntimeEnvConfiguration.AddBootstrapOverrides(settings, Path.Combine(root, "missing.env"));
            Assert.Equal("14", settings["Cache:LyricsDays"]);
        }
        finally { Environment.SetEnvironmentVariable(variable, prior); }
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
