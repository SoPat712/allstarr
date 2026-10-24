namespace allstarr.Core.Health;

public sealed record HealthSmokeStep(string Name, string Status, double LatencyMilliseconds, string? Reason = null);
public sealed record HealthSmokeAlertChange(string Step, string State);
public sealed record HealthSmokeAlertDecision(IReadOnlyList<HealthSmokeAlertChange> Changes, IReadOnlyList<string> OpenSteps);

public static class HealthSmokeAlertPolicy
{
    public static HealthSmokeAlertDecision Evaluate(IReadOnlyList<HealthSmokeStep> previous,
        IReadOnlyList<HealthSmokeStep> current, IReadOnlyList<string> openSteps)
    {
        var open = openSteps.ToHashSet(StringComparer.Ordinal);
        var changes = new List<HealthSmokeAlertChange>();
        foreach (var step in current)
        {
            if (step.Status == "failed" && previous.Any(item => item.Name == step.Name && item.Status == "failed") && open.Add(step.Name))
                changes.Add(new(step.Name, "failed"));
            else if (step.Status == "passed" && open.Remove(step.Name))
                changes.Add(new(step.Name, "recovered"));
        }
        return new(changes, open.Order(StringComparer.Ordinal).ToArray());
    }
}

public sealed class HealthSmokeCredentials
{
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public string AccessToken { get; set; } = "";
}

public sealed record HealthSmokeArtifacts(string? ItemId = null, bool? OriginalHeart = null,
    string? PlaylistId = null, string? PlaylistName = null, bool Playing = false);

public sealed record HealthSmokeProbeContext(string Protocol, Guid RunId, string PrincipalId,
    string Query, int FirstByteBudgetSeconds, HealthSmokeCredentials Credentials,
    Func<string, bool> EligibleItem,
    Func<HealthSmokeArtifacts, CancellationToken, Task> SaveArtifacts);
