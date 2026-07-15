using System.Security.Cryptography;
using System.Text.Json;

namespace allstarr.Tests;

public sealed class ProtocolSupportMatrixTests
{
    [Fact]
    public void SourceLock_MatchesPinnedJellyfinOpenApi()
    {
        using var sourceLock = ReadFixture("protocol-source-lock.json");
        var expected = sourceLock.RootElement.GetProperty("jellyfinOpenApi");
        Assert.Equal(
            "https://fra1.mirror.jellyfin.org/files/files/openapi/jellyfin-openapi-stable.json",
            expected.GetProperty("sourceUrl").GetString());
        var repositoryRoot = FindRepositoryRoot();
        var openApiPath = Path.Combine(repositoryRoot, expected.GetProperty("path").GetString()!);

        using var openApi = JsonDocument.Parse(File.ReadAllText(openApiPath));
        Assert.Equal(expected.GetProperty("version").GetString(),
            openApi.RootElement.GetProperty("info").GetProperty("version").GetString());

        using var stream = File.OpenRead(openApiPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Assert.Equal(expected.GetProperty("sha256").GetString(), actualHash);

        AssertPinnedSource(
            sourceLock.RootElement.GetProperty("octoFiesta"),
            "https://github.com/V1ck3s/octo-fiesta");
        AssertPinnedSource(
            sourceLock.RootElement.GetProperty("jellyfinLastFmReference"),
            "https://github.com/danielfariati/jellyfin-plugin-lastfm");
    }

    private static void AssertPinnedSource(JsonElement source, string expectedUrl)
    {
        Assert.Equal(expectedUrl, source.GetProperty("sourceUrl").GetString());
        var commit = source.GetProperty("commit").GetString();
        Assert.NotNull(commit);
        Assert.Matches("^[0-9a-f]{40}$", commit);
    }

    [Fact]
    public void JellyfinOpenApi_ContainsEveryPinnedInstantMixPath()
    {
        var openApiPath = Path.Combine(FindRepositoryRoot(), "apis", "specifications", "jellyfin", "openapi-12.0.0.json");
        using var openApi = JsonDocument.Parse(File.ReadAllText(openApiPath));
        var paths = openApi.RootElement.GetProperty("paths");
        var required = new[]
        {
            "/Albums/{itemId}/InstantMix",
            "/Artists/{itemId}/InstantMix",
            "/Items/{itemId}/InstantMix",
            "/MusicGenres/{name}/InstantMix",
            "/Playlists/{itemId}/InstantMix",
            "/Songs/{itemId}/InstantMix"
        };

        Assert.All(required, path => Assert.True(paths.TryGetProperty(path, out _), path));
    }

    [Fact]
    public void SupportMatrix_HasUniqueCompleteRowsForBothProtocols()
    {
        using var matrix = ReadFixture("protocol-support-matrix.json");
        var rows = matrix.RootElement.EnumerateArray().ToList();

        Assert.Contains(rows, row => row.GetProperty("protocol").GetString() == "jellyfin");
        Assert.Contains(rows, row => row.GetProperty("protocol").GetString() == "subsonic");
        Assert.Equal(
            rows.Count,
            rows.Select(row => $"{row.GetProperty("protocol").GetString()}:{row.GetProperty("feature").GetString()}")
                .Distinct(StringComparer.Ordinal)
                .Count());

        Assert.All(rows, row =>
        {
            AssertRequired(row, "currentStatus");
            AssertRequired(row, "target");
            AssertRequired(row, "authBoundary");
            AssertRequired(row, "fixture");
            AssertRequired(row, "testLocation");
            AssertRequired(row, "notes");
        });
    }

    [Fact]
    public void SupportMatrix_EveryClaimedFixtureExistsAndContainsValidJson()
    {
        using var matrix = ReadFixture("protocol-support-matrix.json");
        var fixtureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "Protocols");

        foreach (var row in matrix.RootElement.EnumerateArray())
        {
            var rowName = $"{row.GetProperty("protocol").GetString()}:" +
                          row.GetProperty("feature").GetString();
            var fixtures = row.GetProperty("fixture").GetString()!
                .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            Assert.NotEmpty(fixtures);
            foreach (var fixture in fixtures)
            {
                Assert.EndsWith(".json", fixture, StringComparison.Ordinal);
                var path = Path.Combine(fixtureDirectory, fixture);
                Assert.True(File.Exists(path), $"{rowName} references missing fixture {fixture}");
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                Assert.NotEqual(JsonValueKind.Undefined, document.RootElement.ValueKind);
            }
        }
    }

    [Fact]
    public void JellyfinSearchAndInstantMix_DoNotUseFireAndForgetOrRandomOrdering()
    {
        var root = FindRepositoryRoot();
        var search = File.ReadAllText(Path.Combine(root, "allstarr", "Controllers", "JellyfinController.Search.cs"));
        var controller = File.ReadAllText(Path.Combine(root, "allstarr", "Controllers", "JellyfinController.cs"));

        Assert.DoesNotContain("Task.Run", search, StringComparison.Ordinal);
        Assert.DoesNotContain("new Random()", controller, StringComparison.Ordinal);
        Assert.Contains("StableInstantMixOrder", controller, StringComparison.Ordinal);
    }

    [Fact]
    public void Controllers_DoNotStartUntrackedTaskRunWork()
    {
        var directory = Path.Combine(FindRepositoryRoot(), "allstarr", "Controllers");
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
            Assert.DoesNotContain("Task.Run", File.ReadAllText(file), StringComparison.Ordinal);
    }

    private static void AssertRequired(JsonElement row, string property)
    {
        Assert.True(row.TryGetProperty(property, out var value), property);
        Assert.False(string.IsNullOrWhiteSpace(value.GetString()), property);
    }

    private static JsonDocument ReadFixture(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "Protocols", fileName);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Could not locate allstarr.sln");
    }
}
