using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Providers.Qobuz;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace allstarr.Tests;

public sealed class QobuzMediaClientTests
{
    private const string MediaResponse =
        """{"url":"https://media.example.test/audio.flac","mime_type":"audio/flac","bit_depth":24,"sampling_rate":96}""";

    [Fact]
    public async Task ResolveDownloadAsync_SignsExactRequestUsingOnlySuppliedAccountToken()
    {
        using var http = CreateHttp((request, _) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://www.qobuz.com/api.json/0.2/track/getFileUrl", request.RequestUri!.GetLeftPart(UriPartial.Path));
            var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
            Assert.Equal("42", query["track_id"].ToString());
            Assert.Equal("7", query["format_id"].ToString());
            Assert.Equal("stream", query["intent"].ToString());
            var toSign = $"trackgetFileUrlformat_id7intentstreamtrack_id42{query["request_ts"]}fixture-signing-secret";
            var expectedSignature = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(toSign))).ToLowerInvariant();
            Assert.Equal(expectedSignature, query["request_sig"].ToString());
            Assert.Equal("123456789", Assert.Single(request.Headers.GetValues("X-App-Id")));
            Assert.Equal("fixture-account-token", Assert.Single(request.Headers.GetValues("X-User-Auth-Token")));
            Assert.False(request.Headers.Contains("Cookie"));
            return Task.FromResult(Json(HttpStatusCode.OK, MediaResponse));
        });
        // Request-scoped credentials must override, and never mutate, any caller defaults.
        http.DefaultRequestHeaders.Add("X-User-Auth-Token", "unrelated-default-token");
        var bundles = CreateBundles();
        var client = CreateClient(http, bundles.Object);

        var result = await client.ResolveDownloadAsync("42", "fixture-account-token", "FLAC_24_LOW", CancellationToken.None);

        Assert.Equal("https://media.example.test/audio.flac", result.Url);
        Assert.Equal(7, result.FormatId);
        Assert.Equal("audio/flac", result.MimeType);
        Assert.Equal(24, result.BitDepth);
        Assert.Equal(96, result.SamplingRate);
        Assert.False(result.IsSample);
        Assert.False(result.WasQualityDowngraded);
        Assert.Equal("unrelated-default-token", Assert.Single(http.DefaultRequestHeaders.GetValues("X-User-Auth-Token")));
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task ResolveDownloadAsync_MissingCredentialRejectsBeforeBundleOrNetwork(string? credential)
    {
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, MediaResponse));
        });
        var bundles = CreateBundles();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", credential, "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.Unauthorized, exception.StatusCode);
        Assert.Equal(0, requests);
        bundles.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ResolveDownloadAsync_BundleFailurePropagatesWithoutMediaRequest()
    {
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, MediaResponse));
        });
        var bundles = CreateBundles();
        bundles.Setup(bundle => bundle.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Signing bundle unavailable.", null, HttpStatusCode.ServiceUnavailable));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, exception.StatusCode);
        Assert.Equal(0, requests);
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveDownloadAsync_EmptySigningBundleIsNotUsable()
    {
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, MediaResponse));
        });
        var bundles = CreateBundles();
        bundles.Setup(bundle => bundle.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new QobuzSigningCredentials("123456789", []));

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "MP3", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(0, requests);
    }

    [Theory]
    [InlineData("FLAC", "27,7,6,5")]
    [InlineData("FLAC_24_HIGH", "27,7,6,5")]
    [InlineData("FLAC_24_LOW", "7,6,5")]
    [InlineData("24_96", "7,6,5")]
    [InlineData("FLAC_16", "6,5")]
    [InlineData("CD", "6,5")]
    [InlineData("MP3_320", "5")]
    [InlineData("MP3", "5")]
    public async Task ResolveDownloadAsync_OnlyDescendsFromRequestedQuality(string quality, string expectedFormats)
    {
        var formats = new List<string>();
        using var http = CreateHttp((request, _) =>
        {
            var format = QueryHelpers.ParseQuery(request.RequestUri!.Query)["format_id"].ToString();
            formats.Add(format);
            return Task.FromResult(format == "5"
                ? Json(HttpStatusCode.OK, """{"url":"https://media.example.test/audio.mp3","mime_type":"audio/mpeg"}""")
                : Json(HttpStatusCode.NotFound, """{"message":"Unavailable format"}"""));
        });

        var result = await CreateClient(http, CreateBundles().Object)
            .ResolveDownloadAsync("42", "fixture-token", quality, CancellationToken.None);

        Assert.Equal(expectedFormats.Split(','), formats);
        Assert.Equal(5, result.FormatId);
        Assert.Equal("audio/mpeg", result.MimeType);
        Assert.Equal(16, result.BitDepth);
        Assert.Equal(44.1, result.SamplingRate);
    }

    [Fact]
    public async Task ResolveDownloadAsync_UnavailableFormatsExhaustOnceAtEachAllowedTier()
    {
        var formats = new List<string>();
        using var http = CreateHttp((request, _) =>
        {
            formats.Add(QueryHelpers.ParseQuery(request.RequestUri!.Query)["format_id"].ToString());
            return Task.FromResult(Json(HttpStatusCode.NotFound, """{"message":"private provider detail"}"""));
        });
        var bundles = CreateBundles("first-secret", "second-secret");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC_16", CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, exception.StatusCode);
        Assert.Equal(["6", "5"], formats);
        Assert.DoesNotContain("private provider detail", exception.ToString());
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task ResolveDownloadAsync_AccountAndTransientErrorsDoNotWalkSecretsOrFormats(HttpStatusCode status)
    {
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            // Even a signing-looking payload on an auth failure must not trigger refresh.
            return Task.FromResult(Json(status, """{"code":"InvalidAppId","message":"private provider detail"}"""));
        });
        var bundles = CreateBundles("first-secret", "second-secret");

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC", CancellationToken.None));

        Assert.Equal(status, exception.StatusCode);
        Assert.Equal(1, requests);
        Assert.DoesNotContain("private provider detail", exception.ToString());
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("InvalidAppId")]
    [InlineData("InvalidRequestSignature")]
    public async Task ResolveDownloadAsync_ExplicitSigningRejectionRefreshesOnceWithSameAccount(string errorCode)
    {
        var appId = "123456789";
        var requests = new List<string>();
        var bundles = CreateBundles();
        bundles.Setup(bundle => bundle.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QobuzSigningCredentials(appId, ["fixture-signing-secret"]));
        bundles.Setup(bundle => bundle.RefreshAsync("123456789", It.IsAny<CancellationToken>()))
            .Callback(() => appId = "987654321")
            .Returns(Task.CompletedTask);
        using var http = CreateHttp((request, _) =>
        {
            var currentApp = Assert.Single(request.Headers.GetValues("X-App-Id"));
            requests.Add(currentApp);
            Assert.Equal("fixture-account-token", Assert.Single(request.Headers.GetValues("X-User-Auth-Token")));
            Assert.Equal("7", QueryHelpers.ParseQuery(request.RequestUri!.Query)["format_id"].ToString());
            return Task.FromResult(currentApp == "123456789"
                ? Json(HttpStatusCode.BadRequest, $$"""{"code":"{{errorCode}}"}""")
                : Json(HttpStatusCode.OK, MediaResponse));
        });

        var result = await CreateClient(http, bundles.Object)
            .ResolveDownloadAsync("42", "fixture-account-token", "FLAC_24_LOW", CancellationToken.None);

        Assert.Equal(7, result.FormatId);
        Assert.Equal(["123456789", "987654321"], requests);
        bundles.Verify(bundle => bundle.RefreshAsync("123456789", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveDownloadAsync_AnotherBundleSecretCanSucceedWithoutRefresh()
    {
        var requests = 0;
        using var http = CreateHttp((request, _) =>
        {
            requests++;
            var query = QueryHelpers.ParseQuery(request.RequestUri!.Query);
            var expected = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(
                $"trackgetFileUrlformat_id6intentstreamtrack_id42{query["request_ts"]}second-secret"))).ToLowerInvariant();
            return Task.FromResult(query["request_sig"] == expected
                ? Json(HttpStatusCode.OK, MediaResponse)
                : Json(HttpStatusCode.BadRequest, """{"code":"InvalidRequestSignature"}"""));
        });
        var bundles = CreateBundles("first-secret", "second-secret");

        var result = await CreateClient(http, bundles.Object)
            .ResolveDownloadAsync("42", "fixture-token", "FLAC_16", CancellationToken.None);

        Assert.Equal(6, result.FormatId);
        Assert.Equal(2, requests);
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveDownloadAsync_RepeatedSigningRejectionIsBounded()
    {
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"code":"InvalidRequestSignature"}"""));
        });
        var bundles = CreateBundles("first-secret", "second-secret");
        bundles.Setup(bundle => bundle.RefreshAsync("123456789", It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var exception = await Assert.ThrowsAnyAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(4, requests);
        bundles.Verify(bundle => bundle.RefreshAsync("123456789", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveDownloadAsync_UnrecognizedBadRequestDoesNotRefresh()
    {
        var formats = new List<string>();
        using var http = CreateHttp((request, _) =>
        {
            formats.Add(QueryHelpers.ParseQuery(request.RequestUri!.Query)["format_id"].ToString());
            return Task.FromResult(Json(HttpStatusCode.BadRequest, """{"code":"InvalidCredentials"}"""));
        });
        var bundles = CreateBundles();

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC_16", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal(["6", "5"], formats);
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Theory]
    [InlineData("""{"url":"https://media.example.test/sample","sample":true,"sampling_rate":44.1}""")]
    [InlineData("""{"url":"https://media.example.test/sample","sampling_rate":0}""")]
    public async Task ResolveDownloadAsync_PreservesSampleClassification(string payload)
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Json(HttpStatusCode.OK, payload)));

        var result = await CreateClient(http, CreateBundles().Object)
            .ResolveDownloadAsync("42", "fixture-token", "MP3", CancellationToken.None);

        Assert.True(result.IsSample);
        Assert.Equal(5, result.FormatId);
    }

    [Fact]
    public async Task ResolveDownloadAsync_PreservesProviderQualityRestriction()
    {
        using var http = CreateHttp((_, _) => Task.FromResult(Json(HttpStatusCode.OK,
            """{"url":"https://media.example.test/audio","bit_depth":16,"sampling_rate":44.1,"restrictions":[{"code":"FormatRestrictedByFormatAvailability"}]}""")));

        var result = await CreateClient(http, CreateBundles().Object)
            .ResolveDownloadAsync("42", "fixture-token", "FLAC", CancellationToken.None);

        Assert.True(result.WasQualityDowngraded);
        Assert.False(result.IsSample);
        Assert.Equal(16, result.BitDepth);
        Assert.Equal(44.1, result.SamplingRate);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("""{"url":"https://media.example.test/audio","bit_depth":"unexpected"}""")]
    public async Task ResolveDownloadAsync_InvalidSuccessfulResponseFailsWithoutFormatRetries(string payload)
    {
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, payload));
        });

        var exception = await Assert.ThrowsAsync<HttpRequestException>(() =>
            CreateClient(http, CreateBundles().Object).ResolveDownloadAsync("42", "fixture-token", "FLAC", CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadGateway, exception.StatusCode);
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task ResolveDownloadAsync_ConcurrentCallsKeepAccountHeadersIsolated()
    {
        var bothEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new ConcurrentDictionary<string, string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var http = CreateHttp(async (request, cancellationToken) =>
        {
            var trackId = QueryHelpers.ParseQuery(request.RequestUri!.Query)["track_id"].ToString();
            Assert.True(requests.TryAdd(trackId, Assert.Single(request.Headers.GetValues("X-User-Auth-Token"))));
            if (requests.Count == 2) bothEntered.TrySetResult();
            await bothEntered.Task.WaitAsync(cancellationToken);
            return Json(HttpStatusCode.OK, MediaResponse);
        });
        var client = CreateClient(http, CreateBundles().Object);

        await Task.WhenAll(
            client.ResolveDownloadAsync("42", "account-a-token", "MP3", timeout.Token),
            client.ResolveDownloadAsync("84", "account-b-token", "MP3", timeout.Token));

        Assert.Equal("account-a-token", requests["42"]);
        Assert.Equal("account-b-token", requests["84"]);
        Assert.False(http.DefaultRequestHeaders.Contains("X-User-Auth-Token"));
        Assert.False(http.DefaultRequestHeaders.Contains("X-App-Id"));
    }

    [Fact]
    public async Task ResolveDownloadAsync_CancellationDoesNotTryAnotherFormatOrRefresh()
    {
        using var cancellation = new CancellationTokenSource();
        var requests = 0;
        using var http = CreateHttp((_, cancellationToken) =>
        {
            requests++;
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Json(HttpStatusCode.OK, MediaResponse));
        });
        var bundles = CreateBundles("first-secret", "second-secret");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC", cancellation.Token));

        Assert.Equal(1, requests);
        bundles.Verify(bundle => bundle.RefreshAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ResolveDownloadAsync_AlreadyCanceledDoesNotUseBundleOrNetwork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var requests = 0;
        using var http = CreateHttp((_, _) =>
        {
            requests++;
            return Task.FromResult(Json(HttpStatusCode.OK, MediaResponse));
        });
        var bundles = CreateBundles();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateClient(http, bundles.Object).ResolveDownloadAsync("42", "fixture-token", "FLAC", cancellation.Token));

        Assert.Equal(0, requests);
        bundles.VerifyNoOtherCalls();
    }

    private static QobuzMediaClient CreateClient(HttpClient http, QobuzBundleService bundles) =>
        new(http, bundles, NullLogger<QobuzMediaClient>.Instance);

    private static Mock<QobuzBundleService> CreateBundles(params string[] secrets)
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(value => value.CreateClient("QobuzApi")).Returns(() => new HttpClient());
        var bundles = new Mock<QobuzBundleService>(factory.Object, NullLogger<QobuzBundleService>.Instance);
        bundles.Setup(bundle => bundle.GetSigningCredentialsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new QobuzSigningCredentials(
                "123456789", secrets.Length == 0 ? ["fixture-signing-secret"] : secrets.ToList()));
        return bundles;
    }

    private static HttpClient CreateHttp(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new FixtureHandler(send));

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixtureHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
}
