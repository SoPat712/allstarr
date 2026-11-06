using System.Security.Cryptography;
using allstarr.Core.Downloads;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Music;

public sealed class LyricsRefreshSidecars(IDbContextFactory<AllstarrDbContext> factory, ILogger<LyricsRefreshSidecars> logger)
{
    public async Task UpgradeAsync(ProtocolExecutionContext context, Guid recording, Song song, LyricsInfo candidate, CancellationToken token)
    {
        if (LyricsVersions.ProviderId(candidate.Source ?? "") == "spicylyrics") return;
        await using var db = await factory.CreateDbContextAsync(token);
        if (!await CanPublishAsync(db, candidate.CacheProvenance, token)) return;
        var paths = await db.ManagedFiles.AsNoTracking().Where(file => file.IsManaged && file.RemovedAt == null &&
            (db.Set<RecordingKeptRouteEntity>().Any(route => route.CanonicalRecordingId == recording && route.ManagedFileId == file.Id) ||
             db.LibraryTracks.Any(track => track.CanonicalRecordingId == recording && track.FilePath == file.CanonicalPath)))
            .Select(file => file.CanonicalPath).Distinct().Take(10).ToArrayAsync(token);
        foreach (var path in paths)
        {
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(token);
                if (await db.RecordingLyricsChoices.AnyAsync(pin => pin.CanonicalRecordingId == recording && pin.RevokedAt == null, token) ||
                    !await CanPublishAsync(db, candidate.CacheProvenance, token)) return;
                using var audio = TagLib.File.Create(path);
                var duration = (long)audio.Properties.Duration.TotalMilliseconds;
                await ReplaceAsync(path, song, candidate, duration, token);
                await transaction.CommitAsync(token);
            }
            catch (Exception error) when (error is not OperationCanceledException)
            { logger.LogDebug("Better lyrics sidecar deferred ({ErrorType})", error.GetType().Name); }
        }
    }

    internal static async Task<bool> CanPublishAsync(AllstarrDbContext db, string? provenance, CancellationToken token)
    {
        if (provenance is null or "public") return true;
        var binding = provenance.Split(':');
        return binding.Length == 2 && Guid.TryParse(binding[0], out var account) && long.TryParse(binding[1], out var revision) &&
            await db.ProviderAccounts.AnyAsync(item => item.Id == account && item.Enabled && item.OwnerUserId == null &&
                item.Revision == revision && (item.SecretReferenceId == null ||
                db.SecretReferences.Any(secret => secret.Id == item.SecretReferenceId && secret.RevokedAt == null)), token);
    }

    internal static async Task<bool> ReplaceAsync(string audioPath, Song song, LyricsInfo candidate, long measuredDurationMs, CancellationToken token)
    {
        if (measuredDurationMs <= 0 || LyricsQuality.Evaluate(candidate, song, measuredDurationMs) is not { } score ||
            !score.Reasons.Contains("duration_fits")) return false;
        var path = Path.ChangeExtension(audioPath, ".lrc");
        if (!File.Exists(path) || new FileInfo(path).LinkTarget != null) return false;
        var previous = await File.ReadAllBytesAsync(path, token);
        var old = await KeptLyricsReader.ReadStoredSidecarAsync(song, audioPath, token);
        if (score.Score <= (LyricsQuality.Evaluate(old, song, measuredDurationMs)?.Score ?? -1)) return false;
        var content = Content(candidate, measuredDurationMs);
        if (string.IsNullOrWhiteSpace(content)) return false;
        var backup = path + ".previous-" + Convert.ToHexStringLower(SHA256.HashData(previous))[..16];
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, token);
            if (!(await File.ReadAllBytesAsync(path, token)).AsSpan().SequenceEqual(previous)) return false;
            if (!File.Exists(backup)) await File.WriteAllBytesAsync(backup, previous, token);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
            return true;
        }
        finally { File.Delete(temporary); }
    }

    internal static string? Content(LyricsInfo lyrics, long? measuredDurationMs = null)
    {
        if (lyrics.TimedLines is not { Count: > 0 }) return lyrics.SyncedLyrics ?? lyrics.PlainLyrics;
        var projection = new LyricsInfo
        {
            Source = lyrics.Source,
            SyncedLyrics = lyrics.SyncedLyrics,
            TimedLines = lyrics.TimedLines,
            Duration = measuredDurationMs.HasValue ? (int)Math.Clamp((measuredDurationMs.Value + 999) / 1000, 0, 86_400) : lyrics.Duration
        };
        if (RichLyricsMapper.Map(projection) is not { } rich) return null;
        static string Time(long ms) => FormattableString.Invariant($"{ms / 60000:00}:{ms / 1000 % 60:00}.{ms % 1000:000}");
        return string.Join('\n', rich.Lines.Where(line => line.StartMs != null).Select(line =>
            "[" + Time(line.StartMs!.Value) + "]" + (line.Words.Count == 0 ? line.Text :
                string.Concat(line.Words.Select(word => "<" + Time(word.StartMs) + ">" + word.Text)) +
                (line.EndMs is { } end ? "<" + Time(end) + ">" : ""))));
    }
}
