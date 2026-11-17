using System.Collections.Concurrent;
using System.Text.Json;
using allstarr.Core.Matching;
using allstarr.Core.Settings;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Protocols;

/// <summary>A listener's music libraries: the chosen ones, or null for all, and every one they could choose.</summary>
public sealed record ListeningLibraryChoice(IReadOnlyList<string>? Selected, IReadOnlyList<BackendMusicLibrary> Available, long Revision);

/// <summary>
/// Library access narrowed to the music libraries a listener chose, for picking songs: mixes, Made for you and autoplay.
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

    public async Task<BackendLibraryAccess> ResolveAsync(ProtocolExecutionContext context, CancellationToken cancellationToken = default) =>
        Narrow(await inner.ResolveAsync(context, cancellationToken), await SelectedAsync(Owner(context), cancellationToken));

    public async Task<BackendLibraryAccessContext> ResolveUserAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var access = await inner.ResolveUserAsync(userId, cancellationToken);
        return access with { Access = Narrow(access.Access, await SelectedAsync(userId, cancellationToken)) };
    }

    public Task InvalidateAsync(ProtocolKind protocol, string backendInstanceId, string backendPrincipalId) =>
        inner.InvalidateAsync(protocol, backendInstanceId, backendPrincipalId);

    /// <summary>A choice that no longer matches any library the listener can open falls back to all of them.</summary>
    internal static BackendLibraryAccess Narrow(BackendLibraryAccess access, IReadOnlyList<string>? selected)
    {
        if (!access.Succeeded || selected is not { Count: > 0 }) return access;
        var kept = access.LibraryIds.Where(id => selected.Contains(id, StringComparer.Ordinal)).ToArray();
        return kept.Length == 0 ? access : access with { LibraryIds = kept };
    }

    /// <summary>Keeps service songs and library songs from the chosen libraries, in order; without a choice every id stays.</summary>
    public async Task<IReadOnlyList<string>> KeepChosenAsync(ProtocolExecutionContext context, IReadOnlyList<string> ids,
        CancellationToken cancellationToken = default)
    {
        if (ids.Count == 0 || await SelectedAsync(Owner(context), cancellationToken) == null) return ids;
        var native = ids.Where(id => !id.StartsWith("ext-", StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (native.Length == 0) return ids;
        var access = await ResolveAsync(context, cancellationToken);
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var kept = (await LibraryTrackAccess.Query(db, context, access).Where(track => native.Contains(track.BackendItemId))
            .Select(track => track.BackendItemId).ToArrayAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        return ids.Where(id => id.StartsWith("ext-", StringComparison.Ordinal) || kept.Contains(id)).ToArray();
    }

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
            var allowed = BackendMusicLibraries.Select(named.Select(library => library.Id), configuration, context.Protocol).ToHashSet(StringComparer.Ordinal);
            return named.Where(library => allowed.Contains(library.Id)).DistinctBy(library => library.Id).ToArray();
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            logger?.LogWarning(exception, "Music libraries could not be read");
            return null;
        }
    }

    private static Guid? Owner(ProtocolExecutionContext context) => context.Actor?.EffectiveUserId ?? context.Principal?.UserId;
}
