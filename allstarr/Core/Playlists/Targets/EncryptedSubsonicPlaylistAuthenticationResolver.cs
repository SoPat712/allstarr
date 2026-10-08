using System.Text.Json;
using allstarr.Core.Secrets;
using allstarr.Models.Settings;
using allstarr.Services.Subsonic;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Playlists.Targets;

public sealed class EncryptedSubsonicPlaylistAuthenticationResolver : IBackendPlaylistAuthenticationResolver
{
    private readonly EncryptedSecretStore _secrets;
    private readonly SubsonicSettings _settings;
    private readonly IHttpContextAccessor _httpContext;

    public EncryptedSubsonicPlaylistAuthenticationResolver(
        EncryptedSecretStore secrets,
        IOptions<SubsonicSettings> settings,
        IHttpContextAccessor httpContext)
    {
        _secrets = secrets;
        _settings = settings.Value;
        _httpContext = httpContext ?? throw new ArgumentNullException(nameof(httpContext));
    }

    public ValueTask<BackendPlaylistAuthentication> ResolveReadAsync(
        BackendPlaylistTargetContext context,
        CancellationToken cancellationToken)
    {
        if (_httpContext.HttpContext is not { } requestContext)
            return ResolveAsync(context, cancellationToken);
        if (!SubsonicSessionAuthentication.TryGetViewerReadParameters(requestContext,
                context.BackendInstanceId, context.VerifiedPrincipalId, context.TenantId, out var parameters))
            throw new UnauthorizedAccessException("The signed-in viewer is unavailable for this backend read.");
        return ValueTask.FromResult(new BackendPlaylistAuthentication(new Dictionary<string, string>(), parameters));
    }

    public async ValueTask<BackendPlaylistAuthentication> ResolveAsync(
        BackendPlaylistTargetContext context,
        CancellationToken cancellationToken)
    {
        var referenceText = context.CredentialReference ?? _settings.PlaylistCredentialReference;
        if (!Guid.TryParse(referenceText, out var referenceId) || referenceId == Guid.Empty)
        {
            throw new InvalidOperationException(
                "Subsonic background playlist writes require a valid encrypted PlaylistCredentialReference.");
        }

        var usesLinkReference = context.CredentialReference != null;
        using var lease = await _secrets.OpenAsync(
            referenceId,
            new SecretAccessContext(
                TenantId: usesLinkReference ? context.TenantId : null,
                AllowGlobal: !usesLinkReference),
            cancellationToken);
        using var document = JsonDocument.Parse(lease.Value);
        var root = document.RootElement;
        var username = Required(root, "username");
        var password = Required(root, "password");

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
