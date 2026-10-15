using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Models.Domain;
using allstarr.Models.Lyrics;

namespace allstarr.Core.Music;

public sealed record RecordingLyricsReviewSong(
    string Title,
    string Artist,
    string Album,
    int? DurationSeconds);

public sealed record RecordingLyricsReviewSource(
    string SourceId,
    string? Format,
    string? Preview,
    string? PreviewRevision,
    bool Truncated)
{
    public bool Available => PreviewRevision != null;
}

public sealed record RecordingLyricsReviewResult(
    Guid RecordingId,
    string Scope,
    RecordingLyricsReviewSong Song,
    RecordingLyricsChoiceLayers Choices,
    IReadOnlyList<RecordingLyricsReviewSource> Sources);

public interface IRecordingLyricsReview
{
    Task<RecordingLyricsReviewResult> GetAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        Func<string, CancellationToken, Task<LyricsInfo?>>? readBackend = null);

    Task<RecordingLyricsChoiceLayers> ChooseAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string sourceId,
        string scope,
        LyricsChoiceRevision expected,
        string previewRevision,
        Func<string, CancellationToken, Task<LyricsInfo?>>? readBackend = null);

    Task<RecordingLyricsChoiceLayers> ClearAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        LyricsChoiceRevision expected);
}

public sealed class RecordingLyricsReview : IRecordingLyricsReview
{
    private const int MaximumPreviewCharacters = 32_768;

    private readonly ITrackIdentityService _identities;
    private readonly IProtocolProviderGateway _providers;
    private readonly ILibraryIndexService _library;
    private readonly IProtocolLyricsResolver _lyrics;
    private readonly IRecordingLyricsChoices _choices;

    public RecordingLyricsReview(
        ITrackIdentityService identities,
        IProtocolProviderGateway providers,
        ILibraryIndexService library,
        IProtocolLyricsResolver lyrics,
        IRecordingLyricsChoices choices)
    {
        _identities = identities;
        _providers = providers;
        _library = library;
        _lyrics = lyrics;
        _choices = choices;
    }

