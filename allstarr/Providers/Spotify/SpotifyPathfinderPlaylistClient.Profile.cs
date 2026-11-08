using System.Text.Json;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Providers.Spotify;

public sealed partial class SpotifyPathfinderPlaylistClient
{
    internal const string ProfileOperation = "profileAttributes";
    internal const string ProfileQueryHash = "08ffb4730af3746e04a8301396f20875dbbce10c75243803091a9274eacc8ac0";
    private static readonly PathfinderOperationDefinition ProfileQuery = new(ProfileOperation, ProfileQueryHash, 1);

    // The signed-in account's user id, read the way the web player does. Spotify rate-limits /v1/me for web-player
    // sessions, while this query answers with the rest of the web player's traffic.
    public async Task<ProviderOutcome<string>> GetProfileUsernameAsync(string token, CancellationToken cancellationToken)
    {
        var response = await QueryAsync(token, ProfileQuery, new { }, cancellationToken);
        if (!response.Outcome.IsSuccess) return ProviderOutcome<string>.Failure(response.Outcome.Error!);
        try
        {
            if (response.Body!.Length > 256 * 1024) throw new InvalidDataException();
            using var document = JsonDocument.Parse(response.Body);
            if (GraphQlFailure(document.RootElement, ProfileOperation) is { } failure) return ProviderOutcome<string>.Failure(failure);
            if (!TryPath(document.RootElement, out var profile, "data", "me", "profile")) throw new InvalidDataException();
            var username = String(profile, "username");
            if (string.IsNullOrWhiteSpace(username) && String(profile, "uri") is { } uri && uri.StartsWith("spotify:user:", StringComparison.Ordinal))
                username = uri["spotify:user:".Length..];
            return string.IsNullOrWhiteSpace(username)
                ? ProviderOutcome<string>.Failure(new(ProviderErrorKind.PermanentFailure))
                : ProviderOutcome<string>.Success(username);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        {
            return ProviderOutcome<string>.Failure(new(ProviderErrorKind.PermanentFailure));
        }
    }
}
