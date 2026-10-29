using System.Text.RegularExpressions;
using allstarr.Core.Capabilities;
using allstarr.Core.Routing;
using allstarr.Core.Settings;
using allstarr.Core.Storage;

namespace allstarr.Core.Playlists;

public sealed record PlaylistUrlResolution(
    ProviderDescriptor? Source = null,
    ProviderExternalResourceId? Resource = null,
    ProviderExecutionContext? Context = null,
    Guid? ProviderAccountId = null,
    string? ErrorCode = null,
    string? Error = null);

public sealed class PlaylistUrlResolver(
    IProviderRegistry registry,
    IProviderRouter router,
    IEffectiveProviderPolicyResolver? policies = null)
{
    public async Task<PlaylistUrlResolution> ResolveAsync(string url, ProviderActorContext actor,
        IReadOnlyList<ProviderAccountRecord> accounts, string correlationId, CancellationToken cancellationToken = default)
    {
        if (url.Length > 4096 || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 || uri.Scheme is "https" or "http" && !uri.IsDefaultPort)
            return NoSource();
        var policy = policies == null ? null : actor.EffectiveUserId is { } userId
            ? await policies.ResolveForUserAsync(userId, cancellationToken)
            : await policies.ResolveAsync(cancellationToken);
        var owners = registry.Providers.Select(source =>
        {
            var resource = source.Origin == ProviderOrigin.BuiltIn ? ParseBuiltIn(uri, source.Id) : null;
            var handler = source.Capabilities.Select(capability =>
                    registry.TryGetCapability<IProviderUrlCapability>(source.Id, capability.Capability, out var implementation)
                        ? implementation : null)
                .FirstOrDefault(implementation => implementation != null &&
                    implementation.UrlPatterns.Any(pattern => MatchesPattern(uri, pattern)));
            return (source, resource, handler);
        }).Where(item => item.resource != null || item.handler != null)
            .OrderBy(item => item.source.Origin == ProviderOrigin.BuiltIn ? 0 : 1)
            .ThenBy(item => item.source.Id, StringComparer.Ordinal).ToArray();
        PlaylistUrlResolution? unavailable = null;
        foreach (var (source, resource, handler) in owners)
        {
            if ((policy?.GetProviderMode(source.Id) ?? source.DefaultUseMode) == ProviderUseMode.Off)
            {
                unavailable ??= new(Source: source, ErrorCode: "source-off", Error: $"Turn on {source.DisplayName} to import from it.");
                continue;
            }
            var capabilityKind = resource != null ? ProviderCapabilityKind.Playlist : handler!.Capability;
            var capability = source.Capabilities.FirstOrDefault(item => item.Capability == capabilityKind && item.HasUsableImplementation);
            if (capability == null) continue;
            var account = accounts.Where(item => item.Enabled && item.ProviderId == source.Id &&
                    (source.Id != "listenbrainz" || item.Scope == ProviderAccountScope.Personal && item.OwnerUserId == actor.EffectiveUserId) &&
                    (item.OwnerUserId == null || item.OwnerUserId == actor.EffectiveUserId) &&
                    capability.AllowedAccountScopes.Contains(item.Scope))
                .OrderByDescending(item => item.OwnerUserId == actor.EffectiveUserId)
                .ThenBy(item => item.Id).FirstOrDefault();
            if (capability.AccountRequirement == ProviderAccountRequirement.Required && account == null)
            {
                unavailable ??= new(Source: source, ErrorCode: "account-required", Error: $"Connect a {source.DisplayName} account to import from it.");
                continue;
            }
            var request = new ProviderRouteRequest(capabilityKind, actor,
                new(new(ProviderAudioQuality.Any, ProviderAudioQuality.HighResolution, false), ProviderExplicitContentPolicy.Allow,
                    false, source.Id != "listenbrainz", false, [source.Id]), "playlist-url-resolve", correlationId, DateTimeOffset.UtcNow.AddMinutes(2),
                [source.Id], account == null ? [] : [new(source.Id, requestedAccountId: account.Id, expectedAccountRevision: account.Revision)],
                cancellationToken: cancellationToken, purpose: ProviderRoutePurpose.ManualSelection);
            ProviderExecutionContext? context;
            if (capabilityKind == ProviderCapabilityKind.Playlist)
                context = (await router.PlanAsync<IProviderPlaylistCapability>(request)).Candidates.FirstOrDefault()?.Context;
            else if (capabilityKind == ProviderCapabilityKind.Metadata)
                context = (await router.PlanAsync<IProviderMetadataCapability>(request)).Candidates.FirstOrDefault()?.Context;
            else continue;
            if (context == null)
            {
                unavailable ??= new(Source: source, ErrorCode: "source-unavailable", Error: $"{source.DisplayName} cannot currently read this link.");
                continue;
            }
            if (context.Account == null && capability.SupportsPublicRead)
                context = new(new ProviderActorContext(ProviderActorKind.PublicRead, null), source.Id, null,
                    context.Policy, context.OperationId, context.CorrelationId, context.Deadline, context.CancellationToken);
            if (resource != null) return new(source, resource, context, context.Account?.AccountId);
            var resolved = await handler!.ResolveUrlAsync(context, uri.AbsoluteUri);
            if (!resolved.IsSuccess) return NoSource();
            var value = resolved.RequireValue();
            if (value.ProviderId != source.Id || value.ResourceKind is not (ProviderResourceKind.Playlist or ProviderResourceKind.Album or ProviderResourceKind.Track))
                return NoSource();
            return new(source, value, context, context.Account?.AccountId);
        }
        return unavailable ?? NoSource();
    }

    private static PlaylistUrlResolution NoSource() => new(ErrorCode: "no-source", Error: "No source can read this link.");

    public static bool MatchesPattern(Uri uri, string pattern)
    {
        if (pattern.Length > 2048) return false;
        if (pattern.EndsWith(':') && Uri.CheckSchemeName(pattern[..^1]))
            return uri.Scheme.Equals(pattern[..^1], StringComparison.OrdinalIgnoreCase);
        if (!pattern.Contains("://", StringComparison.Ordinal) && !pattern.Contains('\\') &&
            !pattern.Contains('(') && !pattern.StartsWith('^'))
        {
            var parts = pattern.Split('/', 2);
            var host = parts[0].ToLowerInvariant();
            var ownsHost = host.StartsWith("*.", StringComparison.Ordinal)
                ? uri.IdnHost.EndsWith(host[1..], StringComparison.OrdinalIgnoreCase)
                : uri.IdnHost.Equals(host, StringComparison.OrdinalIgnoreCase);
            if (!ownsHost) return false;
            if (parts.Length == 1) return true;
            var path = "/" + parts[1];
            return path.EndsWith('*') ? uri.AbsolutePath.StartsWith(path[..^1], StringComparison.Ordinal) :
                uri.AbsolutePath == path || uri.AbsolutePath.StartsWith(path.TrimEnd('/') + "/", StringComparison.Ordinal);
        }
        try
        {
            var expression = pattern.Contains("://", StringComparison.Ordinal) && !pattern.Contains('\\') && !pattern.Contains('(') && !pattern.StartsWith('^')
                ? "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$"
                : pattern;
            var match = Regex.Match(uri.AbsoluteUri, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
            return match.Success && match.Index == 0;
        }
        catch (ArgumentException) { return false; }
        catch (RegexMatchTimeoutException) { return false; }
    }

    public static ProviderExternalResourceId? ParseBuiltIn(Uri uri, string providerId)
    {
        if (providerId == "listenbrainz")
            return Core.Providers.ListenBrainz.ListenBrainzPlaylistCapabilityAdapter.TryNormalize(uri.AbsoluteUri, out var playlist)
                ? new(providerId, ProviderResourceKind.Playlist, playlist) : null;
        if (providerId == "spotify" && uri.Scheme == "spotify")
        {
            var parts = uri.OriginalString.Split(':');
            if (parts.Length != 3 && !(parts.Length == 5 && parts[1] == "user" && parts[3] == "playlist")) return null;
            var kind = parts[^2] switch
            {
                "playlist" => ProviderResourceKind.Playlist,
                "album" => ProviderResourceKind.Album,
                "track" => ProviderResourceKind.Track,
                _ => (ProviderResourceKind?)null
            };
            var id = parts[^1];
            return kind != null && id.Length is > 0 and <= 500 && id == id.Trim() && !id.Any(char.IsControl)
                ? new(providerId, kind.Value, id) : null;
        }
        if (uri.Scheme is not ("https" or "http")) return null;
        var host = uri.IdnHost.ToLowerInvariant();
        var valid = providerId switch
        {
            "spotify" => host == "open.spotify.com",
            "apple-musickit" or "apple-download" => host == "music.apple.com",
            "deezer" => host is "deezer.com" or "www.deezer.com",
            "qobuz" => host is "qobuz.com" or "www.qobuz.com" or "play.qobuz.com" or "open.qobuz.com",
            _ => false
        };
        if (!valid) return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < segments.Length - 1; index++)
        {
            var kind = segments[index] switch
            {
                "playlist" => ProviderResourceKind.Playlist,
                "album" => ProviderResourceKind.Album,
                "track" or "song" => ProviderResourceKind.Track,
                _ => (ProviderResourceKind?)null
            };
            if (kind == null) continue;
            var id = providerId is "apple-musickit" or "apple-download" or "qobuz" ? segments[^1] : segments[index + 1];
            if (providerId is "apple-musickit" or "apple-download")
            {
                var track = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query).GetValueOrDefault("i").ToString();
                if (track.Length > 0) { kind = ProviderResourceKind.Track; id = track; }
            }
            if (id.Length > 500 || id.Contains('%') || id.Contains(':')) return null;
            return new(providerId, kind.Value, id);
        }
        return null;
    }
}
