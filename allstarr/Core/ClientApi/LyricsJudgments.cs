using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using allstarr.Core.Music;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.ClientApi;

internal sealed record LyricsJudgedRich(string Format, string SourceId);

internal sealed record LyricsJudgedVersion(string? Source, LyricsJudgedRich? Rich, LyricsScore? Score);

internal sealed record LyricsJudgment(IReadOnlyList<LyricsJudgedVersion> Versions, LyricsJudgedRich? Rich);

internal sealed record RetainedLyricsJudgment(LyricsScore? Score, LyricsJudgedRich? Rich);

// Mapping and scoring a stored payload is the costly part of a lyric check, and its answer depends only on the
// payload and the song's length. Entries are keyed by a hash of the payload's full content, so a changed payload
// is a new entry and nothing here can go stale.
internal sealed class LyricsJudgments(int maximumEntries = 65_536, long maximumBytes = 32L * 1024 * 1024)
{
    public static LyricsJudgments Shared { get; } = new();
    public static LyricsJudgments Off { get; } = new(0, 0);

    private static readonly JsonSerializerOptions HashOptions = new() { Converters = { new ExactText() } };
    // The scorer reads only the song's length, so it is given a song that carries nothing else.
    private static readonly Song LengthOnly = new();
    private readonly object gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> recent = new();
    private long bytes;
    private long judged;
    private long reused;

    public bool Enabled => maximumEntries > 0 && maximumBytes > 0;
    public long Judged => Interlocked.Read(ref judged);
    public long Reused => Interlocked.Read(ref reused);

    public LyricsJudgment? Versions(string provider, LyricsInfo info, Song song, long? recordingDurationMs)
    {
        var length = LyricsQuality.Duration(song, recordingDurationMs);
        string key;
        try { key = Key("v", provider, length, info); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
        if (Find(key) is LyricsJudgment found) return found;
        try
        {
            // The listed versions are copied so a failed judgment leaves the stored lyrics as they were read.
            var copy = info.Copy();
            copy.Versions = info.Versions?.Select(version => version.Copy()).ToArray();
            copy.Source = provider;
            var versions = LyricsVersions.Candidates(provider, copy);
            foreach (var version in versions)
            {
                if (copy.Versions != null) version.Source = LyricsVersions.SourceId(provider, version.Metadata);
                if (version.Metadata?.DurationMs is { } milliseconds) version.Duration = (int)((milliseconds + 999) / 1000);
            }
            var judgment = new LyricsJudgment(versions.Select(version => new LyricsJudgedVersion(version.Source, Rich(version),
                LyricsQuality.Evaluate(version, LengthOnly, length))).ToArray(), Rich(copy));
            Interlocked.Increment(ref judged);
            Store(key, judgment, 96 + judgment.Versions.Sum(version => 128 + Size(version.Source) + Size(version.Rich) +
                8 * (version.Score?.Reasons.Count ?? 0)) + Size(judgment.Rich));
            return judgment;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    public RetainedLyricsJudgment? Retained(LyricsInfo lyrics, Song song, long? recordingDurationMs)
    {
        var length = LyricsQuality.Duration(song, recordingDurationMs);
        string key;
        try { key = Key("r", "", length, lyrics); }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
        if (Find(key) is RetainedLyricsJudgment found) return found;
        try
        {
            var judgment = new RetainedLyricsJudgment(LyricsQuality.Evaluate(lyrics, LengthOnly, length), Rich(lyrics));
            Interlocked.Increment(ref judged);
            Store(key, judgment, 128 + Size(judgment.Rich) + 8 * (judgment.Score?.Reasons.Count ?? 0));
            return judgment;
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private static LyricsJudgedRich? Rich(LyricsInfo lyrics) =>
        RichLyricsMapper.Map(lyrics) is { } rich ? new(rich.Format, rich.Source.Id) : null;

    private static string Key(string kind, string provider, long? length, LyricsInfo lyrics) =>
        kind + "\n" + provider + "\n" + length + "\n" + Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(lyrics, HashOptions)));

    private static int Size(string? value) => 24 + 2 * (value?.Length ?? 0);
    private static int Size(LyricsJudgedRich? rich) => rich == null ? 0 : 32 + Size(rich.SourceId);

    private object? Find(string key)
    {
        if (!Enabled) return null;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var node)) return null;
            recent.Remove(node);
            recent.AddLast(node);
            Interlocked.Increment(ref reused);
            return node.Value.Value;
        }
    }

    private void Store(string key, object value, long size)
    {
        if (!Enabled) return;
        size += 64 + 2L * key.Length;
        if (size > maximumBytes) return;
        lock (gate)
        {
            if (entries.Remove(key, out var existing)) Drop(existing);
            while (recent.First is { } oldest && (entries.Count >= maximumEntries || bytes + size > maximumBytes))
            {
                entries.Remove(oldest.Value.Key);
                Drop(oldest);
            }
            entries[key] = recent.AddLast(new Entry(key, value, size));
            bytes += size;
        }
    }

    private void Drop(LinkedListNode<Entry> node)
    {
        recent.Remove(node);
        bytes -= node.Value.Size;
    }

    private sealed record Entry(string Key, object Value, long Size);

    // JSON text replaces broken surrogates with U+FFFD, which would let two different payloads share one hash.
    // Text with any surrogate is hashed as its exact UTF-16 code units instead.
    private sealed class ExactText : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => throw new NotSupportedException();

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            if (value.AsSpan().IndexOfAnyInRange('\ud800', '\udfff') < 0)
            {
                writer.WriteStringValue(value);
                return;
            }
            writer.WriteStartArray();
            foreach (var unit in value) writer.WriteNumberValue(unit);
            writer.WriteEndArray();
        }
    }
}
