using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Identity;
using allstarr.Core.Jobs;
using allstarr.Core.Matching;
using allstarr.Core.Operations;
using allstarr.Core.Protocols;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed record SpotifyLikesImportStatus(Guid JobId, string State, int Processed, int? Total,
    int Imported, int AlreadyHearted, int NeedsReview, int NotFound, string? ErrorCode);

internal sealed record SpotifyLikesImportPayload(string Protocol, string BackendInstanceId, string BackendPrincipalId)
{
    public string? Cursor { get; set; }
    public string? SourceRevision { get; set; }
    public string? PageHash { get; set; }
    public int Index { get; set; }
    public int Pages { get; set; }
    public int? Total { get; set; }
    public int Imported { get; set; }
    public int AlreadyHearted { get; set; }
    public int NeedsReview { get; set; }
    public int NotFound { get; set; }
    public bool Complete { get; set; }
    public int Processed => Imported + AlreadyHearted + NeedsReview + NotFound;
}

public sealed class SpotifyLikesImport(
    IDbContextFactory<AllstarrDbContext> factory,
    DurableJobQueue jobs,
    IProviderRouter router,
    TrackMatchPlayableSearch matches,
    FavoriteActionPipeline favorites,
    JobPayloadPolicy payloadPolicy,
    IPlatformClock clock) : IDurableJobHandler
{
    public const string Type = "spotify-likes.import";
    public string JobType => Type;
    private const int PageSize = 100;
    private static readonly ProviderExternalResourceId Likes = new("spotify", ProviderResourceKind.Playlist, SpotifyPlaylistReference.LikedSongsId);

    public async Task<DurableJobEnqueueResult> StartAsync(ProtocolExecutionContext execution, Guid accountId,
        Guid requestId, CancellationToken token = default)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A request ID is required.");
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await RequireUserAsync(db, execution, token);
        await RequireAccountAsync(db, user.Id, accountId, token);
        return await jobs.EnqueueAsync(new DurableJobEnqueueRequest<SpotifyLikesImportPayload>(Type,
            requestId.ToString("N"), new(user.BackendType, user.BackendInstanceId, user.BackendPrincipalId), user.Id,
            ProviderAccountId: accountId, Capability: "playlist", CorrelationId: execution.CorrelationId), token);
    }

    public async Task<SpotifyLikesImportStatus?> GetAsync(ProtocolExecutionContext execution, Guid jobId,
        CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await RequireUserAsync(db, execution, token);
        var job = await db.Jobs.AsNoTracking().SingleOrDefaultAsync(item => item.Id == jobId &&
            item.Type == Type && item.OwnerUserId == user.Id, token);
        if (job == null) return null;
        var saved = JsonSerializer.Deserialize<SpotifyLikesImportPayload>(job.PayloadJson)!;
        if (!SameListener(saved, user)) return null;
        return new(job.Id, job.State.ToString(), saved.Processed, saved.Total,
            saved.Imported, saved.AlreadyHearted, saved.NeedsReview, saved.NotFound, job.LastErrorCode);
    }

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken token)
    {
        var claim = context.Claim;
        var saved = claim.Payload.Deserialize<SpotifyLikesImportPayload>();
        if (claim.Type != Type || saved == null || claim.OwnerUserId == null || claim.ProviderAccountId == null)
            return DurableJobCompletion.Failure("spotify_likes_invalid", "The import request is invalid.");
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == claim.OwnerUserId && item.Enabled, token)
                ?? throw new UnauthorizedAccessException();
            if (!SameListener(saved, user)) throw new UnauthorizedAccessException();
            while (!saved.Complete)
            {
                token.ThrowIfCancellationRequested();
                await SaveAsync(claim, saved, token);
                var account = await RequireAccountAsync(db, user.Id, claim.ProviderAccountId.Value, token);
                if (claim.PolicySnapshot.Deserialize<DurableJobPolicySnapshot>()?.ProviderAccountRevision != account.Revision)
                    throw new UnauthorizedAccessException();
                var execution = Execution(user, claim, token);
                var policy = new ProviderExecutionPolicy(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false),
                    ProviderExplicitContentPolicy.Allow, false, false, false, ["spotify"]);
                var plan = await router.PlanAsync<IProviderPlaylistCapability>(new(ProviderCapabilityKind.Playlist,
                    execution.RequireActor(), policy, "spotify-likes-import", claim.CorrelationId, execution.Deadline,
                    ["spotify"], [new("spotify", requestedAccountId: account.Id, expectedAccountRevision: account.Revision)],
                    cancellationToken: token));
                var source = plan.Candidates.FirstOrDefault();
                if (source == null) return DurableJobCompletion.Retry("spotify_likes_source_unavailable", "The selected source is unavailable. Retry after restoring its connection.");
                var outcome = await source.Implementation.GetPlaylistTracksAsync(source.Context,
                    new(Likes, new(PageSize, saved.Cursor), saved.SourceRevision));
                if (!outcome.IsSuccess)
                    return outcome.Error!.Kind is ProviderErrorKind.PermanentFailure or ProviderErrorKind.Forbidden
                        ? DurableJobCompletion.Failure("spotify_likes_read_failed", "The source could not be read. Start a new import after checking its connection.")
                        : DurableJobCompletion.Retry("spotify_likes_read_failed", "The source could not be read. Completed hearts are saved.");
                var page = outcome.RequireValue();
                ValidatePage(saved, page);
                saved.Total = page.Playlist.TrackCount;
                saved.SourceRevision = page.Playlist.SourceRevision;
                saved.PageHash = Hash(page);
                await SaveAsync(claim, saved, token);
                await ProgressAsync(context, saved, token);
                while (saved.Index < page.Tracks.Items.Count)
                {
                    token.ThrowIfCancellationRequested();
                    var track = page.Tracks.Items[saved.Index];
                    var revision = $"spotify-like:{account.Id:N}:{track.TrackId.Value}";
                    var receipt = await db.FavoriteEvents.AsNoTracking().FirstOrDefaultAsync(item => item.OwnerUserId == user.Id &&
                        item.Protocol == saved.Protocol && item.BackendInstanceId == saved.BackendInstanceId &&
                        item.BackendPrincipalId == saved.BackendPrincipalId && item.SourceRevision == revision, token);
                    if (receipt != null)
                    {
                        if (receipt.CorrelationId == claim.JobId.ToString("N")) saved.Imported++;
                        else saved.AlreadyHearted++;
                    }
                    else if (track.Metadata is not { } metadata) saved.NotFound++;
                    else
                    {
                        execution = Execution(user, claim, token);
                        var match = await matches.MatchAsync(execution, new(track.TrackId.Value, "spotify", track.TrackId.Value,
                            metadata.Title, string.Join(", ", metadata.Artists.Select(item => item.Name)), metadata.AlbumTitle,
                            metadata.AlbumArtist, metadata.Duration is { } duration ? (long)duration.TotalMilliseconds : null,
                            metadata.Isrc, null, metadata.IsExplicit, track.CanonicalRecordingId),
                            new(user.Id, user.BackendInstanceId, account.Id, 2, 1, new HashSet<string>()), [], null, token);
                        if (match.Decision.State is TrackMatchReviewState.Accepted or TrackMatchReviewState.Pinned &&
                            !match.Decision.RequiresReview && match.SelectedExternal is { } selected)
                        {
                            // Check the claim again before recording intent; receipt recovery covers a lost checkpoint.
                            await SaveAsync(claim, saved, token);
                            if (await favorites.ImportSpotifyLikeAsync(execution,
                                $"ext-{selected.ExternalProvider}-song-{selected.ExternalId}", revision, token)) saved.Imported++;
                            else saved.AlreadyHearted++;
                        }
                        else if (match.Decision.Candidates.Count > 0) saved.NeedsReview++;
                        else saved.NotFound++;
                    }
                    saved.Index++;
                    await SaveAsync(claim, saved, token);
                    if (saved.Index % 10 == 0) await ProgressAsync(context, saved, token);
                }
                saved.Pages++;
                saved.Cursor = page.Tracks.NextCursor;
                saved.Index = 0;
                saved.PageHash = null;
                saved.Complete = saved.Cursor == null;
                if (saved.Complete && saved.Total != saved.Processed) throw new InvalidDataException();
                await SaveAsync(claim, saved, token);
                await ProgressAsync(context, saved, token);
            }
            return DurableJobCompletion.Success();
        }
        catch (DurableJobExecutionChangedException) { return DurableJobCompletion.Cancelled(); }
        catch (UnauthorizedAccessException) { return DurableJobCompletion.Failure("spotify_likes_unauthorized", "The listener or selected connection changed. Start a new import."); }
        catch (InvalidDataException) { return DurableJobCompletion.Failure("spotify_likes_source_changed", "The source changed or returned an incomplete page. Completed hearts are saved; start a new import."); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception) { return DurableJobCompletion.Retry("spotify_likes_interrupted", "The import was interrupted. Completed hearts are saved."); }
    }

    private async Task SaveAsync(DurableJobClaim claim, SpotifyLikesImportPayload saved, CancellationToken token)
    {
        var json = payloadPolicy.SerializeAndValidate(saved);
        await using var db = await factory.CreateDbContextAsync(token);
        if (await db.Jobs.Where(item => item.Id == claim.JobId && item.Type == Type && item.OwnerUserId == claim.OwnerUserId &&
                item.State == DurableJobState.Running && item.AttemptCount == claim.AttemptNumber && item.CancellationRequestedAt == null)
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.PayloadJson, json)
                .SetProperty(item => item.UpdatedAt, clock.UtcNow).SetProperty(item => item.Revision, item => item.Revision + 1), token) != 1)
            throw new DurableJobExecutionChangedException();
    }

    private static async Task ProgressAsync(DurableJobExecutionContext context, SpotifyLikesImportPayload saved, CancellationToken token)
    {
        if (!await context.ReportProgressAsync(new("spotify-likes", "Importing Spotify likes as hearts.", saved.Processed, saved.Total, "spotify"), token))
            throw new DurableJobExecutionChangedException();
    }

    private static void ValidatePage(SpotifyLikesImportPayload saved, ProviderPlaylistTrackPage page)
    {
        var offset = saved.Cursor == null ? 0 : int.Parse(saved.Cursor, System.Globalization.CultureInfo.InvariantCulture);
        if (saved.Pages >= 1000 || page.Playlist.Id != Likes || page.Playlist.TrackCount is not (>= 0 and <= 100_000) ||
            page.Tracks.Items.Count > PageSize || saved.SourceRevision != null && saved.SourceRevision != page.Playlist.SourceRevision ||
            saved.Total != null && saved.Total != page.Playlist.TrackCount || saved.PageHash != null && saved.PageHash != Hash(page) ||
            !page.Tracks.Items.Select(item => item.Position).SequenceEqual(Enumerable.Range(offset, page.Tracks.Items.Count)) ||
            page.Tracks.Items.Select(item => item.TrackId).Distinct().Count() != page.Tracks.Items.Count ||
            page.Tracks.NextCursor == null && (page.Tracks.IsPartial || offset + page.Tracks.Items.Count != page.Playlist.TrackCount) ||
            page.Tracks.NextCursor != null && (!int.TryParse(page.Tracks.NextCursor, out var next) || next <= offset || next != offset + page.Tracks.Items.Count))
            throw new InvalidDataException();
    }

    private static string Hash(ProviderPlaylistTrackPage page) => Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(JsonSerializer.Serialize(page.Tracks.Items.Select(item => new
        {
            item.Position,
            item.TrackId,
            item.CanonicalRecordingId,
            Title = item.Metadata?.Title,
            Artists = item.Metadata?.Artists,
            Album = item.Metadata?.AlbumTitle,
            AlbumArtist = item.Metadata?.AlbumArtist,
            Duration = item.Metadata?.Duration,
            Isrc = item.Metadata?.Isrc,
            IsExplicit = item.Metadata?.IsExplicit
        })))));

    private ProtocolExecutionContext Execution(UserRecord user, DurableJobClaim claim, CancellationToken token) => new(
        user.BackendType == "jellyfin" ? ProtocolKind.Jellyfin : ProtocolKind.Subsonic, user.BackendInstanceId, user.BackendPrincipalId,
        new AllstarrPrincipal(user.Id, user.BackendType, user.BackendInstanceId, user.BackendPrincipalId, user.DisplayName, user.IsAdmin),
        claim.JobId.ToString("N"), clock.UtcNow.AddMinutes(5), token);

    private static bool SameListener(SpotifyLikesImportPayload saved, UserRecord user) => saved.Protocol == user.BackendType &&
        saved.BackendInstanceId == user.BackendInstanceId && saved.BackendPrincipalId == user.BackendPrincipalId;

    private static async Task<UserRecord> RequireUserAsync(AllstarrDbContext db, ProtocolExecutionContext execution, CancellationToken token) =>
        await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == execution.RequireActor().EffectiveUserId && item.Enabled &&
            item.BackendType == execution.Protocol.ToString().ToLowerInvariant() && item.BackendInstanceId == execution.BackendInstanceId &&
            item.BackendPrincipalId == execution.VerifiedBackendPrincipalId, token) ?? throw new UnauthorizedAccessException();

    private static async Task<ProviderAccountRecord> RequireAccountAsync(AllstarrDbContext db, Guid owner, Guid accountId, CancellationToken token) =>
        await db.ProviderAccounts.AsNoTracking().SingleOrDefaultAsync(item => item.Id == accountId && item.Enabled && item.ProviderId == "spotify" &&
            item.OwnerUserId == owner, token) ?? throw new UnauthorizedAccessException();
}
