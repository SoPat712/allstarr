using System.Threading.Channels;
using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Health;
using allstarr.Core.Jobs;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed class LyricsRefreshTriggers(IDbContextFactory<AllstarrDbContext> factory, IEffectiveProviderPolicyResolver policies,
    IProtocolProviderGateway providers, DurableJobQueue jobs, IRuntimeSettingsChangeSignal settings, ProviderRuntimeHealth health,
    DurableStorageState storage, TimeProvider time, ILogger<LyricsRefreshTriggers> logger) : BackgroundService
{
    private readonly Channel<bool> pending = Channel.CreateBounded<bool>(1);
    private readonly ConcurrentDictionary<(string Source, Guid? Account), byte> recoveries = new();
    private readonly Dictionary<Guid, HashSet<string>> enabled = [];
    private Dictionary<Guid, long>? accounts;
    private bool loaded;
    private sealed record Availability(Dictionary<Guid, HashSet<string>> Enabled, Dictionary<Guid, long> Accounts);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        settings.Changed += SettingsChanged;
        health.LyricsRecovered += SourceRecovered;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                if (storage.GetSnapshot().Readiness != DurableStorageReadiness.Ready)
                { await Task.Delay(TimeSpan.FromSeconds(5), time, stoppingToken); continue; }
                try
                {
                    while (pending.Reader.TryRead(out _)) { }
                    await CheckSettingsAsync(stoppingToken);
                    await DrainRecoveriesAsync(stoppingToken);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                { logger.LogDebug("Lyrics source refresh will retry ({ErrorType})", error.GetType().Name); }
                using var waiting = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var wake = pending.Reader.WaitToReadAsync(waiting.Token).AsTask();
                var interval = Task.Delay(TimeSpan.FromMinutes(1), time, waiting.Token);
                await Task.WhenAny(wake, interval);
                await waiting.CancelAsync();
            }
        }
        finally
        {
            settings.Changed -= SettingsChanged;
            health.LyricsRecovered -= SourceRecovered;
        }
    }

    internal async Task CheckSettingsAsync(CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var saved = await db.LyricsRefreshAvailability.SingleOrDefaultAsync(item => item.Key == "sources", token);
        if (!loaded)
        {
            if (saved != null && JsonSerializer.Deserialize<Availability>(saved.PayloadJson) is { } previous)
            {
                foreach (var entry in previous.Enabled) enabled[entry.Key] = entry.Value;
                accounts = previous.Accounts;
            }
            loaded = true;
        }
        var users = await db.Users.AsNoTracking().Where(user => user.Enabled).Select(user => user.Id).ToArrayAsync(token);
        foreach (var user in users)
        {
            var policy = await policies.ResolveForUserAsync(user, token);
            var current = policy.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics,
                providers.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Where(source => source is not ("backend" or "kept")).ToHashSet(StringComparer.Ordinal);
            if (enabled.TryGetValue(user, out var previous))
                foreach (var source in current.Except(previous)) await EnqueueAsync(source, user, token);
            enabled[user] = current;
        }
        foreach (var removed in enabled.Keys.Except(users).ToArray()) enabled.Remove(removed);
        var activeAccounts = await db.ProviderAccounts.AsNoTracking().Where(account => account.Enabled &&
            (account.SecretReferenceId == null || db.SecretReferences.Any(secret => secret.Id == account.SecretReferenceId && secret.RevokedAt == null)))
            .Select(account => new { account.Id, account.ProviderId, account.OwnerUserId, account.Revision }).ToArrayAsync(token);
        var lyricsSources = providers.GetProviderOrder(ProviderCapabilityKind.Lyrics).ToHashSet(StringComparer.Ordinal);
        var previousAccounts = accounts;
        if (previousAccounts != null)
            foreach (var account in activeAccounts.Where(account => lyricsSources.Contains(account.ProviderId) &&
                         (!previousAccounts.TryGetValue(account.Id, out var revision) || revision != account.Revision)))
                await EnqueueAsync(account.ProviderId, account.OwnerUserId, token);
        accounts = activeAccounts.ToDictionary(account => account.Id, account => account.Revision);
        var json = JsonSerializer.Serialize(new Availability(enabled, accounts));
        if (saved?.PayloadJson == json) return;
        if (saved == null)
        {
            saved = new() { Key = "sources" };
            db.LyricsRefreshAvailability.Add(saved);
        }
        saved.PayloadJson = json;
        saved.Revision++;
        await db.SaveChangesAsync(token);
    }

    private Task<DurableJobEnqueueResult> EnqueueAsync(string source, Guid? owner, CancellationToken token, TimeSpan? delay = null) =>
        jobs.EnqueueAsync(new DurableJobEnqueueRequest<LyricsRefreshRequest>(LyricsRefreshService.Type,
            $"lyrics-source:{source}:{owner:N}:{time.GetUtcNow():yyyyMMddHH}:{(delay.HasValue ? "settled" : "ready")}", new(source, owner),
            AvailableAt: delay.HasValue ? time.GetUtcNow() + delay.Value : null,
            MaxDeferrals: 10000, CorrelationId: "lyrics-source-available"), token);

    internal async Task DrainRecoveriesAsync(CancellationToken token)
    {
        foreach (var signal in recoveries.Keys.Take(64))
        {
            if (!recoveries.TryRemove(signal, out _)) continue;
            try
            {
                Guid? owner = null;
                if (signal.Account is { } account)
                {
                    await using var db = await factory.CreateDbContextAsync(token);
                    var row = await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == account && item.Enabled, token);
                    if (row == null) continue;
                    owner = row.OwnerUserId;
                }
                await EnqueueAsync(signal.Source, owner, token);
                await EnqueueAsync(signal.Source, owner, token, TimeSpan.FromMinutes(10));
            }
            catch
            {
                recoveries.TryAdd(signal, 0);
                throw;
            }
        }
        if (!recoveries.IsEmpty) pending.Writer.TryWrite(true);
    }

    private void SettingsChanged(long _) => pending.Writer.TryWrite(true);
    internal void SourceRecovered(string source, Guid? account)
    {
        recoveries.TryAdd((source, account), 0);
        pending.Writer.TryWrite(true);
    }
}