    public Task<RecordingLyricsReviewResult> GetAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        Func<string, CancellationToken, Task<LyricsInfo?>>? readBackend = null) =>
        ReviewAsync(context, recordingId, scope, readBackend, onlySourceId: null);

    private async Task<RecordingLyricsReviewResult> ReviewAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        Func<string, CancellationToken, Task<LyricsInfo?>>? readBackend,
        string? onlySourceId)
    {
        var token = RequireContext(context);
        var reviewScope = ParseScope(scope);
        var actor = context.RequireActor();
        token.ThrowIfCancellationRequested();
        var recording = await _identities.ResolveRecordingAsync(
            actor,
            recordingId.ToString("D"),
            token);
        if (recording == null)
            throw new KeyNotFoundException("The canonical recording was not found.");

        var choices = await _choices.GetAsync(context, recording.Id);
        token.ThrowIfCancellationRequested();

        IndexedLibraryTrack? native = null;
        var nativeId = await _library.ResolveKnownTrackAsync(
            context,
            recording.PublicId,
            token);
        token.ThrowIfCancellationRequested();
        if (!string.IsNullOrWhiteSpace(nativeId))
        {
            var candidate = await _library.FindByBackendItemAsync(context, nativeId, token);
            token.ThrowIfCancellationRequested();
            if (candidate?.CanonicalRecordingId == recording.Id)
                native = candidate;
        }

        Song? providerSong = null;
        try
        {
            providerSong = await _providers.GetSongAsync(
                context,
                "allstarr",
                recording.Id.ToString("D"));
            token.ThrowIfCancellationRequested();
        }
        catch (HttpRequestException)
        {
            token.ThrowIfCancellationRequested();
        }

        if (providerSong?.CanonicalRecordingId != recording.Id)
            providerSong = null;

        var song = providerSong ?? (native == null ? null : BuildNativeSong(native, recording.Id));
        if (song == null)
            throw new KeyNotFoundException("Lyrics metadata for this recording is unavailable.");

        var reviewSong = ProjectSong(song);
        Func<CancellationToken, Task<LyricsInfo?>>? backend = null;
        if (native != null && readBackend != null)
        {
            var verifiedNativeId = native.BackendItemId;
            backend = token =>
            {
                token.ThrowIfCancellationRequested();
                return readBackend(verifiedNativeId, token);
            };
        }

        var candidates = await _lyrics.FindAllAsync(
            context,
            MusicLyrics.ForLookup(song),
            recording.Id.ToString("N"),
            new ProtocolLyricsSources(Backend: backend, HouseholdOnly: reviewScope == ReviewScope.Household)
            {
                OnlySourceId = onlySourceId
            });
        token.ThrowIfCancellationRequested();
        var sources = (candidates ?? Array.Empty<ProtocolLyricsCandidate>())
            .Select(candidate => ProjectSource(recording.Id, candidate))
            .ToArray();
        return new RecordingLyricsReviewResult(
            recording.Id,
            ScopeName(reviewScope),
            reviewSong,
            choices,
            sources);
    }

    public async Task<RecordingLyricsChoiceLayers> ChooseAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string sourceId,
        string scope,
        LyricsChoiceRevision expected,
        string previewRevision,
        Func<string, CancellationToken, Task<LyricsInfo?>>? readBackend = null)
    {
        var token = RequireContext(context);
        var reviewScope = ParseScope(scope);
        ArgumentNullException.ThrowIfNull(expected);
        token.ThrowIfCancellationRequested();
        // Revalidate only the chosen source so an unrelated slow or changing source cannot block the save.
        var preview = await ReviewAsync(context, recordingId, ScopeName(reviewScope), readBackend, sourceId);
        token.ThrowIfCancellationRequested();
        var source = preview.Sources.FirstOrDefault(item =>
            string.Equals(item.SourceId, sourceId, StringComparison.Ordinal));
        if (source == null || !source.Available ||
            !string.Equals(source.PreviewRevision, previewRevision, StringComparison.Ordinal))
        {
            throw new LyricsChoiceConflictException(
                "The lyrics preview is stale or unavailable. Reload it before choosing a source.");
        }

        return await _choices.SetAsync(
            context,
            preview.RecordingId,
            sourceId,
            ScopeName(reviewScope),
            expected);
    }

    public Task<RecordingLyricsChoiceLayers> ClearAsync(
        ProtocolExecutionContext context,
        Guid recordingId,
        string scope,
        LyricsChoiceRevision expected)
    {
        var token = RequireContext(context);
        var reviewScope = ParseScope(scope);
        token.ThrowIfCancellationRequested();
        return _choices.ClearAsync(context, recordingId, ScopeName(reviewScope), expected);
    }

    private static CancellationToken RequireContext(ProtocolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.CancellationToken;
    }

    private static ReviewScope ParseScope(string scope) => scope switch
    {
        "personal" => ReviewScope.Personal,
        "household" => ReviewScope.Household,
        _ => throw new ArgumentException(
            "The lyrics review scope must be 'personal' or 'household'.", nameof(scope))
    };

    private static string ScopeName(ReviewScope scope) =>
        scope == ReviewScope.Personal ? "personal" : "household";

    private static Song BuildNativeSong(IndexedLibraryTrack native, Guid recordingId) => new()
    {
        Id = native.BackendItemId,
        IsLocal = true,
        CanonicalRecordingId = recordingId,
        Title = native.Title,
        Artist = native.Artist,
        Artists = string.IsNullOrWhiteSpace(native.Artist) ? [] : [native.Artist],
        Album = native.Album ?? string.Empty,
        Duration = DurationSeconds(native.DurationMilliseconds),
        SpotifyId = native.ProviderTrackIds.GetValueOrDefault("spotify")
    };

    private static RecordingLyricsReviewSong ProjectSong(Song song) => new(
        song.Title,
        song.Artist,
        song.Album,
        DurationSeconds(song));

    private static int? DurationSeconds(Song song)
    {
        if (song.Duration is { } seconds)
            return Math.Clamp(seconds, 0, 86_400);
        if (song.PreciseDuration is not { } precise)
            return null;
        return DurationSeconds(precise.TotalMilliseconds);
    }

    private static int? DurationSeconds(long? milliseconds) => milliseconds is not { } value
        ? null
        : DurationSeconds((double)value);

    private static int DurationSeconds(double milliseconds)
    {
        if (milliseconds <= 0) return 0;
        var seconds = milliseconds / 1000d;
        if (seconds >= 86_400) return 86_400;
        return (int)Math.Round(seconds, MidpointRounding.AwayFromZero);
    }

    private static RecordingLyricsReviewSource ProjectSource(
        Guid recordingId,
        ProtocolLyricsCandidate candidate)
    {
        var lyrics = candidate.Lyrics;
        var (format, content) = lyrics switch
        {
            { SyncedLyrics: { } synced } when !string.IsNullOrWhiteSpace(synced) => ("synced", synced),
            { PlainLyrics: { } plain } when !string.IsNullOrWhiteSpace(plain) => ("plain", plain),
            _ => (null, null)
        };
        if (format == null || content == null)
            return new RecordingLyricsReviewSource(candidate.SourceId, null, null, null, false);

        var revisionInput = string.Concat(
            recordingId.ToString("D"), "\n", candidate.SourceId, "\n", format, "\n", content);
        var revision = "sha256:" + Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(revisionInput))).ToLowerInvariant();
        var preview = TruncatePreview(content, out var truncated);
        return new RecordingLyricsReviewSource(
            candidate.SourceId,
            format,
            preview,
            revision,
            truncated);
    }

    private static string TruncatePreview(string content, out bool truncated)
    {
        if (content.Length <= MaximumPreviewCharacters)
        {
            truncated = false;
            return content;
        }

        var length = MaximumPreviewCharacters;
        if (length > 0 && length < content.Length &&
            char.IsHighSurrogate(content[length - 1]) &&
            char.IsLowSurrogate(content[length]))
        {
            length--;
        }

        truncated = true;
        return content[..length];
    }

    private enum ReviewScope
    {
        Personal,
        Household
    }
}
