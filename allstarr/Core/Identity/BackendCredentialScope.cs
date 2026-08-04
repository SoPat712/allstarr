using allstarr.Core.Storage;

namespace allstarr.Core.Identity;

public static class BackendCredentialScope
{
    public const string SubsonicPurpose = "playlist-backend:subsonic";

    public static bool Matches(SecretReferenceRecord secret, BackendIdentityRecord identity) =>
        secret.TenantId == identity.TenantId && secret.BackendIdentityId == identity.Id &&
        secret.Purpose == SubsonicPurpose && secret.RevokedAt == null;
}
