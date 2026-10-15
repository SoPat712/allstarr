namespace allstarr.Core.Jobs;

public sealed class DurableJobOptions
{
    public const string SectionName = "Jobs";

    public int DefaultMaxAttempts { get; set; } = 5;
    public int DefaultMaxDeferrals { get; set; } = 96;
    public int MaximumConcurrentJobs { get; set; } = 4;
    public int MaxConsecutiveInterruptions { get; set; } = 5;
    public Dictionary<string, int> TypeConcurrencyLimits { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);
    public int PollIntervalMilliseconds { get; set; } = 1000;
    public int MaxPayloadBytes { get; set; } = 256 * 1024;

    public void Validate()
    {
        if (DefaultMaxAttempts is < 1 or > 100)
        {
            throw new InvalidOperationException("Jobs:DefaultMaxAttempts must be between 1 and 100.");
        }

        if (DefaultMaxDeferrals is < 1 or > 10000)
        {
            throw new InvalidOperationException("Jobs:DefaultMaxDeferrals must be between 1 and 10000.");
        }

        if (MaximumConcurrentJobs is < 1 or > 32)
        {
            throw new InvalidOperationException("Jobs:MaximumConcurrentJobs must be between 1 and 32.");
        }

        if (MaxConsecutiveInterruptions is < 1 or > 100)
        {
            throw new InvalidOperationException("Jobs:MaxConsecutiveInterruptions must be between 1 and 100.");
        }

        ValidateTypeConcurrencyLimits();

        if (PollIntervalMilliseconds is < 50 or > 60000)
        {
            throw new InvalidOperationException("Jobs:PollIntervalMilliseconds must be between 50 and 60000.");
        }

        if (MaxPayloadBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("Jobs:MaxPayloadBytes must be between 1024 and 4194304.");
        }
    }

    private void ValidateTypeConcurrencyLimits()
    {
        if (TypeConcurrencyLimits == null)
            throw new InvalidOperationException("Jobs:TypeConcurrencyLimits is required.");

        var normalized = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (rawType, limit) in TypeConcurrencyLimits)
        {
            var type = (rawType ?? string.Empty).Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(type) ||
                type.Length > 200 ||
                !string.Equals(rawType, type, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Jobs:TypeConcurrencyLimits keys must be normalized job types of at most 200 characters.");
            }

            if (!normalized.Add(type))
            {
                throw new InvalidOperationException(
                    $"Jobs:TypeConcurrencyLimits contains duplicate job type '{type}'.");
            }

            if (limit is < 1 || limit > MaximumConcurrentJobs)
            {
                throw new InvalidOperationException(
                    $"Jobs:TypeConcurrencyLimits['{type}'] must be between 1 and MaximumConcurrentJobs.");
            }
        }
    }
}
