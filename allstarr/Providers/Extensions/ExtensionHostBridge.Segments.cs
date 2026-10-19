// Adapted from SpotiFLAC-Mobile (MIT), rust_backend/crates/extensions/src/download/segments.rs.
using System.Text.Json;
using allstarr.Core.Extensions;

namespace allstarr.Services.Common;

public partial class ExtensionHostBridge
{
    public object DownloadSegments(string segmentsJson, string output, string optionsJson, Action<long, long, int, int>? onProgress)
    {
        var files = new List<string>();
        var tasks = new List<Task>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            ExtensionArtifactInvocationScope.Current?.CancellationToken ?? CancellationToken.None);
        try
        {
            var scope = RequireFileScope();
            using var document = JsonDocument.Parse(segmentsJson);
            using var optionsDocument = JsonDocument.Parse(optionsJson);
            var options = optionsDocument.RootElement;
            var segments = document.RootElement.EnumerateArray().Select(item => (
                Uri: RequireSegmentUri(item.GetProperty("url").GetString()!.Trim()),
                Headers: item.GetProperty("headers").EnumerateObject().ToDictionary(header => header.Name, header => header.Value.GetString()!))).ToArray();
            string target;
            try { target = scope.ResolveTemporaryPath(output); }
            catch (Exception) { throw new SegmentFailure("permission", 0); }
            int Option(string name, int fallback) => options.ValueKind == JsonValueKind.Object && options.TryGetProperty(name, out var value) &&
                value.ValueKind == JsonValueKind.Number ? (int)Math.Clamp(Math.Round(value.GetDouble()), 1, 8) : fallback;
            var parallel = Option("maxParallel", 4);
            var attempts = Option("maxAttempts", 3);
            var prefix = output + "." + Guid.NewGuid().ToString("N");
            var staged = scope.ResolveTemporaryPath(prefix + ".partial");
            files.Add(staged);
            long received = 0;
            var workers = new Dictionary<Task, (int Index, string Path)>();
            var pending = new SortedDictionary<int, string>();
            using var client = _httpClientFactory.CreateClient("ExtensionSdkV1");
            var next = 0;
            void StartNext()
            {
                var segment = segments[next];
                var index = next++;
                var path = scope.ResolveTemporaryPath(prefix + "." + index);
                files.Add(path);
                var task = DownloadSegmentAsync(client, segment.Uri, segment.Headers, path, attempts, scope.MaximumBytes,
                    count => Interlocked.Add(ref received, count), cancellation.Token);
                tasks.Add(task);
                workers.Add(task, (index, path));
            }
            for (var index = 0; index < Math.Min(parallel, segments.Length); index++) StartNext();
            long written = 0;
            var completed = 0;
            scope.Progressive?.Begin(staged, target, null, null);
            using (var destination = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            {
                while (workers.Count > 0)
                {
                    var ready = Task.WhenAny(workers.Keys).GetAwaiter().GetResult();
                    var (index, path) = workers[ready];
                    workers.Remove(ready);
                    ready.GetAwaiter().GetResult();
                    pending.Add(index, path);
                    while (pending.Remove(completed, out var segmentPath))
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        using (var source = File.OpenRead(segmentPath))
                        {
                            var buffer = new byte[128 * 1024];
                            int count;
                            while ((count = source.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                cancellation.Token.ThrowIfCancellationRequested();
                                destination.Write(buffer, 0, count);
                                destination.Flush();
                                scope.Progressive?.Append(buffer.AsSpan(0, count));
                                written += count;
                            }
                        }
                        File.Delete(segmentPath);
                        completed++;
                        // Only assembly releases a worker slot; only this thread invokes script callbacks.
                        try { onProgress?.Invoke(written, 0, completed, segments.Length); }
                        catch (Exception) { }
                        if (next < segments.Length) StartNext();
                    }
                }
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (scope.Progressive != null) scope.Progressive.Move(staged, target, () => File.Move(staged, target, overwrite: true));
            else File.Move(staged, target, overwrite: true);
            return new { success = true, path = output, size = written, segments = completed };
        }
        catch (Exception exception)
        {
            ExtensionArtifactInvocationScope.Current?.Progressive?.Fail();
            var failure = exception as SegmentFailure ?? new SegmentFailure(exception switch
            {
                OperationCanceledException => "cancelled",
                UnauthorizedAccessException => "permission",
                JsonException or ArgumentException or InvalidOperationException => "invalid_request",
                _ => "storage_error"
            }, 0);
            var result = new Dictionary<string, object>
            {
                ["success"] = false,
                ["error"] = "Segment download failed: " + failure.Kind,
                ["error_type"] = failure.Kind,
                ["attempts"] = failure.Attempts
            };
            if (failure.Status != null) result["http_status"] = failure.Status.Value;
            if (failure.RetryAfter != null) result["retry_after_seconds"] = failure.RetryAfter.Value;
            return result;
        }
        finally
        {
            cancellation.Cancel();
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
            catch (Exception) { }
            foreach (var file in files)
            {
                try { File.Delete(file); }
                catch (IOException) { }
            }
        }
    }

    private Uri RequireSegmentUri(string url)
    {
        if (!OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) ||
            uri!.Scheme != Uri.UriSchemeHttps || !IsNetworkAllowed(uri))
            throw new SegmentFailure("permission", 0);
        return uri;
    }

