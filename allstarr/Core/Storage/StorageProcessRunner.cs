using System.Diagnostics;
using allstarr.Core.Operations;

namespace allstarr.Core.Storage;

public sealed record StorageProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string?> Environment);

public sealed record StorageProcessResult(int ExitCode, string? SafeError);

public interface IStorageProcessRunner
{
    Task<StorageProcessResult> RunAsync(
        StorageProcessRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class StorageProcessRunner : IStorageProcessRunner
{
    public async Task<StorageProcessResult> RunAsync(
        StorageProcessRequest request,
        CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var (key, value) in request.Environment)
        {
            if (value == null)
            {
                startInfo.Environment.Remove(key);
            }
            else
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        var standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            throw;
        }

        _ = await standardOutput;
        var error = await standardError;
        return new StorageProcessResult(
            process.ExitCode,
            process.ExitCode == 0 ? null : SafeOperationalText.Sanitize(error));
    }
}
