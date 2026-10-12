using allstarr.Core.Favorites;
using allstarr.Core.Protocols;

namespace allstarr.Core.Music;

/// <summary>
/// Records a protocol favorite mutation in the favorites pipeline for a linked user. Each protocol
/// decides when the mutation is accepted (relayed or answered itself); this owns how it is recorded.
/// A recording failure is logged and never changes the protocol response; cancellation propagates.
/// </summary>
public sealed class MusicFavorites(IFavoriteActionPipeline? pipeline, ILogger logger)
{
    public const string DefaultSourceRevision = "protocol-state-v1";

    public static string SourceRevision(IHeaderDictionary headers) =>
        headers["Idempotency-Key"].FirstOrDefault()
        ?? headers["X-Allstarr-Source-Revision"].FirstOrDefault()
        ?? DefaultSourceRevision;

    public async Task RecordAsync(
        ProtocolExecutionContext context,
        string itemId,
        FavoriteOperation operation,
        IHeaderDictionary headers,
        CancellationToken cancellationToken)
    {
        if (pipeline == null || !context.CanRunUserScopedWork) return;
        try
        {
            await pipeline.RecordAsync(
                new FavoriteMutationRequest(context, itemId, operation, SourceRevision(headers)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backend mutation already succeeded. Optional work is observable through the durable system
            // when recorded, but must never rewrite the backend's favorite response.
            logger.LogWarning("Favorite workflow recording failed ({ExceptionType})", ex.GetType().Name);
        }
    }
}