    private async Task DownloadSegmentAsync(HttpClient client, Uri uri, Dictionary<string, string> headers,
        string path, int maximumAttempts, long maximumBytes, Func<long, long> accountBytes, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            var attemptHeaders = headers;
            long written = 0;
            SegmentFailure? failure = null;
            try
            {
                var current = uri;
                for (var redirect = 0; ; redirect++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, current);
                    AddHeaders(request, attemptHeaders);
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                    if (response.RequestMessage?.RequestUri is not { } finalUri)
                        throw new SegmentFailure("permission", attempt);
                    RequireSegmentUri(finalUri.AbsoluteUri);
                    var status = (int)response.StatusCode;
                    if (status is 301 or 302 or 303 or 307 or 308)
                    {
                        if (redirect >= 5 || response.Headers.Location == null) throw new SegmentFailure("http_error", attempt, status);
                        var redirected = RequireSegmentUri(new Uri(current, response.Headers.Location).AbsoluteUri);
                        if (redirected.GetLeftPart(UriPartial.Authority) != current.GetLeftPart(UriPartial.Authority))
                            attemptHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        current = redirected;
                        continue;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var retryAfter = response.Headers.RetryAfter;
                        var delay = retryAfter?.Delta?.TotalSeconds ??
                            (retryAfter?.Date is { } date ? Math.Max(0, (date - DateTimeOffset.UtcNow).TotalSeconds) : (double?)null);
                        throw new SegmentFailure(status switch
                        {
                            429 => "rate_limited",
                            401 or 403 => "expired_stream",
                            408 or 425 or >= 500 => "transient_network",
                            _ => "http_error"
                        }, attempt, status, delay);
                    }
                    if (response.Content.Headers.ContentLength > maximumBytes) throw new SegmentFailure("storage_error", attempt);
                    using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                    using var destination = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true);
                    var buffer = new byte[65536];
                    int count;
                    while ((count = await source.ReadAsync(buffer, cancellationToken)) > 0)
                    {
                        written += count;
                        if (accountBytes(count) > maximumBytes) throw new SegmentFailure("storage_error", attempt);
                        await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
                    }
                    if (written == 0 || response.Content.Headers.ContentLength is { } expected && written != expected)
                        throw new SegmentFailure("transient_network", attempt);
                    return;
                }
            }
            catch (SegmentFailure error) { failure = error; }
            catch (HttpRequestException) { failure = new SegmentFailure("transient_network", attempt); }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { failure = new SegmentFailure("transient_network", attempt); }
            if (written != 0) accountBytes(-written);
            if (attempt == maximumAttempts || failure.Kind is not ("transient_network" or "rate_limited")) throw failure;
            await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(failure.RetryAfter ?? 0.5 * Math.Pow(2, attempt - 1), 0, 10)), cancellationToken);
        }
    }

    private sealed class SegmentFailure(string kind, int attempts, int? status = null, double? retryAfter = null) : Exception
    {
        public string Kind { get; } = kind;
        public int Attempts { get; } = attempts;
        public int? Status { get; } = status;
        public double? RetryAfter { get; } = retryAfter;
    }
}
