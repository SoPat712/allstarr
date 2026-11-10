using System.Data;
using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Core.Intelligence;
using allstarr.Core.ClientApi;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed record LyricsRefreshSnapshot(Song Song, LyricsInfo? Lyrics, long? DurationMs, string? Provenance);

public sealed class LyricsRefreshStore(IDbContextFactory<AllstarrDbContext> factory, IPlatformClock clock,
    IEffectiveProviderPolicyResolver? policies = null, IProviderRouter? router = null,
    allstarr.Core.ClientEvents.IClientEventChanges? events = null, LyricsSearchChanges? changes = null,
    LyricsRefreshSidecars? sidecars = null)
{
    public static LyricsRefreshSnapshot Read(string json) => JsonSerializer.Deserialize<LyricsRefreshSnapshot>(json)!;

    internal static LyricsInfo? FromIndexedLines(RichLyrics rich, Song song) => new()
    {
        Source = rich.Source.Id,
        TrackName = song.Title,
        ArtistName = song.Artist,
        AlbumName = song.Album,
        Duration = song.Duration ?? 0,
        TimingCoverageMilliseconds = rich.Format == "word" ? rich.Lines.Select(line =>
            Math.Max(line.StartMs ?? 0, line.Words.Select(word => word.StartMs).DefaultIfEmpty(0).Max())).DefaultIfEmpty(0).Max() : null,
        PlainLyrics = rich.Format == "plain" ? string.Join('\n', rich.Lines.Select(line => line.Text)) : null,
        SyncedLyrics = rich.Format == "plain" ? null : string.Join('\n', rich.Lines.Where(line => line.StartMs.HasValue).Select(line =>
            FormattableString.Invariant($"[{line.StartMs!.Value / 60000:00}:{line.StartMs.Value / 1000 % 60:00}.{line.StartMs.Value % 1000:000}]{line.Text}"))),
        TimedLines = rich.Format == "word" ? rich.Lines.Where(line => line.StartMs.HasValue).Select(line =>
            new ProviderLyricsTimedLine(line.Text, TimeSpan.FromMilliseconds(line.StartMs!.Value), segments:
                line.Words.Select(word => new ProviderLyricsTimedSegment(word.Text, TimeSpan.FromMilliseconds(word.StartMs),
                    word.EndMs is { } end ? TimeSpan.FromMilliseconds(Math.Max(0, end - word.StartMs)) : null)).ToArray(),
                isBackground: line.Part == "background" ? true : null, voice: line.Voice, singer: line.Singer)).ToArray() : null
    };

    public async Task ObserveAsync(ProtocolExecutionContext context, Song song, LyricsInfo? lyrics, long? durationMs)
    {
        if (context.Actor?.EffectiveUserId is not { } owner || song.CanonicalRecordingId is not { } recording ||
            context.IsReadOnly || context.IsPreparation) return;
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken);
        var existing = await db.RecordingLyricsRefreshes.SingleOrDefaultAsync(item => item.OwnerUserId == owner &&
            item.CanonicalRecordingId == recording, context.CancellationToken);
        if (existing != null)
        {
            var prior = Read(existing.PayloadJson);
            var obsolete = prior.Lyrics == null && lyrics != null;
            if (policies != null)
                obsolete |= !(await policies.ResolveForUserAsync(owner, context.CancellationToken)).IsAutomatic(LyricsVersions.ProviderId(existing.SourceId));
            if (prior.Provenance is { } provenance && provenance != "public")
            {
                var binding = provenance.Split(':');
                obsolete |= binding.Length != 2 || !Guid.TryParse(binding[0], out var account) || !long.TryParse(binding[1], out var revision) ||
                    !await db.ProviderAccounts.AnyAsync(item => item.Id == account && item.Revision == revision && item.Enabled &&
                        (item.OwnerUserId == null || item.OwnerUserId == owner) && (item.SecretReferenceId == null ||
                        db.SecretReferences.Any(secret => secret.Id == item.SecretReferenceId && secret.RevokedAt == null)), context.CancellationToken);
            }
            if (!obsolete)
            {
                var candidate = LyricsQuality.Evaluate(lyrics, prior.Song, prior.DurationMs);
                if (candidate?.Score > existing.Score && candidate.Reasons.Contains("duration_fits") &&
                    await CompleteAsync(existing, lyrics, "Source answered with better lyrics", context.CancellationToken))
                {
                    changes?.RecordingChanged(owner, recording);
                    if (events != null) await events.LyricsAsync(recording, owner, automatic: true);
                    if (sidecars != null) await sidecars.UpgradeAsync(context, recording, prior.Song, lyrics!, context.CancellationToken);
                }
                return;
            }
        }
        if (await PinnedAsync(db, recording, owner, context.CancellationToken) ||
            !await db.CanonicalRecordings.AnyAsync(item => item.Id == recording && item.MergedIntoId == null, context.CancellationToken)) return;
        var score = LyricsQuality.Evaluate(lyrics, song, durationMs);
        var row = existing ?? new RecordingLyricsRefreshRecord { OwnerUserId = owner, CanonicalRecordingId = recording };
        if (existing == null) db.RecordingLyricsRefreshes.Add(row);
        else { row.PreviousJson = row.PayloadJson; row.Reason = "Source availability changed"; }
        row.SourceId = lyrics?.Source ?? "";
        row.PayloadJson = Serialize(song, lyrics, durationMs);
        row.Score = score?.Score ?? -1;
        row.TargetDurationMilliseconds = LyricsQuality.Duration(song, durationMs);
        row.WordSynced = score?.Reasons.Contains("synced_words") == true;
        row.NextCheckAt = clock.UtcNow;
        row.Misses = 0;
        row.Revision++;
        try { await db.SaveChangesAsync(context.CancellationToken); }
        catch (DbUpdateException error) when (DbErrors.IsUniqueViolation(error) || error is DbUpdateConcurrencyException) { }
    }

    public async Task<LyricsRefreshSnapshot?> CurrentAsync(Guid owner, Guid recording, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var row = await db.RecordingLyricsRefreshes.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == owner && item.CanonicalRecordingId == recording, token);
        if (row == null) return null;
        var snapshot = Read(row.PayloadJson);
        if (snapshot.Lyrics != null) snapshot.Lyrics.CacheProvenance = snapshot.Provenance;
        return snapshot;
    }

    public async Task<LyricsInfo?> RetainedAsync(ProtocolExecutionContext context, Guid recording)
    {
        if (context.Actor?.EffectiveUserId is not { } owner) return null;
        await using var db = await factory.CreateDbContextAsync(context.CancellationToken);
        var row = await db.RecordingLyricsRefreshes.AsNoTracking().SingleOrDefaultAsync(item =>
            item.OwnerUserId == owner && item.CanonicalRecordingId == recording, context.CancellationToken);
        if (row == null) return null;
        var snapshot = Read(row.PayloadJson);
        if (snapshot?.Lyrics?.Source is not { } source) return null;
        var provider = LyricsVersions.ProviderId(source);
        if (provider is "spicylyrics" or "backend" or "kept") return null;
        snapshot.Lyrics.CacheProvenance = snapshot.Provenance;
        if (policies != null)
        {
            var policy = await policies.ResolveForUserAsync(owner, context.CancellationToken);
            if (!policy.IsAutomatic(provider) || policy.Preferences.ExplicitFilter == "CleanOnly" &&
                (provider is not ("backend" or "kept") || !CleanRecordingPolicy.IsVerifiedClean(snapshot.Song))) return null;
        }
        if (router != null && provider is not ("backend" or "kept"))
        {
            var plan = await router.PlanAsync<IProviderLyricsCapability>(LyricsSearchIndex.SourceRequest(context, provider) with { CacheOnly = true });
            if (plan.Candidates.FirstOrDefault() is not { } selected ||
                LyricsSearchIndex.AccountProvenance(selected.Context.Account) != (snapshot.Provenance ?? "public")) return null;
        }
        return snapshot.Lyrics;
    }

    public async Task<IReadOnlyList<RecordingLyricsRefreshRecord>> DueAsync(string? source, Guid? owner, int cap, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var now = clock.UtcNow;
        var maximumKnown = LyricsQuality.Maximum(source ?? "", new Song { Duration = 1 });
        var maximumUnknown = LyricsQuality.Maximum(source ?? "", new Song());
        var candidates = db.RecordingLyricsRefreshes.AsNoTracking().Where(item =>
            (source != null || (!item.WordSynced || item.Score < 0.9) && item.NextCheckAt <= now) &&
            (source == null || item.Score < (item.TargetDurationMilliseconds > 0 ? maximumKnown : maximumUnknown)) &&
            (owner == null || item.OwnerUserId == owner) &&
            db.Users.Any(user => user.Id == item.OwnerUserId && user.Enabled) &&
            db.CanonicalRecordings.Any(recording => recording.Id == item.CanonicalRecordingId && recording.MergedIntoId == null) &&
            !db.RecordingLyricsChoices.Any(pin => pin.CanonicalRecordingId == item.CanonicalRecordingId && pin.RevokedAt == null &&
                (pin.OwnerUserId == null || pin.OwnerUserId == item.OwnerUserId)));
        var activity = db.ListeningEvents.AsNoTracking().Where(play => play.CanonicalRecordingId != null)
            .GroupBy(play => new { play.OwnerUserId, Recording = play.CanonicalRecordingId!.Value })
            .Select(group => new
            {
                group.Key.OwnerUserId,
                group.Key.Recording,
                Plays = group.Count(play => play.State == ListeningEventState.Completed),
                Recent = group.Max(play => play.ListenedAt ?? play.StartedAt)
            });
        var ranked = await (from item in candidates
                            join play in activity on new { item.OwnerUserId, Recording = item.CanonicalRecordingId } equals
                                new { play.OwnerUserId, play.Recording } into listening
                            from play in listening.DefaultIfEmpty()
                            select new { Item = item, Plays = (int?)play.Plays ?? 0, play.Recent })
            .OrderByDescending(item => item.Plays).ThenByDescending(item => item.Recent)
            .ThenBy(item => item.Item.NextCheckAt).ThenBy(item => item.Item.CanonicalRecordingId)
            .Take(Math.Clamp(cap, 1, 100)).ToArrayAsync(token);
        return ranked.Select(item => item.Item).ToArray();
    }

    public async Task<bool> CompleteAsync(RecordingLyricsRefreshRecord expected, LyricsInfo? candidate, string trigger, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var row = await db.RecordingLyricsRefreshes.SingleAsync(item => item.OwnerUserId == expected.OwnerUserId &&
            item.CanonicalRecordingId == expected.CanonicalRecordingId, token);
        if (row.Revision != expected.Revision || await PinnedAsync(db, row.CanonicalRecordingId, row.OwnerUserId, token)) return false;
        if (candidate != null)
        {
            if (policies != null && !(await policies.ResolveForUserAsync(row.OwnerUserId, token)).IsAutomatic(LyricsVersions.ProviderId(candidate.Source ?? ""))) return false;
            if (!await CurrentBindingAsync(db, row.OwnerUserId, candidate.CacheProvenance, token)) return false;
        }
        var snapshot = Read(row.PayloadJson);
        var score = LyricsQuality.Evaluate(candidate, snapshot.Song, snapshot.DurationMs);
        var improved = score != null && score.Score > row.Score && score.Reasons.Contains("duration_fits");
        if (improved)
        {
            row.PreviousJson = row.PayloadJson;
            row.PayloadJson = Serialize(snapshot.Song, candidate, snapshot.DurationMs);
            row.SourceId = candidate!.Source!;
            row.Score = score!.Score;
            row.WordSynced = score.Reasons.Contains("synced_words");
            row.Reason = trigger + ": " + string.Join(", ", score.Reasons);
            row.Misses = 0;
        }
        else row.Misses = Math.Min(row.Misses + 1, 3);
        row.CheckedAt = clock.UtcNow;
        row.NextCheckAt = clock.UtcNow.AddDays(7 * (1 << Math.Clamp(row.Misses - 1, 0, 2)));
        row.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return improved;
    }

    public async Task<bool> RevertAsync(Guid owner, Guid recording, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
        var row = await db.RecordingLyricsRefreshes.SingleOrDefaultAsync(item => item.OwnerUserId == owner && item.CanonicalRecordingId == recording, token);
        if (row?.PreviousJson == null || await PinnedAsync(db, recording, owner, token)) return false;
        (row.PayloadJson, row.PreviousJson) = (row.PreviousJson, row.PayloadJson);
        var snapshot = Read(row.PayloadJson);
        var score = LyricsQuality.Evaluate(snapshot.Lyrics, snapshot.Song, snapshot.DurationMs);
        row.SourceId = snapshot.Lyrics?.Source ?? "";
        row.Score = score?.Score ?? -1;
        row.TargetDurationMilliseconds = LyricsQuality.Duration(snapshot.Song, snapshot.DurationMs);
        row.WordSynced = score?.Reasons.Contains("synced_words") == true;
        row.Reason = "Restored previous lyrics";
        row.NextCheckAt = clock.UtcNow.AddDays(7);
        row.Revision++;
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        changes?.RecordingChanged(owner, recording);
        if (events != null) await events.LyricsAsync(recording, owner, automatic: true);
        return true;
    }

    private static Task<bool> PinnedAsync(AllstarrDbContext db, Guid recording, Guid owner, CancellationToken token) =>
        db.RecordingLyricsChoices.AnyAsync(item => item.CanonicalRecordingId == recording && item.RevokedAt == null &&
            (item.OwnerUserId == null || item.OwnerUserId == owner), token);

    private static async Task<bool> CurrentBindingAsync(AllstarrDbContext db, Guid owner, string? provenance, CancellationToken token)
    {
        if (provenance is null or "public") return true;
        var binding = provenance.Split(':');
        return binding.Length == 2 && Guid.TryParse(binding[0], out var account) && long.TryParse(binding[1], out var revision) &&
            await db.ProviderAccounts.AnyAsync(item => item.Id == account && item.Revision == revision && item.Enabled &&
                (item.OwnerUserId == null || item.OwnerUserId == owner) && (item.SecretReferenceId == null ||
                db.SecretReferences.Any(secret => secret.Id == item.SecretReferenceId && secret.RevokedAt == null)), token);
    }

    private static string Serialize(Song song, LyricsInfo? lyrics, long? durationMs) =>
        JsonSerializer.Serialize(new LyricsRefreshSnapshot(song, lyrics, durationMs, lyrics?.CacheProvenance));
}
