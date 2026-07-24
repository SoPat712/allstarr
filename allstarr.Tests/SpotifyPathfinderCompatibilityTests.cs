using System.Net;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;

namespace allstarr.Tests;

public sealed class SpotifyPathfinderCompatibilityTests
{
    [Fact]
    public async Task StalePersistedQuery_ReturnsActionableHostError()
    {
        using var http = new HttpClient(new JsonHandler(
            """{"errors":[{"message":"PersistedQueryNotFound","extensions":{"code":"PERSISTED_QUERY_NOT_FOUND"}}]}"""));
        var client = new SpotifyPathfinderPlaylistClient(http);

        var outcome = await client.GetUserPlaylistsAsync(
            "token",
            new ProviderPageRequest(20),
            null,
            CancellationToken.None);

        Assert.False(outcome.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, outcome.Error!.Kind);
        Assert.Equal("provider-contract-changed", outcome.Error.Code);
        Assert.Contains("Update Allstarr", outcome.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistedQueries_HaveExplicitVersionedDefinitions()
    {
        var source = File.ReadAllText(FindRepositoryFile(
            "allstarr", "Core", "Providers", "Spotify", "SpotifyPathfinderPlaylistClient.cs"));

        Assert.Contains("PathfinderOperationDefinition LibraryQuery", source, StringComparison.Ordinal);
        Assert.Contains("PathfinderOperationDefinition PlaylistQuery", source, StringComparison.Ordinal);
        Assert.Contains("new(LibraryOperation, LibraryQueryHash, 1)", source, StringComparison.Ordinal);
        Assert.Contains("new(PlaylistOperation, PlaylistQueryHash, 1)", source, StringComparison.Ordinal);
        Assert.Contains("version = operation.Version", source, StringComparison.Ordinal);
        Assert.Contains("sha256Hash = operation.Sha256Hash", source, StringComparison.Ordinal);
    }

    private sealed class JsonHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }

    private static string FindRepositoryFile(params string[] parts)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "allstarr.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory?.FullName
            ?? throw new DirectoryNotFoundException("Could not locate the repository root."), Path.Combine(parts));
    }
}
