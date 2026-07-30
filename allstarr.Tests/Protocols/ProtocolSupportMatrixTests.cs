using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using allstarr.Services.Jellyfin;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests;

public sealed class ProtocolSupportMatrixTests
{
    [Fact]
    public void SourceLock_MatchesPinnedJellyfinOpenApi()
    {
        using var sourceLock = ReadFixture("protocol-source-lock.json");
        AssertPinnedOpenApi(
            sourceLock.RootElement.GetProperty("jellyfinOpenApi"),
            "https://fra1.mirror.jellyfin.org/files/files/openapi/jellyfin-openapi-stable.json",
            expectedServerCommit: null);
        AssertPinnedOpenApi(
            sourceLock.RootElement.GetProperty("jellyfinOpenApi10"),
            "https://blr1.mirror.jellyfin.org/files/files/files/openapi/stable/jellyfin-openapi-10.11.11.json",
            "1fbd8739292cce610231be93daf43368733edf63");
        AssertPinnedSource(
            sourceLock.RootElement.GetProperty("octoFiesta"),
            "https://github.com/V1ck3s/octo-fiesta");
        AssertPinnedSource(
            sourceLock.RootElement.GetProperty("jellyfinLastFmReference"),
            "https://github.com/danielfariati/jellyfin-plugin-lastfm");
    }

    private static void AssertPinnedOpenApi(
        JsonElement expected,
        string expectedUrl,
        string? expectedServerCommit)
    {
        Assert.Equal(expectedUrl, expected.GetProperty("sourceUrl").GetString());
        var repositoryRoot = FindRepositoryRoot();
        var openApiPath = Path.Combine(repositoryRoot, expected.GetProperty("path").GetString()!);

        using var openApi = JsonDocument.Parse(File.ReadAllText(openApiPath));
        Assert.Equal(expected.GetProperty("version").GetString(),
            openApi.RootElement.GetProperty("info").GetProperty("version").GetString());

        using var stream = File.OpenRead(openApiPath);
        var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        Assert.Equal(expected.GetProperty("sha256").GetString(), actualHash);

        if (expectedServerCommit != null)
        {
            Assert.Equal(expectedServerCommit, expected.GetProperty("serverCommit").GetString());
        }
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
            "/MusicGenres/InstantMix",
            "/MusicGenres/{name}/InstantMix",
            "/Playlists/{itemId}/InstantMix",
            "/Songs/{itemId}/InstantMix"
        };

