using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Acornima;
using Acornima.Ast;
using allstarr.Services.Common;

namespace allstarr.Core.Extensions;

internal sealed partial class ExtensionProgressiveTransfer(TimeProvider clock, bool eligible, ExtensionDeezerSpeculation? speculation = null,
    bool immutableFlacEligible = false)
{
    private readonly TimeProvider timeProvider = clock;
    private readonly object gate = new();
    private TaskCompletionSource changed = Signal();
    private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private string? path;
    private string? committedPath;
    private byte[]? digest;
    private readonly MemoryStream header = new();
    private long written;
    private bool finished;
    private bool failed;
    private int transfers;
    private Action? cleanup;
    private FileStream? immutableCopy;
    public bool CanCaptureImmutableFlac => immutableFlacEligible && !eligible;
    public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public string MimeType { get; private set; } = "application/octet-stream";
    public string Container { get; private set; } = "unknown";
    public long? Total { get; private set; }
    public bool Published => Ready.Task.IsCompletedSuccessfully && Ready.Task.Result;

    // A source that can acquire a writable file capability must finish before playback.
    internal static bool IsEligible(string script) => IsEligible(script, false);

    internal static bool IsImmutableFlacEligible(string script) => IsEligible(script, true);

    private static bool IsEligible(string script, bool immutableFlac)
    {
        try
        {
            bool Visit(Node node, Node? parent)
            {
                if (node is ThisExpression) return false;
                if (node is MemberExpression { Property: Identifier { Name: "constructor" } }) return false;
                if (node is Identifier identifier)
                {
                    if (identifier.Name is "host" or "ffmpeg" or "eval" or "Function" or "globalThis" or "window" or "global") return false;
                    if (identifier.Name == "file" && !(immutableFlac && parent is UnaryExpression) &&
                        (parent is not MemberExpression member || member.Object != node || member.Computed ||
                         member.Property is not Identifier property ||
                         property.Name is not ("download" or "downloadSegments" or "exists" or "getSize" or "readBytes") &&
                         !(immutableFlac && property.Name == "delete"))) return false;
                }
                if (node is StringLiteral { Value: "file" or "host" or "ffmpeg" or "decryption" or "decryption_key" or "encrypted" or "needs_decryption" or "encryption_scheme" or "requires_container_conversion" }) return false;
                if (node is Identifier { Name: "decryption" or "decryption_key" or "encrypted" or "needs_decryption" or "encryption_scheme" or "requires_container_conversion" }) return false;
                foreach (var child in node.ChildNodes)
                    if (!Visit(child, node)) return false;
                return true;
            }
            return Visit(new Parser().ParseScript(script), null);
        }
        catch { return false; }
    }

    public void Begin(string sourcePath, string finalPath, long? total, string? mimeType, bool decrypted = false) =>
        Begin(sourcePath, finalPath, total, mimeType, decrypted, null);

    public void BeginImmutableFlac(FileStream capture, string finalPath, long? total) =>
        Begin(capture.Name, finalPath, total, "audio/flac", false, capture);

    private void Begin(string sourcePath, string finalPath, long? total, string? mimeType, bool decrypted, FileStream? capture)
    {
        lock (gate)
        {
            if (++transfers > 1 && Published)
                throw new IOException("A published download cannot be replaced by another transfer.");
            if ((!eligible && !decrypted && !(capture != null && CanCaptureImmutableFlac)) || transfers != 1)
            {
                path = null;
                Ready.TrySetResult(false);
                return;
            }
            var media = mimeType?.ToLowerInvariant() switch
            {
                "audio/flac" or "audio/x-flac" => ("audio/flac", "flac"),
                "audio/mpeg" or "audio/mp3" => ("audio/mpeg", "mp3"),
                "audio/ogg" or "application/ogg" or "audio/opus" => ("audio/ogg", "ogg"),
                "audio/mp4" or "video/mp4" or "audio/x-m4a" => ("audio/mp4", "mp4"),
                "audio/webm" or "video/webm" => ("audio/webm", "webm"),
                _ => Path.GetExtension(finalPath).ToLowerInvariant() switch
                {
                    ".flac" => ("audio/flac", "flac"),
                    ".mp3" => ("audio/mpeg", "mp3"),
                    ".ogg" or ".opus" => ("audio/ogg", "ogg"),
                    ".m4a" or ".mp4" => ("audio/mp4", "mp4"),
                    ".webm" => ("audio/webm", "webm"),
                    _ => ("application/octet-stream", "unknown")
                }
            };
            path = sourcePath;
            committedPath = finalPath;
            immutableCopy = capture;
            Total = total is > 0 ? total : null;
            (MimeType, Container) = media;
        }
    }

    public void Move(string source, string target, Action move)
    {
        lock (gate)
        {
            move();
            if (path == source) path = target;
        }
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        lock (gate)
        {
            if (path == null) return;
            if (immutableCopy != null)
            {
                immutableCopy.Write(bytes);
                immutableCopy.Flush();
            }
            hash.AppendData(bytes);
            written += bytes.Length;
            if (header.Length < 256 * 1024) header.Write(bytes[..(int)Math.Min(bytes.Length, 256 * 1024 - header.Length)]);
            var prefix = header.GetBuffer().AsSpan(0, (int)header.Length);
            if (Container == "unknown")
            {
                (MimeType, Container) = prefix.StartsWith("fLaC"u8) ? ("audio/flac", "flac") :
                    prefix.StartsWith("OggS"u8) ? ("audio/ogg", "ogg") :
                    prefix.StartsWith("ID3"u8) ? ("audio/mpeg", "mp3") :
                    prefix.Length >= 8 && prefix.Slice(4, 4).SequenceEqual("ftyp"u8) ? ("audio/mp4", "mp4") :
                    (MimeType, Container);
            }
            var validFlac = encryptedPath == null && immutableCopy == null ||
                (prefix.Length >= 42 && prefix.StartsWith("fLaC"u8) && (prefix[4] & 0x7f) == 0 &&
                 prefix[5] == 0 && prefix[6] == 0 && prefix[7] == 34);
            if (written >= 256 * 1024 && validFlac && Container != "unknown" && (Container != "mp4" || HasMp4Metadata(prefix)))
                Ready.TrySetResult(true);
            Pulse();
        }
    }

    private static bool HasMp4Metadata(ReadOnlySpan<byte> bytes)
    {
        var offset = 0;
        while (bytes.Length - offset >= 8)
        {
            var box = bytes[offset..];
            ulong size = BinaryPrimitives.ReadUInt32BigEndian(box);
            var minimum = 8;
            if (size == 1)
            {
                if (box.Length < 16) return false;
                size = BinaryPrimitives.ReadUInt64BigEndian(box[8..]);
                minimum = 16;
            }
            if (box.Slice(4, 4).SequenceEqual("mdat"u8)) return false;
            if (size < (ulong)minimum || size > (ulong)box.Length) return false;
            if (box.Slice(4, 4).SequenceEqual("moov"u8))
            {
                var metadata = box[..(int)size];
                return metadata.IndexOf("sinf"u8) < 0 && metadata.IndexOf("enca"u8) < 0 &&
                    metadata.IndexOf("encv"u8) < 0 && metadata.IndexOf("pssh"u8) < 0 && metadata.IndexOf("tenc"u8) < 0;
            }
            offset += (int)size;
        }
        return false;
    }

    public void Verify(string finalPath, string sha256, long size)
    {
        lock (gate)
        {
            if (!Published) return;
            digest ??= hash.GetHashAndReset();
            if (failed || (encryptedPath != null && !transformConfirmed) || finalPath != committedPath || written != size ||
                !Convert.ToHexString(digest).Equals(sha256, StringComparison.OrdinalIgnoreCase))
            {
                RejectSpeculation("artifact verification mismatch");
                throw new InvalidDataException("The final artifact differs from the published download.");
            }
        }
    }

    public void Fail()
    {
        lock (gate) { failed = true; Pulse(); }
    }

    public void BeforeDelete(string target)
    {
        lock (gate)
        {
            if (!Published || immutableCopy == null || target != committedPath) return;
            failed = true;
            Pulse();
            throw new InvalidDataException("The published download cannot be deleted before verification.");
        }
    }

    public void Finish(bool success)
    {
        lock (gate)
        {
            finished = true;
            failed |= !success;
            Ready.TrySetResult(false);
            Pulse();
        }
    }

    public void RetainTemporaryFiles(Action remove)
    {
        lock (gate) cleanup = remove;
    }

    public void Cleanup()
    {
        Action? remove;
        lock (gate) { remove = cleanup; cleanup = null; }
        immutableCopy?.Dispose();
        remove?.Invoke();
        hash.Dispose();
        header.Dispose();
        if (predictedKey != null) CryptographicOperations.ZeroMemory(predictedKey);
    }

    public async Task<HttpResponseMessage> OpenAsync(HttpRequestMessage request, CancellationToken token)
    {
        if (request.Method != HttpMethod.Head && request.Headers.Range != null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60), timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, timeout.Token);
            try
            {
                while (true)
                {
                    Task wait;
                    lock (gate)
                    {
                        if (failed) throw new IOException("The download failed.");
                        if (Total.HasValue || finished) break;
                        wait = changed.Task;
                    }
                    await wait.WaitAsync(linked.Token);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("The download did not establish its length before the seek timeout.");
            }
        }
        return Open(request, token);
    }

    public HttpResponseMessage Open(HttpRequestMessage request, CancellationToken token)
    {
        long? length;
        lock (gate)
        {
            if (failed) throw new IOException("The download failed.");
            length = Total ?? (finished ? written : null);
        }
        var range = request.Method == HttpMethod.Head ? null : request.Headers.Range;
        long start = 0, end = (length ?? long.MaxValue) - 1;
        var status = HttpStatusCode.OK;
        HttpContent content;
        if (range != null && length != null && !StreamByteRanges.TryResolve(range, length.Value, out start, out end))
        {
            var valid = StreamByteRanges.IsSingleByteRange(range);
            var invalid = new HttpResponseMessage(valid ? HttpStatusCode.RequestedRangeNotSatisfiable : HttpStatusCode.BadRequest)
            { Content = new ByteArrayContent([]) };
            if (valid) invalid.Content.Headers.ContentRange = new ContentRangeHeaderValue(length!.Value);
            return invalid;
        }
        if (range != null && length != null) status = HttpStatusCode.PartialContent;
        if (request.Method == HttpMethod.Head) content = length.HasValue ? new ByteArrayContent([]) : new HeadersContent();
        else content = new StreamContent(new GrowingStream(this, start, length.HasValue ? end - start + 1 : null, token));
        content.Headers.ContentType = new MediaTypeHeaderValue(MimeType);
        if (length.HasValue) content.Headers.ContentLength = end - start + 1;
        if (status == HttpStatusCode.PartialContent) content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, length!.Value);
        var response = new HttpResponseMessage(status) { Content = content };
        if (length.HasValue) response.Headers.AcceptRanges.Add("bytes");
        return response;
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Pulse() { var prior = changed; changed = Signal(); prior.TrySetResult(); }

    private sealed class HeadersContent : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => Task.CompletedTask;
    }

    private sealed class GrowingStream : Stream
    {
        private readonly ExtensionProgressiveTransfer owner;
        private readonly FileStream file;
        private readonly long? length;
        private readonly CancellationToken token;
        private long remaining;

        public GrowingStream(ExtensionProgressiveTransfer owner, long start, long? length, CancellationToken token)
        {
            this.owner = owner;
            this.length = length;
            this.token = token;
            remaining = length ?? long.MaxValue;
            lock (owner.gate)
                file = new FileStream(owner.path!, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.RandomAccess);
            file.Position = start;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (buffer.IsEmpty) return 0;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            var waitingStarted = owner.timeProvider.GetTimestamp();
            var waitingSince = waitingStarted;
            long observedLength = 0;
            while (true)
            {
                Task wait;
                int count;
                lock (owner.gate)
                {
                    if (owner.failed) throw new IOException("The download failed before verification completed.");
                    if (owner.written > observedLength)
                    {
                        observedLength = owner.written;
                        waitingSince = owner.timeProvider.GetTimestamp();
                    }
                    // Even a finite range waits for verification before reporting its end.
                    if (owner.finished && (remaining == 0 || file.Position >= owner.written)) return 0;
                    count = (int)Math.Min(buffer.Length, Math.Min(remaining, Math.Max(0, owner.written - file.Position)));
                    // Fixed-length responses must remain incomplete until the artifact is verified.
                    if (!owner.finished && length.HasValue) count = (int)Math.Min(count, Math.Max(0, remaining - 1));
                    wait = owner.changed.Task;
                }
                if (count > 0)
                {
                    var read = await file.ReadAsync(buffer[..count], linked.Token);
                    if (read == 0) throw new IOException("The growing download became unavailable.");
                    remaining -= read;
                    return read;
                }
                var remainingWait = TimeSpan.FromSeconds(60) - owner.timeProvider.GetElapsedTime(waitingStarted);
                var stallWait = TimeSpan.FromSeconds(30) - owner.timeProvider.GetElapsedTime(waitingSince);
                if (stallWait < remainingWait) remainingWait = stallWait;
                if (remainingWait <= TimeSpan.Zero) throw new IOException("The download did not reach the requested offset before the read deadline.");
                try { await wait.WaitAsync(remainingWait, owner.timeProvider, linked.Token); }
                catch (TimeoutException)
                {
                    // Recheck published bytes and completion when a notification races the deadline.
                }
            }
        }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length ?? throw new NotSupportedException();
        public override long Position { get => file.Position; set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { if (disposing) file.Dispose(); base.Dispose(disposing); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
