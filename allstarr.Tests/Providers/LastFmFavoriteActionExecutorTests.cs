using System.Net;
using System.Text.Json;
using allstarr.Core.Favorites;
using allstarr.Core.ManagedFiles;

namespace allstarr.Tests;

public sealed class LastFmFavoriteActionExecutorTests
{
    private static readonly ManagedTrackPathValues Track = new("Track", "Artist", "Album");

    [Fact]
    public async Task MissingLibraryScope_IsSuccessfulWithoutCreatingHttpClient()
    {
        var clients = new RecordingClientFactory();
        var executor = new LastFmFavoriteActionExecutor(clients, null!, null!);

        var result = await executor.ExecuteAsync(
            new FavoriteEventRecord { Operation = FavoriteOperation.Favorite },
            new FavoriteActionRecord(), CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Null(clients.CreatedName);
    }

    [Theory]
    [InlineData(FavoriteOperation.Favorite, "track.love")]
    [InlineData(FavoriteOperation.Unfavorite, "track.unlove")]
    public void Request_ContainsOnlySignedLoveFields(FavoriteOperation operation, string method)
    {
        using var secret = JsonDocument.Parse("{\"apiKey\":\"key\",\"sessionKey\":\"session\",\"sharedSecret\":\"secret\"}");

        var values = LastFmFavoriteActionExecutor.BuildRequestValues(operation, Track, secret.RootElement);

        Assert.Equal(["api_key", "api_sig", "artist", "method", "sk", "track"], values.Keys.Order(StringComparer.Ordinal));
        Assert.Equal(method, values["method"]);
        Assert.Equal("Artist", values["artist"]);
        Assert.Equal("Track", values["track"]);
        Assert.DoesNotContain("album", values.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", values.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Matches("^[a-f0-9]{32}$", values["api_sig"]);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(11)]
    [InlineData(16)]
    [InlineData(29)]
    public void Classifier_RetryableApiErrorsRetry(int code)
    {
        var result = LastFmFavoriteActionExecutor.Classify(
            HttpStatusCode.OK, $"{{\"error\":{code},\"message\":\"not returned\"}}");

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(26)]
    public void Classifier_AuthAndConfigApiErrorsArePermanent(int code)
    {
        var result = LastFmFavoriteActionExecutor.Classify(
            HttpStatusCode.OK, $"{{\"error\":{code},\"message\":\"not returned\"}}");

        Assert.False(result.Succeeded);
        Assert.False(result.Retryable);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public void Classifier_TransientHttpErrorsRetry(HttpStatusCode status)
    {
        var result = LastFmFavoriteActionExecutor.Classify(status, "");

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [Theory]
    [InlineData("not-json-or-xml")]
    [InlineData("<lfm status=\"ok\">")]
    public void Classifier_MalformedResponseRetries(string body)
    {
        var result = LastFmFavoriteActionExecutor.Classify(HttpStatusCode.OK, body);

        Assert.False(result.Succeeded);
        Assert.True(result.Retryable);
    }

    [Theory]
    [InlineData("{\"status\":\"ok\"}")]
    [InlineData("<lfm status=\"ok\" />")]
    public void Classifier_ProviderSuccessCompletes(string body)
    {
        var result = LastFmFavoriteActionExecutor.Classify(HttpStatusCode.OK, body);

        Assert.True(result.Succeeded);
        Assert.False(result.Retryable);
    }

    private sealed class RecordingClientFactory : IHttpClientFactory
    {
        public string? CreatedName { get; private set; }

        public HttpClient CreateClient(string name)
        {
            CreatedName = name;
            throw new InvalidOperationException("The no-op path must not create a client.");
        }
    }
}
