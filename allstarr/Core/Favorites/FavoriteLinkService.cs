using System.Data;
using System.Text.Json;
using allstarr.Core.Jobs;
using allstarr.Core.Operations;
using allstarr.Core.Storage;
using allstarr.Services.Admin;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Favorites;

public sealed class FavoriteLinkService(IDbContextFactory<AllstarrDbContext> factory, DurableJobQueue jobs,
    IPlatformClock clock, IConfiguration? configuration = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task RequireAdministratorAsync(AdminAuthSession session, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AdministratorAsync(db, session, token);
    }
    private async Task<UserRecord> AdministratorAsync(AllstarrDbContext db, AdminAuthSession session, CancellationToken token)
    {
        var protocol = session.BackendType.ToLowerInvariant();
        if (!session.IsAdministrator || session.AllstarrUserId == null || session.ExpiresAtUtc <= clock.UtcNow.UtcDateTime ||
            protocol is not ("jellyfin" or "subsonic") || configuration?["Backend:Type"] is { } selected &&
            !selected.Trim().Equals(protocol, StringComparison.OrdinalIgnoreCase)) throw Denied();
        return await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == session.AllstarrUserId && item.Enabled && item.IsAdmin &&
            item.BackendType == protocol && item.BackendInstanceId == session.BackendInstanceId && item.BackendPrincipalId == session.UserId, token) ?? throw Denied();
    }
    private static FavoriteLinkException Denied() => new(403, "administrator_required", "Current administrator access is required.");
    private static bool Eligible(UserRecord user) => user.Enabled && !user.IsSynthetic && user.LastSeenAt != default &&
        user.BackendType is "jellyfin" or "subsonic" && !string.IsNullOrWhiteSpace(user.BackendInstanceId) && !string.IsNullOrWhiteSpace(user.BackendPrincipalId);
    private static string Name(UserRecord user) => Name(user.DisplayName, user.BackendPrincipalId);
    private static string Name(string name, string principal) => string.IsNullOrWhiteSpace(name) || name.Any(char.IsControl) ||
        name == principal && (Guid.TryParse(principal, out _) || principal.Length >= 24 && principal.All(Uri.IsHexDigit))
        ? "Previously signed-in profile" : name.Trim()[..Math.Min(name.Trim().Length, 200)];
    private static FavoriteLinkMember Member(UserRecord user, ProviderFavoriteLinkMemberRecord? binding)
    {
        var matches = binding == null || binding.IdentityFingerprint == FavoriteLinkScope.Fingerprint(user);
        var participation = !matches ? "identity_changed" : !user.Enabled ? "disabled" : user.IsSynthetic ? "synthetic" : binding == null ? "available" : "active";
        return new(user.Id, matches ? Name(user) : binding!.ConfirmedDisplayName, binding?.Protocol ?? user.BackendType,
            "Instance " + FavoriteLinkScope.Hash(binding?.BackendInstanceId ?? user.BackendInstanceId)[..8],
            ((binding?.Protocol ?? user.BackendType) == "jellyfin" ? "JF-" : "SUB-") + user.Id.ToString("N")[..12],
            binding?.IdentityFingerprint ?? FavoriteLinkScope.Fingerprint(user), user.LastSeenAt == default ? null : user.LastSeenAt,
            participation, binding?.LinkId);
    }
    public async Task<FavoriteLinkPage<FavoriteLinkMember>> MembersAsync(AdminAuthSession session, string? protocol, string? cursor, int limit, CancellationToken token)
    {
        if (protocol is not (null or "jellyfin" or "subsonic") || limit is < 1 or > 200) throw FavoriteLinkException.Invalid();
        var page = Cursor.Read(cursor, "members:" + protocol);
        await using var db = await factory.CreateDbContextAsync(token);
        await AdministratorAsync(db, session, token);
        var query = db.Users.AsNoTracking().Where(item => (protocol == null || item.BackendType == protocol) &&
            (item.BackendType == "jellyfin" || item.BackendType == "subsonic") && item.Enabled && !item.IsSynthetic && item.LastSeenAt != default);
        if (page != null) query = query.Where(item => item.Id.CompareTo(page.Upper) <= 0 && item.Id.CompareTo(page.Before) < 0);
        var rows = await query.OrderByDescending(item => item.Id).Take(limit + 1).ToArrayAsync(token);
        var shown = rows.Take(limit).ToArray();
        var ids = shown.Select(item => item.Id).ToArray();
        var bindings = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => ids.Contains(item.UserId)).ToDictionaryAsync(item => item.UserId, token);
        return new(session.BackendType.ToLowerInvariant(), shown.Where(Eligible).Select(item => Member(item, bindings.GetValueOrDefault(item.Id))).ToArray(),
            rows.Length > limit ? Cursor.Next("members:" + protocol, page?.Upper ?? rows[0].Id, shown[^1].Id) : null);
    }
    public async Task<FavoriteLinkPage<FavoriteLinkDetail>> ListAsync(AdminAuthSession session, string? cursor, int limit, CancellationToken token)
    {
        if (limit is < 1 or > 100) throw FavoriteLinkException.Invalid();
        var page = Cursor.Read(cursor, "links");
        await using var db = await factory.CreateDbContextAsync(token);
        await AdministratorAsync(db, session, token);
        var query = db.Set<ProviderFavoriteLinkRecord>().AsNoTracking();
        if (page != null) query = query.Where(item => item.Id.CompareTo(page.Upper) <= 0 && item.Id.CompareTo(page.Before) < 0);
        var rows = await query.OrderByDescending(item => item.Id).Take(limit + 1).ToArrayAsync(token);
        var values = new List<FavoriteLinkDetail>();
        foreach (var row in rows.Take(limit)) values.Add(await DetailAsync(db, row, token));
        return new(session.BackendType.ToLowerInvariant(), values, rows.Length > limit ? Cursor.Next("links", page?.Upper ?? rows[0].Id, values[^1].Id) : null);
    }
    public async Task<FavoriteLinkDetail> DetailAsync(AdminAuthSession session, Guid id, CancellationToken token)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await AdministratorAsync(db, session, token);
        return await DetailAsync(db, await db.Set<ProviderFavoriteLinkRecord>().AsNoTracking().SingleOrDefaultAsync(item => item.Id == id, token)
            ?? throw new FavoriteLinkException(404, "favorite_link_missing", "The shared provider hearts link is unavailable."), token);
    }
    private static async Task<FavoriteLinkDetail> DetailAsync(AllstarrDbContext db, ProviderFavoriteLinkRecord link, CancellationToken token)
    {
        var bindings = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => item.LinkId == link.Id).ToArrayAsync(token);
        var ids = bindings.Select(item => item.UserId).ToArray();
        var users = await db.Users.AsNoTracking().Where(item => ids.Contains(item.Id)).ToDictionaryAsync(item => item.Id, token);
        var members = bindings.Select(item => Member(users[item.UserId], item)).ToArray();
        var job = await db.Jobs.AsNoTracking().SingleAsync(item => item.Id == link.UnionJobId, token);
        var terminal = job.State is DurableJobState.Failed or DurableJobState.Cancelled;
        var state = terminal || members.Any(item => item.Participation != "active") ? "needs_attention" : link.CompletedAt.HasValue ? "shared" : "merging";
        var jobState = job.State == DurableJobState.RetryScheduled ? "retry_scheduled" : job.State.ToString().ToLowerInvariant();
        return new(link.Id, members.Single(item => item.Protocol == "jellyfin"), members.Single(item => item.Protocol == "subsonic"),
            state, link.CreatedAt, link.Revision, new(job.Id, jobState, link.ProcessedRows, link.SeededItems,
                link.CompletedAt, job.LastErrorCode == null ? null : "favorite_link_union_failed", terminal && link.CompletedAt == null));
    }
    public async Task<FavoriteLinkPreview> PreviewAsync(AdminAuthSession session, FavoriteLinkPair pair, CancellationToken token)
    {
        pair = Normalize(pair);
        await using var db = await factory.CreateDbContextAsync(token);
        await AdministratorAsync(db, session, token);
        var (first, second) = await PairAsync(db, pair, token);
        var bindings = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => item.UserId == first.Id || item.UserId == second.Id).ToArrayAsync(token);
        CheckPairBindings(bindings, first.Id, second.Id);
        CheckBindingFingerprints(bindings, first, second);
        return new(Member(first, bindings.SingleOrDefault(item => item.UserId == first.Id)), Member(second, bindings.SingleOrDefault(item => item.UserId == second.Id)),
            1, bindings.FirstOrDefault()?.LinkId);
    }
    public Task<FavoriteLinkReceipt> CreateAsync(AdminAuthSession session, CreateFavoriteLinkRequest request, CancellationToken token)
    {
        if (request == null || request.RequestId == Guid.Empty || !request.ConfirmedSamePerson || request.ConfirmationVersion != 1) throw FavoriteLinkException.Invalid();
        var pair = Normalize(new(request.JellyfinUserId, request.JellyfinFingerprint, request.SubsonicUserId, request.SubsonicFingerprint));
        var hash = FavoriteLinkScope.Hash("create", pair.JellyfinUserId.ToString("N"), pair.JellyfinFingerprint, pair.SubsonicUserId.ToString("N"), pair.SubsonicFingerprint, "1");
        return MutateAsync(async db =>
        {
            var admin = await AdministratorAsync(db, session, token);
            var prior = await ReplayAsync(db, admin.Id, request.RequestId, hash, token);
            if (prior != null) return prior;
            var (first, second) = await PairAsync(db, pair, token);
            var bindings = await db.Set<ProviderFavoriteLinkMemberRecord>().AsNoTracking().Where(item => item.UserId == first.Id || item.UserId == second.Id).ToArrayAsync(token);
            CheckPairBindings(bindings, first.Id, second.Id);
            CheckBindingFingerprints(bindings, first, second);
            ProviderFavoriteLinkRecord link;
            var created = bindings.Length == 0;
            if (!created) link = await db.Set<ProviderFavoriteLinkRecord>().SingleAsync(item => item.Id == bindings[0].LinkId, token);
            else
            {
                var id = Guid.CreateVersion7();
                var job = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<FavoriteLinkUnionPayload>(
                    FavoriteLinkUnionJobHandler.Type, $"favorite-link:{id:N}:1", new(id, 1), admin.Id, MaxDeferrals: 10000), token);
                link = new()
                {
                    Id = id,
                    PairKey = FavoriteLinkScope.Hash("pair-v1", pair.JellyfinFingerprint, pair.SubsonicFingerprint),
                    ConfirmedByUserId = admin.Id,
                    ConfirmationVersion = 1,
                    CreatedAt = clock.UtcNow,
                    UnionJobId = job.JobId,
                    UnionAttempt = 1,
                    UnionAdministratorFingerprint = FavoriteLinkScope.Fingerprint(admin),
                    Revision = 1
                };
                db.Add(link);
                foreach (var user in new[] { first, second })
                {
                    var upper = await db.FavoriteEvents.AsNoTracking().Where(item => item.OwnerUserId == user.Id && item.Protocol == user.BackendType &&
                        item.BackendInstanceId == user.BackendInstanceId && item.BackendPrincipalId == user.BackendPrincipalId &&
                        item.IsRealProviderIntent && item.SharedLinkId == null).OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id).FirstOrDefaultAsync(token);
                    db.Add(new ProviderFavoriteLinkMemberRecord
                    {
                        UserId = user.Id,
                        LinkId = id,
                        Protocol = user.BackendType,
                        BackendInstanceId = user.BackendInstanceId,
                        BackendPrincipalId = user.BackendPrincipalId,
                        IdentityFingerprint = FavoriteLinkScope.Fingerprint(user),
                        ConfirmedDisplayName = Name(user),
                        UpperAt = upper?.CreatedAt,
                        UpperId = upper?.Id
                    });
                }
            }
            db.Add(new ProviderFavoriteLinkRequestRecord
            {
                AdministratorUserId = admin.Id,
                RequestId = request.RequestId,
                Operation = "create",
                RequestHash = hash,
                LinkId = link.Id,
                JobId = link.UnionJobId,
                Created = created,
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(token);
            return new(link.Id, link.UnionJobId, created, false);
        }, token);
    }
    public Task<FavoriteLinkReceipt> RetryAsync(AdminAuthSession session, Guid id, RetryFavoriteLinkRequest request, CancellationToken token)
    {
        if (request == null || request.RequestId == Guid.Empty || request.ExpectedRevision < 1 || id == Guid.Empty) throw FavoriteLinkException.Invalid();
        var hash = FavoriteLinkScope.Hash("retry", id.ToString("N"), request.ExpectedRevision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return MutateAsync(async db =>
        {
            var admin = await AdministratorAsync(db, session, token);
            var prior = await ReplayAsync(db, admin.Id, request.RequestId, hash, token);
            if (prior != null) return prior;
            var link = await db.Set<ProviderFavoriteLinkRecord>().SingleOrDefaultAsync(item => item.Id == id, token)
                ?? throw new FavoriteLinkException(404, "favorite_link_missing", "The shared provider hearts link is unavailable.");
            if (link.Revision != request.ExpectedRevision) throw FavoriteLinkException.Changed();
            var old = await db.Jobs.SingleAsync(item => item.Id == link.UnionJobId, token);
            if (link.CompletedAt != null || old.State is not (DurableJobState.Failed or DurableJobState.Cancelled))
                throw new FavoriteLinkException(409, "favorite_link_not_retryable", "This union cannot be retried in its current state.");
            old.CancellationRequestedAt ??= clock.UtcNow;
            old.Revision++;
            link.UnionAttempt++;
            var job = await jobs.EnqueueInExistingTransactionAsync(db, new DurableJobEnqueueRequest<FavoriteLinkUnionPayload>(
                FavoriteLinkUnionJobHandler.Type, $"favorite-link:{id:N}:{link.UnionAttempt}", new(id, link.UnionAttempt), admin.Id, MaxDeferrals: 10000), token);
            link.UnionJobId = job.JobId;
            link.UnionAdministratorFingerprint = FavoriteLinkScope.Fingerprint(admin);
            link.Revision++;
            db.Add(new ProviderFavoriteLinkRequestRecord
            {
                AdministratorUserId = admin.Id,
                RequestId = request.RequestId,
                Operation = "retry",
                RequestHash = hash,
                LinkId = id,
                JobId = job.JobId,
                Created = false,
                CreatedAt = clock.UtcNow
            });
            await db.SaveChangesAsync(token);
            return new(id, job.JobId, false, false);
        }, token);
    }
    private async Task<FavoriteLinkReceipt> MutateAsync(Func<AllstarrDbContext, Task<FavoriteLinkReceipt>> mutation, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var db = await factory.CreateDbContextAsync(token);
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, token);
                var result = await mutation(db);
                await transaction.CommitAsync(token);
                return result;
            }
            catch (Exception error) when (Retryable(error) && attempt < 3) { }
            catch (Exception error) when (Retryable(error)) { throw FavoriteLinkException.Changed(); }
        }
    }
    internal static bool Retryable(Exception error) => error is DbUpdateConcurrencyException ||
        error is SqliteException { SqliteErrorCode: 5 or 6 } || error is DbUpdateException update &&
        (DbErrors.IsUniqueViolation(update) || update.InnerException is SqliteException { SqliteErrorCode: 5 or 6 });
    private static async Task<FavoriteLinkReceipt?> ReplayAsync(AllstarrDbContext db, Guid admin, Guid request, string hash, CancellationToken token)
    {
        var prior = await db.Set<ProviderFavoriteLinkRequestRecord>().AsNoTracking().SingleOrDefaultAsync(item => item.AdministratorUserId == admin && item.RequestId == request, token);
        if (prior == null) return null;
        if (prior.RequestHash != hash) throw new FavoriteLinkException(409, "favorite_link_request_conflict", "This request ID was already used for a different request.");
        return new(prior.LinkId, prior.JobId, false, true);
    }
    private static async Task<(UserRecord, UserRecord)> PairAsync(AllstarrDbContext db, FavoriteLinkPair pair, CancellationToken token)
    {
        var users = await db.Users.AsNoTracking().Where(item => item.Id == pair.JellyfinUserId || item.Id == pair.SubsonicUserId).ToArrayAsync(token);
        var first = users.SingleOrDefault(item => item.Id == pair.JellyfinUserId && item.BackendType == "jellyfin");
        var second = users.SingleOrDefault(item => item.Id == pair.SubsonicUserId && item.BackendType == "subsonic");
        if (first == null || second == null || !Eligible(first) || !Eligible(second))
            throw new FavoriteLinkException(409, "favorite_link_member_unavailable", "Choose two enabled, previously signed-in profiles that are not synthetic listeners.");
        if (FavoriteLinkScope.Fingerprint(first) != pair.JellyfinFingerprint || FavoriteLinkScope.Fingerprint(second) != pair.SubsonicFingerprint)
            throw new FavoriteLinkException(409, "favorite_link_member_changed", "A selected profile identity changed. Refresh the available profiles.");
        return (first, second);
    }
    private static void CheckPairBindings(ProviderFavoriteLinkMemberRecord[] bindings, Guid first, Guid second)
    {
        if (bindings.Length != 0 && (bindings.Length != 2 || bindings[0].LinkId != bindings[1].LinkId ||
            !bindings.Any(item => item.UserId == first) || !bindings.Any(item => item.UserId == second)))
            throw new FavoriteLinkException(409, "favorite_link_collision", "A selected profile already belongs to another shared provider hearts link.");
    }
    private static void CheckBindingFingerprints(ProviderFavoriteLinkMemberRecord[] bindings, params UserRecord[] users)
    {
        if (bindings.Any(binding => binding.IdentityFingerprint != FavoriteLinkScope.Fingerprint(users.Single(user => user.Id == binding.UserId))))
            throw new FavoriteLinkException(409, "favorite_link_member_changed", "A confirmed profile identity changed. Its existing link remains reserved.");
    }
    private static FavoriteLinkPair Normalize(FavoriteLinkPair pair)
    {
        if (pair == null || pair.JellyfinUserId == Guid.Empty || pair.SubsonicUserId == Guid.Empty || pair.JellyfinUserId == pair.SubsonicUserId ||
            !Hex(pair.JellyfinFingerprint) || !Hex(pair.SubsonicFingerprint)) throw FavoriteLinkException.Invalid();
        return pair;
    }
    private static bool Hex(string? value) => value is { Length: 64 } && value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    private sealed record Cursor(int Version, string Kind, Guid Upper, Guid Before)
    {
        internal static Cursor? Read(string? text, string kind)
        {
            if (text == null) return null;
            if (text.Length > 1024) throw FavoriteLinkException.Invalid();
            try
            {
                var value = JsonSerializer.Deserialize<Cursor>(Convert.FromBase64String(text), Json);
                return value is { Version: 1 } && value.Kind == kind && value.Upper != Guid.Empty && value.Before != Guid.Empty && value.Before.CompareTo(value.Upper) <= 0
                    ? value : throw FavoriteLinkException.Invalid();
            }
            catch (Exception error) when (error is JsonException or FormatException) { throw FavoriteLinkException.Invalid(); }
        }
        internal static string Next(string kind, Guid upper, Guid before) => Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Cursor(1, kind, upper, before), Json));
    }
}
