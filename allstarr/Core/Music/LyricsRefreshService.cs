using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.ClientApi;
using allstarr.Core.ClientEvents;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Lyrics;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record LyricsRefreshRequest(string? SourceId = null, Guid? OwnerUserId = null);
public sealed record LyricsRefreshResult(int Checked, int Changed, int WordSynced);

public sealed class LyricsRefreshOptions
{
    public int MaximumSongs { get; set; } = 25;
    public int PaceMilliseconds { get; set; } = 2000;
    internal int Cap => Math.Clamp(MaximumSongs, 1, 100);
    internal TimeSpan Pace => TimeSpan.FromMilliseconds(Math.Clamp(PaceMilliseconds, 1000, 60_000));
}

public sealed class LyricsRefreshService(LyricsRefreshStore store, IDbContextFactory<AllstarrDbContext> factory,
    IProtocolLyricsResolver lyrics, IProtocolProviderGateway providers, IEffectiveProviderPolicyResolver policies,
    TimeProvider time, LyricsRefreshOptions options, ILogger<LyricsRefreshService> logger,
    IClientEventChanges? events = null, LyricsSearchChanges? changes = null, LyricsSearchIndex? index = null,
    LyricsRefreshSidecars? sidecars = null) : IDurableJobHandler
{
    public const string Type = "lyrics.refresh";
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset nextRead;
    string IDurableJobHandler.JobType => Type;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        var request = context.Claim.Payload.Deserialize<LyricsRefreshRequest>() ?? new();
        await RefreshAsync(request, context.Claim.CorrelationId, cancellationToken);
        return DurableJobCompletion.Success();
    }

    public async Task<LyricsRefreshResult> RefreshAsync(LyricsRefreshRequest request, string correlationId, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var due = await store.DueAsync(request.SourceId, request.OwnerUserId, options.Cap, token);
            var checkedSongs = 0;
            var changedSongs = new HashSet<Guid>();
            var words = new HashSet<Guid>();
            foreach (var state in due)
            {
                token.ThrowIfCancellationRequested();
                await using var db = await factory.CreateDbContextAsync(token);
                var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == state.OwnerUserId && item.Enabled, token);
                if (user == null || user.BackendType is not ("jellyfin" or "subsonic")) continue;
                var policy = await policies.ResolveForUserAsync(user.Id, token);
                var snapshot = LyricsRefreshStore.Read(state.PayloadJson);
                var song = MusicLyrics.ForLookup(snapshot.Song);
                var order = policy.ApplyAutomaticProviderAvailability(ProviderCapabilityKind.Lyrics,
                    providers.GetProviderOrder(ProviderCapabilityKind.Lyrics)).Where(source => source is not ("backend" or "kept") &&
                    (request.SourceId == null || source == request.SourceId) &&
                    LyricsQuality.Maximum(source, song, snapshot.DurationMs) > state.Score).ToArray();
                if (order.Length == 0)
                {
                    checkedSongs++;
                    await store.CompleteAsync(state, null, "No automatic source can improve these lyrics", token);
                    continue;
                }
                var context = new ProtocolExecutionContext(user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic,
                    user.BackendInstanceId, user.BackendPrincipalId,
                    new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
                    correlationId, time.GetUtcNow().AddMinutes(10), token)
                { Policy = policy, IsReadOnly = true, RefreshLyrics = true };
                ProtocolLyricsCandidate? best = null;
                foreach (var source in order)
                {
                    if (await db.RecordingLyricsChoices.AnyAsync(pin => pin.CanonicalRecordingId == state.CanonicalRecordingId && pin.RevokedAt == null &&
                        (pin.OwnerUserId == null || pin.OwnerUserId == state.OwnerUserId), token)) break;
                    var delay = nextRead - time.GetUtcNow();
                    if (delay > TimeSpan.Zero) await Task.Delay(delay, time, token);
                    nextRead = time.GetUtcNow() + options.Pace;
                    try
                    {
                        var candidates = await lyrics.FindAllAsync(context, song, state.CanonicalRecordingId.ToString("N"),
                            new ProtocolLyricsSources { OnlySourceId = source, ReadOnly = true });
                        var ranked = ProtocolLyricsResolver.Assess(candidates, song, snapshot.DurationMs)
                            .Where(candidate => candidate.Assessment != null).OrderByDescending(candidate => candidate.Assessment!.Score).FirstOrDefault();
                        if (ranked?.Assessment?.Score > (best?.Assessment?.Score ?? -1)) best = ranked;
                    }
                    catch (Exception error) when (error is not OperationCanceledException)
                    { logger.LogDebug("Better lyrics lookup deferred for {Source} ({ErrorType})", source, error.GetType().Name); }
                }
                if (request.SourceId != null)
                {
                    var cached = await lyrics.FindAllAsync(context, song, state.CanonicalRecordingId.ToString("N"),
                        new ProtocolLyricsSources { CacheOnly = true, ReadOnly = true });
                    var ranked = ProtocolLyricsResolver.Assess(cached, song, snapshot.DurationMs)
                        .Where(candidate => candidate.Assessment != null).OrderByDescending(candidate => candidate.Assessment!.Score).FirstOrDefault();
                    if (ranked?.Assessment?.Score > (best?.Assessment?.Score ?? -1)) best = ranked;
                }
                checkedSongs++;
                if (best?.Lyrics != null) best.Lyrics.Source = best.SourceId;
                if (!await store.CompleteAsync(state, best?.Lyrics, request.SourceId == null ? "Scheduled search" : "Source available: " + request.SourceId, token)) continue;
                changedSongs.Add(state.CanonicalRecordingId);
                if (best!.Assessment!.Reasons.Contains("synced_words")) words.Add(state.CanonicalRecordingId);
                changes?.RecordingChanged(user.Id, state.CanonicalRecordingId);
                if (events != null) await events.LyricsAsync(state.CanonicalRecordingId, user.Id, automatic: true);
                if (index != null && RichLyricsMapper.Map(best.Lyrics) is { } rich)
                    await index.StoreAsync(context, song, state.CanonicalRecordingId, rich, expectedProvenance: best.Lyrics?.CacheProvenance);
                if (sidecars != null) await sidecars.UpgradeAsync(context, state.CanonicalRecordingId, song, best.Lyrics!, token);
            }
            if (changedSongs.Count > 0)
            {
                await using var db = await factory.CreateDbContextAsync(token);
                db.AuditEvents.Add(new()
                {
                    Id = Guid.CreateVersion7(),
                    Category = "lyrics",
                    Action = "automatic.upgraded",
                    Outcome = "succeeded",
                    CorrelationId = correlationId,
                    CreatedAt = time.GetUtcNow(),
                    DetailsJson = JsonSerializer.Serialize(new { changed = changedSongs.Count, wordSynced = words.Count })
                });
                await db.SaveChangesAsync(token);
            }
            return new(checkedSongs, changedSongs.Count, words.Count);
        }
        finally { gate.Release(); }
    }
}
