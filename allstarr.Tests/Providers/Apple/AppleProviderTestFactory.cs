using System.Net;
using System.Text;
using System.Text.Json;
using allstarr.Core.Providers.AppleMusicKit;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Capabilities;

namespace allstarr.Tests;

internal static class AppleProviderTestFactory
{
    public static readonly string Bearer = "eyJhbGciOiJFUzI1NiJ9." +
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new { exp = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds() }))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".c2lnbmF0dXJl";
    public static AppleMusicClient Client(HttpClient? http = null, IProviderAccountSecretAccessor? secrets = null,
        IProviderAccountSettingsReader? settings = null) => new(
        http ?? new HttpClient(new Handler(_ => throw new InvalidOperationException("Unexpected Apple API request."))),
        new AppleWebTokenProvider(new HttpClient(new Handler(request => new(HttpStatusCode.OK)
        {
            Content = new StringContent(request.RequestUri!.AbsolutePath == "/"
                ? "<script src=\"/assets/index-test.js\"></script>" : $"const token=\"{Bearer}\";")
        }))), secrets ?? new Secrets(), settings);
    public static AppleMusicKitMetadataCapabilityAdapter Metadata(HttpClient http, IProviderAccountSecretAccessor secrets) => new(Client(http, secrets));
    public static AppleMusicKitPlaylistCapabilityAdapter Playlist(HttpClient http, IProviderAccountSecretAccessor secrets) => new(Client(http, secrets), http);
    private sealed class Secrets : IProviderAccountSecretAccessor
    {
        public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken) => operation(Encoding.UTF8.GetBytes("{\"MusicUserToken\":\"fixture-user\",\"Storefront\":\"us\"}"));
    }
    internal sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(send(request));
    }
}
