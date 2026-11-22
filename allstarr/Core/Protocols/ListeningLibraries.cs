using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Music;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols;

/// <summary>A listener's music libraries: the chosen ones, or null for all, and every one they could choose.</summary>
public sealed record ListeningLibraryChoice(IReadOnlyList<string>? Selected, IReadOnlyList<BackendMusicLibrary> Available, long Revision);

/// <summary>
/// Library access narrowed to the music libraries a listener chose, for picking songs (mixes, Made for you, autoplay) and for search.
/// Indexing, matching and permissions keep using the full access, so other libraries stay known and reachable.
/// </summary>
public sealed class ListeningLibraries(
    IBackendLibraryAccessResolver inner,
    IDurableRuntimeSettings settings,
    IDbContextFactory<AllstarrDbContext> factory,
    IBackendLibraryPermissionSource permissions,
    IHttpClientFactory clients,
    IConfiguration configuration,
    TimeProvider clock,
    ILogger<ListeningLibraries>? logger = null,
    allstarr.Core.ClientEvents.IClientEventPublisher? events = null) : IBackendLibraryAccessResolver
{
    public const string Key = "listening";
    private static readonly TimeSpan Remembered = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, IReadOnlyList<string>? Selected)> chosen = new();
    // A listener's libraries rarely change, and asking the media server took about 75 ms on every preferences read.
    private readonly ConcurrentDictionary<(ProtocolKind, string, string), (DateTimeOffset At, BackendMusicLibrary[] Named)> listed = new();

    public async Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
        Narrow(await inner.ResolveAsync(context, cancellationToken), await SelectedAsync(Owner(context), cancellationToken));

    public async Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var access = await inner.ResolveUserAsync(userId, cancellationToken);
        return access with { Access = Narrow(access.Access, await SelectedAsync(userId, cancellationToken)) };
    }

    public Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId)
    {
        listed.TryRemove((protocol, backendInstanceId, backendPrincipalId), out _);
        return inner.InvalidateAsync(protocol, backendInstanceId, backendPrincipalId);
    }

    /// <summary>A choice that no longer matches any library the listener can open falls back to all of them.</summary>
    internal static BackendLibraryAccess Narrow(BackendLibraryAccess access, IReadOnlyList<string>? selected)
    {
        if (!access.Succeeded || selected is not { Count: > 0 }) return access;
        var kept = access.LibraryIds.Where(id => selected.Contains(id, StringComparer.Ordinal)).ToArray();
        return kept.Length == 0 ? access : access with { LibraryIds = kept };
    }

    /// <summary>The chosen libraries when they are fewer than the listener can open; null when every library is in use.</summary>
    public Task<IReadOnlyList<string>?> NarrowedAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
        SearchedAsync(context, null, cancellationToken);

    /// <summary>
    /// The libraries a search may show library items from; null when every library is in use. An app's own pick of one
    /// of the listener's libraries stays when it is chosen and leaves none when it is not; any other pick, such as a
    /// folder, keeps the chosen libraries.
    /// </summary>
    public async Task<IReadOnlyList<string>?> SearchedAsync(ProtocolExecutionContext context, string? picked,
        CancellationToken cancellationToken = default)
    {
        if (await SelectedAsync(Owner(context), cancellationToken) is not { Count: > 0 } selected) return null;
        var access = await inner.ResolveAsync(context, cancellationToken);
        if (!access.Succeeded) return null;
        var kept = access.LibraryIds.Where(id => selected.Contains(id, StringComparer.Ordinal)).ToArray();
        if (kept.Length == 0 || kept.Length == access.LibraryIds.Length) return null;
        if (string.IsNullOrWhiteSpace(picked) || access.LibraryIds.FirstOrDefault(id => SameId(id, picked)) is not { } library) return kept;
        return kept.Contains(library, StringComparer.Ordinal) ? [library] : [];
    }

    /// <summary>Keeps service songs and library songs from the chosen libraries, in order; without a choice every id stays.</summary>
    public async Task<IReadOnlyList<string>> KeepChosenAsync(ProtocolExecutionContext context, IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 || await SelectedAsync(Owner(context), cancellationToken) == null ||
            ids.All(id => id.StartsWith("ext-", StringComparison.Ordinal))) return ids;
        return await KeepAsync(context, ids, await ResolveAsync(context, cancellationToken), cancellationToken);
    }

    /// <summary>Keeps service songs and library songs from these libraries, in order.</summary>
    public Task<IReadOnlyList<string>> KeepInAsync(ProtocolExecutionContext context, IReadOnlyList<string> ids,
        IReadOnlyList<string> libraries, CancellationToken cancellationToken = default) =>
        KeepAsync(context, ids, new(true, libraries.ToArray()), cancellationToken);

    private async Task<IReadOnlyList<string>> KeepAsync(ProtocolExecutionContext context, IReadOnlyList<string> ids,
        BackendLibraryAccess access, CancellationToken cancellationToken)
    {
        var native = ids.Where(id => !id.StartsWith("ext-", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (native.Length == 0) return ids;
        var kept = new HashSet<string>(StringComparer.Ordinal);
        if (access.LibraryIds.Length > 0)
        {
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            kept.UnionWith(await LibraryTrackAccess.Query(db, context, access).Where(track => native.Contains(track.BackendItemId))
                .Select(track => track.BackendItemId).ToArrayAsync(cancellationToken));
        }
        return ids.Where(id => id.StartsWith("ext-", StringComparison.Ordinal) || kept.Contains(id)).ToArray();
    }

    // Apps may spell a library id in another case or with dashes.
    private static bool SameId(string left, string right) =>
        string.Equals(left.Replace("-", "", StringComparison.Ordinal), right.Trim().Replace("-", "", StringComparison.Ordinal),
            StringComparison.OrdinalIgnoreCase);

    public async Task<ListeningLibraryChoice?> ReadAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default)
    {
        var available = await AvailableAsync(context, cancellationToken);
        if (available == null) return null;
        var saved = await settings.GetMyLibrariesAsync(Owner(context) ?? throw new UnauthorizedAccessException(), cancellationToken);
        var shown = saved.Selected?.Where(id => available.Any(library => library.Id == id)).ToArray();
        return new(shown is { Length: > 0 } ? shown : null, available, saved.Revision);
    }

    /// <summary>Saves the choice; null, or every available library, means all of them. Unknown ids are rejected.</summary>
    public async Task<ListeningLibraryChoice?> UpdateAsync(ProtocolExecutionContext context, IReadOnlyList<string>? selected,
        CancellationToken cancellationToken = default)
    {
        var owner = Owner(context) ?? throw new UnauthorizedAccessException();
        var available = await AvailableAsync(context, cancellationToken);
        if (available == null) return null;
        if (selected != null && (selected.Count == 0 || selected.Any(id => available.All(library => library.Id != id))))
            throw new ArgumentException("Choose one or more of your music libraries.", nameof(selected));
        var stored = selected == null || available.All(library => selected.Contains(library.Id, StringComparer.Ordinal)) ? null : selected;
        await settings.UpdateMyLibrariesAsync(owner, stored, cancellationToken);
        chosen.TryRemove(owner, out _);
        // Every app of this listener re-reads the choice, wherever it was changed.
        events?.Publish(owner, allstarr.Core.ClientEvents.ClientEvent.Preferences("libraries"));
        return new(stored, available, (await settings.GetMyLibrariesAsync(owner, cancellationToken)).Revision);
    }

    private async Task<IReadOnlyList<string>?> SelectedAsync(Guid? owner, CancellationToken cancellationToken)
    {
        if (owner is not { } user) return null;
        var now = clock.GetUtcNow();
        if (chosen.TryGetValue(user, out var known) && now - known.At < Remembered) return known.Selected;
        var selected = (await settings.GetMyLibrariesAsync(user, cancellationToken)).Selected;
        chosen[user] = (now, selected);
        return selected;
    }

    private async Task<IReadOnlyList<BackendMusicLibrary>?> AvailableAsync(ProtocolExecutionContext context, CancellationToken cancellationToken)
    {
        var key = (context.Protocol, context.BackendInstanceId, context.VerifiedBackendPrincipalId);
        if (listed.TryGetValue(key, out var known) && clock.GetUtcNow() - known.At < Remembered) return Allowed(known.Named, context.Protocol);
        try
        {
            using var request = await permissions.CreateRequestAsync(context, cancellationToken);
            if (request == null)
            {
                logger?.LogWarning("Music libraries could not be read: no credential for the listener");
                return null;
            }
            using var client = clients.CreateClient(BackendLibraryAccessResolver.HttpClientName);
            using var response = await client.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger?.LogWarning("Music libraries could not be read: the media server answered {Status}", (int)response.StatusCode);
                return null;
            }
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var named = BackendMusicLibraries.ReadNamed(context.Protocol, document.RootElement);
            if (listed.Count >= 1024) listed.Clear();
            listed[key] = (clock.GetUtcNow(), named);
            return Allowed(named, context.Protocol);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Music libraries could not be read");
            return null;
        }
    }

    // The household's choice of libraries applies on every read, so a change to it shows at once.
    private BackendMusicLibrary[] Allowed(BackendMusicLibrary[] named, ProtocolKind protocol)
    {
        var allowed = BackendMusicLibraries.Select(named.Select(library => library.Id), configuration, protocol).ToHashSet(StringComparer.Ordinal);
        return named.Where(library => allowed.Contains(library.Id)).DistinctBy(library => library.Id).ToArray();
    }

    private static Guid? Owner(ProtocolExecutionContext context) => context.Actor?.EffectiveUserId ?? context.Principal?.UserId;
}

