using System.Text.Json;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.Enrichment;

public sealed record PromotionQualityFloor(string CodecFamily = "lossless", int MinBitDepth = 16, int MinSampleRate = 44100);
public sealed record PromotionQualityCeiling(int MaxBitDepth = 24, int MaxSampleRate = 96000);

public sealed record PromotionProfile
{
    public const string DefaultNamingScript = "$if2(%albumartist%,%artist%)/%album%$if(%year%, \\(%year%\\))/$if($gt(%totaldiscs%,1),%discnumber%-)$num(%tracknumber%,2) - %title%.%_extension%";
    public string NamingScript { get; init; } = DefaultNamingScript;
    public Guid? LibraryRootId { get; init; }
    public PromotionQualityFloor QualityFloor { get; init; } = new();
    public PromotionQualityCeiling HiResCeiling { get; init; } = new();
    public bool FakeLosslessCheck { get; init; } = true;
    public bool AllowLossy { get; init; }
    public bool MusicBrainzTags { get; init; } = true;
    public string CoverMode { get; init; } = "both";
    public string LyricsMode { get; init; } = "both";
    public bool ReplayGain { get; init; } = true;
    public string DefaultUnit { get; init; } = "album";
    public bool RemoveKeptCopy { get; init; }
}

public sealed record HouseholdPromotionProfile(PromotionProfile Values, string AddWithoutApproval = "admins-and-trusted-listeners");
public sealed record PersonalPromotionProfile(PromotionProfile Values, HouseholdPromotionProfile HouseholdDefault, bool UsesHouseholdDefault);

public sealed class PromotionProfileValidationException(IReadOnlyList<PromotionNamingError> errors)
    : ArgumentException("The naming script is invalid.")
{
    public IReadOnlyList<PromotionNamingError> Errors { get; } = errors;
}

