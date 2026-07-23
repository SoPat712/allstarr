using System.Data;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Extensions;

public sealed record ExtensionRegistryInput(string Name, string RegistryUrl, bool Enabled = true);
public sealed record ExtensionPermissionDecisionInput(string Kind, string Value, bool Approved);
public sealed record ExtensionRegistryDependency(
    Guid PackageId,
    string ExtensionId,
    string DisplayName,
    string Version,
    ExtensionPackageState State);

public sealed class ExtensionRegistryInUseException(
    string registryName,
    IReadOnlyList<ExtensionRegistryDependency> dependencies)
    : InvalidOperationException(BuildMessage(registryName, dependencies))
{
    public IReadOnlyList<ExtensionRegistryDependency> Dependencies { get; } = dependencies;

    private static string BuildMessage(string registryName, IReadOnlyList<ExtensionRegistryDependency> dependencies)
    {
        var names = dependencies
            .Select(item => item.DisplayName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase);
        return $"{registryName} still supplies installed extension packages: {string.Join(", ", names)}. " +
               "Disable and uninstall every listed package before removing this registry.";
    }
}

public sealed partial class ExtensionControlPlaneService
{
    private readonly IDbContextFactory<AllstarrDbContext> _factory;
    private readonly IPlatformClock _clock;
    private readonly string _packageRoot;

    public ExtensionControlPlaneService(
        IDbContextFactory<AllstarrDbContext> factory,
        IPlatformClock clock,
        IConfiguration configuration)
    {
        _factory = factory;
        _clock = clock;
        _packageRoot = Path.GetFullPath(configuration["Extensions:Directory"] ??
                                        Path.Combine(Directory.GetCurrentDirectory(), "extensions"));
    }

