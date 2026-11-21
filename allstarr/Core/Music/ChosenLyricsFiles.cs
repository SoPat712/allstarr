using System.Text;
using System.Text.Json;
using allstarr.Core.ClientEvents;
using allstarr.Core.Enrichment;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using allstarr.Models.Lyrics;
using allstarr.Models.Settings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Music;

public sealed record ChosenLyricsFilePayload(string BackendItemId, string BackendPath, Guid RecordingId, string SourceId, string Lyrics);

/// <summary>Hands a Jellyfin server the lyrics of one of its songs, in place of the ones it holds.</summary>
public sealed class JellyfinItemLyrics
{
    private readonly HttpClient _http;
    private readonly JellyfinSettings _settings;
    public JellyfinItemLyrics(IHttpClientFactory clients, IOptions<JellyfinSettings> settings)
        : this(clients.CreateClient(JellyfinLibraryRefresher.HttpClientName), settings.Value) { }
    public JellyfinItemLyrics(HttpClient http, JellyfinSettings settings) => (_http, _settings) = (http, settings);

    public async Task ReplaceAsync(string itemId, string lyrics, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(_settings.Url) || string.IsNullOrWhiteSpace(_settings.ApiKey))
            throw new InvalidOperationException("Jellyfin lyrics are not configured.");
        var synced = LyricsTiming.HasLineTimes(lyrics);
        var address = new Uri(new Uri(_settings.Url.TrimEnd('/') + "/"), $"Audio/{Uri.EscapeDataString(itemId)}/Lyrics");
        // The server keeps one file per format. Timed lines replace the timed file it has; plain text would be kept
        // beside it, so what it has goes first.
        if (!synced)
        {
            using var removed = await SendAsync(new(HttpMethod.Delete, address), token);
            if (removed.StatusCode != System.Net.HttpStatusCode.NotFound) removed.EnsureSuccessStatusCode();
        }
        using var saved = await SendAsync(new(HttpMethod.Post, new Uri(address + (synced ? "?fileName=lyrics.lrc" : "?fileName=lyrics.txt")))
        {
            Content = new StringContent(lyrics, Encoding.UTF8, "text/plain")
        }, token);
        saved.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token)
    {
        using (message)
        {
            message.Headers.TryAddWithoutValidation("Authorization",
                AuthHeaderHelper.CreateAuthHeader(_settings.ApiKey!, "Allstarr", "Server", "allstarr-server", AppVersion.Version));
            return await _http.SendAsync(message, token);
        }
    }
}