public sealed class PromotionProfiles(IDbContextFactory<AllstarrDbContext> factory, PromotionRoots? roots = null,
    IBackendLibraryAccessResolver? libraryAccess = null)
{
    private const string HouseholdKey = "household";
    private readonly SemaphoreSlim gate = new(1, 1);

    // effectiveRoot fills in the folder the listener's adds actually go to; the settings page reads the saved choice instead.
    public async Task<PersonalPromotionProfile> GetMineAsync(ProtocolExecutionContext context, CancellationToken token = default,
        bool effectiveRoot = true)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        var user = await RequireUserAsync(db, context, token);
        var household = await ReadHouseholdAsync(db, token);
        var key = user.Id.ToString("N");
        var saved = await db.PromotionProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Key == key && item.OwnerUserId == user.Id, token);
        var values = saved == null ? household.Values : JsonSerializer.Deserialize<PromotionProfile>(saved.PayloadJson)!;
        return new(effectiveRoot ? await WithEffectiveRootAsync(context, values, household, token) : values, household, saved == null);
    }

    internal async Task<PromotionRoot[]> AccessibleRootsAsync(ProtocolExecutionContext context, CancellationToken token)
    {
        if (roots == null || libraryAccess == null) return [];
        var access = await libraryAccess.ResolveAsync(context, token);
        var candidates = new List<PromotionRoot>();
        foreach (var root in await roots.ReadAsync(token))
        {
            if (root.BackendLibraryId == null || !access.Allows(root.BackendLibraryId)) continue;
            try { candidates.Add(await roots.RequireAsync(root.Id, token, checkFreeSpace: false)); }
            catch (PromotionException) { }
        }
        return candidates.ToArray();
    }

    public async Task<PromotionSetup> SetupAsync(ProtocolExecutionContext context, CancellationToken token = default) =>
        await roots!.SetupAsync((await GetMineAsync(context, token)).Values.LibraryRootId ?? Guid.Empty, token);

    private async Task<PromotionProfile> WithEffectiveRootAsync(ProtocolExecutionContext context, PromotionProfile values,
        HouseholdPromotionProfile household, CancellationToken token)
    {
        var rootId = values.LibraryRootId ?? household.Values.LibraryRootId;
        if (rootId == null && await AccessibleRootsAsync(context, token) is { Length: 1 } candidates)
            rootId = candidates[0].Id;
        return values with { LibraryRootId = rootId };
    }

    public async Task<HouseholdPromotionProfile> GetHouseholdAsync(ProtocolExecutionContext context, CancellationToken token = default)
    {
        await using var db = await factory.CreateDbContextAsync(token);
        await RequireUserAsync(db, context, token, administrator: true);
        return await ReadHouseholdAsync(db, token);
    }

    public async Task<PersonalPromotionProfile> SaveMineAsync(ProtocolExecutionContext context, PromotionProfile? profile, CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            var user = await RequireUserAsync(db, context, token);
            if (profile != null) await ValidateAsync(profile, token);
            var key = user.Id.ToString("N");
            var saved = await db.PromotionProfiles.SingleOrDefaultAsync(item => item.Key == key && item.OwnerUserId == user.Id, token);
            if (profile == null)
            {
                if (saved != null) db.PromotionProfiles.Remove(saved);
            }
            else await SaveAsync(db, key, user.Id, JsonSerializer.Serialize(profile), token);
            await db.SaveChangesAsync(token);
            var household = await ReadHouseholdAsync(db, token);
            return new(profile ?? household.Values, household, profile == null);
        }
        finally { gate.Release(); }
    }

    public async Task<HouseholdPromotionProfile> SaveHouseholdAsync(ProtocolExecutionContext context, HouseholdPromotionProfile profile,
        CancellationToken token = default)
    {
        await gate.WaitAsync(token);
        try
        {
            await using var db = await factory.CreateDbContextAsync(token);
            await RequireUserAsync(db, context, token, administrator: true);
            if (profile == null || profile.AddWithoutApproval is not ("admins" or "admins-and-trusted-listeners"))
                throw new ArgumentException("Choose which household roles may add without approval.");
            await ValidateAsync(profile.Values, token);
            await SaveAsync(db, HouseholdKey, null, JsonSerializer.Serialize(profile), token);
            await db.SaveChangesAsync(token);
            return profile;
        }
        finally { gate.Release(); }
    }

    internal static async Task<UserRecord> RequireUserAsync(AllstarrDbContext db, ProtocolExecutionContext context,
        CancellationToken token, bool administrator = false)
    {
        if (context.Principal == null) throw new UnauthorizedAccessException("A linked backend user is required.");
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(item => item.Id == context.Principal.UserId && item.Enabled &&
            item.BackendInstanceId == context.BackendInstanceId && item.BackendPrincipalId == context.VerifiedBackendPrincipalId, token);
        var protocol = context.Protocol == ProtocolKind.Jellyfin ? "jellyfin" : "subsonic";
        if (user == null || user.BackendType != protocol && !(protocol == "subsonic" && user.BackendType is "navidrome" or "opensubsonic") ||
            administrator && !user.IsAdmin)
            throw new UnauthorizedAccessException("The linked backend identity is unavailable.");
        return user;
    }

    private async Task ValidateAsync(PromotionProfile profile, CancellationToken token)
    {
        if (profile == null) throw new ArgumentException("A promotion profile is required.");
        var errors = PromotionNaming.Validate(profile.NamingScript);
        if (errors.Count > 0) throw new PromotionProfileValidationException(errors);
        var floor = profile.QualityFloor;
        var ceiling = profile.HiResCeiling;
        if (profile.LibraryRootId == Guid.Empty || floor == null || ceiling == null ||
            floor.CodecFamily is not ("lossless" or "lossy" or "any") ||
            floor.MinBitDepth is < 0 or > 32 || floor.CodecFamily == "lossless" && floor.MinBitDepth == 0 ||
            floor.MinSampleRate is < 8000 or > 768000 || ceiling.MaxBitDepth is < 16 or > 32 ||
            ceiling.MaxSampleRate is < 44100 or > 768000 || floor.MinBitDepth > ceiling.MaxBitDepth || floor.MinSampleRate > ceiling.MaxSampleRate ||
            floor.CodecFamily is "lossy" or "any" && !profile.AllowLossy ||
            profile.CoverMode is not ("none" or "embedded" or "cover.jpg" or "both") ||
            profile.LyricsMode is not ("off" or ".lrc" or "embedded" or "both") || profile.DefaultUnit is not ("album" or "song"))
            throw new ArgumentException("The promotion profile contains an invalid quality, root or mode.");
        if (roots != null && profile.LibraryRootId is { } rootId && !(await roots.ReadAsync(token)).Any(item => item.Id == rootId))
            throw new ArgumentException("Choose an approved library root.");
    }

    private static async Task<HouseholdPromotionProfile> ReadHouseholdAsync(AllstarrDbContext db, CancellationToken token)
    {
        var saved = await db.PromotionProfiles.AsNoTracking().SingleOrDefaultAsync(item => item.Key == HouseholdKey && item.OwnerUserId == null, token);
        return saved == null ? new(new()) : JsonSerializer.Deserialize<HouseholdPromotionProfile>(saved.PayloadJson)!;
    }

    private static async Task SaveAsync(AllstarrDbContext db, string key, Guid? owner, string payload, CancellationToken token)
    {
        var saved = await db.PromotionProfiles.SingleOrDefaultAsync(item => item.Key == key, token);
        if (saved == null)
        {
            saved = new() { Key = key, OwnerUserId = owner };
            db.PromotionProfiles.Add(saved);
        }
        saved.PayloadJson = payload;
        saved.Revision++;
    }
}
