using allstarr.Core.Operations;

namespace allstarr.Core.Jobs;

public sealed class SidecarJobGate(SidecarStatusCatalog catalog)
{
    public DurableJobCompletion? Check(
        string sidecarId,
        TimeSpan? retryDelay = null)
    {
        if (!catalog.TryGet(sidecarId, out var status))
        {
            return DurableJobCompletion.Failure(
                "sidecar_unknown",
                "The job references a sidecar that is not declared by this deployment.");
        }

        if (status.State == SidecarRuntimeState.Ready)
        {
            return null;
        }

        return DurableJobCompletion.Defer(
            status.ErrorCode ?? $"sidecar_{status.State.ToString().ToLowerInvariant()}",
            $"The job is waiting for sidecar '{status.Id}' to become ready.",
            retryDelay ?? TimeSpan.FromMinutes(5));
    }
}