/// <summary>
/// Puts the lyrics version an administrator chose for a library song into the song's own file, in the tags an added
/// song gets, and hands the media server the same lines, so every listener and every other app gets that version.
/// Only when the household turned this on.
/// </summary>
public sealed class ChosenLyricsFiles(IDurableRuntimeSettings settings, IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue queue,
    LibraryFileLyrics library, IManagedTagFileMutator mutator, IManagedFileOperations files, JellyfinItemLyrics jellyfin,
    StorageOptions storage, TimeProvider time, ILogger<ChosenLyricsFiles> logger, IClientEventChanges? events = null) : IDurableJobHandler
{
    public const string JobType = "lyrics.write-file";
    public const string SettingKey = "Lyrics:WriteChosenToFile";
    string IDurableJobHandler.JobType => JobType;

    public async Task RequestAsync(ProtocolExecutionContext execution, string backendItemId, string? backendPath, Guid recordingId,
        string sourceId, LyricsInfo lyrics, long? durationMs)
    {
        var token = execution.CancellationToken;
        // The library's own version is what the file holds already.
        if (execution.Principal is not { IsAdministrator: true } principal || string.IsNullOrWhiteSpace(backendPath) ||
            LyricsVersions.ProviderId(sourceId) == "backend" || !(bool)(await settings.GetAsync(SettingKey, token)).Value) return;
        var text = LyricsTiming.SidecarContent(lyrics, durationMs);
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            await queue.EnqueueAsync(new DurableJobEnqueueRequest<ChosenLyricsFilePayload>(JobType, $"lyrics-file:{Guid.CreateVersion7():N}",
                new(backendItemId, backendPath, recordingId, sourceId, text), principal.UserId, CorrelationId: execution.CorrelationId), token);
        }
        // Work that is queued may not look like it carries a password, and a lyric can. The choice stands without the file.
        catch (Exception error) when (error is InvalidOperationException or ArgumentOutOfRangeException)
        {
            logger.LogWarning("Chosen lyrics could not be queued for a song file ({ErrorType})", error.GetType().Name);
        }
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        ChosenLyricsFilePayload? payload;
        try { payload = context.Claim.Payload.Deserialize<ChosenLyricsFilePayload>(); } catch (JsonException) { payload = null; }
        if (payload == null || string.IsNullOrWhiteSpace(payload.BackendItemId) || string.IsNullOrWhiteSpace(payload.BackendPath) ||
            string.IsNullOrWhiteSpace(payload.Lyrics) || context.Claim.OwnerUserId is not { } owner)
            return DurableJobCompletion.Failure("lyrics_file_payload_invalid", "The lyrics file request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == owner && item.Enabled && item.IsAdmin, cancellationToken);
        if (user == null || !(bool)(await settings.GetAsync(SettingKey, cancellationToken)).Value)
            return DurableJobCompletion.Failure("lyrics_file_not_allowed", "Saving chosen lyrics to song files is off, or the listener is not an administrator.");
        var path = await library.LocalPathAsync(payload.BackendPath, cancellationToken);
        if (path == null)
            return DurableJobCompletion.Failure("lyrics_file_unreachable", "No music folder Allstarr knows holds this song's file.");

        string? kept;
        try
        {
            kept = await WriteAsync(path, payload, cancellationToken);
        }
        catch (Exception error) when (error is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or InvalidDataException or UnauthorizedAccessException)
        {
            logger.LogWarning("Chosen lyrics could not be written to a song file ({ErrorType})", error.GetType().Name);
            return DurableJobCompletion.Failure("lyrics_file_write_failed", "The song's file could not take the chosen lyrics.");
        }
        catch (IOException error)
        {
            logger.LogWarning("Chosen lyrics could not be written to a song file ({ErrorType})", error.GetType().Name);
            return DurableJobCompletion.Retry("lyrics_file_busy", "The song's file could not be rewritten just now.");
        }
        if (kept != null)
        {
            db.AuditEvents.Add(new AuditEventRecord
            {
                Id = Guid.CreateVersion7(),
                ActorUserId = user.Id,
                Category = "lyrics",
                Action = "file.written",
                Outcome = "succeeded",
                SubjectType = "library-item",
                SubjectId = payload.BackendItemId,
                CorrelationId = context.Claim.CorrelationId,
                DetailsJson = JsonSerializer.Serialize(new { canonicalRecordingId = payload.RecordingId, sourceId = payload.SourceId, previous = kept }),
                CreatedAt = time.GetUtcNow()
            });
            await db.SaveChangesAsync(cancellationToken);
        }
        if (user.BackendType == "jellyfin")
        {
            try { await jellyfin.ReplaceAsync(payload.BackendItemId, LyricsTiming.TagContent(payload.Lyrics), cancellationToken); }
            catch (Exception error) when (error is HttpRequestException or InvalidOperationException)
            {
                logger.LogWarning("The media server did not take the chosen lyrics ({ErrorType})", error.GetType().Name);
                return DurableJobCompletion.Retry("lyrics_file_server_unavailable", "The file holds the chosen lyrics; the media server has not taken them yet.");
            }
        }
        if (events != null) await events.LyricsAsync(payload.RecordingId, null);
        return DurableJobCompletion.Success();
    }

    internal static Dictionary<string, string> Tags(string lyrics) => new()
    {
        // As an added song has them: line times in the tag everything reads, word times in a tag of their own.
        ["lyrics"] = LyricsTiming.TagContent(lyrics),
        ["wordlyrics"] = LyricsTiming.HasWordTimes(lyrics) ? lyrics : ""
    };

    // Returns where the lyrics the file held before were kept, "" when it held none, or null when it already holds these.
    private async Task<string?> WriteAsync(string path, ChosenLyricsFilePayload payload, CancellationToken token)
    {
        var file = new FileInfo(path);
        if (!file.Exists) throw new FileNotFoundException("The song's file is gone.");
        var tags = Tags(payload.Lyrics);
        if (mutator.Matches(path, tags)) return null;
        var (length, modified) = (file.Length, file.LastWriteTimeUtc);
        var kept = await KeepPreviousAsync(path, payload, token);
        // The changed copy takes the file's place in one step, so a reader never meets a half-written song. Its name
        // is not a song's, so a library scan passing by leaves it alone.
        var type = "taglib/" + file.Extension.TrimStart('.').ToLowerInvariant();
        var staging = Path.Combine(file.DirectoryName!, $".{file.Name}.{Guid.NewGuid():N}.partial");
        try
        {
            await files.CopyAsync(path, staging, token);
            using (var audio = TagLib.File.Create(staging, type, TagLib.ReadStyle.Average))
            {
                TagLibManagedTagFileMutator.ApplyTags(audio.Tag, tags);
                audio.Save();
            }
            using (var written = new FileStream(staging, FileMode.Open, FileAccess.ReadWrite)) written.Flush(flushToDisk: true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(staging, File.GetUnixFileMode(path));
                if (LibraryFileOwner.TryOwner(path, out var owner)) _ = LibraryFileOwner.LChown(staging, owner.User, owner.Group);
            }
            token.ThrowIfCancellationRequested();
            file.Refresh();
            if (!file.Exists || file.Length != length || file.LastWriteTimeUtc != modified)
                throw new IOException("The song's file changed while its lyrics were being written.");
            File.Move(staging, path, overwrite: true);
            return kept;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    private async Task<string> KeepPreviousAsync(string path, ChosenLyricsFilePayload payload, CancellationToken token)
    {
        string? lines;
        using (var audio = TagLib.File.Create(path, TagLib.ReadStyle.PictureLazy)) lines = audio.Tag.Lyrics;
        var words = TagLibManagedTagFileMutator.ReadWordLyrics(path);
        if (string.IsNullOrEmpty(lines) && string.IsNullOrEmpty(words)) return "";
        var directory = Path.Combine(Path.GetFullPath(storage.DataDirectory), "replaced-lyrics");
        Directory.CreateDirectory(directory);
        var name = $"{time.GetUtcNow():yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
        await File.WriteAllTextAsync(Path.Combine(directory, name), JsonSerializer.Serialize(new
        {
            path,
            backendItemId = payload.BackendItemId,
            replacedBy = payload.SourceId,
            lyrics = lines,
            wordLyrics = words
        }), token);
        return name;
    }
}
