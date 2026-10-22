using allstarr.Core.Extensions;

namespace allstarr.Tests;

public sealed class ExtensionMediaProcessDiagnosticsTests
{
    [Fact]
    public async Task MediaProcess_FailureRetainsBoundedSanitizedStderrTail()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExtensionDownloadPostProcessor.RunAsync("/bin/sh",
            ["-c", "exec 1>&2; printf '%s\\n' 'https://media.example.test/track?token=private-value' 'decryption_key=00112233445566778899aabbccddeeff' 'Authorization: Bearer hidden-value'; i=0; while [ $i -lt 200 ]; do printf 'padding for stderr tail %s\\n' $i; i=$((i+1)); done; printf '%s\\n' 'https://media.example.test/track?token=private-value' 'key: 00112233445566778899aabbccddeeff' 'Authorization: Bearer hidden-value' 'invalid input trailer'; exit 7"], null, 0, default));
        Assert.Equal("Download decryption failed.", error.Message);
        var stderr = Assert.IsType<string>(error.Data["MediaStderr"]);
        Assert.InRange(stderr.Length, 1, 4096);
        Assert.Contains("invalid input trailer", stderr);
        Assert.DoesNotContain("private-value", stderr);
        Assert.DoesNotContain("00112233445566778899aabbccddeeff", stderr);
        Assert.DoesNotContain("hidden-value", stderr);
        Assert.DoesNotContain("https://", stderr);
        Assert.Equal(7, error.Data["MediaExitCode"]);
    }

    [Theory]
    [InlineData("access_token=private-value")]
    [InlineData("client_secret: private-value")]
    [InlineData("session-id=private-value")]
    public async Task MediaProcess_StderrRedactsCredentialFieldVariants(string message)
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExtensionDownloadPostProcessor.RunAsync("/bin/sh",
            ["-c", "printf '%s\\n' \"$1\" >&2; exit 1", "stderr-fixture", message], null, 0, default));
        var stderr = Assert.IsType<string>(error.Data["MediaStderr"]);
        Assert.DoesNotContain("private-value", stderr);
    }

    [Fact]
    public async Task MediaProcess_OverlongStderrLineCannotLeakTruncatedSecret()
    {
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => ExtensionDownloadPostProcessor.RunAsync("/bin/sh",
            ["-c", "exec 1>&2; printf 'token='; i=0; while [ $i -lt 1000 ]; do printf 'private-value'; i=$((i+1)); done; printf '\\nfinal error\\n'; exit 1"], null, 0, default));
        var stderr = Assert.IsType<string>(error.Data["MediaStderr"]);
        Assert.InRange(stderr.Length, 1, 4096);
        Assert.Contains("final error", stderr);
        Assert.DoesNotContain("private-value", stderr);
    }

    [Fact]
    public async Task MediaProcess_TimeoutRetainsSanitizedStderrBeforeReaping()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExtensionDownloadPostProcessor.RunAsync("/bin/sh",
            ["-c", "printf '%s\\n' 'invalid media trailer token=private-value' >&2; exec sleep 30"], null, 0, cancellation.Token));
        var stderr = Assert.IsType<string>(error.Data["MediaStderr"]);
        Assert.Contains("invalid media trailer", stderr);
        Assert.DoesNotContain("private-value", stderr);
    }
}
