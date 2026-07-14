using System.Text.Json;
using allstarr.Core.Secrets;
using allstarr.Models.Settings;
using Microsoft.Extensions.Options;

namespace allstarr.Core.Playlists.Targets;

public sealed class EncryptedSubsonicPlaylistAuthenticationResolver : IBackendPlaylistAuthenticationResolver
{
    private readonly EncryptedSecretStore _secrets;
    private readonly SubsonicSettings _settings;

    public EncryptedSubsonicPlaylistAuthenticationResolver(
        EncryptedSecretStore secrets,
        IOptions<SubsonicSettings> settings)
    {
        _secrets = secrets;
        _settings = settings.Value;
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
