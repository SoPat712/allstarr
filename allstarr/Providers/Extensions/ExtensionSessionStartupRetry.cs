namespace allstarr.Core.Extensions;

internal static class ExtensionSessionStartupRetry
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(60)
    ];

    public static async Task<bool> RunAsync(
        Func<CancellationToken, Task<bool>> operation,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var networkNotReady = await operation(cancellationToken);
        foreach (var delay in Delays)
        {
            if (!networkNotReady) break;
            await Task.Delay(delay, timeProvider, cancellationToken);
            networkNotReady = await operation(cancellationToken);
        }
        return networkNotReady;
    }
}
