using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using allstarr.Core.Extensions;
using allstarr.Services.Common;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;

namespace allstarr.Tests;

public sealed class ExtensionSignedProtocolTests : IDisposable
{
    private const string Manifest = """
        {"name":"protocol-fixture","version":"1.0.0","type":["metadata_provider","download_provider"],
         "permissions":{"network":["api.example.test"]},
         "signedSession":{"namespace":"fixture","baseUrl":"https://api.example.test/v2","appVersion":"fixture@1.2.5","platform":"extension","schemeLabel":"FIXTURE-HMAC-V1","headerPrefix":"X-Test-"}}
        """;
    private readonly string root = Path.Combine(Path.GetTempPath(), "signed-protocol", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider clock = new(new DateTimeOffset(2026, 10, 18, 12, 0, 0, TimeSpan.Zero));
    private readonly List<(string Level, string Code, string Message)> logs = [];
    private readonly Handler handler = new();
    private readonly IDataProtector protector = new EphemeralDataProtectionProvider().CreateProtector("test");

    [Fact]
    public async Task SignedFetch_MatchesObjectBytesHeadersAndSignature()
    {
        var sandbox = Sandbox("""
            registerExtension({customSearch:function(){
              session.signedFetch('POST','/tickets',{service:'tid',type:'track',id:'123'});
              session.signedFetch(' post ','/dl/tid?private=hidden',{quality:'HI_RES_LOSSLESS',id:'123'},
                {'X-Test-Ticket':'fixture-ticket','user-agent':'explicit/1','accept':'text/plain','content-type':'application/custom'});
              session.signedFetch('POST','/bytes',{z:undefined,a:'é<&>\u2028',nested:{z:1,a:2}});
              session.signedFetch('POST','/raw',' {"z":2,"a":1} ');
              return [];
            }});
            """);
        await sandbox.CompleteSignedSessionGrantAsync("fixture-grant");

        sandbox.InvokeJson("searchTracks", "{\"query\":\"fixture\"}");

        Assert.Equal("{\"id\":\"123\",\"service\":\"tid\",\"type\":\"track\"}", handler.Requests[1].Body);
        Assert.Equal("application/json", handler.Requests[1].Headers["Content-Type"]);
        Assert.Equal("SpotiFLAC-Mobile/fixture@1.2.5", handler.Requests[1].Headers["User-Agent"]);
        var request = handler.Requests[2];
        Assert.Equal("{\"id\":\"123\",\"quality\":\"HI_RES_LOSSLESS\"}", request.Body);
        Assert.Equal("fixture-ticket", request.Headers["X-Test-Ticket"]);
        Assert.Equal("explicit/1", request.Headers["User-Agent"]);
        Assert.Equal("text/plain", request.Headers["Accept"]);
        Assert.Equal("application/custom", request.Headers["Content-Type"]);
        Assert.Equal("fixture@1.2.5", request.Headers["X-Test-App-Version"]);
        Assert.Equal("extension", request.Headers["X-Test-Platform"]);
        Assert.Equal(HttpVersion.Version20, request.Version);
        Assert.Equal(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
        Assert.Matches("^[0-9a-f]{24}$", request.Headers["X-Test-Nonce"]);
        Assert.Equal("2026-10-18T12:00:00.000Z", request.Headers["X-Test-Timestamp"]);
        AssertSignature(request);
        Assert.Equal("{\"a\":\"é\\u003c\\u0026\\u003e\\u2028\",\"nested\":{\"a\":2,\"z\":1},\"z\":null}", handler.Requests[3].Body);
        Assert.Equal(" {\"z\":2,\"a\":1} ", handler.Requests[4].Body);
        Assert.All(handler.Requests.Skip(1), AssertSignature);
    }

    [Theory]
    [InlineData("same_operation", 25, 25, 25)]
    [InlineData("same_operation", 25, 60, 25)]
    [InlineData("same_operation", 0, 25, 25)]
    [InlineData("same_operation", 121, 0, 120)]
    [InlineData("same_operation", 0, 0, 1)]
    [InlineData("poll_existing", 25, 25, 0)]
    [InlineData("new_ticket", 25, 25, 0)]
    public async Task SignedFetch_PreservesRetryContractAndTicket(string mode, int headerDelay, int bodyDelay, int expectedDelay)
    {
        var client = await SignedInAsync();
        handler.Failures = 2;
        handler.ErrorBody = JsonSerializer.Serialize(new
        {
            origin = "provider", code = "PROVIDER_UNAVAILABLE", retryable = true,
            retry_mode = mode, retry_after_seconds = bodyDelay
        });
        handler.RetryAfter = headerDelay;
        using var budget = new ExtensionInvocationBudget(TimeSpan.FromSeconds(300), default, clock, isDownload: true);
        var releases = Channel.CreateUnbounded<bool>();
        handler.Wait = async token => { await releases.Reader.ReadAsync(token); };
        var pending = Task.Run(() => client.SignedFetch("POST", "/dl/tid", "{\"id\":\"123\"}",
            new Dictionary<string, string> { ["X-Test-Ticket"] = "fixture-ticket" }));
        Assert.Equal(TimeSpan.FromSeconds(300), await NextTimerAsync());
        if (expectedDelay > 0)
            for (var i = 0; i < 2; i++)
            {
                Assert.Equal(TimeSpan.FromSeconds(120), await NextTimerAsync());
                await releases.Writer.WriteAsync(true);
                Assert.Equal(TimeSpan.FromSeconds(expectedDelay), await NextTimerAsync());
                clock.Advance(TimeSpan.FromSeconds(expectedDelay));
            }
        Assert.Equal(TimeSpan.FromSeconds(Math.Min(120, 300 - 2 * expectedDelay)), await NextTimerAsync());
        await releases.Writer.WriteAsync(true);
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(await pending.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(expectedDelay > 0 ? 200 : 503, response.RootElement.GetProperty("statusCode").GetInt32());
        var requests = handler.Requests.Skip(1).ToArray();
        Assert.Equal(expectedDelay > 0 ? 3 : 1, requests.Length);
        Assert.All(requests, r =>
        {
            Assert.Equal("fixture-ticket", r.Headers["X-Test-Ticket"]);
            Assert.Equal("{\"id\":\"123\"}", r.Body);
            AssertSignature(r);
        });
        Assert.Equal(requests.Length, requests.Select(r => r.Headers["X-Test-Nonce"]).Distinct().Count());
        Assert.Equal(expectedDelay > 0 ? 2 : 1, logs.Count(item => item.Code == "session.response"));
    }

    [Fact]
    public async Task SignedDownload_EachAttemptGetsItsOwnRequestTimeoutWithinInvocationBudget()
    {
        handler.Failures = 1;
        handler.RetryAfter = 25;
        handler.Wait = token => Task.Delay(TimeSpan.FromSeconds(60), clock, token);
        var sandbox = Sandbox("registerExtension({download:function(){return session.signedFetch('POST','/dl/tid',{});}});", native: true);
        await sandbox.CompleteSignedSessionGrantAsync("fixture-grant");
        var pending = Task.Run(() => sandbox.InvokeJson("download", "{}"));
        Assert.Equal(TimeSpan.FromSeconds(150), await NextTimerAsync());
        Assert.Equal(TimeSpan.FromSeconds(120), await NextTimerAsync());
        Assert.Equal(TimeSpan.FromSeconds(60), await NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(60));
        Assert.Equal(TimeSpan.FromSeconds(25), await NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(25));
        Assert.Equal(TimeSpan.FromSeconds(65), await NextTimerAsync());
        Assert.Equal(TimeSpan.FromSeconds(60), await NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(60));
        using var response = JsonDocument.Parse((await pending.WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal(200, response.RootElement.GetProperty("statusCode").GetInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedFetch_ParsesCaseInsensitiveContractsAndRejectsMalformedScalarTypes(bool malformed)
    {
        var client = await SignedInAsync();
        handler.Failures = 1;
        handler.FailureStatus = malformed ? HttpStatusCode.Unauthorized : HttpStatusCode.ServiceUnavailable;
        handler.ErrorBody = malformed
            ? """{"origin":"gateway","code":"SESSION_INVALID","action":"bootstrap_session","retryable":"private"}"""
            : """{"ORIGIN":" provider ","CODE":" provider_unavailable ","RETRYABLE":true,"RETRY_MODE":" same_operation ","RETRY_AFTER_SECONDS":1}""";
        var pending = Task.Run(() => client.SignedFetch("POST", "/dl/tid", "{}", null));
        Assert.Equal(TimeSpan.FromSeconds(30), await NextTimerAsync());
        if (!malformed)
        {
            Assert.Equal(TimeSpan.FromSeconds(1), await NextTimerAsync());
            clock.Advance(TimeSpan.FromSeconds(1));
        }
        using var response = JsonDocument.Parse(JsonSerializer.Serialize(await pending.WaitAsync(TimeSpan.FromSeconds(10))));
        Assert.Equal(malformed ? 401 : 200, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.True((await client.ViewAsync(default)).State == ExtensionSessionStates.SignedIn);
    }

    [Fact]
    public async Task SignedFetch_HonorsDeclaredNetworkTimeout()
    {
        handler.Wait = token => Task.Delay(Timeout.InfiniteTimeSpan, token);
        var sandbox = Sandbox("""
            registerExtension({customSearch:function(){
              var response = session.signedFetch('POST','/dl/tid',{});
              return [{id:'fixture',name:response.ok === false ? 'limited' : 'unexpected',artists:'Fixture'}];
            }});
            """, networkSeconds: 15);
        await sandbox.CompleteSignedSessionGrantAsync("fixture-grant");
        var pending = Task.Run(() => sandbox.InvokeJson("searchTracks", "{\"query\":\"fixture\"}"));
        Assert.Equal(TimeSpan.FromSeconds(60), await NextTimerAsync());
        Assert.Equal(TimeSpan.FromSeconds(30), await NextTimerAsync());
        Assert.Equal(TimeSpan.FromSeconds(15), await NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(15));
        using var response = JsonDocument.Parse((await pending.WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal("limited", response.RootElement.GetProperty("items")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task SignedFetch_DecodesCompressedContractWithinResponseLimit()
    {
        var client = await SignedInAsync();
        handler.Failures = 1;
        handler.Gzip = true;
        handler.ErrorBody = """{"origin":"provider","code":"PROVIDER_UNAVAILABLE","retryable":true,"retry_mode":"poll_existing"}""";

        using var response = JsonDocument.Parse(JsonSerializer.Serialize(client.SignedFetch("POST", "/dl/tid", "{}", null)));

        Assert.Equal("gzip", handler.Requests[^1].Headers["Accept-Encoding"]);
        Assert.Equal("poll_existing", response.RootElement.GetProperty("retryMode").GetString());
        Assert.DoesNotContain(response.RootElement.GetProperty("headers").EnumerateObject(),
            field => field.Name.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task SignedResponse_LogsOnlySafeContractFieldsAndElapsedTimeForEveryFailure()
    {
        var client = await SignedInAsync();
        handler.Failures = 1;
        handler.ErrorBody = """{"error":"fixture-secret","code":"PROVIDER_UNAVAILABLE","origin":"provider","action":"fixture-ticket","retry_mode":"poll_existing","retry_after_seconds":25,"id":"private-id"}""";
        client.SignedFetch("POST", "/dl/tid?private=hidden", "{}", null);
        var log = Assert.Single(logs, item => item.Code == "session.response");
        Assert.Equal("warning", log.Level);
        Assert.Contains("method=POST; path=/v2/dl/tid; status=503", log.Message);
        Assert.Contains("code=PROVIDER_UNAVAILABLE", log.Message);
        Assert.Contains("origin=provider", log.Message);
        Assert.Contains("retry_mode=poll_existing", log.Message);
        Assert.Contains("retry_after=25", log.Message);
        Assert.Contains("elapsed_ms=0", log.Message);
        Assert.DoesNotContain("fixture-secret", log.Message);
        Assert.DoesNotContain("fixture-ticket", log.Message);
        Assert.DoesNotContain("private", log.Message);
        Assert.DoesNotContain("hidden", log.Message);
    }

    [Fact]
    public async Task DownloadPolling_ReusesTicketAndBodyAfterSuccessfulSleep()
    {
        handler.Failures = 1;
        handler.ErrorBody = """{"origin":"provider","code":"PROVIDER_UNAVAILABLE","retryable":true,"retry_mode":"poll_existing","retry_after_seconds":25}""";
        var sandbox = Sandbox("""
            registerExtension({download:function(){
              var ticket = JSON.parse(session.signedFetch('POST','/tickets',{}).body).ticket_id;
              var headers = {'X-Test-Ticket':ticket};
              var body = {id:'123',quality:'HI_RES_LOSSLESS'};
              var response = session.signedFetch('POST','/dl/tid',body,headers);
              if (response.retryMode !== 'poll_existing' || !utils.sleep(25000)) throw new Error('Polling interrupted');
              return session.signedFetch('POST','/dl/tid',body,headers);
            }});
            """, native: true);
        await sandbox.CompleteSignedSessionGrantAsync("fixture-grant");
        var pending = Task.Run(() => sandbox.InvokeJson("download", "{}"));
        while (await NextTimerAsync() != TimeSpan.FromSeconds(25)) { }
        clock.Advance(TimeSpan.FromSeconds(25));
        using var response = JsonDocument.Parse((await pending.WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.Equal(200, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(["/v2/session/exchange", "/v2/tickets", "/v2/dl/tid", "/v2/dl/tid"], handler.Requests.Select(r => r.Path));
        Assert.All(handler.Requests.Skip(2), r => Assert.Equal("fixture-ticket", r.Headers["X-Test-Ticket"]));
        Assert.Equal(handler.Requests[2].Body, handler.Requests[3].Body);
    }

    [Fact]
    public async Task SignedFetch_ContinuesAfterBootstrapRestoresSession()
    {
        var client = await SignedInAsync();
        client.Clear();
        handler.BootstrapSession = true;

        using var response = JsonDocument.Parse(JsonSerializer.Serialize(client.SignedFetch("POST", "/tickets", "{}", null)));

        Assert.Equal(200, response.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(["/v2/session/exchange", "/v2/bootstrap", "/v2/tickets"], handler.Requests.Select(r => r.Path));
        AssertSignature(handler.Requests[^1]);
    }

    [Fact]
    public async Task DownloadSleep_ReturnsTrueAndAllowsProviderWaitWithinDeadline()
    {
        var sandbox = Sandbox("registerExtension({download:function(){return {slept:utils.sleep(60000)};}});", native: true);
        var pending = Task.Run(() => sandbox.InvokeJson("download", "{}"));
        Assert.Equal(TimeSpan.FromSeconds(150), await NextTimerAsync());
        Assert.Equal(TimeSpan.FromSeconds(60), await NextTimerAsync());
        clock.Advance(TimeSpan.FromSeconds(60));
        using var response = JsonDocument.Parse((await pending.WaitAsync(TimeSpan.FromSeconds(10)))!);
        Assert.True(response.RootElement.GetProperty("slept").GetBoolean());
    }

    [Fact]
    public void CompatibilityUtilitiesAndHttpDefaults_MatchMobileIdentity()
    {
        var sandbox = Sandbox("""
            registerExtension({customSearch:function(){
              http.get('https://api.example.test/defaults');
              return [{id:'fixture',name:JSON.stringify({version:utils.appVersion(),ua:utils.appUserAgent(),random:utils.randomUserAgent(),slept:utils.sleep(0),decoded:utils.base64Decode('PE1QRC8+')}),artists:'Fixture'}];
            }});
            """);
        using var response = JsonDocument.Parse(sandbox.InvokeJson("searchTracks", "{\"query\":\"fixture\"}")!);
        using var identity = JsonDocument.Parse(response.RootElement.GetProperty("items")[0].GetProperty("title").GetString()!);
        Assert.True(identity.RootElement.GetProperty("slept").GetBoolean());
        Assert.Equal("<MPD/>", identity.RootElement.GetProperty("decoded").GetString());
        Assert.Equal("5.1.1", identity.RootElement.GetProperty("version").GetString());
        Assert.Equal("SpotiFLAC-Mobile/5.1.1", identity.RootElement.GetProperty("ua").GetString());
        Assert.Matches(@"Chrome/(12[7-9]|1[3-4][0-9]|15[0-2])\.0\.[0-9]{4}\.[1-2][0-9]{2} ", identity.RootElement.GetProperty("random").GetString()!);
        Assert.Equal("Spotiflac-Extension/1.0", Assert.Single(handler.Requests).Headers["User-Agent"]);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.Requests[0].Timeout);
    }

    [Fact]
    public void HttpRequest_HonorsDeclaredNetworkTimeout()
    {
        var sandbox = Sandbox("""
            registerExtension({customSearch:function(){http.get('https://api.example.test/defaults');return [];}});
            """, networkSeconds: 60);
        sandbox.InvokeJson("searchTracks", "{\"query\":\"fixture\"}");
        Assert.Equal(TimeSpan.FromSeconds(60), Assert.Single(handler.Requests).Timeout);
    }

    [Fact]
    public void Availability_ForwardsServiceIdentityAndPreparedContext()
    {
        var sandbox = Sandbox("""
            registerExtension({
              getTrack:function(id){return {id:id,name:'Fixture',artists:'Artist',duration_ms:200000,tidal_id:'123'};},
              checkAvailability:function(isrc,name,artists,options){
                if (options.tidal_id !== '123' || options.duration_ms !== 200000 || options.spotify_id)
                  throw new Error('Wrong availability options');
                return {available:true,prepared_context:{track_id:'123'}};
              },
              download:function(){}
            });
            """);
        using var response = JsonDocument.Parse(sandbox.InvokeJson("checkAvailability", "{\"trackId\":\"tidal:123\",\"requestedQuality\":\"Any\",\"prepareForStreaming\":true}")!);
        Assert.Equal("Available", response.RootElement.GetProperty("state").GetString());
    }

    private async Task<TimeSpan> NextTimerAsync()
    {
        TimeSpan delay;
        do { delay = await clock.NextTimerAsync(); }
        while (delay == TimeSpan.FromSeconds(35));
        return delay;
    }

    private ExtensionSandbox Sandbox(string script, bool native = false, int? networkSeconds = null)
    {
        var source = System.Text.Json.Nodes.JsonNode.Parse(Manifest)!.AsObject();
        if (networkSeconds.HasValue)
            source["capabilities"] = new System.Text.Json.Nodes.JsonObject { ["networkTimeoutSeconds"] = networkSeconds.Value };
        var normalized = SpotiFlacExtensionCompatibility.NormalizeManifest(source.ToJsonString(), script);
        if (native)
        {
            var manifest = System.Text.Json.Nodes.JsonNode.Parse(normalized)!.AsObject();
            manifest.Remove("compatibility");
            normalized = manifest.ToJsonString();
        }
        return new(root, normalized, script, new Factory(handler), NullLogger.Instance,
            new(new HashSet<string>(["https://api.example.test/"]), new HashSet<string>(), new HashSet<string>()),
            root, protector, timeProvider: clock, hookTimeout: TimeSpan.FromSeconds(60), downloadTimeout: TimeSpan.FromSeconds(150));
    }

    private async Task<ExtensionSignedSessionClient> SignedInAsync()
    {
        Directory.CreateDirectory(root);
        var config = ExtensionSdkV1.ParseManifest(SpotiFlacExtensionCompatibility.NormalizeManifest(Manifest, "download")).SignedSession!;
        var client = new ExtensionSignedSessionClient(config, new Factory(handler), protector,
            new HashSet<string>(["https://api.example.test/"]), root, "fixture", timeProvider: clock,
            logSink: (level, code, message) => logs.Add((level, code, message)));
        await client.CompleteGrantViewAsync("fixture-grant", default);
        return client;
    }

    private static void AssertSignature(Captured request)
    {
        var headers = request.Headers;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.Body))).ToLowerInvariant();
        Assert.Equal(hash, headers["X-Test-Body-SHA256"]);
        var window = DateTimeOffset.Parse(headers["X-Test-Timestamp"]).ToUnixTimeSeconds() / 300;
        static string Sign(string key, string value) => Convert.ToBase64String(HMACSHA256.HashData(
            Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(value))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var rolling = Sign("fixture-secret", $"{window}:fixture-session");
        var canonical = string.Join('\n', "FIXTURE-HMAC-V1", request.Method, request.Path, "", hash,
            headers["X-Test-Timestamp"], headers["X-Test-Nonce"], "fixture-session", "fixture@1.2.5", "extension");
        Assert.Equal(Sign(rolling, canonical), headers["X-Test-Signature"]);
    }

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed record Captured(string Method, string Path, string Body, Dictionary<string, string> Headers,
        Version Version, HttpVersionPolicy VersionPolicy, TimeSpan Timeout);
    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => handler.Client = new(handler, false);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public HttpClient? Client { get; set; }
        public List<Captured> Requests { get; } = [];
        public int Failures { get; set; }
        public HttpStatusCode FailureStatus { get; set; } = HttpStatusCode.ServiceUnavailable;
        public int RetryAfter { get; set; }
        public bool BootstrapSession { get; set; }
        public bool Gzip { get; set; }
        public string ErrorBody { get; set; } = """{"origin":"provider","code":"PROVIDER_UNAVAILABLE","retryable":true,"retry_mode":"same_operation"}""";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var headers = request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? [])
                .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
            var body = request.Content == null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken);
            Requests.Add(new(request.Method.Method, request.RequestUri!.AbsolutePath,
                Encoding.UTF8.GetString(body), headers, request.Version, request.VersionPolicy, Client!.Timeout));
            var exchange = request.RequestUri.AbsolutePath.EndsWith("/exchange", StringComparison.Ordinal);
            if (!exchange && Wait != null) await Wait(cancellationToken);
            var ticket = request.RequestUri.AbsolutePath.EndsWith("/tickets", StringComparison.Ordinal);
            var failed = !exchange && !ticket && Failures-- > 0;
            var response = new HttpResponseMessage(failed ? FailureStatus : HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(exchange || BootstrapSession && request.RequestUri.AbsolutePath.EndsWith("/bootstrap", StringComparison.Ordinal)
                    ? """{"session_id":"fixture-session","session_secret":"fixture-secret","expires_at":"2099-01-01T00:00:00Z"}"""
                    : ticket ? "{\"ticket_id\":\"fixture-ticket\"}" : failed ? ErrorBody : "{}")
            };
            if (Gzip && !exchange)
            {
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                using var output = new MemoryStream();
                using (var gzip = new GZipStream(output, CompressionMode.Compress, leaveOpen: true)) gzip.Write(bytes);
                response.Content.Dispose();
                response.Content = new ByteArrayContent(output.ToArray());
                response.Content.Headers.ContentEncoding.Add("gzip");
            }
            if (RetryAfter > 0) response.Headers.RetryAfter = new(TimeSpan.FromSeconds(RetryAfter));
            return response;
        }
        public Func<CancellationToken, Task>? Wait { get; set; }
    }
}
