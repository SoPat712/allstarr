using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using allstarr.Core.Playback;
using allstarr.Services.Scrobbling;
using allstarr.Core.Intelligence;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Tests;

public sealed partial class PlaybackSignalPipelineTests
{
    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("lastfm", "before-send", 1, false)]
    [InlineData("lastfm", "after-send", 1, true)]
    [InlineData("lastfm", "after-save", 1, false)]
    [InlineData("listenbrainz", "before-send", 1, false)]
    [InlineData("listenbrainz", "after-send", 2, false)]
    [InlineData("listenbrainz", "after-save", 1, false)]
    public async Task ScrobbleProcessKill_RecoversWithoutLosingOrDoublingTheListen(string provider,
        string stopAt, int expectedRequests, bool needsReview)
    {
        await AddScrobbleAccountAsync(provider);
        var accounts = new ScopedRecommendationAccountAccessor(factory, new ScrobbleSecrets());
        IExactScopePlaybackScrobbleTarget target = provider == "lastfm"
            ? new LastFmScopedPlaybackScrobbleTarget(new(new ScrobbleHttp(HttpStatusCode.OK)), accounts)
            : new ListenBrainzScopedPlaybackScrobbleTarget(new(new ScrobbleHttp(HttpStatusCode.OK)), accounts);
        await new PlaybackSignalPipeline(jobs, scrobbleAcceptance: new(factory, jobs, [target]))
            .RecordAsync(Signal(PlaybackTransition.Stop, "track-1", TimeSpan.FromSeconds(100).Ticks));
        using var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        var port = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        using var listener = new HttpListener();
        var endpoint = $"http://127.0.0.1:{port}/";
        listener.Prefixes.Add(endpoint);
        listener.Start();
        var requests = new List<string>();
        var uniqueListens = new HashSet<string>(StringComparer.Ordinal);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var serve = Task.Run(async () =>
        {
            while (!cancellation.IsCancellationRequested)
            {
                var request = await listener.GetContextAsync().WaitAsync(cancellation.Token);
                using var reader = new StreamReader(request.Request.InputStream);
                var body = await reader.ReadToEndAsync(cancellation.Token);
                lock (requests) { requests.Add(body); uniqueListens.Add(body); }
                var response = Encoding.UTF8.GetBytes(provider == "lastfm"
                    ? "{\"scrobbles\":{\"scrobble\":{\"ignoredMessage\":{\"code\":\"0\"}},\"@attr\":{\"accepted\":\"1\",\"ignored\":\"0\"}}}"
                    : "{\"status\":\"ok\"}");
                request.Response.StatusCode = 200;
                request.Response.ContentLength64 = response.Length;
                await request.Response.OutputStream.WriteAsync(response, cancellation.Token);
                request.Response.Close();
            }
        }, cancellation.Token);
        var marker = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "checkpoint-marker");
        try
        {
            using (var interrupted = StartProbe(stopAt))
            {
                try
                {
                    while (!File.Exists(marker))
                    {
                        if (interrupted.HasExited) throw new InvalidOperationException(await interrupted.StandardError.ReadToEndAsync(cancellation.Token));
                        await Task.Delay(20, cancellation.Token);
                    }
                }
                finally
                {
                    if (!interrupted.HasExited) interrupted.Kill(entireProcessTree: true);
                    await interrupted.WaitForExitAsync(CancellationToken.None);
                }
            }
            using (var restarted = StartProbe("complete"))
            {
                try { await restarted.WaitForExitAsync(cancellation.Token); }
                finally { if (!restarted.HasExited) restarted.Kill(entireProcessTree: true); }
                Assert.True(restarted.ExitCode == 0, await restarted.StandardError.ReadToEndAsync(cancellation.Token));
            }
            Assert.Equal(expectedRequests, requests.Count);
            Assert.Single(uniqueListens);
            await using var db = await factory.CreateDbContextAsync();
            var saved = Assert.Single(await db.PlaybackDeliveryCheckpoints.ToListAsync());
            Assert.Equal(needsReview ? "ambiguous" : null, saved.ProviderCode);
            Assert.Equal(needsReview ? ScopedPlaybackScrobbleOutcome.PermanentFailure : ScopedPlaybackScrobbleOutcome.Delivered, saved.State);
            if (needsReview)
            {
                Assert.True(await new PlaybackScrobbleRepair(factory, jobs).ResolveAsync(user, saved.Id, saved.Revision, "confirm-delivered", null, default));
                Assert.Single(requests);
            }
        }
        finally
        {
            cancellation.Cancel();
            listener.Stop();
            try { await serve; } catch (OperationCanceledException) { } catch (HttpListenerException) { }
        }

        Process StartProbe(string boundary)
        {
            var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;
            var probe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                $"../../../../tools/tests/ScrobbleCrashProbe/bin/{configuration}/net10.0/ScrobbleCrashProbe.dll"));
            var start = new ProcessStartInfo("dotnet") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
            foreach (var argument in new[] { probe, database.DatabasePath, marker, boundary, endpoint, provider }) start.ArgumentList.Add(argument);
            return Process.Start(start)!;
        }
    }
}
