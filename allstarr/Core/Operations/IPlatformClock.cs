namespace allstarr.Core.Operations;

public interface IPlatformClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemPlatformClock : IPlatformClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