        Assert.All(required, path => Assert.True(paths.TryGetProperty(path, out _), path));
    }

    [Fact]
    public void JellyfinOpenApi_EveryOperationMatchesReviewedMusicOnlyPolicy()
    {
        using var qualification = ReadFixture("jellyfin-openapi-qualification.json");
        var contract = qualification.RootElement;
        var openApiContract = contract.GetProperty("openApi");
        Assert.Equal("Denied", openApiContract.GetProperty("defaultAccess").GetString());

        var openApiPath = Path.Combine(
            FindRepositoryRoot(),
            "apis",
            "specifications",
            "jellyfin",
            "openapi-12.0.0.json");
        using var openApi = JsonDocument.Parse(File.ReadAllText(openApiPath));
        Assert.Equal(
            openApiContract.GetProperty("version").GetString(),
            openApi.RootElement.GetProperty("info").GetProperty("version").GetString());

        var reviewed = new Dictionary<string, JellyfinEndpointAccess>(StringComparer.Ordinal);
        foreach (var accessGroup in contract.GetProperty("allowedOperations").EnumerateObject())
        {
            var access = Enum.Parse<JellyfinEndpointAccess>(accessGroup.Name);
            foreach (var operationId in accessGroup.Value.EnumerateArray().Select(value => value.GetString()!))
                Assert.True(reviewed.TryAdd(operationId, access), $"Duplicate reviewed operation {operationId}");
        }
        var synthesizedDenied = contract.GetProperty("synthesizedDeniedOperations")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        Assert.All(synthesizedDenied, operationId =>
            Assert.Equal(JellyfinEndpointAccess.RequiresMusicItem, reviewed[operationId]));

        var operations = new List<(string Id, string Method, string Path)>();
        var methods = new HashSet<string>(
            ["GET", "PUT", "POST", "DELETE", "PATCH", "OPTIONS", "HEAD", "TRACE"],
            StringComparer.Ordinal);
        foreach (var path in openApi.RootElement.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                if (!methods.Contains(method.Name.ToUpperInvariant())) continue;
                operations.Add((
                    method.Value.GetProperty("operationId").GetString()!,
                    method.Name.ToUpperInvariant(),
                    path.Name));
            }
        }

        Assert.Equal(openApiContract.GetProperty("operationCount").GetInt32(), operations.Count);
        Assert.Equal(operations.Count, operations.Select(operation => operation.Id).Distinct().Count());
        Assert.All(reviewed.Keys, operationId =>
            Assert.Contains(operations, operation => operation.Id == operationId));

        foreach (var operation in operations)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = operation.Method;
            context.Request.Path = Regex.Replace(operation.Path, "\\{[^}]+\\}", "fixture-id");
            var actual = JellyfinMusicEndpointPolicy.Evaluate(context.Request).Access;
            var expected = reviewed.GetValueOrDefault(operation.Id, JellyfinEndpointAccess.Denied);
            Assert.True(
                expected == actual,
                $"{operation.Method} {operation.Path} ({operation.Id}) expected {expected}, actual {actual}");

            if (expected == JellyfinEndpointAccess.RequiresPlaylistItem)
            {
                Assert.Equal("DeleteItem", operation.Id);
                Assert.Equal("DELETE", operation.Method);
                continue;
            }

            if (expected != JellyfinEndpointAccess.RequiresMusicItem) continue;
            const string synthesizedId = "ext-fixture-song-1";
            var synthesizedContext = new DefaultHttpContext();
            synthesizedContext.Request.Method = operation.Method;
            synthesizedContext.Request.Path = SynthesizedOperationPath(operation.Path, synthesizedId);
            var supportsSynthesized = JellyfinMusicEndpointPolicy.SupportsSynthesizedItemRoute(
                synthesizedContext.Request,
                synthesizedId);
            Assert.True(
                supportsSynthesized != synthesizedDenied.Contains(operation.Id),
                $"{operation.Method} {operation.Path} ({operation.Id}) has an unreviewed synthesized-resource mode");
        }

        var typedModes = contract.GetProperty("synthesizedTypedOperations");
        var supportedTyped = typedModes.GetProperty("supported").EnumerateArray().ToList();
        var deniedTyped = typedModes.GetProperty("denied").EnumerateArray().ToList();
        var reviewedTypedIds = supportedTyped.Concat(deniedTyped)
            .Select(mode => mode.GetProperty("operationId").GetString()!)
            .ToArray();
        var expectedTypedIds = operations.Where(operation =>
                operation.Path.Contains('{') &&
                (operation.Path.StartsWith("/Artists/", StringComparison.Ordinal) ||
                 operation.Path.StartsWith("/Albums/", StringComparison.Ordinal) ||
                 operation.Path.StartsWith("/Songs/", StringComparison.Ordinal) ||
                 operation.Path.StartsWith("/Genres/", StringComparison.Ordinal) ||
                 operation.Path.StartsWith("/MusicGenres/", StringComparison.Ordinal)))
            .Select(operation => operation.Id)
            .Append("GetInstantMixFromMusicGenreById")
            .Order()
            .ToArray();
        Assert.Equal(expectedTypedIds, reviewedTypedIds.Order().ToArray());
        Assert.Equal(reviewedTypedIds.Length, reviewedTypedIds.Distinct(StringComparer.Ordinal).Count());
        foreach (var mode in supportedTyped.Concat(deniedTyped))
        {
            Assert.Contains(
                mode.GetProperty("resourceType").GetString(),
                new[] { "song", "album", "artist", "genre" });
            var uri = new Uri("http://localhost" + mode.GetProperty("path").GetString());
            var context = new DefaultHttpContext();
            context.Request.Method = mode.GetProperty("method").GetString()!;
            context.Request.Path = uri.AbsolutePath;
            context.Request.QueryString = new QueryString(uri.Query);
            var controllerEnforced = mode.TryGetProperty("enforcement", out var enforcement) &&
                                     enforcement.GetString() == "controller";
            var expected = supportedTyped.Any(candidate =>
                               candidate.GetProperty("operationId").GetString() ==
                               mode.GetProperty("operationId").GetString()) ||
                           controllerEnforced
                ? JellyfinEndpointAccess.Music
                : JellyfinEndpointAccess.Denied;
            Assert.Equal(expected, JellyfinMusicEndpointPolicy.Evaluate(context.Request).Access);
        }

        var synthesizedPlaylistModes = contract.GetProperty("synthesizedPlaylistOperations");
        var projectedPlaylistOperations = synthesizedPlaylistModes.GetProperty("projectedReads")
            .EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var targetRequiredPlaylistOperations = synthesizedPlaylistModes.GetProperty("targetRequired")
            .EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var notApplicablePlaylistOperations = synthesizedPlaylistModes.GetProperty("notApplicable")
            .EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var supportedPlaylistOperations = projectedPlaylistOperations
            .Concat(targetRequiredPlaylistOperations)
            .ToHashSet(StringComparer.Ordinal);
        var reviewedPlaylistOperations = supportedPlaylistOperations
            .Concat(notApplicablePlaylistOperations)
            .ToList();
        Assert.Equal(reviewedPlaylistOperations.Count,
            reviewedPlaylistOperations.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(
            operations.Where(operation => operation.Path.StartsWith("/Playlists", StringComparison.Ordinal))
                .Select(operation => operation.Id).Order(),
            reviewedPlaylistOperations.Order());

        const string synthesizedPlaylistId = "allstarr-vpl-0198a537719c7ea89e5a17e1f2f963f0";
        foreach (var operationId in supportedPlaylistOperations)
        {
            var operation = Assert.Single(operations, candidate => candidate.Id == operationId);
            var context = new DefaultHttpContext();
            context.Request.Method = operation.Method;
            context.Request.Path = Regex.Replace(
                operation.Path
                    .Replace("{playlistId}", synthesizedPlaylistId, StringComparison.OrdinalIgnoreCase)
                    .Replace("{itemId}", synthesizedPlaylistId, StringComparison.OrdinalIgnoreCase),
                "\\{[^}]+\\}",
                "fixture-id");
            Assert.True(JellyfinMusicEndpointPolicy.SupportsSynthesizedPlaylistRoute(
                context.Request, synthesizedPlaylistId));
            Assert.Equal(
                JellyfinEndpointAccess.Music,
                JellyfinMusicEndpointPolicy.Evaluate(context.Request).Access);
        }
    }

    [Fact]
    public void JellyfinOpenApi_10_11EveryOperationMatchesReviewedMusicOnlyPolicy()
    {
        using var baseline = ReadFixture("jellyfin-openapi-qualification.json");
        using var qualification = ReadFixture("jellyfin-openapi-10.11-qualification.json");
        var reviewed = new Dictionary<string, JellyfinEndpointAccess>(StringComparer.Ordinal);
        foreach (var accessGroup in baseline.RootElement.GetProperty("allowedOperations").EnumerateObject())
        {
            var access = Enum.Parse<JellyfinEndpointAccess>(accessGroup.Name);
            foreach (var operationId in accessGroup.Value.EnumerateArray().Select(value => value.GetString()!))
                Assert.True(reviewed.TryAdd(operationId, access), operationId);
        }

        var versionOnly = qualification.RootElement.GetProperty("versionOnlyOperations");
        foreach (var accessGroup in versionOnly.GetProperty("allowed").EnumerateObject())
        {
            var access = Enum.Parse<JellyfinEndpointAccess>(accessGroup.Name);
            foreach (var operationId in accessGroup.Value.EnumerateArray().Select(value => value.GetString()!))
                Assert.True(reviewed.TryAdd(operationId, access), operationId);
        }

        var root = FindRepositoryRoot();
        using var openApi10 = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "apis", "specifications", "jellyfin", "openapi-10.11.11.json")));
        using var openApi12 = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            root, "apis", "specifications", "jellyfin", "openapi-12.0.0.json")));
        var operations10 = ReadOperations(openApi10.RootElement);
        var operations12 = ReadOperations(openApi12.RootElement);
        var contract = qualification.RootElement.GetProperty("openApi");

        Assert.Equal(contract.GetProperty("version").GetString(),
            openApi10.RootElement.GetProperty("info").GetProperty("version").GetString());
        Assert.Equal(contract.GetProperty("operationCount").GetInt32(), operations10.Count);
        Assert.Equal(operations10.Count, operations10.Select(operation => operation.Id).Distinct().Count());

        var onlyIn10 = operations10.Select(operation => operation.Id)
            .Except(operations12.Select(operation => operation.Id), StringComparer.Ordinal)
            .Order().ToArray();
        var reviewedOnlyIn10 = versionOnly.GetProperty("allowed").EnumerateObject()
            .SelectMany(group => group.Value.EnumerateArray().Select(value => value.GetString()!))
            .Concat(versionOnly.GetProperty("denied").EnumerateArray().Select(value => value.GetString()!))
            .Order().ToArray();
        Assert.Equal(reviewedOnlyIn10, onlyIn10);

        var onlyIn12 = operations12.Select(operation => operation.Id)
            .Except(operations10.Select(operation => operation.Id), StringComparer.Ordinal)
            .Order().ToArray();
        Assert.Equal(
            qualification.RootElement.GetProperty("onlyInTwelve")
                .EnumerateArray().Select(value => value.GetString()!).Order().ToArray(),
            onlyIn12);

        foreach (var operation in operations10)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = operation.Method;
            context.Request.Path = Regex.Replace(operation.Path, "\\{[^}]+\\}", "fixture-id");
            var expected = reviewed.GetValueOrDefault(operation.Id, JellyfinEndpointAccess.Denied);
            var actual = JellyfinMusicEndpointPolicy.Evaluate(context.Request).Access;
            Assert.True(
                expected == actual,
                $"{operation.Method} {operation.Path} ({operation.Id}) expected {expected}, actual {actual}");

            if (!onlyIn10.Contains(operation.Id, StringComparer.Ordinal) ||
                expected != JellyfinEndpointAccess.RequiresMusicItem)
                continue;

            const string synthesizedId = "ext-fixture-song-1";
            var synthesizedContext = new DefaultHttpContext();
            synthesizedContext.Request.Method = operation.Method;
            synthesizedContext.Request.Path = SynthesizedOperationPath(operation.Path, synthesizedId);
            Assert.False(JellyfinMusicEndpointPolicy.SupportsSynthesizedItemRoute(
                synthesizedContext.Request, synthesizedId));
        }

        Assert.Equal(
            operations12.Where(operation => operation.Path.StartsWith("/Playlists", StringComparison.Ordinal))
                .Select(operation => operation.Id).Order(),
            operations10.Where(operation => operation.Path.StartsWith("/Playlists", StringComparison.Ordinal))
                .Select(operation => operation.Id).Order());

        var typed10 = Assert.Single(
            qualification.RootElement.GetProperty("synthesizedTypedOperations")
                .GetProperty("supported").EnumerateArray());
        Assert.Equal("GetInstantMixFromArtists2", typed10.GetProperty("operationId").GetString());
        var typed10Uri = new Uri("http://localhost" + typed10.GetProperty("path").GetString());
        var typed10Context = new DefaultHttpContext();
        typed10Context.Request.Method = typed10.GetProperty("method").GetString()!;
        typed10Context.Request.Path = typed10Uri.AbsolutePath;
        typed10Context.Request.QueryString = new QueryString(typed10Uri.Query);
        Assert.Equal(
            JellyfinEndpointAccess.Music,
            JellyfinMusicEndpointPolicy.Evaluate(typed10Context.Request).Access);
    }

    [Fact]
    public void JellyfinOpenApi_QualificationRecordsComparisonsDtoRulesAndLiveBlockers()
    {
        using var qualification = ReadFixture("jellyfin-openapi-qualification.json");
        var root = qualification.RootElement;

        Assert.Equal("blocked-no-runtime",
            root.GetProperty("liveEvidence").GetProperty("runtime12Status").GetString());
        Assert.All(
            new[] { "native-relay", "constrained-relay", "synthesized", "transformed", "binary-relay", "denied" },
            name => Assert.True(root.GetProperty("comparisonClasses").TryGetProperty(name, out _), name));
        Assert.NotEmpty(root.GetProperty("clientDtoRequirements").EnumerateObject());
        Assert.Equal(
            ["ItemIds", "Shares"],
            root.GetProperty("clientDtoRequirements").GetProperty("playlistDefinitionRequired")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.Equal(
            ["Key"],
            root.GetProperty("clientDtoRequirements").GetProperty("userDataRequired")
                .EnumerateArray().Select(value => value.GetString()!).ToArray());
        Assert.NotEmpty(root.GetProperty("intentionalDifferences").EnumerateArray());
        Assert.Contains(root.GetProperty("liveModes").EnumerateArray(),
            mode => mode.GetProperty("status").GetString() == "blocked-without-explicit-opt-in");
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
    public void SupportMatrix_CurrentDurableAndScopedProtocolClaimsNameTheirRegressionTests()
    {
        using var matrix = ReadFixture("protocol-support-matrix.json");
        var rows = matrix.RootElement.EnumerateArray().ToList();

        var playlistUpdate = Assert.Single(rows, row =>
            row.GetProperty("protocol").GetString() == "subsonic" &&
            row.GetProperty("feature").GetString() == "playlist-update");
        Assert.Equal("explicit", playlistUpdate.GetProperty("currentStatus").GetString());
        Assert.Contains("exact tenant, owner", playlistUpdate.GetProperty("authBoundary").GetString(),
            StringComparison.Ordinal);
        Assert.Contains("SubsonicPlaylistMutationTests", playlistUpdate.GetProperty("testLocation").GetString(),
            StringComparison.Ordinal);

        Assert.All(rows.Where(row => row.GetProperty("feature").GetString() is "favorites" or "star-and-unstar"),
            row => Assert.Contains("FavoriteActionPipelineTests", row.GetProperty("testLocation").GetString(),
                StringComparison.Ordinal));
        Assert.All(rows.Where(row => row.GetProperty("feature").GetString() is "playback-and-scrobbling" or "scrobble"),
            row => Assert.Contains("PlaybackSignalPipelineTests", row.GetProperty("testLocation").GetString(),
                StringComparison.Ordinal));

        var routedRows = rows.Where(row =>
            row.GetProperty("feature").GetString() is "search-and-browse" or "search3" or
                "item-metadata-and-images" or "item-metadata-and-cover-art" or "streaming-and-ranges");
        Assert.All(routedRows, row => Assert.Contains(
            "ProtocolProviderGatewayContractTests",
            row.GetProperty("testLocation").GetString(),
            StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row =>
            row.GetProperty("authBoundary").GetString()!.Contains(
                "does not yet select a provider account",
                StringComparison.Ordinal));
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

    private static string SynthesizedOperationPath(string template, string itemId) =>
        Regex.Replace(
            template
                .Replace("{itemId}", itemId, StringComparison.OrdinalIgnoreCase)
                .Replace("{imageType}", "Primary", StringComparison.OrdinalIgnoreCase)
                .Replace("{imageIndex}", "0", StringComparison.OrdinalIgnoreCase)
                .Replace("{tag}", "revision", StringComparison.OrdinalIgnoreCase)
                .Replace("{format}", "jpg", StringComparison.OrdinalIgnoreCase)
                .Replace("{maxWidth}", "300", StringComparison.OrdinalIgnoreCase)
                .Replace("{maxHeight}", "300", StringComparison.OrdinalIgnoreCase)
                .Replace("{percentPlayed}", "0", StringComparison.OrdinalIgnoreCase)
                .Replace("{unplayedCount}", "0", StringComparison.OrdinalIgnoreCase),
            "\\{[^}]+\\}",
            "fixture-id");

    private static List<(string Id, string Method, string Path)> ReadOperations(JsonElement openApi)
    {
        var methods = new HashSet<string>(
            ["GET", "PUT", "POST", "DELETE", "PATCH", "OPTIONS", "HEAD", "TRACE"],
            StringComparer.Ordinal);
        var operations = new List<(string Id, string Method, string Path)>();
        foreach (var path in openApi.GetProperty("paths").EnumerateObject())
        {
            foreach (var method in path.Value.EnumerateObject())
            {
                if (!methods.Contains(method.Name.ToUpperInvariant())) continue;
                operations.Add((
                    method.Value.GetProperty("operationId").GetString()!,
                    method.Name.ToUpperInvariant(),
                    path.Name));
            }
        }

        return operations;
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