    public async Task<ExtensionRegistryRecord> AddRegistryAsync(
        ExtensionRegistryInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || input.Name.Length > 200 ||
            !Uri.TryCreate(input.RegistryUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("An HTTPS registry URL without credentials or a fragment is required.", nameof(input));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var canonical = uri.AbsoluteUri;
        var existing = await db.ExtensionRegistries.SingleOrDefaultAsync(item => item.RegistryUrl == canonical, cancellationToken);
        if (existing != null) return existing;
        var now = _clock.UtcNow;
        var record = new ExtensionRegistryRecord
        {
            Id = Guid.CreateVersion7(),
            Name = input.Name.Trim(),
            RegistryUrl = canonical,
            Enabled = input.Enabled,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExtensionRegistries.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        return record;
    }

    public async Task<IReadOnlyList<ExtensionRegistryRecord>> ListRegistriesAsync(
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        return await db.ExtensionRegistries.AsNoTracking()
            .OrderBy(item => item.Name)
            .ThenBy(item => item.RegistryUrl)
            .ToListAsync(cancellationToken);
    }

    public async Task<ExtensionRegistryRecord> SetRegistryEnabledAsync(
        Guid registryId,
        bool enabled,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var registry = await db.ExtensionRegistries.SingleOrDefaultAsync(item => item.Id == registryId, cancellationToken)
                       ?? throw new KeyNotFoundException("Extension registry not found.");
        if (registry.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The extension registry changed before this update.");
        registry.Enabled = enabled;
        registry.UpdatedAt = _clock.UtcNow;
        registry.Revision++;
        await db.SaveChangesAsync(cancellationToken);
        return registry;
    }

    public async Task RemoveRegistryAsync(
        Guid registryId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var registry = await db.ExtensionRegistries.SingleOrDefaultAsync(item => item.Id == registryId, cancellationToken)
                       ?? throw new KeyNotFoundException("Extension registry not found.");
        if (registry.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The extension registry changed before removal.");

        var dependencies = await db.ExtensionPackages.AsNoTracking()
            .Where(item => item.RegistryId == registryId && item.State != ExtensionPackageState.Uninstalled)
            .OrderBy(item => item.DisplayName)
            .ThenByDescending(item => item.StagedAt)
            .Select(item => new ExtensionRegistryDependency(
                item.Id, item.ExtensionId, item.DisplayName, item.Version, item.State))
            .ToListAsync(cancellationToken);
        if (dependencies.Count > 0)
            throw new ExtensionRegistryInUseException(registry.Name, dependencies);

        var historicalPackages = await db.ExtensionPackages
            .Where(item => item.RegistryId == registryId)
            .ToListAsync(cancellationToken);
        foreach (var package in historicalPackages)
        {
            package.RegistryId = null;
            package.Revision++;
        }

        db.ExtensionRegistries.Remove(registry);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExtensionPackageRecord>> ListPackagesAsync(
        string? extensionId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var query = db.ExtensionPackages.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(extensionId))
            query = query.Where(item => item.ExtensionId == extensionId.Trim().ToLowerInvariant());
        return await query.OrderBy(item => item.ExtensionId)
            .ThenByDescending(item => item.StagedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExtensionPermissionReviewRecord>> ListPermissionReviewsAsync(
        Guid packageId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        if (!await db.ExtensionPackages.AsNoTracking().AnyAsync(item => item.Id == packageId, cancellationToken))
            throw new KeyNotFoundException("Extension package not found.");
        return await db.ExtensionPermissionReviews.AsNoTracking()
            .Where(item => item.ExtensionPackageId == packageId)
            .OrderBy(item => item.PermissionKind)
            .ThenBy(item => item.PermissionValue)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ExtensionLogRecord>> ListLogsAsync(
        Guid? packageId = null,
        string? extensionId = null,
        int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(limit), "Limit must be between 1 and 500.");
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var query = db.ExtensionLogs.AsNoTracking();
        if (packageId.HasValue) query = query.Where(item => item.ExtensionPackageId == packageId.Value);
        if (!string.IsNullOrWhiteSpace(extensionId))
            query = query.Where(item => item.ExtensionId == extensionId.Trim().ToLowerInvariant());
        return await query.OrderByDescending(item => item.CreatedAt)
            .ThenByDescending(item => item.Id)
            .Take(limit)
            .ToListAsync(cancellationToken);
    }

    public async Task<ExtensionPackageRecord> StageAsync(
        VerifiedExtensionPackage verified,
        Guid? registryId = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(verified);
        EnsureContainedPackagePath(verified.PackageRoot);
        try
        {
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
            if (registryId.HasValue && !await db.ExtensionRegistries.AnyAsync(item => item.Id == registryId && item.Enabled, cancellationToken))
                throw new KeyNotFoundException("The enabled extension registry is unavailable.");
            var existing = await db.ExtensionPackages.SingleOrDefaultAsync(item =>
                item.ExtensionId == verified.Manifest.Id && item.Version == verified.Manifest.Version &&
                item.Sha256 == verified.Sha256 && item.State != ExtensionPackageState.Uninstalled,
                cancellationToken);
            if (existing != null)
            {
                if (!Path.GetFullPath(existing.PackagePath).Equals(
                        Path.GetFullPath(verified.PackageRoot), StringComparison.Ordinal))
                    DeletePackageContents(verified.PackageRoot);
                return existing;
            }
            var previous = await db.ExtensionPackages.AsNoTracking().SingleOrDefaultAsync(item =>
                item.ExtensionId == verified.Manifest.Id && item.State == ExtensionPackageState.Active, cancellationToken);
            var now = _clock.UtcNow;
            var package = new ExtensionPackageRecord
            {
                Id = Guid.CreateVersion7(),
                RegistryId = registryId,
                PreviousPackageId = previous?.Id,
                ExtensionId = verified.Manifest.Id,
                DisplayName = verified.Manifest.DisplayName,
                Version = verified.Manifest.Version,
                SdkVersion = verified.Manifest.SdkVersion,
                Sha256 = verified.Sha256,
                ContentSha256 = verified.ContentSha256,
                PackagePath = Path.GetFullPath(verified.PackageRoot),
                ManifestJson = File.ReadAllText(Path.Combine(verified.PackageRoot, "manifest.json")),
                State = verified.Manifest.Permissions.Count == 0
                    ? ExtensionPackageState.Staged
                    : ExtensionPackageState.ReviewRequired,
                StagedAt = now
            };
            db.ExtensionPackages.Add(package);
            db.ExtensionPermissionReviews.AddRange(verified.Manifest.Permissions.Select(permission =>
                new ExtensionPermissionReviewRecord
                {
                    Id = Guid.CreateVersion7(),
                    ExtensionPackageId = package.Id,
                    PermissionKind = permission.Kind.ToString().ToLowerInvariant(),
                    PermissionValue = permission.Value,
                    Required = permission.Required,
                    Decision = ExtensionPermissionDecision.Pending,
                    CreatedAt = now
                }));
            await AddLogAsync(db, package, "information", "package.staged", "Package staged for permission review.", "extension-stage", now);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return package;
        }
        catch
        {
            DeletePackageContents(verified.PackageRoot);
            throw;
        }
    }

    public async Task<ExtensionPackageRecord> ReviewAsync(
        Guid packageId,
        Guid reviewerUserId,
        long expectedRevision,
        IReadOnlyCollection<ExtensionPermissionDecisionInput> decisions,
        CancellationToken cancellationToken = default)
    {
        if (reviewerUserId == Guid.Empty) throw new ArgumentException("A reviewer user is required.", nameof(reviewerUserId));
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision) throw new DbUpdateConcurrencyException("The extension package changed before review.");
        if (package.State != ExtensionPackageState.ReviewRequired) throw new InvalidOperationException("Only a review-required package can be reviewed.");
        if (!await db.Users.AnyAsync(item => item.Id == reviewerUserId && item.Status == PlatformUserStatus.Active, cancellationToken))
            throw new UnauthorizedAccessException("The extension reviewer is unavailable.");
        var reviews = await db.ExtensionPermissionReviews.Where(item => item.ExtensionPackageId == package.Id).ToListAsync(cancellationToken);
        var lookup = decisions.ToDictionary(item => (item.Kind.Trim().ToLowerInvariant(), item.Value.Trim()));
        if (lookup.Count != reviews.Count || reviews.Any(review => !lookup.ContainsKey((review.PermissionKind, review.PermissionValue))))
            throw new ArgumentException("Every requested permission must receive an explicit decision.", nameof(decisions));
        var now = _clock.UtcNow;
        foreach (var review in reviews)
        {
            review.Decision = lookup[(review.PermissionKind, review.PermissionValue)].Approved
                ? ExtensionPermissionDecision.Approved
                : ExtensionPermissionDecision.Denied;
            review.ReviewedByUserId = reviewerUserId;
            review.ReviewedAt = now;
            review.Revision++;
        }
        package.ReviewedAt = now;
        package.State = reviews.Any(item => item.Required && item.Decision == ExtensionPermissionDecision.Denied)
            ? ExtensionPackageState.Failed
            : ExtensionPackageState.Staged;
        package.FailureCode = package.State == ExtensionPackageState.Failed ? "required_permission_denied" : null;
        package.Revision++;
        await AddLogAsync(db, package, package.State == ExtensionPackageState.Failed ? "warning" : "information",
            "permissions.reviewed", package.State == ExtensionPackageState.Failed
                ? "A required permission was denied."
                : "Permissions reviewed; package is ready for activation.", "extension-review", now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (package.State == ExtensionPackageState.Failed)
            DeletePackageContents(package.PackagePath);
        return package;
    }

    public async Task<ExtensionPackageRecord> ResetPermissionsForReviewAsync(
        Guid packageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The extension package changed before its grants could be revoked.");
        if (package.State is not (ExtensionPackageState.Disabled or ExtensionPackageState.RolledBack or ExtensionPackageState.Staged))
            throw new InvalidOperationException(
                "Only a disabled, rolled-back, or reviewed staged package can have its grants revoked.");

        var reviews = await db.ExtensionPermissionReviews
            .Where(item => item.ExtensionPackageId == package.Id)
            .ToListAsync(cancellationToken);
        if (reviews.Count == 0)
            throw new InvalidOperationException("This extension package does not request permissions.");

        var now = _clock.UtcNow;
        foreach (var review in reviews)
        {
            review.Decision = ExtensionPermissionDecision.Pending;
            review.ReviewedByUserId = null;
            review.ReviewedAt = null;
            review.Revision++;
        }

        package.State = ExtensionPackageState.ReviewRequired;
        package.ReviewedAt = null;
        package.FailureCode = null;
        package.DisabledAt ??= now;
        package.Revision++;
        await AddLogAsync(db, package, "warning", "permissions.revoked",
            "Extension permission grants revoked; review is required before activation.",
            "extension-permissions-revoked", now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return package;
    }

    public async Task<ExtensionPackageRecord> CancelStagingAsync(
        Guid packageId,
        long expectedRevision,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision)
            throw new DbUpdateConcurrencyException("The extension package changed before staging could be cancelled.");
        if (package.State is not (ExtensionPackageState.Staged or ExtensionPackageState.ReviewRequired or ExtensionPackageState.Failed))
            throw new InvalidOperationException("Only a package awaiting activation can be cancelled.");

        package.State = ExtensionPackageState.Uninstalled;
        package.FailureCode = "staging_cancelled";
        package.DisabledAt = _clock.UtcNow;
        package.Revision++;
        await AddLogAsync(db, package, "information", "package.staging-cancelled",
            "Extension package staging cancelled and staged content removed.",
            "extension-staging-cancelled", _clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        DeletePackageContents(package.PackagePath);
        return package;
    }

    public async Task FailStagingAsync(
        Guid packageId,
        long expectedRevision,
        string failureCode,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision ||
            package.State is not (ExtensionPackageState.Staged or ExtensionPackageState.ReviewRequired))
            return;

        var normalizedFailure = string.IsNullOrWhiteSpace(failureCode)
            ? "activation_failed"
            : failureCode.Trim();
        package.State = ExtensionPackageState.Failed;
        package.FailureCode = normalizedFailure[..Math.Min(normalizedFailure.Length, 100)];
        package.DisabledAt = _clock.UtcNow;
        package.Revision++;
        await AddLogAsync(db, package, "warning", "package.activation-failed",
            "Extension activation failed and staged content was removed.",
            "extension-activation-failed", _clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        DeletePackageContents(package.PackagePath);
    }

    public Task<ExtensionPackageRecord> ActivateAsync(Guid packageId, long expectedRevision, CancellationToken cancellationToken = default) =>
        TransitionActiveAsync(packageId, expectedRevision, rollback: false, cancellationToken);

    public async Task<ExtensionPackageRecord> RollbackAsync(
        Guid activePackageId,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        await TransitionActiveAsync(activePackageId, expectedRevision, rollback: true, cancellationToken);

    public async Task DisableAsync(Guid packageId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        if (package.State != ExtensionPackageState.Active) throw new InvalidOperationException("Only an active package can be disabled.");
        package.State = ExtensionPackageState.Disabled;
        package.DisabledAt = _clock.UtcNow;
        package.Revision++;
        await AddLogAsync(db, package, "information", "package.disabled", "Extension package disabled.", "extension-disable", _clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<ExtensionPackageRecord> UninstallAsync(
        Guid packageId, long expectedRevision, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        if (package.State is ExtensionPackageState.Active or ExtensionPackageState.ReviewRequired or ExtensionPackageState.Uninstalled)
            throw new InvalidOperationException("Disable or finish reviewing the package before uninstalling it.");
        if (await db.ExtensionPackages.AnyAsync(item => item.PreviousPackageId == package.Id &&
                item.State != ExtensionPackageState.Uninstalled, cancellationToken))
            throw new InvalidOperationException("This package is retained as a rollback target for another version.");
        package.State = ExtensionPackageState.Uninstalled;
        package.DisabledAt ??= _clock.UtcNow;
        package.Revision++;
        await AddLogAsync(db, package, "information", "package.uninstalled",
            "Extension package content removed; provider account records were retained.",
            "extension-uninstall", _clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        return package;
    }

    public async Task WriteLogAsync(
        Guid packageId,
        string level,
        string eventCode,
        string message,
        string correlationId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        var package = await db.ExtensionPackages.AsNoTracking().SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        await AddLogAsync(db, package, level, eventCode, message, correlationId, _clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<ExtensionPackageRecord> TransitionActiveAsync(
        Guid packageId,
        long expectedRevision,
        bool rollback,
        CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var package = await db.ExtensionPackages.SingleOrDefaultAsync(item => item.Id == packageId, cancellationToken)
                      ?? throw new KeyNotFoundException("Extension package not found.");
        if (package.Revision != expectedRevision) throw new DbUpdateConcurrencyException();
        ExtensionPackageRecord target;
        if (rollback)
        {
            if (package.State != ExtensionPackageState.Active || !package.PreviousPackageId.HasValue)
                throw new InvalidOperationException("The active package has no rollback version.");
            target = await db.ExtensionPackages.SingleAsync(item => item.Id == package.PreviousPackageId, cancellationToken);
            if (target.State is ExtensionPackageState.RolledBack or ExtensionPackageState.Disabled &&
                await db.ExtensionPermissionReviews.AnyAsync(
                    item => item.ExtensionPackageId == target.Id,
                    cancellationToken))
                throw new InvalidOperationException(
                    "Rollback requires the previous package permissions to be revoked and reviewed again.");
            if (target.State is not (ExtensionPackageState.RolledBack or ExtensionPackageState.Disabled or ExtensionPackageState.Staged))
                throw new InvalidOperationException("The rollback package is not available.");
        }
        else
        {
            if (package.State == ExtensionPackageState.Disabled &&
                await db.ExtensionPermissionReviews.AnyAsync(
                    item => item.ExtensionPackageId == package.Id,
                    cancellationToken))
                throw new InvalidOperationException(
                    "Reactivation requires permission grants to be revoked and reviewed again.");
            if (package.State is not (ExtensionPackageState.Staged or ExtensionPackageState.Disabled))
                throw new InvalidOperationException("Only a reviewed or previously disabled package can be activated.");
            target = package;
        }
        try
        {
            VerifyStagedContents(target);
        }
        catch (ExtensionSdkValidationException)
        {
            target.State = ExtensionPackageState.Failed;
            target.FailureCode = "staged_contents_invalid";
            target.DisabledAt = _clock.UtcNow;
            target.Revision++;
            await AddLogAsync(db, target, "warning", "package.activation-failed",
                "Extension activation validation failed and staged content was removed.",
                "extension-activation-failed", _clock.UtcNow);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            DeletePackageContents(target.PackagePath);
            throw;
        }
        var current = await db.ExtensionPackages.SingleOrDefaultAsync(item =>
            item.ExtensionId == target.ExtensionId && item.State == ExtensionPackageState.Active, cancellationToken);
        var now = _clock.UtcNow;
        if (current != null && current.Id != target.Id)
        {
            current.State = ExtensionPackageState.RolledBack;
            current.DisabledAt = now;
            current.Revision++;
        }
        if (rollback)
        {
            package.State = ExtensionPackageState.RolledBack;
            package.DisabledAt = now;
            package.Revision++;
        }
        target.State = ExtensionPackageState.Active;
        target.ActivatedAt = now;
        target.DisabledAt = null;
        target.Revision++;
        await AddLogAsync(db, target, "information", rollback ? "package.rolled-back" : "package.activated",
            rollback ? "Previous extension package restored." : "Extension package activated.",
            rollback ? "extension-rollback" : "extension-activate", now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return target;
    }

    private void VerifyStagedContents(ExtensionPackageRecord package)
    {
        EnsureContainedPackagePath(package.PackagePath);
        var actual = ExtensionSdkV1.ComputePackageContentSha256(package.PackagePath);
        if (package.ContentSha256.Length != 64 || !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actual), Convert.FromHexString(package.ContentSha256)))
            throw new ExtensionSdkValidationException(
                "Extension package contents changed after checksum verification; stage the package again.");
    }

    private void EnsureContainedPackagePath(string path)
    {
        var full = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(_packageRoot, full);
        if (Path.IsPathRooted(relative) || relative is "" or "." or ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new UnauthorizedAccessException("The extension package path is outside the configured package root.");
    }

    private void DeletePackageContents(string path)
    {
        EnsureContainedPackagePath(path);
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);
    }

    private static Task AddLogAsync(AllstarrDbContext db, ExtensionPackageRecord package, string level,
        string eventCode, string message, string correlationId, DateTimeOffset now)
    {
        var safeMessage = SecretPattern().Replace(message ?? string.Empty, "$1=[redacted]");
        db.ExtensionLogs.Add(new ExtensionLogRecord
        {
            Id = Guid.CreateVersion7(),
            ExtensionPackageId = package.Id,
            ExtensionId = package.ExtensionId,
            Level = Normalize(level, 20),
            EventCode = Normalize(eventCode, 100),
            Message = Normalize(safeMessage, 2000),
            CorrelationId = Normalize(correlationId, 100),
            CreatedAt = now
        });
        return Task.CompletedTask;
    }

    private static string Normalize(string value, int maximum)
    {
        value = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
        return value.Length <= maximum ? value : value[..maximum];
    }

    [GeneratedRegex("(?i)(authorization|password|secret|token|cookie|api[-_]?key)\\s*[=:]\\s*[^\\s,;]+")]
    private static partial Regex SecretPattern();
}

public static class ExtensionControlPlaneRegistration
{
    public static IServiceCollection AddExtensionControlPlane(this IServiceCollection services)
    {
        services.AddSingleton<ExtensionControlPlaneService>();
        services.AddSingleton<FirstPartyExtensionPolicy>();
        services.AddSingleton<FirstPartyExtensionBootstrapper>();
        services.AddHostedService(provider => provider.GetRequiredService<FirstPartyExtensionBootstrapper>());
        services.AddSingleton<ExtensionRuntimeCoordinator>();
        services.AddHostedService(provider => provider.GetRequiredService<ExtensionRuntimeCoordinator>());
        return services;
    }
}
