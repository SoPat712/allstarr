using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Capabilities;
using allstarr.Core.Storage;
using allstarr.Core.Protocols;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Playback;

[JsonConverter(typeof(JsonStringEnumConverter<PlaybackStartPhase>))]
public enum PlaybackStartPhase
{
    Route, Account, Lease, Source, Verify, Retry, Segments, Process, Headers, FirstByte, Preparation, Kept, Match,
    // Request: before the stream opener takes the request. Opening: the opener's own work before routing.
    // Selection: after the route is planned until a source is tried. Acceptance: taking on the source that answered.
    // Resolution: recording what was chosen. Delivery: conversion, caching and the response. Send: until the first byte.
    Request, Opening, Selection, Acceptance, Resolution, Delivery, Send
}

public sealed record PlaybackStartPhaseTiming(PlaybackStartPhase Phase, string? Provider, double StartedMs, double ElapsedMs);
public sealed record PlaybackStartSample(Guid Id, DateTimeOffset StartedAt, string Outcome, string? Provider,
    string? Title, string? Artist, string? AccountScope, bool Cached, double ElapsedMs,
    double? HeadersMs, double? FirstByteMs, IReadOnlyList<PlaybackStartPhaseTiming> Phases, int OmittedPhases, PlayTraceData? Trace = null,
    string? DeviceId = null, IReadOnlyList<PlaybackSourceStart>? Starts = null, int? Status = null, string? Code = null);

/// <summary>One start a service made, as the start-time memory learned it, kept so a restart can learn it again.</summary>
public sealed record PlaybackSourceStart(string Provider, Guid? Account, long? Revision,
    [property: JsonConverter(typeof(JsonStringEnumConverter<ProviderAudioQuality>))] ProviderAudioQuality Quality,
    DateTimeOffset At, double ElapsedMs, ProviderMediaFormat? Media)
{
    public const int MaximumPerRequest = 8;
}

public sealed class PlaybackStartTimings(TimeProvider clock) : IDisposable
{
    private static readonly AsyncLocal<PlaybackStartTimings?> Ambient = new();
    private readonly PlaybackStartTimings? previous = Ambient.Value;
    private readonly object sync = new();
    private readonly long started = clock.GetTimestamp();
    private readonly DateTimeOffset startedAt = clock.GetUtcNow();
    private Guid id = Guid.CreateVersion7();
    private readonly List<PlaybackStartPhaseTiming> phases = [];
    private bool closed;
    private int omitted;
    private string? provider, title, artist, accountScope;
    private bool cached;
    private string? deviceId;
    private PlayTraceData? play;
    private List<PlaybackSourceStart>? starts;
    private Func<TrackMediaFacts?>? received;
    private double? headers, firstByte, sending;
    private IDisposable? selecting;
    private Task pendingRecords = Task.CompletedTask;
    private int? status;
    private string? refusal, answerCode;
    private bool answered;
    public const int MaximumPhases = 96;
    public static PlaybackStartTimings? Current => Ambient.Value;
    public bool Enabled { get; private set; }
    public Guid Id => id;
    internal void Correlate(Guid playbackId) { if (!Enabled) id = playbackId; }
    private double Elapsed => Math.Max(0, clock.GetElapsedTime(started).TotalMilliseconds);

