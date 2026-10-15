using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Scrobbling;
using Microsoft.EntityFrameworkCore;

// This disposable worker is used only by the process interruption tests.
var factory = new Factory(args[0]);
var marker = args[1];
var stopAt = args[2];
var endpoint = args[3];
var provider = args[4];
var options = new DurableJobOptions();
var queue = new DurableJobQueue(factory, options, new JobPayloadPolicy(options), new Clock());
await queue.RecoverInterruptedAsync();
var claim = await queue.ClaimNextAsync("crash-fixture");
if (claim == null) return 2;
var payload = claim.Payload.Deserialize<PlaybackSignalPayload>()!;
var accounts = new ScopedRecommendationAccountAccessor(factory, new Secrets());
using var http = new HttpClient(new RedirectFixture(endpoint));
IExactScopePlaybackScrobbleTarget target = provider == "lastfm"
    ? new LastFmScopedPlaybackScrobbleTarget(http, accounts) : new ListenBrainzScopedPlaybackScrobbleTarget(http, accounts);
if (stopAt == "before-send") { File.WriteAllText(marker, "ready"); await Task.Delay(Timeout.Infinite); }
IPlaybackDeliveryCheckpointStore checkpoints = new Checkpoints(new EfPlaybackDeliveryCheckpointStore(factory), marker, stopAt);
var delivery = new ScopedPlaybackScrobbleDelivery(factory, [target], checkpoints, new Tracks());
DurableJobCompletion result;
try { await delivery.DeliverAsync(payload, default); result = DurableJobCompletion.Success(); }
catch (ScopedPlaybackScrobbleDeliveryException ex)
{ result = ex.Retryable ? DurableJobCompletion.Retry(ex.Code, ex.Message, ex.RetryAfter) : DurableJobCompletion.Failure(ex.Code, ex.Message); }
await queue.CompleteAsync(claim, result);
return 0;

sealed class Factory(string path) : IDbContextFactory<AllstarrDbContext>
{
    public AllstarrDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AllstarrDbContext>()
        .UseSqlite($"Data Source={path};Pooling=False;Foreign Keys=True")
        .AddInterceptors(new SqlitePragmaInterceptor()).Options);
    public Task<AllstarrDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(CreateDbContext());
}
sealed class Clock : IPlatformClock { public DateTimeOffset UtcNow => DateTimeOffset.UtcNow; }
sealed class Tracks : IPlaybackTrackResolver
{
    public Task<PlaybackTrackSnapshot?> ResolveAsync(PlaybackSignalPayload payload, CancellationToken cancellationToken = default) =>
        Task.FromResult<PlaybackTrackSnapshot?>(new(null, null, "track-1", "Track", "Artist", null, 120000));
}
sealed class Secrets : IProviderAccountSecretAccessor
{
    public Task<T> UseAsync<T>(ProviderAccountContext account, Func<ReadOnlyMemory<byte>, Task<T>> operation, CancellationToken cancellationToken) =>
        operation(System.Text.Encoding.UTF8.GetBytes("{\"apiKey\":\"fixture-key\",\"sharedSecret\":\"fixture-secret\",\"sessionKey\":\"fixture-session\",\"token\":\"fixture-token\"}"));
}
sealed class RedirectFixture(string endpoint) : DelegatingHandler(new HttpClientHandler { AllowAutoRedirect = false })
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    { request.RequestUri = new Uri(endpoint); return base.SendAsync(request, cancellationToken); }
}
sealed class Checkpoints(IPlaybackDeliveryCheckpointStore inner, string marker, string stopAt) : IPlaybackDeliveryCheckpointStore
{
    public Task<bool> IsCompletedAsync(Guid owner, string key, string target, CancellationToken token) => inner.IsCompletedAsync(owner, key, target, token);
    public async Task RecordAsync(Guid owner, string occurrence, string key, PlaybackScrobbleDeliveryKind kind, string target, ScopedPlaybackScrobbleResult result, CancellationToken token)
    {
        if (stopAt == "after-send") { File.WriteAllText(marker, "ready"); await Task.Delay(Timeout.Infinite, token); }
        await inner.RecordAsync(owner, occurrence, key, kind, target, result, token);
        if (stopAt == "after-save") { File.WriteAllText(marker, "ready"); await Task.Delay(Timeout.Infinite, token); }
    }
}
