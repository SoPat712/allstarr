using allstarr.Core.Matching;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace allstarr.Core.Music;

/// <summary>
/// The library's own spelling of a search typed without its spaces or punctuation: "cybersex" finds "Cyber Sex",
/// "acdc" finds "AC/DC", "dont" finds "Don't". The media server's own search can't split a joined word, so search asks
/// it again with the spelling found here.
/// </summary>
public interface ILibrarySpellings
{
    /// <summary>The library's spelling of the query when it differs only by spaces and punctuation, otherwise null.</summary>
    Task<string?> RespellAsync(ProtocolExecutionContext context, string query, CancellationToken token);
}

public sealed class LibrarySpellings(IDbContextFactory<AllstarrDbContext> factory, IBackendLibraryAccessResolver libraryAccess,
    ILogger<LibrarySpellings>? logger = null) : ILibrarySpellings
{
    private const int MinimumLength = 3, Scanned = 500;

    public async Task<string?> RespellAsync(ProtocolExecutionContext context, string query, CancellationToken token)
    {
        var compact = Compact(query);
        if (compact.Length < MinimumLength || context.Actor?.EffectiveUserId is not { } owner) return null;
        var access = await libraryAccess.ResolveUserAsync(owner, token);
        if (access.Context == null || !access.Access.Succeeded) return null;
        await using var db = await factory.CreateDbContextAsync(token);
        var tracks = LibraryTrackAccess.Query(db, access);
        // SQLite keeps names that start with the query's letters in order, with anything between them ("c%y%b%e%r%s%e%x%"),
        // which leaves only a handful even in a large library; the exact comparison happens here.
        var letters = compact.Where(char.IsLetterOrDigit).ToArray();
        if (letters.Length < MinimumLength) return null;
        var pattern = string.Join("%", letters) + "%";
        var titles = await tracks.Where(track => EF.Functions.Like(track.Title, pattern)).Select(track => track.Title).Distinct().Take(Scanned).ToArrayAsync(token);
        var artists = await tracks.Where(track => EF.Functions.Like(track.Artist, pattern)).Select(track => track.Artist).Distinct().Take(Scanned).ToArrayAsync(token);
        var albums = await tracks.Where(track => track.Album != null && EF.Functions.Like(track.Album, pattern)).Select(track => track.Album!)
            .Distinct().Take(Scanned).ToArrayAsync(token);
        string? Exact(IEnumerable<string> names) => names.FirstOrDefault(name => Compact(name) == compact);
        string? Prefix(IEnumerable<string> names) => names.FirstOrDefault(name => Compact(name).StartsWith(compact, StringComparison.Ordinal));
        var found = Exact(artists) ?? Exact(titles) ?? Exact(albums) ?? Prefix(titles) ?? Prefix(artists) ?? Prefix(albums);
        if (found == null) return null;
        var spelled = Spelling(found, compact);
        if (string.Equals(spelled.Trim(), query.Trim(), StringComparison.OrdinalIgnoreCase)) return null;
        logger?.LogInformation("Search for {Query} also asks the media server for the library's spelling {Spelled}", query, spelled);
        return spelled;
    }

    internal static string Compact(string value) => new(value.ToLowerInvariant().Where(character =>
        character is not (' ' or '/' or '\'' or '’' or '-' or '.' or ',' or '!' or '?' or '&' or ':')).ToArray());

    // The part of the library's name that the query covers, so "cyberse" asks for "Cyber Se" and not a longer title.
    internal static string Spelling(string found, string compact)
    {
        var matched = 0;
        for (var index = 0; index < found.Length; index++)
        {
            if (Compact(found[index].ToString()).Length == 1) matched++;
            if (matched == compact.Length) return found[..(index + 1)];
        }
        return found;
    }
}
