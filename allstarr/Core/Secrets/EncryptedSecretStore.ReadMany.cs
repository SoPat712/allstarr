using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Secrets;

public sealed partial class EncryptedSecretStore
{
    internal async Task ReadManyAsync(
        IReadOnlyList<(Guid ReferenceId, SecretAccessContext Access)> requests,
        Action<int, ReadOnlyMemory<byte>> read,
        CancellationToken cancellationToken = default)
    {
        if (requests.Count == 0) return;

        var keyRing = await _keyRingProvider.LoadAsync(cancellationToken);
        try
        {
            await using var context = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var referenceIds = requests.Select(item => item.ReferenceId).Distinct().ToArray();
            var secrets = await (
                from reference in context.SecretReferences.AsNoTracking()
                where referenceIds.Contains(reference.Id)
                join version in context.SecretVersions.AsNoTracking()
                    on new { ReferenceId = reference.Id, Version = reference.ActiveVersion }
                    equals new { ReferenceId = version.SecretReferenceId, version.Version } into versions
                from version in versions.DefaultIfEmpty()
                select new { Reference = reference, Version = version })
                .ToDictionaryAsync(item => item.Reference.Id, cancellationToken);

            for (var index = 0; index < requests.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[index];
                var secret = secrets.GetValueOrDefault(request.ReferenceId)
                    ?? throw new KeyNotFoundException("Secret reference not found.");
                EnsureOwnerMatch(secret.Reference, request.Access);
                if (secret.Reference.RevokedAt.HasValue)
                    throw new InvalidOperationException("Secret reference is revoked.");
                var version = secret.Version
                    ?? throw new InvalidOperationException("The active secret version was not found.");
                using var lease = new SecretLease(Decrypt(
                    version,
                    keyRing.GetKey(version.KeyId),
                    AssociatedData(secret.Reference, version.Version)));
                read(index, lease.Value);
            }
        }
        finally
        {
            ClearKeyRing(keyRing);
        }
    }
}
