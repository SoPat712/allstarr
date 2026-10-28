using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Storage;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackStartPhase>))]
public enum PlaybackStartPhase { Route, Account, Lease, Source, Verify, Retry, Segments, Process, Headers, FirstByte }

public sealed record PlaybackStartPhaseTiming(PlaybackStartPhase Phase, string? Provider, double StartedMs, double ElapsedMs);
public sealed record PlaybackStartSample(Guid Id, DateTimeOffset StartedAt, string Outcome, string? Provider,
    string? Title, string? Artist, string? AccountScope, bool Cached, double ElapsedMs,
    double? HeadersMs, double? FirstByteMs, IReadOnlyList<PlaybackStartPhaseTiming> Phases, int OmittedPhases);

public sealed class PlaybackStartTimings(TimeProvider clock) : IDisposable
{
    private static readonly AsyncLocal<PlaybackStartTimings?> Ambient = new();
    private readonly PlaybackStartTimings? previous = Ambient.Value;
    private readonly object sync = new();
    private readonly long started = clock.GetTimestamp();
    private readonly DateTimeOffset startedAt = clock.GetUtcNow();
    private readonly Guid id = Guid.CreateVersion7();
    private readonly List<PlaybackStartPhaseTiming> phases = [];
    private bool closed;
    private int omitted;
    private string? provider, title, artist, accountScope;
    private bool cached;
    private double? headers, firstByte;
    public const int MaximumPhases = 96;
    public static PlaybackStartTimings? Current => Ambient.Value;
    public bool Enabled { get; private set; }
    public Guid Id => id;
    private double Elapsed => Math.Max(0, clock.GetElapsedTime(started).TotalMilliseconds);

    public void Enter() => Ambient.Value = this;
    public void Enable() => Enabled = true;
    public IDisposable? Measure(PlaybackStartPhase phase, string? source = null) => Enabled ? new Measurement(this, phase, source, Elapsed) : null;
    public void Select(string source, bool fromCache, string? scope)
    {
        lock (sync)
        {
            if (closed) return;
            provider = source; cached = fromCache; accountScope = scope;
        }
    }
    public void Catalog(Song? song)
    {
        if (song == null) return;
        lock (sync)
        {
            if (closed) return;
            title = Label(song.Title); artist = Label(song.Artist);
        }
    }
    private static string? Label(string? value) => string.IsNullOrWhiteSpace(value) ? null :
        new string(value.Where(character => !char.IsControl(character)).Take(200).ToArray());
    public void Headers() { lock (sync) { if (!closed) headers ??= Elapsed; } }
    public bool FirstByte()
    {
        lock (sync)
        {
            if (closed || firstByte.HasValue) return false;
            firstByte = Elapsed;
            return true;
        }
    }
    private void Finish(PlaybackStartPhase phase, string? source, double from)
    {
        lock (sync)
        {
            if (closed) return;
            if (phases.Count < MaximumPhases) phases.Add(new(phase, source, from, Math.Max(0, Elapsed - from)));
            else omitted++;
        }
    }
    public PlaybackStartSample Snapshot(string outcome, bool complete = false)
    {
        lock (sync)
        {
            if (complete) closed = true;
            return new(id, startedAt, outcome, provider ?? phases.LastOrDefault(phase => phase.Provider != null)?.Provider, title, artist, accountScope, cached,
                Elapsed, headers, firstByte, phases.OrderBy(item => item.StartedMs).ToArray(), omitted);
        }
    }
    public void Dispose() => Ambient.Value = previous;
    private sealed class Measurement(PlaybackStartTimings owner, PlaybackStartPhase phase, string? source, double started) : IDisposable
    {
        private int disposed;
        public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) owner.Finish(phase, source, started); }
    }
}

public sealed class PlaybackStartTimingStore(IDbContextFactory<AllstarrDbContext> factory, TimeProvider clock,
    ILogger<PlaybackStartTimingStore> logger)
{
    public const string Category = "playback-start";
    public const int MaximumSamples = 500;
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
    private readonly SemaphoreSlim writing = new(1, 1);

    public async Task RecordAsync(PlaybackStartSample sample)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var entered = false;
        try
        {
            await writing.WaitAsync(timeout.Token);
            entered = true;
            await using var db = await factory.CreateDbContextAsync(timeout.Token);
            db.Database.SetCommandTimeout(1);
            await using var transaction = await db.Database.BeginTransactionAsync(timeout.Token);
            var row = await db.AuditEvents.SingleOrDefaultAsync(item => item.Id == sample.Id, timeout.Token);
            if (row == null)
            {
                row = new()
                {
                    Id = sample.Id,
                    Category = Category,
                    SubjectType = Category,
                    SubjectId = "recent",
                    Action = "stream-start",
                    CorrelationId = sample.Id.ToString("N"),
                    CreatedAt = sample.StartedAt
                };
                db.AuditEvents.Add(row);
            }
            else if (row.Category != Category) return;
            row.Outcome = sample.Outcome;
            row.DetailsJson = JsonSerializer.Serialize(sample);
            await db.SaveChangesAsync(timeout.Token);
            var cutoff = clock.GetUtcNow() - Retention;
            await db.AuditEvents.Where(item => item.SubjectType == Category && item.SubjectId == "recent" && item.CreatedAt < cutoff)
                .ExecuteDeleteAsync(timeout.Token);
            var retained = db.AuditEvents.Where(item => item.SubjectType == Category && item.SubjectId == "recent")
                .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).Take(MaximumSamples).Select(item => item.Id);
            await db.AuditEvents.Where(item => item.SubjectType == Category && item.SubjectId == "recent" && !retained.Contains(item.Id))
                .ExecuteDeleteAsync(timeout.Token);
            await transaction.CommitAsync(timeout.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { logger.LogDebug("Playback start timing could not be retained ({ErrorType})", error.GetType().Name); }
        finally { if (entered) writing.Release(); }
    }

    public async Task<IReadOnlyList<PlaybackStartSample>> ReadAsync(int limit, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var cutoff = clock.GetUtcNow() - Retention;
        var rows = await db.AuditEvents.AsNoTracking().Where(item => item.SubjectType == Category && item.SubjectId == "recent" && item.CreatedAt >= cutoff)
            .OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(Math.Clamp(limit, 1, 100)).Select(item => item.DetailsJson).ToListAsync(token);
        var samples = new List<PlaybackStartSample>();
        foreach (var row in rows)
            try { if (JsonSerializer.Deserialize<PlaybackStartSample>(row) is { } sample) samples.Add(sample); }
            catch (JsonException) { }
        return samples;
    }
}
