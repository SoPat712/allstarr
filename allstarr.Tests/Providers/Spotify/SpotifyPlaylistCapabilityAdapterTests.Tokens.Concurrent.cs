using System.Net;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Spotify;
using allstarr.Models.Settings;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed partial class SpotifyPlaylistCapabilityAdapterTests
{
    [Fact]
    public async Task TokenExchange_DistinctScopesProceedWhileOneExchangeIsBlocked()
    {
        var handler = new ConcurrentTokenHandler("sp_dc=account-a");
        using var http = new HttpClient(handler);
        var exchange = new SpotifyWebTokenExchange(http, new ConcurrentSecretAccessor(),
            new SpotifyApiSettings(), TimeProvider.System);
        var first = exchange.GetAsync(ConcurrentAccountA(), CancellationToken.None);

        try
        {
            await handler.BlockedTokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = exchange.GetAsync(ConcurrentAccountB(), CancellationToken.None);
            var resultB = await second.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(resultB.Success);
            Assert.Equal("account-b-token", resultB.AccessToken);
            Assert.Equal(2, handler.TokenRequests);
            Assert.Equal(1, handler.SecretFetches);

            handler.ReleaseBlockedToken.TrySetResult();
            var resultA = await first.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(resultA.Success);
            Assert.Equal("account-a-token", resultA.AccessToken);
        }
        finally
        {
            handler.ReleaseBlockedToken.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TokenExchange_KeepsOnePendingExchangePerScope()
    {
        var handler = new ConcurrentTokenHandler("sp_dc=account-a");
        using var http = new HttpClient(handler);
        var exchange = new SpotifyWebTokenExchange(http, new ConcurrentSecretAccessor(),
            new SpotifyApiSettings(), TimeProvider.System);
        var first = exchange.GetAsync(ConcurrentAccountA(), CancellationToken.None);
        await handler.BlockedTokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = exchange.GetAsync(ConcurrentAccountA(), CancellationToken.None);
        Assert.False(second.IsCompleted);
        Assert.Equal(1, handler.TokenRequests);

        handler.ReleaseBlockedToken.TrySetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, result => Assert.True(result.Success));
        Assert.Equal(results[0].AccessToken, results[1].AccessToken);
        Assert.Equal(1, handler.TokenRequests);
    }

    [Fact]
    public async Task TokenExchange_AnonymousCacheHitIsNotBlockedByAccountExchange()
    {
        var handler = new ConcurrentTokenHandler("sp_dc=account-a");
        using var http = new HttpClient(handler);
        var exchange = new SpotifyWebTokenExchange(http, new ConcurrentSecretAccessor(),
            new SpotifyApiSettings(), TimeProvider.System);

        var warm = await exchange.GetAsync(null, CancellationToken.None);
        Assert.True(warm.Success);
        Assert.True(warm.IsAnonymous);

        var blocked = exchange.GetAsync(ConcurrentAccountA(), CancellationToken.None);
        Task<SpotifyWebTokenResult>? cacheHit = null;
        try
        {
            await handler.BlockedTokenRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cacheHit = exchange.GetAsync(null, CancellationToken.None);
            var cached = await cacheHit.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(cached.Success);
            Assert.Equal(warm.AccessToken, cached.AccessToken);
            Assert.Equal(2, handler.TokenRequests);

            handler.ReleaseBlockedToken.TrySetResult();
            var account = await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(account.Success);
        }
        finally
        {
            handler.ReleaseBlockedToken.TrySetResult();
            await blocked.WaitAsync(TimeSpan.FromSeconds(5));
            if (cacheHit != null)
                await cacheHit.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static readonly Guid ConcurrentOwner =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ConcurrentAccountIdA =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ConcurrentAccountIdB =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid ConcurrentSecretIdA =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaab");
    private static readonly Guid ConcurrentSecretIdB =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbc");

    private static ProviderAccountContext ConcurrentAccountA() =>
        ConcurrentAccount(ConcurrentAccountIdA, ConcurrentSecretIdA);

    private static ProviderAccountContext ConcurrentAccountB() =>
        ConcurrentAccount(ConcurrentAccountIdB, ConcurrentSecretIdB);

    private static ProviderAccountContext ConcurrentAccount(Guid accountId, Guid secretReferenceId) =>
        new(accountId, "spotify", ProviderAccountScope.Personal, 1,
            ownerUserId: ConcurrentOwner, secretReferenceId: secretReferenceId);

    private sealed class ConcurrentSecretAccessor : IProviderAccountSecretAccessor
    {
        public async Task<T> UseAsync<T>(ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var cookie = account.AccountId == ConcurrentAccountIdA
                ? "account-a"
                : "account-b";
            var bytes = Encoding.UTF8.GetBytes(cookie);
            try
            {
                return await operation(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private sealed class ConcurrentTokenHandler(string blockedCookie) : HttpMessageHandler
    {
        private int secretFetches;
        private int tokenRequests;

        public TaskCompletionSource BlockedTokenRequested { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseBlockedToken { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int SecretFetches => Volatile.Read(ref secretFetches);
        public int TokenRequests => Volatile.Read(ref tokenRequests);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException("Request URI missing.");
            if (uri.AbsolutePath.EndsWith("secretBytes.json", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref secretFetches);
                return JsonResponse("[{\"version\":1,\"secret\":[1,2,3,4]}]");
            }

            if (request.Method == HttpMethod.Head && uri.Host == "open.spotify.com")
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK);
                response.Headers.Date = DateTimeOffset.UtcNow;
                return response;
            }

            if (request.Method == HttpMethod.Get && uri.AbsolutePath == "/api/token")
            {
                Interlocked.Increment(ref tokenRequests);
                var cookie = request.Headers.TryGetValues("Cookie", out var values)
                    ? values.FirstOrDefault()
                    : null;
                if (cookie == blockedCookie)
                {
                    BlockedTokenRequested.TrySetResult();
                    await ReleaseBlockedToken.Task.WaitAsync(cancellationToken);
                }

                var anonymous = string.IsNullOrWhiteSpace(cookie);
                var token = cookie switch
                {
                    "sp_dc=account-a" => "account-a-token",
                    "sp_dc=account-b" => "account-b-token",
                    _ => "anonymous-token"
                };
                var expiry = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds();
                return JsonResponse($"{{\"accessToken\":\"{token}\",\"isAnonymous\":{anonymous.ToString().ToLowerInvariant()},\"accessTokenExpirationTimestampMs\":{expiry}}}");
            }

            throw new InvalidOperationException($"Unexpected Spotify fixture request: {request.Method} {uri}");
        }

        private static HttpResponseMessage JsonResponse(string json) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
    }
}
