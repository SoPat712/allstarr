using System.Text.Json;
using System.Text.Json.Nodes;
using allstarr.Core.Jobs;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Downloads;

public sealed record AudioQualityCheckPayload(Guid ArtifactId, bool Force = false);

public sealed class AudioQualityCheckJobHandler(IDbContextFactory<AllstarrDbContext> factory,
    ProviderDownloadArtifactResolver artifacts, AudioQualityAnalyzer analyzer, DurableJobQueue jobs) : IDurableJobHandler
{
    public const string Type = "downloads.quality-check";
    public string JobType => Type;

    public static DurableJobEnqueueRequest<AudioQualityCheckPayload> Request(Guid artifactId, Guid owner, bool force = false) =>
        new(Type, $"quality:{artifactId:N}{(force ? ":" + Guid.NewGuid().ToString("N") : "")}",
            new(artifactId, force), owner, Priority: -200, MaxAttempts: 3, CorrelationId: "audio-quality");

    public async Task<DurableJobCompletion> ExecuteAsync(DurableJobExecutionContext context, CancellationToken cancellationToken)
    {
        var payload = context.Claim.Payload.Deserialize<AudioQualityCheckPayload>();
        if (payload == null || payload.ArtifactId == Guid.Empty)
            return DurableJobCompletion.Failure("quality_request_invalid", "The quality check request is invalid.");
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        var item = await db.Set<ProviderDownloadArtifactEntity>().AsNoTracking().SingleOrDefaultAsync(value =>
            value.Id == payload.ArtifactId && value.OwnerUserId == context.Claim.OwnerUserId && value.SupersededAt == null,
            cancellationToken);
        if (item == null) return DurableJobCompletion.Cancelled();
        try
        {
            var keptFile = await (from route in db.Set<RecordingKeptRouteEntity>().AsNoTracking()
                                  join file in db.ManagedFiles.AsNoTracking() on route.ManagedFileId equals file.Id
                                  where route.SourceArtifactId == item.Id && file.RemovedAt == null &&
                                        file.ContentSha256 == item.ContentSha256 && file.Length == item.Length
                                  select file).FirstOrDefaultAsync(cancellationToken);
            keptFile ??= item.ManagedFileId is { } fileId
                ? await db.ManagedFiles.AsNoTracking().SingleOrDefaultAsync(file => file.Id == fileId &&
                    file.RemovedAt == null && file.ContentSha256 == item.ContentSha256 && file.Length == item.Length, cancellationToken)
                : null;
            string path;
            if (keptFile != null)
            {
                using var scope = context.Services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<FilePlacementService>()
                    .VerifyOwnedFileAsync(EfManagedFileOwnershipStore.Map(keptFile)!, cancellationToken);
                path = keptFile.CanonicalPath;
            }
            else
            {
                var artifact = await artifacts.FindByJobAsync(item.DurableJobId, item.ProviderId, cancellationToken);
                if (artifact?.Id != item.Id) return DurableJobCompletion.Cancelled();
                path = artifact.SourcePath;
            }
            var check = await analyzer.AnalyzeAsync(path, cancellationToken, payload.Force);
            if (!await jobs.IsExecutionCurrentAsync(context.Claim, cancellationToken))
                return DurableJobCompletion.Cancelled();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var changed = await db.Set<ProviderDownloadArtifactEntity>().Where(value => value.Id == item.Id &&
                value.ContentSha256 == item.ContentSha256 && value.SupersededAt == null)
                .ExecuteUpdateAsync(update => update.SetProperty(value => value.QualityCheckJson, JsonSerializer.Serialize(check)), cancellationToken);
            if (changed == 0) return DurableJobCompletion.Cancelled();
            var routes = await db.Set<RecordingKeptRouteEntity>().Where(value => value.SourceArtifactId == item.Id)
                .ToArrayAsync(cancellationToken);
            foreach (var route in routes)
            {
                route.DisplayMetadataJson = WithCheck(route.DisplayMetadataJson, check);
                route.Revision++;
            }
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return DurableJobCompletion.Success();
        }
        catch (allstarr.Services.Common.LocalCapacityException)
        {
            return DurableJobCompletion.Retry("local-capacity-unavailable", "Media conversion capacity is full. Retry shortly.", TimeSpan.FromSeconds(5));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException or DbUpdateConcurrencyException)
        {
            return DurableJobCompletion.Retry("quality_file_unavailable", "The file could not be checked right now.");
        }
    }

    internal static AudioQualityCheck? ReadCheck(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<AudioQualityCheck>(json); }
        catch (JsonException) { return null; }
    }

    internal static string? WithCheck(string? metadata, AudioQualityCheck? check)
    {
        if (metadata == null || check == null) return metadata;
        var value = JsonNode.Parse(metadata)!.AsObject();
        value["QualityCheck"] = JsonSerializer.SerializeToNode(check);
        return value.ToJsonString();
    }
}
