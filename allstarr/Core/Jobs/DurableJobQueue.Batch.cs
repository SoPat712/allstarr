using System.Data.Common;
using System.Text.Json;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace allstarr.Core.Jobs;

public sealed partial class DurableJobQueue
{
    internal async Task<IReadOnlyList<DurableJobEnqueueResult>> EnqueueManyInExistingTransactionAsync<T>(
        AllstarrDbContext context, IReadOnlyList<DurableJobEnqueueRequest<T>> requests,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requests);
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Batch job staging requires the caller's transaction.");
        var prepared = new List<PreparedJobEnqueue>(requests.Count);
        var authorizations = new Dictionary<(Guid? Owner, Guid? Account, string? Capability,
            string? Correlation, string Type), DurableJobSavedContext>();
        foreach (var request in requests)
        {
            ValidateEnqueueRequest(request.Type, request.IdempotencyKey, request.Priority,
                request.MaxAttempts, request.MaxDeferrals);
            var payloadJson = _payloadPolicy.SerializeAndValidate(request.Payload);
            var type = request.Type.Trim().ToLowerInvariant();
            var key = (request.OwnerUserId, request.ProviderAccountId, request.Capability, request.CorrelationId, type);
            if (!authorizations.TryGetValue(key, out var saved))
            {
                saved = await _contextAuthorizer.AuthorizeEnqueueAsync(request.OwnerUserId,
                    request.ProviderAccountId, request.Capability, request.CorrelationId, cancellationToken, type);
                authorizations.Add(key, saved);
            }
            var attempts = request.MaxAttempts ?? _options.DefaultMaxAttempts;
            var deferrals = request.MaxDeferrals ?? _options.DefaultMaxDeferrals;
            prepared.Add(new(saved, CreateSavedScopeKey(saved), type, request.IdempotencyKey.Trim(),
                payloadJson, CreateRequestFingerprint(payloadJson, request.Priority, attempts, deferrals, request.AvailableAt),
                request.Priority, attempts, deferrals, request.AvailableAt));
        }
        var existing = new Dictionary<(string Scope, string Type, string Key), DurableJobRecord>();
        var wanted = prepared.Select(item => (item.ScopeKey, item.Type, item.IdempotencyKey)).ToHashSet();
        foreach (var group in prepared.GroupBy(item => (item.ScopeKey, item.Type)))
        {
            var keys = group.Select(item => item.IdempotencyKey).Distinct(StringComparer.Ordinal).ToArray();
            var rows = await context.Jobs.Where(item => item.ScopeKey == group.Key.ScopeKey &&
                item.Type == group.Key.Type && EF.Parameter(keys).Contains(item.IdempotencyKey)).ToArrayAsync(cancellationToken);
            foreach (var row in rows) existing[(row.ScopeKey, row.Type, row.IdempotencyKey)] = row;
        }
        foreach (var row in context.Jobs.Local)
        {
            var key = (row.ScopeKey, row.Type, row.IdempotencyKey);
            if (wanted.Contains(key)) existing.TryAdd(key, row);
        }
        var now = _clock.UtcNow;
        var created = new List<DurableJobRecord>(prepared.Count);
        var results = new List<DurableJobEnqueueResult>(prepared.Count);
        foreach (var request in prepared)
        {
            var key = (request.ScopeKey, request.Type, request.IdempotencyKey);
            if (existing.TryGetValue(key, out var prior))
            {
                EnsureIdempotentRequestMatches(prior, request.Context, request.Fingerprint);
                results.Add(new(prior.Id, false));
                continue;
            }
            var job = new DurableJobRecord
            {
                Id = Guid.CreateVersion7(),
                ScopeKey = request.ScopeKey,
                OwnerUserId = request.Context.OwnerUserId,
                ProviderAccountId = request.Context.ProviderAccountId,
                ProviderCapability = request.Context.ProviderCapability,
                PolicySnapshotJson = request.Context.PolicySnapshotJson,
                RequestFingerprint = request.Fingerprint,
                CorrelationId = request.Context.CorrelationId,
                Type = request.Type,
                PayloadJson = request.PayloadJson,
                IdempotencyKey = request.IdempotencyKey,
                State = DurableJobState.Pending,
                Priority = request.Priority,
                MaxAttempts = request.MaxAttempts,
                MaxDeferrals = request.MaxDeferrals,
                AvailableAt = request.AvailableAt ?? now,
                CreatedAt = now,
                UpdatedAt = now
            };
            existing.Add(key, job);
            created.Add(job);
            results.Add(new(job.Id, true));
        }
        context.Jobs.AddRange(created);
        return results;
    }

    private sealed record PreparedJobEnqueue(DurableJobSavedContext Context, string ScopeKey, string Type,
        string IdempotencyKey, string PayloadJson, string Fingerprint, int Priority, int MaxAttempts,
        int MaxDeferrals, DateTimeOffset? AvailableAt);

    internal async Task PersistEnqueuedJobsAsync(AllstarrDbContext context,
        IReadOnlyList<DurableJobEnqueueResult> results, CancellationToken cancellationToken = default)
    {
        if (context.Database.CurrentTransaction == null)
            throw new InvalidOperationException("Batch job persistence requires the caller's transaction.");
        var ids = results.Where(item => item.Created).Select(item => item.JobId).ToHashSet();
        if (ids.Count == 0) return;
        var entries = context.ChangeTracker.Entries<DurableJobRecord>()
            .Where(item => ids.Contains(item.Entity.Id)).ToArray();
        if (entries.Length != ids.Count || entries.Any(item => item.State != EntityState.Added))
            throw new InvalidOperationException("Only newly enqueued jobs can be persisted as a batch.");
        var entity = context.Model.FindEntityType(typeof(DurableJobRecord))!;
        var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
        var properties = entity.GetProperties().ToArray();
        static string Quote(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var columns = string.Join(",", properties.Select(item => Quote(item.GetColumnName(table)!)));
        var values = string.Join(",", properties.Select((_, index) => $"json_extract(value, '$[{index}]')"));
        var sql = $"INSERT INTO {Quote(table.Name)} ({columns}) SELECT {values} FROM json_each({{0}})";
        // Use the model's converters so enum, timestamp and null representations stay
        // identical to ordinary persistence. SQLite binds Guid values as uppercase text.
        foreach (var batch in entries.Chunk(1000))
        {
            var rows = batch.Select(entry => properties.Select(property =>
            {
                var value = entry.Property(property.Name).CurrentValue;
                var converter = property.GetTypeMapping().Converter;
                if (converter != null) value = converter.ConvertToProvider(value);
                return value is Guid id ? id.ToString("D").ToUpperInvariant() : value;
            }).ToArray()).ToArray();
            var json = JsonSerializer.Serialize(rows);
            try
            {
                await context.Database.ExecuteSqlRawAsync(sql, [json], cancellationToken);
            }
            catch (DbException error)
            {
                throw new DbUpdateException("The enqueued job batch could not be persisted.", error);
            }
        }
        foreach (var entry in entries) entry.State = EntityState.Detached;
    }
}
