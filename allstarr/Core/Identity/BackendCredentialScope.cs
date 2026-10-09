using allstarr.Core.Storage;

namespace allstarr.Core.Identity;

public static class BackendCredentialScope
{
    public const string SubsonicPurpose = "playlist-backend:subsonic";

    public static bool Matches(SecretReferenceRecord secret, UserRecord user) =>
        user.Enabled && secret.UserId == user.Id &&
        secret.Purpose == SubsonicPurpose && secret.RevokedAt == null;
}