    public void Enter() => Ambient.Value = this;
    public void Enable() => Enabled = true;
    public IDisposable? Measure(PlaybackStartPhase phase, string? source = null) => Enabled ? new Measurement(this, phase, source, Elapsed) : null;
    /// <summary>Records a phase that began when the request did.</summary>
    public void FromStart(PlaybackStartPhase phase) { if (Enabled) Finish(phase, null, 0); }
    /// <summary>Opens the selection phase; it ends when a source is first tried, or when the returned scope ends.</summary>
    public IDisposable? Selecting()
    {
        var measure = Measure(PlaybackStartPhase.Selection);
        if (measure == null) return null;
        IDisposable? earlier;
        lock (sync) { earlier = selecting; selecting = measure; }
        earlier?.Dispose();
        return measure;
    }
    public void Selected()
    {
        IDisposable? measure;
        lock (sync) { measure = selecting; selecting = null; }
        measure?.Dispose();
    }
    /// <summary>The response is ready; the time until its first byte is the send phase.</summary>
    public void Sending() { lock (sync) { if (Enabled && !closed && !firstByte.HasValue) sending = Elapsed; } }
    /// <summary>Notes why a start was refused; the last refusal before the answer is the one kept.</summary>
    public void Refuse(string? code)
    {
        if (PlayTraceText.Reference(code) is not { Length: <= 100 } safe) return;
        lock (sync) { if (!closed) refusal = safe; }
    }
    /// <summary>A source opened after all, so refusals noted on the way were not the answer.</summary>
    internal void Recovered() { lock (sync) refusal = null; }
    /// <summary>The status the client was answered with, when known, and the code its error body carried.</summary>
    public void Answer(int? statusCode, string? code)
    {
        lock (sync)
        {
            if (closed) return;
            answered = true;
            status = statusCode;
            answerCode = PlayTraceText.Reference(code) is { Length: <= 100 } safe ? safe : null;
        }
    }
    /// <summary>Records written in the background for this start; the final record waits for them so it stays last.</summary>
    internal Task PendingRecords { get { lock (sync) return pendingRecords; } }
    internal void Recording(Task write) { lock (sync) pendingRecords = Task.WhenAll(pendingRecords, write); }
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
            title = Label(song.Title) ?? title; artist = Label(song.Artist) ?? artist;
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
            deviceId = context.Client.DeviceId;
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
    internal void SourceStarted(PlaybackSourceStart start)
    {
        if (!Enabled) return;
        lock (sync) { if (!closed && (starts ??= []).Count < PlaybackSourceStart.MaximumPerRequest) starts.Add(start); }
    }
    public void Headers() { lock (sync) { if (!closed) headers ??= Elapsed; } }
    public bool FirstByte()
    {
        lock (sync)
        {
            if (closed || firstByte.HasValue) return false;
            firstByte = Elapsed;
            if (sending is { } from)
            {
                if (phases.Count < MaximumPhases) phases.Add(new(PlaybackStartPhase.Send, null, from, Math.Max(0, firstByte.Value - from)));
                else omitted++;
            }
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
            var trace = play == null ? null : play with { Request = PlayTraceText.CatalogLabels(play.Request, title, artist) };
            // A refusal noted on the way is only the answer when the answer was not a success.
            var code = !answered ? null : answerCode ?? (status is null or >= 400 ? refusal : null);
            return new(id, startedAt, outcome, provider ?? phases.LastOrDefault(phase => phase.Provider != null)?.Provider, title, artist, accountScope, cached,
                Elapsed, headers, firstByte, phases.OrderBy(item => item.StartedMs).ToArray(), omitted, trace, deviceId, starts?.ToArray(),
                answered ? status : null, code);
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
    public const string OpeningOutcome = "opening";
    public const string StartedOutcome = "started";
    private static int Stage(string? outcome) => outcome switch { OpeningOutcome => 0, StartedOutcome => 1, _ => 2 };
    private readonly SemaphoreSlim writing = new(1, 1);

    internal static IQueryable<AuditEventRecord> RecentEvents(AllstarrDbContext db, DateTimeOffset now)
    {
        var cutoff = now - Retention;
        return db.AuditEvents.AsNoTracking().Where(item => item.Category == Category &&
            item.SubjectType == Category && item.SubjectId == "recent" && item.CreatedAt >= cutoff);
    }

    /// <summary>Writes a record away from the request, so the listener never waits for it; failures are only logged.</summary>
    public Task RecordInBackground(PlaybackStartSample sample) => Task.Run(() => RecordAsync(sample));

    public async Task RecordAsync(PlaybackStartSample sample)
    {
        var interim = Stage(sample.Outcome) < 2;
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
            // Records before the final one are written in the background, so one can land after a later record of the same start.
            else if (interim && Stage(row.Outcome) > Stage(sample.Outcome) && SameStart(row.DetailsJson, sample.StartedAt)) return;
            row.ActorUserId = sample.Trace?.OwnerUserId;
            row.Outcome = sample.Outcome;
            row.DetailsJson = JsonSerializer.Serialize(sample);
            await db.SaveChangesAsync(timeout.Token);
            if (interim)
            {
                await transaction.CommitAsync(timeout.Token);
                return;
            }
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

    private static bool SameStart(string? details, DateTimeOffset startedAt)
    {
        if (string.IsNullOrEmpty(details)) return false;
        try
        {
            using var document = JsonDocument.Parse(details);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty(nameof(PlaybackStartSample.StartedAt), out var value) &&
                value.TryGetDateTimeOffset(out var stored) && stored == startedAt;
        }
        catch (JsonException) { return false; }
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
