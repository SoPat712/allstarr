using allstarr.Core.Artwork;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Operations;
using allstarr.Core.Playback;
using allstarr.Core.Protocols;
using allstarr.Core.Storage;
using Microsoft.EntityFrameworkCore;

namespace allstarr.Core.ClientApi;

public sealed class PlaybackReader(
    IDbContextFactory<AllstarrDbContext> factory,
    TimeProvider clock,
    IProviderRegistry registry,
    IEnumerable<IPlaybackActivitySource> activity,
    IPlaybackDeliveryActivitySource deliveries,
    LibraryProviderResolver libraryProvider,
    CleanOnlyProtocolContent content)
{
    public async Task<ClientPlayback?> ReadAsync(HttpContext http, ProtocolExecutionContext execution, Guid id)
    {
        if (execution.Client.DeviceId == null) return null;
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        var row = await Visible(db, execution).SingleOrDefaultAsync(item => item.Id == id, execution.CancellationToken);
        var sample = row == null ? null : Read(row, execution);
        return sample?.Trace?.Request.ItemId is not { } itemId || !await content.AllowsAsync(http, itemId)
            ? null : await ProjectAsync(http, execution, sample);
    }

    public async Task<ClientPlayback?> CurrentAsync(HttpContext http, ProtocolExecutionContext execution)
    {
        using var matching = RequestTiming.Current?.Stage("match");
        if (execution.Client.DeviceId is not { } device) return null;
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        var state = activity.SelectMany(source => source.GetActivePlaybackStates(TimeSpan.FromMinutes(5)))
            .Where(state => state.UserId == owner && state.DeviceId == device &&
                state.BackendUserId == execution.VerifiedBackendPrincipalId)
            .OrderByDescending(state => state.LastActivity).FirstOrDefault();
        if (state == null || !await content.AllowsAsync(http, state.ItemId)) return null;
        await using var db = await factory.CreateDbContextAsync(execution.CancellationToken);
        var stream = deliveries.StreamFor(owner, device, state.ItemId);
        if (stream != null && !stream.Matches(execution.Protocol.ToString().ToLowerInvariant(), execution.BackendInstanceId)) stream = null;
        var recent = Visible(db, execution).OrderByDescending(item => item.CreatedAt).ThenByDescending(item => item.Id)
            .Take(PlaybackStartTimingStore.MaximumSamples);
        var rows = stream?.PlaybackRequestId is { } requestId
            ? await recent.Where(item => item.Id == requestId).ToArrayAsync(execution.CancellationToken)
            : await recent.ToArrayAsync(execution.CancellationToken);
        var sample = rows.Select(row => Read(row, execution)).OfType<PlaybackStartSample>()
            .FirstOrDefault(sample => SameItem(sample.Trace!.Request.ItemId, state.ItemId) &&
                (stream == null || stream.PlaybackRequestId == sample.Id));
        if (sample != null) return await ProjectAsync(http, execution, sample);
        if (stream != null)
        {
            var scope = stream.AccountId is { } accountId ? await db.ProviderAccounts.AsNoTracking()
                .Where(account => account.Id == accountId && (account.OwnerUserId == null || account.OwnerUserId == owner))
                .Select(account => (ProviderAccountScope?)account.Scope).SingleOrDefaultAsync(execution.CancellationToken) : null;
            return new(await ProviderAsync(http, execution, stream.ProviderId, state.ItemId), Scope(scope?.ToString()), stream.Cached,
                null, ProblemReportText.Route(stream.SelectionReason), state.ItemId, stream.OpenedAt);
        }
        if (ExternalPlaybackMetadataResolver.ParseTrackIdentity(state.ItemId) != null) return null;
        return new(await libraryProvider.ResolveAsync(http, execution), null, false, null, "native-local-library", state.ItemId,
            state.StartedAt ?? new DateTimeOffset(DateTime.SpecifyKind(state.LastActivity, DateTimeKind.Utc)));
    }

    private IQueryable<AuditEventRecord> Visible(AllstarrDbContext db, ProtocolExecutionContext execution)
    {
        var owner = execution.RequireActor().EffectiveUserId!.Value;
        return PlaybackStartTimingStore.RecentEvents(db, clock.GetUtcNow()).Where(item => item.ActorUserId == owner);
    }

    private static PlaybackStartSample? Read(AuditEventRecord row, ProtocolExecutionContext execution)
    {
        if (row.DetailsJson.Length > 131072) return null;
        try
        {
            var sample = JsonSerializer.Deserialize<PlaybackStartSample>(row.DetailsJson);
            return sample?.Trace is { } trace && trace.OwnerUserId == row.ActorUserId &&
                trace.Request.Protocol == execution.Protocol.ToString() && sample.DeviceId != null &&
                sample.DeviceId == execution.Client.DeviceId ? sample : null;
        }
        catch (JsonException) { return null; }
    }

    private async Task<ClientPlayback> ProjectAsync(HttpContext http, ProtocolExecutionContext execution, PlaybackStartSample sample)
    {
        var trace = sample.Trace!;
        var media = trace.Delivery?.Converted == true ? trace.Delivery.Output : trace.Received?.Media;
        return new(await ProviderAsync(http, execution, trace.Resolution.Provider ?? sample.Provider, trace.Request.ItemId), Scope(sample.AccountScope),
            trace.Resolution.Cached ?? sample.Cached, Delivered(media), trace.Resolution.Route, trace.Request.ItemId, sample.StartedAt);
    }

    private async Task<ItemDetailsProvider?> ProviderAsync(HttpContext http, ProtocolExecutionContext execution, string? provider, string? itemId)
    {
        if (itemId != null && ExternalPlaybackMetadataResolver.ParseTrackIdentity(itemId) == null)
            return await libraryProvider.ResolveAsync(http, execution);
        if (provider == null || !registry.TryGet(provider, out var descriptor)) return null;
        var icon = (await ProviderArtworkResolver.ResolveIconsAsync(http.RequestServices, [provider], http.RequestAborted)).GetValueOrDefault(provider);
        return icon?.Provider(provider, descriptor!.DisplayName) ?? new(provider, descriptor!.DisplayName, null);
    }

    private static PlaybackAccountScope? Scope(string? value) => value?.ToLowerInvariant() switch
    {
        "personal" => PlaybackAccountScope.Personal,
        "shared" => PlaybackAccountScope.Shared,
        _ => null
    };

    private static PlaybackDelivered? Delivered(ProviderMediaFormat? media) => media == null ? null :
        new(media.Container, media.Codec, media.BitDepth, media.SampleRate, media.Bitrate);

    private static bool SameItem(string? first, string second)
    {
        if (string.Equals(first, second, StringComparison.OrdinalIgnoreCase)) return true;
        if (Guid.TryParse(first, out var firstId) && Guid.TryParse(second, out var secondId)) return firstId == secondId;
        var firstExternal = first == null ? null : ExternalPlaybackMetadataResolver.ParseTrackIdentity(first);
        var secondExternal = ExternalPlaybackMetadataResolver.ParseTrackIdentity(second);
        return firstExternal != null && firstExternal == secondExternal;
    }
}
