using allstarr.Services.Common;

namespace allstarr.Tests;

public sealed class BoundedOperationGateTests
{
    [Fact]
    public async Task TryEnterAsync_RejectsOverflowAndRecoversReleasedCapacity()
    {
        var gate = new BoundedOperationGate(2);
        using var first = await gate.TryEnterAsync();
        var second = await gate.TryEnterAsync();

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Null(await gate.TryEnterAsync());

        second.Dispose();
        second.Dispose();
        using var replacement = await gate.TryEnterAsync();
        Assert.NotNull(replacement);
        Assert.Null(await gate.TryEnterAsync());
    }

    [Fact]
    public async Task TryEnterAsync_ObservesCancellation()
    {
        var gate = new BoundedOperationGate(1);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await gate.TryEnterAsync(cancellation.Token));
    }
}