/// <summary>
/// A library search whose copies of service songs come only from the libraries searched, so a service song stays a
/// service song when the listener's copy is in a library the search leaves out.
/// </summary>
public sealed class SearchedLibraryCopies<TPayload>(INativeMusicSearch<TPayload> inner, ListeningLibraries libraries,
    ProtocolExecutionContext context, IReadOnlyList<string> searched) : INativeMusicSearch<TPayload>
{
    public Task<IReadOnlyList<NativeMusicItem<TPayload>>> SearchAsync(CancellationToken cancellationToken) => inner.SearchAsync(cancellationToken);

    public async Task<NativeMusicItem<TPayload>?> GetSongAsync(string id, CancellationToken cancellationToken) =>
        (await KeepAsync([id], cancellationToken)).Count == 0 ? null : await inner.GetSongAsync(id, cancellationToken);

    public async Task<IReadOnlyList<NativeMusicItem<TPayload>>> GetSongsAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken) =>
        await KeepAsync(ids, cancellationToken) is { Count: > 0 } kept ? await inner.GetSongsAsync(kept, cancellationToken) : [];

    private async Task<IReadOnlyList<string>> KeepAsync(IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        try
        {
            return (await libraries.KeepInAsync(context, ids, searched, cancellationToken))
                .Where(id => !id.StartsWith("ext-", StringComparison.Ordinal)).ToArray();
        }
        // Without proof that a copy is in a searched library, the service song is shown.
        catch (Exception error) when (error is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { return []; }
    }
}
