using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackStartPhase>))]
public enum PlaybackStartPhase { Route, Account, Lease, Source, Verify, Retry, Segments, Process, Headers, FirstByte, Preparation }

public sealed record PlaybackStartPhaseTiming(PlaybackStartPhase Phase, string? Provider, double StartedMs, double ElapsedMs);
public sealed record PlaybackStartSample(Guid Id, DateTimeOffset StartedAt, string Outcome, string? Provider,
    string? Title, string? Artist, string? AccountScope, bool Cached, double ElapsedMs,
    double? HeadersMs, double? FirstByteMs, IReadOnlyList<PlaybackStartPhaseTiming> Phases, int OmittedPhases, PlayTraceData? Trace = null);

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
    private PlayTraceData? play;
    private Func<TrackMediaFacts?>? received;
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
        PlayTraceText.Clean(new string(value.Where(character => !char.IsControl(character)).Take(200).ToArray()));
    public void Prepared(HttpResponseMessage response, string source)
    {
        if (!Enabled || !response.IsSuccessStatusCode) return;
        static long? Read(HttpResponseMessage message, string name)
        {
            if (!message.Headers.TryGetValues(name, out var values)) return null;
            var entries = values.Take(2).ToArray();
            return entries.Length == 1 && long.TryParse(entries[0], NumberStyles.None, CultureInfo.InvariantCulture, out var value) &&
                value is >= 0 and <= 300_000 ? value : null;
        }
        var preparation = Read(response, "X-Allstarr-Prepare-Download-Ms");
        var process = Read(response, "X-Allstarr-Prepare-Process-Ms");
        lock (sync)
        {
            if (closed || preparation == null || process == null || preparation + process > Elapsed + 2) return;
            var from = Math.Max(0, Elapsed - preparation.Value - process.Value);
            foreach (var phase in new[]
            {
                new PlaybackStartPhaseTiming(PlaybackStartPhase.Preparation, source, from, preparation.Value),
                new PlaybackStartPhaseTiming(PlaybackStartPhase.Process, source, from + preparation.Value, process.Value)
            })
            {
                if (phase.ElapsedMs <= 0) continue;
                if (phases.Count < MaximumPhases) phases.Add(phase); else omitted++;
            }
        }
    }
    public void BeginPlay(ProtocolExecutionContext context, string itemId)
    {
        if (!Enabled || context.IsPreparation || context.IsReadOnly || context.Principal == null) return;
        lock (sync)
        {
            if (closed || play != null) return;
            play = new(context.Principal.UserId, new(context.Protocol.ToString(), PlayTraceText.Reference(itemId),
                PlayTraceText.Clean(context.Client.ClientId), PlayTraceText.Clean(context.Client.DeviceName), new()), new());
        }
    }
    public void Present(Song song)
    {
        lock (sync)
        {
            if (!closed && play != null)
            {
                var metadata = PlayTraceText.Metadata(song);
                if (song.CanonicalRecordingId.HasValue) play = play with { Resolution = play.Resolution with { CanonicalRecordingId = song.CanonicalRecordingId } };
                var artwork = play.Resolution.ExternalSnapshotId is { } snapshot && metadata.Artwork != null
                    ? $"/api/admin/track-matches/{snapshot:D}/artwork" : metadata.Artwork;
                play = play with { Request = play.Request with { Presented = metadata with { Artwork = artwork } } };
            }
        }
    }
    public void Resolve(PlayTraceResolution resolution)
    {
        lock (sync)
        {
            if (closed || play == null) return;
            var selected = resolution with
            {
                CanonicalRecordingId = resolution.CanonicalRecordingId ?? play.Resolution.CanonicalRecordingId,
                State = PlayTraceText.Reference(resolution.State),
                Provider = PlayTraceText.Reference(resolution.Provider),
                Route = ProblemRoute(resolution.Route) ?? play.Resolution.Route,
                AccountLabel = PlayTraceText.Clean(resolution.AccountLabel),
                Score = resolution.Score is { } score && double.IsFinite(score) ? Math.Clamp(score, 0, 1) : null
            };
            play = play with { Resolution = selected };
        }
    }
    public void Route(string source, bool fromCache, string? reason, string quality, string? scope, string? servingExternalId = null)
    {
        lock (sync)
        {
            if (closed || play == null) return;
            var selected = play.Resolution with
            {
                ExternalId = PlayTraceText.Reference(servingExternalId),
                Provider = PlayTraceText.Reference(source),
                Cached = fromCache,
                Route = ProblemRoute(reason) ?? play.Resolution.Route,
                Quality = PlayTraceText.Reference(quality),
                AccountLabel = scope == null ? "Account-free" : scope == "Personal" ? "Personal account" : scope == "Shared" ? "Shared account" : "Selected account"
            };
            play = play with { Resolution = selected };
        }
    }
    private static string? ProblemRoute(string? value) => allstarr.Core.Operations.ProblemReportText.Route(value);
    public void AccountLabel(string? value)
    {
        lock (sync) { if (!closed && play != null && value != null) play = play with { Resolution = play.Resolution with { AccountLabel = PlayTraceText.Clean(value) } }; }
    }
    public void Artwork(string? value)
    {
        lock (sync)
        {
            if (!closed && play != null && play.Request.Presented.Artwork != null)
                play = play with { Request = play.Request with { Presented = play.Request.Presented with { Artwork = value } } };
        }
    }
    public void Receive(TrackMediaFacts facts)
    {
        lock (sync) { if (!closed && play != null) play = play with { Received = PlayTraceText.Facts(facts) }; }
    }
    internal void ObserveMedia(Func<TrackMediaFacts?> read)
    {
        lock (sync) { if (!closed) received = read; }
    }
    public void Deliver(ProviderAudioDeliveryRequest? request, ProviderAudioConversion? conversion)
    {
        lock (sync)
        {
            if (closed || play == null) return;
            var delivery = new PlayTraceDelivery(request?.MaxBitrate, request?.MaxSampleRate,
                request?.MaxChannels, request?.MaxBitDepth, conversion != null, PlayTraceText.Reference(conversion?.Media.Container),
                PlayTraceText.Reference(conversion?.Media.Codec), request?.StartTimeTicks ?? 0,
                (request?.Formats ?? request?.Profiles ?? []).SelectMany(profile => profile.Containers).Select(PlayTraceText.Reference).OfType<string>().Distinct().Take(12).ToArray(),
                (request?.Formats ?? request?.Profiles ?? []).SelectMany(profile => profile.Codecs).Select(PlayTraceText.Reference).OfType<string>().Distinct().Take(12).ToArray(), conversion?.Media);
            play = play with { Delivery = delivery };
        }
    }
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
            if (!closed && received?.Invoke() is { } facts && play != null) play = play with { Received = PlayTraceText.Facts(facts) };
            if (complete) { closed = true; received = null; }
            return new(id, startedAt, outcome, provider ?? phases.LastOrDefault(phase => phase.Provider != null)?.Provider, title, artist, accountScope, cached,
                Elapsed, headers, firstByte, phases.OrderBy(item => item.StartedMs).ToArray(), omitted, play);
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
            row.ActorUserId = sample.Trace?.OwnerUserId;
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
