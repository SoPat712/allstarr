using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Models.Domain;

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

    /// <summary>The links matched songs prove and the fixes people made, kept apart.</summary>
    Task<(IReadOnlyList<LibraryArtistLink> Songs, IReadOnlyList<LibraryArtistFix> Fixes)> ReadPartsAsync(Guid owner, string name,
        CancellationToken token);

    /// <summary>Saves a person's "same artist" (true) or "not the same" (false) answer; null takes it back.</summary>
    Task<bool> SetFixAsync(Guid owner, string name, string providerId, string artistId, bool? same, CancellationToken token);

    Task RecordCandidatesAsync(Guid owner, IReadOnlyCollection<string> names, IReadOnlyCollection<Artist> artists,
        CancellationToken token);

    /// <summary>Song links, shared-album links and people's answers, each on its own.</summary>
    async Task<LibraryArtistProof> ReadProofAsync(Guid owner, string name, CancellationToken token)
    {
        var (songs, fixes) = await ReadPartsAsync(owner, name, token);
        return new(songs, [], fixes);
    }

    /// <summary>Same-named service artists nothing ties to the library artist yet, for a person to decide.</summary>
    Task<IReadOnlyList<LibraryArtistEvidence>> ReadCandidatesAsync(Guid owner, string name, CancellationToken token) =>
        Task.FromResult<IReadOnlyList<LibraryArtistEvidence>>([]);

    /// <summary>Ties a same-named service artist that shares an album with the library artist; true when they are tied.</summary>
    Task<bool> ProveByAlbumsAsync(Guid owner, string name, string providerId, string artistId, IReadOnlyCollection<Album> albums,
        CancellationToken token) => Task.FromResult(false);

    /// <summary>Checks the same-named service artists just shown against the library's albums, in the background's own time.</summary>
    Task ProveCandidatesAsync(Guid owner, IReadOnlyCollection<string> names, IReadOnlyCollection<Artist> artists, CancellationToken token) =>
        Task.CompletedTask;

    /// <summary>Looks the name up on the listener's services, records who answers to it and checks their albums; at most once every few hours.</summary>
    Task DiscoverAsync(Guid owner, string name, CancellationToken token) => Task.CompletedTask;
}

public sealed record LibraryArtistLinkPayload(Guid UserId, string Name);

public sealed class LibraryArtistLinks(LibraryArtistIndex index, DurableJobQueue jobs, IPlatformClock clock) : ILibraryArtistLinks
{
    public const string JobType = "library-artist.link";

    public Task<IReadOnlyList<LibraryArtistLink>> ReadAsync(Guid owner, string name, CancellationToken token) =>
        index.ReadAsync(owner, name, token);

    public Task<IReadOnlyDictionary<string, IReadOnlyList<LibraryArtistLink>>> ReadSavedAsync(Guid owner,
        IReadOnlyCollection<string> names, CancellationToken token) => index.ReadSavedAsync(owner, names, token);

    public Task<(IReadOnlyList<LibraryArtistLink> Songs, IReadOnlyList<LibraryArtistFix> Fixes)> ReadPartsAsync(Guid owner, string name,
        CancellationToken token) => index.ReadPartsAsync(owner, name, token);

    public Task<bool> SetFixAsync(Guid owner, string name, string providerId, string artistId, bool? same, CancellationToken token) =>
        index.SetFixAsync(owner, name, providerId, artistId, same, token);

    public Task RecordCandidatesAsync(Guid owner, IReadOnlyCollection<string> names, IReadOnlyCollection<Artist> artists,
        CancellationToken token) => index.RecordCandidatesAsync(owner, names, artists, token);

    public Task<LibraryArtistProof> ReadProofAsync(Guid owner, string name, CancellationToken token) => index.ReadProofAsync(owner, name, token);

    public async Task<IReadOnlyList<LibraryArtistEvidence>> ReadCandidatesAsync(Guid owner, string name, CancellationToken token) =>
        (await index.ReadReviewAsync(owner, [name], token)).GetValueOrDefault(name)?.Candidates ?? [];

    public Task<bool> ProveByAlbumsAsync(Guid owner, string name, string providerId, string artistId, IReadOnlyCollection<Album> albums,
        CancellationToken token) => index.ProveByAlbumsAsync(owner, name, providerId, artistId, albums, token);

    public Task ProveCandidatesAsync(Guid owner, IReadOnlyCollection<string> names, IReadOnlyCollection<Artist> artists, CancellationToken token) =>
        index.ProveCandidatesAsync(owner, names, artists, token);

    public Task DiscoverAsync(Guid owner, string name, CancellationToken token) => index.DiscoverAsync(owner, name, token);

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
