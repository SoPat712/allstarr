using System.Net;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.AudioMuse;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;

namespace allstarr.Tests;

public sealed class AudioMuseIntelligenceCapabilityAdapterTests
{
    [Fact]
    public void Registration_is_a_built_in_Intelligence_connection_with_server_fields()
    {
        var handler = new AudioMuseHandler();
        var secrets = new SecretAccessor("""{"baseUrl":"http://audiomuse.test","apiToken":"secret"}""");
        var endpoint = new AudioMuseEndpointClient(new HttpClient(handler), secrets);

        var registration = ProviderRegistrationValidator.Validate(
            AudioMuseCapabilityRegistration.CreateRegistration(
                new AudioMuseIntelligenceCapabilityAdapter(endpoint),
                new AudioMuseHealthProbeCapabilityAdapter(endpoint)));

        Assert.Equal("audiomuse-ai", registration.Descriptor.Id);
        Assert.Equal(ProviderOrigin.BuiltIn, registration.Descriptor.Origin);
        Assert.Equal(["baseUrl", "apiToken", "server"],
            registration.Descriptor.Settings.Select(item => item.Key));
        Assert.Equal([ProviderCapabilityKind.Intelligence, ProviderCapabilityKind.Health],
            registration.Descriptor.Capabilities.Select(item => item.Capability));
        Assert.All(registration.Descriptor.Capabilities,
            item => Assert.Equal(ProviderAccountRequirement.Required, item.AccountRequirement));
    }

    [Fact]
    public async Task Recommend_search_and_health_use_the_configured_server_and_token()
    {
        var handler = new AudioMuseHandler();
        var secrets = new SecretAccessor(
            """{"baseUrl":"http://audiomuse.test/subpath","apiToken":"secret-token","server":"music"}""");
        var endpoint = new AudioMuseEndpointClient(new HttpClient(handler), secrets);
        var capability = new AudioMuseIntelligenceCapabilityAdapter(endpoint);
        var health = new AudioMuseHealthProbeCapabilityAdapter(endpoint);

        var recommendations = await capability.RecommendAsync(Context(), ["seed"], 5);
        var search = await capability.SearchAsync(Context(), "bright guitar", false, 5);
        var probe = await health.ProbeAsync(Context(), new(ProviderCapabilityKind.Intelligence));

        Assert.True(recommendations.IsSuccess, recommendations.Error?.ToString());
        Assert.Equal("track-1", Assert.Single(recommendations.RequireValue()).TrackId);
        Assert.True(search.IsSuccess, search.Error?.ToString());
        Assert.Equal(.91, Assert.Single(search.RequireValue()).Score, 2);
        Assert.True(probe.IsSuccess, probe.Error?.ToString());
        Assert.Equal(ProviderProbeStatus.Healthy, probe.RequireValue().Status);
        Assert.Contains(handler.Requests, item =>
            item.PathAndQuery.StartsWith("/subpath/api/similar_tracks?", StringComparison.Ordinal) &&
            item.PathAndQuery.Contains("server=music", StringComparison.Ordinal));
        Assert.Contains(handler.Requests, item => item.PathAndQuery == "/subpath/api/clap/search");
        Assert.Contains(handler.Requests, item => item.PathAndQuery == "/subpath/api/health?server=music");
        Assert.All(handler.Requests, item => Assert.Equal("Bearer secret-token", item.Authorization));
        Assert.Contains(handler.Requests, item => item.Body.Contains("\"server\":\"music\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Invalid_or_cross_origin_server_configuration_is_rejected_before_a_request()
    {
        var handler = new AudioMuseHandler();
        var invalidEndpoint = new AudioMuseEndpointClient(new HttpClient(handler),
            new SecretAccessor("""{"baseUrl":"ftp://audiomuse.test","apiToken":"secret"}"""));
        var invalid = new AudioMuseIntelligenceCapabilityAdapter(invalidEndpoint);

        var result = await invalid.SearchAsync(Context(), "query", false, 5);

        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
        Assert.Empty(handler.Requests);
    }

    private static ProviderExecutionContext Context()
    {
        var tenant = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var user = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return new(new ProviderActorContext(tenant, ProviderActorKind.User, user,
                new("jellyfin", "backend", "principal")),
            "audiomuse-ai",
            new(Guid.Parse("33333333-3333-3333-3333-333333333333"), "audiomuse-ai",
                ProviderAccountScope.User, 1, tenantId: tenant, ownerUserId: user,
                secretReferenceId: Guid.Parse("44444444-4444-4444-4444-444444444444")),
            new(tenant, "music"),
            new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, true),
                ProviderExplicitContentPolicy.Allow, true, true, false, ["audiomuse-ai"]),
            "intelligence-test", "correlation", DateTimeOffset.UtcNow.AddMinutes(1), default);
    }

    private sealed class SecretAccessor(string json) : IProviderAccountSecretAccessor
    {
        public async Task<T> UseAsync<T>(ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            try { return await operation(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }

    private sealed class AudioMuseHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(new(request.RequestUri!.PathAndQuery,
                request.Headers.Authorization?.ToString() ?? string.Empty, body));
            var json = request.RequestUri.AbsolutePath switch
            {
                var path when path.EndsWith("/api/similar_tracks", StringComparison.Ordinal) =>
                    """[{"item_id":"track-1","title":"Song","author":"Artist","album":"Album","distance":0.08}]""",
                var path when path.EndsWith("/api/clap/search", StringComparison.Ordinal) =>
                    """{"results":[{"item_id":"track-2","title":"Found","author":"Artist","similarity":0.91}]}""",
                var path when path.EndsWith("/api/health", StringComparison.Ordinal) =>
                    """{"status":"ok"}""",
                _ => "{}"
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private sealed record CapturedRequest(string PathAndQuery, string Authorization, string Body);
}
