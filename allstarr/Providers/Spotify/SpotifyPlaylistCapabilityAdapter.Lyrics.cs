using System.Security.Cryptography;
using System.Text;
using allstarr.Core.Capabilities;
using allstarr.Services.Common;

namespace allstarr.Core.Providers.Spotify;

public sealed partial class SpotifyPlaylistCapabilityAdapter
{
    internal async Task<ProviderOutcome<ProviderLyricsResult>> ReadLyricsAsync(
        ProviderExecutionContext context,
        ProviderLyricsRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(request);
        var contextError = ValidateLyricsContext(context, request);
        if (contextError != null)
            return ProviderOutcome<ProviderLyricsResult>.Failure(contextError);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        deadline.CancelAfter(context.Remaining(DateTimeOffset.UtcNow));
        context = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
            context.OperationId, context.CorrelationId, context.Deadline, deadline.Token, context.IdempotencyKey);
        var cacheKey = LyricsCacheKey(context.Account!, request);
        if (_cache != null)
        {
            try
            {
                var cached = await _cache.GetAsync<ProviderLyricsResult>(cacheKey).WaitAsync(context.CancellationToken);
                context.CancellationToken.ThrowIfCancellationRequested();
                if (cached is { Availability: ProviderLyricsAvailabilityState.Available, Content: not null })
                    return ProjectLyrics(cached, request.AvailabilityOnly);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                return ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.Canceled));
            }
            catch
            {
                // Cache failures are deliberately fail-open for provider reads.
            }
        }

        var outcome = await ExecuteAsync(
            context,
            async (token, cancellationToken) =>
            {
                var fetchRequest = request.AvailabilityOnly
                    ? new ProviderLyricsRequest(
                        request.CanonicalRecordingId,
                        request.ProviderTrackId,
                        trackTitle: request.TrackTitle,
                        artistNames: request.ArtistNames,
                        albumTitle: request.AlbumTitle,
                        durationSeconds: request.DurationSeconds,
                        preferredFormat: request.PreferredFormat)
                    : request;
                var fetched = await SpotifyLyricsCapabilityAdapter.ReadLyricsFromTokenAsync(
                    _http, token, fetchRequest, cancellationToken);
                if (fetched.IsSuccess)
                {
                    var value = fetched.RequireValue();
                    if (value.Availability == ProviderLyricsAvailabilityState.Available &&
                        value.Content != null && _cache != null)
                    {
                        try { await _cache.SetAsync(cacheKey, value, CacheExtensions.LyricsTTL); }
                        catch { /* Cache failures must not change the provider result. */ }
                    }
                }
                return fetched;
            });
        if (context.CancellationToken.IsCancellationRequested)
            return ProviderOutcome<ProviderLyricsResult>.Failure(new(ProviderErrorKind.Canceled));
        return outcome.IsSuccess
            ? ProjectLyrics(outcome.RequireValue(), request.AvailabilityOnly)
            : outcome;
    }

    private static ProviderError? ValidateLyricsContext(
        ProviderExecutionContext context,
        ProviderLyricsRequest request)
    {
        if (!context.ProviderId.Equals(StableProviderId, StringComparison.Ordinal) ||
            !context.Policy.AllowsProvider(StableProviderId) ||
            !request.ProviderTrackId.ProviderId.Equals(StableProviderId, StringComparison.Ordinal) ||
            request.ProviderTrackId.ResourceKind != ProviderResourceKind.Track)
            return new ProviderError(ProviderErrorKind.Forbidden);
        if (!SpotifyTrackId.IsValid(request.ProviderTrackId.Value))
            return new ProviderError(ProviderErrorKind.PermanentFailure);
        if (context.Actor.Kind == ProviderActorKind.PublicRead)
            return new ProviderError(ProviderErrorKind.Forbidden);
        if (context.Account == null || context.Account.SecretReferenceId == null)
            return new ProviderError(ProviderErrorKind.AccountNeedsConfiguration);
        if (context.CancellationToken.IsCancellationRequested)
            return new ProviderError(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow))
            return new ProviderError(ProviderErrorKind.CapabilityUnavailable);
        return null;
    }

    private static string LyricsCacheKey(ProviderAccountContext account, ProviderLyricsRequest request)
    {
        var trackHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(request.ProviderTrackId.Value)))
            .ToLowerInvariant();
        var format = request.PreferredFormat?.ToString().ToLowerInvariant() ?? "auto";
        return $"lyrics:v2:{StableProviderId}:{account.AccountId:N}:{account.Revision}:" +
            $"{account.SecretReferenceId!.Value:N}:{trackHash}:{format}";
    }

    private static ProviderOutcome<ProviderLyricsResult> ProjectLyrics(
        ProviderLyricsResult result,
        bool availabilityOnly) =>
        availabilityOnly
            ? ProviderOutcome<ProviderLyricsResult>.Success(new(
                result.Availability, result.Source, revision: result.Revision))
            : ProviderOutcome<ProviderLyricsResult>.Success(result);

    private static class SpotifyTrackId
    {
        internal static bool IsValid(string value) => value.Length == 22 &&
            value.All(character => character is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');
    }
}
