using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;

namespace allstarr.Core.Matching;

/// <summary>
/// Which service artists are the same as a library artist, proven by matched songs rather than names.
/// </summary>
public interface ILibraryArtistLinks
{
    Task<IReadOnlyList<LibraryArtistLink>> ReadAsync(Guid owner, string name, CancellationToken token);

    /// <summary>The last saved links for each name, keyed by the name as given; names never checked are absent.</summary>
    Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>>> ReadSavedAsync(Guid owner,
        IReadOnlyCollection<string> names, CancellationToken token);

    /// <summary>Queues background song matching for an artist with no links yet, at most once a day.</summary>
    Task RequestAsync(Guid owner, string name, CancellationToken token);
}

public sealed record LibraryArtistLinkPayload(Guid UserId, string Name);

public sealed class LibraryArtistLinks(LibraryArtistIndex index, DurableJobQueue jobs, IPlatformClock clock) : ILibraryArtistLinks
{
    public const string JobType = "library-artist.link";

    public Task<IReadOnlyList<LibraryArtistLink>> ReadAsync(Guid owner, string name, CancellationToken token) =>
        index.ReadAsync(owner, name, token);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>>> ReadSavedAsync(Guid owner,
        IReadOnlyCollection<string> names, CancellationToken token) => index.ReadSavedAsync(owner, names, token);

    public async Task RequestAsync(Guid owner, string name, CancellationToken token)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is 0 or > 300 || trimmed.Any(char.IsControl)) return;
        var key = $"{CanonicalCatalogKeys.Hash(trimmed)}:{clock.UtcNow:yyyy-MM-dd}";
        await jobs.EnqueueAsync(new DurableJobEnqueueRequest<LibraryArtistLinkPayload>(
            JobType, key, new(owner, trimmed), owner, Priority: -50, CorrelationId: "library-artist-link"), token);
    }
}

public sealed class LibraryArtistLinkJobHandler(LibraryArtistIndex index) : IDurableJobHandler
{
    public string JobType => LibraryArtistLinks.JobType;

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext execution, CancellationToken cancellationToken)
    {
        LibraryArtistLinkPayload? payload;
        try { payload = execution.Claim.Payload.Deserialize<LibraryArtistLinkPayload>(); }
        catch (JsonException) { payload = null; }
        if (payload?.UserId is not { } user || user == Guid.Empty || execution.Claim.OwnerUserId != user ||
            string.IsNullOrWhiteSpace(payload.Name))
            return DurableJobCompletion.Failure("library_artist_link_invalid", "The saved artist linking request is invalid.");
        await index.BackfillAsync(user, payload.Name, execution.Claim.JobId, cancellationToken);
        return DurableJobCompletion.Success();
    }
}
