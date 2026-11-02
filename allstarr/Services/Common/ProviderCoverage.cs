using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Intelligence;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Services.Common;

public sealed record ProviderCoverageResult(string Provider, string Capability, string Song, bool Found, DateTimeOffset At);

/// <summary>Asks each source for a rotating sample of the household's most-played songs, so its score can say whether it has them.</summary>
public sealed class ProviderCoverageRunner(
    IDbContextFactory<AllstarrDbContext> factory,
    TrackMatchPlayableSearch matches,
    IProtocolProviderGateway providers,
    TimeProvider time,
    IEffectiveProviderPolicyResolver? policies = null)
{
    public const string Category = "provider-coverage";
    internal const int PoolSize = 200;
    internal const int SongsPerRun = 10;
    private const int RunHours = 6;
    private static readonly TimeSpan RetainFor = TimeSpan.FromDays(30);
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);
    private static readonly string[] LocalLyricsSources = ["backend", "kept"];

    private sealed record PoolSong(string Title, string Artist, string? Album);

    public async Task<DateTimeOffset?> LastRunAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        return await db.AuditEvents.AsNoTracking().Where(row => row.Category == Category)
            .OrderByDescending(row => row.CreatedAt).Select(row => (DateTimeOffset?)row.CreatedAt).FirstOrDefaultAsync(token);
    }

    public async Task RunAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await db.Users.AsNoTracking().Where(row => row.Enabled && row.IsAdmin && !row.IsSynthetic)
            .OrderBy(row => row.Id).FirstOrDefaultAsync(token);
        if (user == null) return;
        var pool = await db.ListeningEvents.AsNoTracking()
            .Where(play => play.Title != null && play.Artist != null &&
                (play.State == ListeningEventState.Completed || play.PositionTicks >= 30 * TimeSpan.TicksPerSecond) &&
                db.Users.Any(owner => owner.Id == play.OwnerUserId && !owner.IsSynthetic))
            .GroupBy(play => new { play.Title, play.Artist })
            .Select(group => new { group.Key.Title, group.Key.Artist, Album = group.Max(play => play.Album), Plays = group.Count() })
            .OrderByDescending(song => song.Plays).ThenBy(song => song.Title).ThenBy(song => song.Artist)
            .Take(PoolSize).ToArrayAsync(token);
        if (pool.Length == 0) return;
        ProtocolExecutionContext Context() => new(Enum.Parse<ProtocolKind>(user.BackendType, true), user.BackendInstanceId,
            user.BackendPrincipalId, new(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
            Guid.CreateVersion7().ToString("N"), time.GetUtcNow().AddMinutes(10), token);
        var streaming = await matches.AutomaticStreamingSourcesAsync(Context(), token);
        var lyricsOrder = providers.GetProviderOrder(ProviderCapabilityKind.Lyrics);
        var lyrics = (policies == null ? lyricsOrder :
            (await policies.ResolveForUserAsync(user.Id, token)).ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics, lyricsOrder))
            .Except(LocalLyricsSources, StringComparer.OrdinalIgnoreCase).ToArray();
        var scope = new TrackMatchScope(user.Id, user.BackendInstanceId, null, 2, 1, new HashSet<string>());
        var offset = (int)(time.GetUtcNow().ToUnixTimeSeconds() / (RunHours * 3600) * SongsPerRun % pool.Length);
        using var traffic = new HistoryRequestBudget();
        for (var index = 0; index < Math.Min(SongsPerRun, pool.Length); index++)
        {
            var picked = pool[(offset + index) % pool.Length];
            var song = new PoolSong(picked.Title!, picked.Artist!, picked.Album);
            var key = ListeningHistoryImportService.Hash(FuzzyKey(song));
            var source = new ExternalTrackMatchSnapshot(key, "history", key, song.Title, song.Artist, song.Album, null, null, null, null, null);
            var results = new List<ProviderCoverageResult>();
            foreach (var provider in streaming)
                if (await matches.SourceHasSongAsync(Context(), provider, source, scope, token) is { } found)
                    results.Add(new(provider, "streaming", key, found, time.GetUtcNow()));
            var recording = await db.ListeningEvents.AsNoTracking()
                .Where(play => play.Title == song.Title && play.Artist == song.Artist && play.CanonicalRecordingId != null)
                .Select(play => play.CanonicalRecordingId).FirstOrDefaultAsync(token);
            if (recording is { } known)
                foreach (var provider in lyrics)
                    if (await HasLyricsAsync(Context(), provider, known, song, token) is { } found)
                        results.Add(new(provider, "lyrics", key, found, time.GetUtcNow()));
            await SaveAsync(results, token);
        }
        var cutoff = time.GetUtcNow() - RetainFor;
        await db.AuditEvents.Where(row => row.Category == Category && row.CreatedAt < cutoff).ExecuteDeleteAsync(token);
    }

    private static string FuzzyKey(PoolSong song) =>
        allstarr.Services.Common.FuzzyMatcher.NormalizeTitleForMatching(song.Title, song.Artist) + ":" + allstarr.Services.Common.FuzzyMatcher.NormalizeForMatching(song.Artist);

    // A source that can't answer says nothing about what it has, and its reliability score already reflects the failure.
    private async Task<bool?> HasLyricsAsync(ProtocolExecutionContext context, string provider, Guid recording, PoolSong song, CancellationToken token)
    {
        try
        {
            return await providers.GetRecordingLyricsAsync(context, provider, recording, null, song.Title, [song.Artist], song.Album)
                .WaitAsync(Patience, token) != null;
        }
        catch (Exception) when (!token.IsCancellationRequested) { return null; }
    }

    private async Task SaveAsync(IReadOnlyList<ProviderCoverageResult> results, CancellationToken token)
    {
        if (results.Count == 0) return;
        await using var db = await factory.CreateDbContextAsync(token);
        foreach (var result in results)
            db.AuditEvents.Add(new()
            {
                Id = Guid.CreateVersion7(),
                Category = Category,
                Action = "song-checked",
                Outcome = result.Found ? "found" : "missing",
                SubjectType = Category,
                SubjectId = $"{result.Provider}/{result.Capability}",
                CorrelationId = result.Song,
                DetailsJson = JsonSerializer.Serialize(result),
                CreatedAt = result.At
            });
        await db.SaveChangesAsync(token);
    }
}

public sealed class ProviderCoverageService(
    ProviderCoverageRunner runner,
    BackgroundTaskActivity activity,
    IConfiguration configuration,
    TimeProvider time,
    ILogger<ProviderCoverageService> logger) : BackgroundService
{
    private readonly TimeSpan interval = TimeSpan.FromHours(Math.Clamp(configuration.GetValue("ProviderCoverage:IntervalHours", 6d), 1, 168));
    private readonly TimeSpan initialDelay = TimeSpan.FromSeconds(Math.Clamp(configuration.GetValue("ProviderCoverage:InitialDelaySeconds", 600), 0, 86400));

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        activity.Register("provider-coverage", "Source song checks", interval, initialDelay: initialDelay);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(initialDelay, time, stoppingToken);
            // A restart shouldn't repeat a run that only just finished.
            if (await runner.LastRunAsync(stoppingToken) is { } last && time.GetUtcNow() - last < interval)
                await Task.Delay(interval - (time.GetUtcNow() - last), time, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                activity.Started("provider-coverage");
                var success = false;
                try
                {
                    await runner.RunAsync(stoppingToken);
                    success = true;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception) { logger.LogWarning("Source song checks could not complete; they will resume at the next run"); }
                await activity.CompletedAsync("provider-coverage", success, stoppingToken);
                await Task.Delay(interval, time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
