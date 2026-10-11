using allstarr.Core.Storage;
using System.Globalization;
using System.Numerics;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Extensions;

public sealed partial class ExtensionControlPlaneService
{
    internal async Task ReconcileSupersededPackagesAsync(CancellationToken cancellationToken)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var retained = await db.ExtensionPackages.Where(item =>
            item.State == ExtensionPackageState.Active || item.State == ExtensionPackageState.Disabled)
            .ToListAsync(cancellationToken);
        var legacy = await db.ExtensionPackages.Where(item => item.State == ExtensionPackageState.RolledBack)
            .ToListAsync(cancellationToken);
        foreach (var package in legacy.Where(package => retained.Any(current =>
                     current.ExtensionId == package.ExtensionId && IsNewerVersion(current.Version, package.Version))))
            await RetireSupersededPackageAsync(db, package, _clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        // The durable event also lets the next startup finish an interrupted filesystem cleanup.
        var pending = await db.ExtensionPackages.AsNoTracking().Where(package =>
            package.State == ExtensionPackageState.Uninstalled && db.ExtensionLogs.Any(log =>
                log.ExtensionPackageId == package.Id && log.EventCode == "package.superseded"))
            .ToListAsync(cancellationToken);
        foreach (var package in pending)
            await CleanupSupersededPackageAsync(package, cancellationToken);
    }

    private static Task RetireSupersededPackageAsync(
        AllstarrDbContext db, ExtensionPackageRecord package, DateTimeOffset now)
    {
        package.State = ExtensionPackageState.Uninstalled;
        package.DisabledAt ??= now;
        package.Revision++;
        return AddLogAsync(db, package, "information", "package.superseded",
            "Previous extension version uninstalled; permission history and sign-in retained.",
            "extension-update", now);
    }

    private async Task CleanupSupersededPackageAsync(ExtensionPackageRecord package, CancellationToken cancellationToken)
    {
        try
        {
            EnsureContainedPackagePath(package.PackagePath);
            var relative = Path.GetRelativePath(_packageRoot, Path.GetFullPath(package.PackagePath));
            if (relative.Split(Path.DirectorySeparatorChar)[0].Equals(".runtime", StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Runtime state is not package content.");
            if (!Directory.Exists(package.PackagePath)) return;
            for (var directory = new DirectoryInfo(package.PackagePath);
                 directory != null && directory.FullName != Path.TrimEndingDirectorySeparator(_packageRoot); directory = directory.Parent)
                if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Package cleanup cannot traverse symbolic links.");
            await using var db = await _factory.CreateDbContextAsync(cancellationToken);
            var retainedPaths = await db.ExtensionPackages.Where(item => item.Id != package.Id &&
                    item.State != ExtensionPackageState.Uninstalled)
                .Select(item => item.PackagePath).ToListAsync(cancellationToken);
            var packagePath = Path.GetFullPath(package.PackagePath);
            if (retainedPaths.Any(path => Path.GetFullPath(path).Equals(packagePath, StringComparison.OrdinalIgnoreCase) ||
                                         Path.GetFullPath(path).StartsWith(packagePath + Path.DirectorySeparatorChar,
                                             StringComparison.OrdinalIgnoreCase)))
                throw new UnauthorizedAccessException("Package content is still in use.");
            DeletePackageContents(package.PackagePath);
            await WriteLogAsync(package.Id, "information", "package.contents-removed",
                "Previous extension version files removed.", "extension-update", cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            await WriteLogAsync(package.Id, "warning", "package.cleanup-pending",
                "Previous extension version files could not be removed; cleanup will retry at startup.",
                "extension-update", cancellationToken);
        }
    }

    private static bool IsNewerVersion(string current, string previous)
    {
        var left = current.Split('-', 2);
        var right = previous.Split('-', 2);
        if (!Version.TryParse(left[0], out var a) || !Version.TryParse(right[0], out var b)) return false;
        var comparison = a.CompareTo(b);
        if (comparison != 0) return comparison > 0;
        if (left.Length != right.Length) return left.Length == 1;
        if (left.Length == 1) return false;
        var leftParts = left[1].Split('.');
        var rightParts = right[1].Split('.');
        for (var index = 0; index < Math.Min(leftParts.Length, rightParts.Length); index++)
        {
            var leftNumeric = BigInteger.TryParse(leftParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = BigInteger.TryParse(rightParts[index], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            comparison = leftNumeric && rightNumeric ? leftNumber.CompareTo(rightNumber)
                : leftNumeric != rightNumeric ? (leftNumeric ? -1 : 1)
                : string.CompareOrdinal(leftParts[index], rightParts[index]);
            if (comparison != 0) return comparison > 0;
        }
        return leftParts.Length > rightParts.Length;
    }
}
