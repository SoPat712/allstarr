namespace allstarr.Core.ManagedFiles;

public sealed class ManagedFileReferenceService(
    IManagedFileOwnershipStore ownership,
    IManagedFileRemovalStore records)
{
    public async Task<ManagedFileRecord> ReleaseAsync(
        Guid managedFileId,
        string referenceKey,
        string requestingScopeKey,
        bool explicitlyConfirmed,
        CancellationToken cancellationToken = default)
    {
        if (!explicitlyConfirmed)
            throw new InvalidOperationException("Managed-file reference release requires explicit confirmation.");
        var record = await records.GetAsync(managedFileId, cancellationToken)
            ?? throw new KeyNotFoundException("Managed file not found.");
        if (!record.IsManaged || !StringComparer.Ordinal.Equals(record.ScopeKey, requestingScopeKey))
            throw new UnauthorizedAccessException("The file reference is not owned by this managed scope.");
        return await ownership.ReleaseReferenceAsync(managedFileId, referenceKey, cancellationToken);
    }
}
