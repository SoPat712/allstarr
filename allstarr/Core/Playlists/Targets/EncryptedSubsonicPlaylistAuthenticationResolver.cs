using System.Text.Json;
using allstarr.Core.Secrets;
using allstarr.Services.Subsonic;

namespace allstarr.Core.Playlists.Targets;

public sealed class EncryptedSubsonicPlaylistAuthenticationResolver : IBackendPlaylistAuthenticationResolver
{
    private readonly EncryptedSecretStore _secrets;
    private readonly IHttpContextAccessor _httpContext;

    public EncryptedSubsonicPlaylistAuthenticationResolver(
        EncryptedSecretStore secrets,
        IHttpContextAccessor httpContext)
    {
        _secrets = secrets;
        _httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
    }

    public ValueTask<BackendPlaylistAuthentication> ResolveReadAsync(
        BackendPlaylistTargetContext context,
        CancellationToken cancellationToken)
    {
        if (_httpContext.HttpContext is not { } requestContext)
            return ResolveAsync(context, cancellationToken);
        if (!SubsonicSessionAuthentication.TryGetViewerReadParameters(requestContext,
                context.BackendInstanceId, context.VerifiedPrincipalId, out var parameters))
            throw new UnauthorizedAccessException("The signed-in viewer is unavailable for this backend read.");
        return ValueTask.FromResult(new BackendPlaylistAuthentication(new Dictionary<string, string>(), parameters));
    }

    public async ValueTask<BackendPlaylistAuthentication> ResolveAsync(
        BackendPlaylistTargetContext context,
        CancellationToken cancellationToken)
    {
        if (context.CredentialReference != null &&
            (!Guid.TryParse(context.CredentialReference, out var parsed) || parsed == Guid.Empty))
            throw new UnauthorizedAccessException("The playlist credential context is unavailable.");
        Guid? referenceId = context.CredentialReference == null ? null : Guid.Parse(context.CredentialReference);
        using var lease = await _secrets.OpenSubsonicPlaylistCredentialAsync(
            context.BackendInstanceId, context.VerifiedPrincipalId,
            referenceId, cancellationToken);
        using var document = JsonDocument.Parse(lease.Value);
        var root = document.RootElement;
        var username = Required(root, "username");
        var password = Required(root, "password");
        if (!username.Equals(context.VerifiedPrincipalId, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The playlist credential belongs to another listener.");

        return new BackendPlaylistAuthentication(
            new Dictionary<string, string>(),
            [
                new("u", username),
                new("p", password),
                new("v", "1.16.1"),
                new("c", "allstarr")
            ]);
    }

    private static string Required(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(
                $"The Subsonic playlist credential secret requires a non-empty {propertyName} field.");
        }

        return property.GetString()!;
    }
}
