namespace allstarr.Core.Jobs;

public sealed class DurableJobOptions
{
    public const string SectionName = "Jobs";

    public int DefaultMaxAttempts { get; set; } = 5;
    public int DefaultMaxDeferrals { get; set; } = 96;
    public int MaxOutboxAttempts { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 60;
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

        if (MaxOutboxAttempts is < 1 or > 10000)
        {
            throw new InvalidOperationException("Jobs:MaxOutboxAttempts must be between 1 and 10000.");
        }

        if (LeaseSeconds is < 5 or > 3600)
        {
            throw new InvalidOperationException("Jobs:LeaseSeconds must be between 5 and 3600.");
        }

        if (PollIntervalMilliseconds is < 50 or > 60000)
        {
            throw new InvalidOperationException("Jobs:PollIntervalMilliseconds must be between 50 and 60000.");
        }

        if (MaxPayloadBytes is < 1024 or > 4 * 1024 * 1024)
        {
            throw new InvalidOperationException("Jobs:MaxPayloadBytes must be between 1024 and 4194304.");
        }
    }
}
