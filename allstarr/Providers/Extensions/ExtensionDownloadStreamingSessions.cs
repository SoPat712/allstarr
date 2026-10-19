using System.Net;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Extensions;

public sealed partial class ExtensionDownloadStreamingCapabilityAdapter
{
    private readonly TimeProvider clock;
    private readonly bool allowsProgressive;
    private readonly ExtensionDeezerSpeculation? deezerSpeculation;
    private readonly object sessionsGate = new();
    private readonly Dictionary<SessionKey, DownloadSession> sessions = [];

    private sealed record SessionKey(ProviderActorContext Actor, ProviderAccountContext? Account, string Policy,
        string Track, ProviderAudioQuality Quality);

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        if (!context.Policy.AllowManagedDownloads || context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId))
            return Failure<ProviderStreamLease>(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested) return Failure<ProviderStreamLease>(ProviderErrorKind.Canceled);
        if (context.IsExpired(DateTimeOffset.UtcNow)) return Failure<ProviderStreamLease>(ProviderErrorKind.CapabilityUnavailable);
        var key = new SessionKey(context.Actor, context.Account, System.Text.Json.JsonSerializer.Serialize(context.Policy), request.TrackId.Value, request.RequestedQuality);
        DownloadSession session;
        Listener listener;
        lock (sessionsGate)
        {
            if (!sessions.TryGetValue(key, out session!))
            {
                session = new DownloadSession(new ExtensionProgressiveTransfer(clock, allowsProgressive, deezerSpeculation),
                    context.Remaining(clock.GetUtcNow()), clock);
                sessions.Add(key, session);
            }
            session.Listeners++;
            listener = new Listener(() => Release(key, session));
            if (session.Completion == null)
            {
                var downloadContext = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
                    context.OperationId, context.CorrelationId, context.Deadline, session.Cancellation.Token, context.IdempotencyKey);
                session.Completion = RunSessionAsync(downloadContext, request, key, session);
            }
        }
        listener.Register(context.CancellationToken, session.Cancellation.Token);
        try
        {
            await Task.WhenAny(session.Transfer.Ready.Task, session.Completion).WaitAsync(context.CancellationToken);
            ProviderStreamLease prepared;
            if (session.Transfer.Published && !session.Completion.IsCompleted)
            {
                var total = session.Transfer.Total;
                prepared = new ProviderStreamLease($"download-{Guid.CreateVersion7():N}",
                    new Uri("https://extension-download.invalid/progressive"), clock.GetUtcNow().AddMinutes(5),
                    total.HasValue, total.HasValue,
                    new ProviderMediaFormat(session.Transfer.MimeType, session.Transfer.Container,
                        session.Transfer.Container is "flac" or "mp3" ? session.Transfer.Container : "unknown", bitrate: 1337),
                    ProviderStreamRetryBehavior.DoNotRetry,
                    (message, token) => Task.FromResult(session.Transfer.Open(message, token)));
            }
            else
            {
                var result = await session.Completion.WaitAsync(context.CancellationToken);
                if (!result.IsSuccess)
                {
                    listener.Dispose();
                    return result;
                }
                prepared = result.RequireValue();
            }
            var opened = 0;
            async Task<HttpResponseMessage> OpenAsync(HttpRequestMessage message, CancellationToken token)
            {
                if (Interlocked.Exchange(ref opened, 1) != 0)
                    throw new InvalidOperationException("A prepared extension stream lease can only be opened once.");
                try
                {
                    listener.MarkOpened();
                    token.ThrowIfCancellationRequested();
                    context.CancellationToken.ThrowIfCancellationRequested();
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, token);
                    listener.SetCancellation(cancellation);
                    var response = await prepared.ProtectedResponseFactory!(message, cancellation.Token);
                    response.Content = new ListenerContent(response.Content, listener);
                    return response;
                }
                catch { listener.Dispose(); throw; }
            }
            return ProviderOutcome<ProviderStreamLease>.Success(new(prepared.LeaseId, prepared.ProtectedSourceUri,
                prepared.ExpiresAt, prepared.SupportsByteRanges, prepared.SupportsSeeking, prepared.Media,
                prepared.RetryBehavior, OpenAsync, prepared.QualityDowngradeReason));
        }
        catch (OperationCanceledException)
        {
            listener.Dispose();
            bool last;
            lock (sessionsGate) last = session.Listeners == 0;
            if (last) await session.Completion;
            return Failure<ProviderStreamLease>(ProviderErrorKind.Canceled);
        }
        catch
        {
            listener.Dispose();
            return Failure<ProviderStreamLease>(ProviderErrorKind.TransientFailure);
        }
    }

    private async Task<ProviderOutcome<ProviderStreamLease>> RunSessionAsync(
        ProviderExecutionContext context, ProviderStreamLeaseRequest request, SessionKey key, DownloadSession session)
    {
        ProviderOutcome<ProviderStreamLease> result;
        try { result = await PrepareStreamAsync(context, request, session); }
        catch { result = Failure<ProviderStreamLease>(ProviderErrorKind.TransientFailure); }
        session.Transfer.Finish(result.IsSuccess);
        lock (sessionsGate)
        {
            session.Finished = true;
            if (session.Listeners == 0) CleanupSession(key, session);
        }
        return result;
    }

    private void Release(SessionKey key, DownloadSession session)
    {
        lock (sessionsGate)
        {
            if (--session.Listeners != 0) return;
            if (session.Finished) CleanupSession(key, session);
            else session.Cancellation.Cancel();
        }
    }

    private void CleanupSession(SessionKey key, DownloadSession session)
    {
        sessions.Remove(key);
        session.Transfer.Cleanup();
        session.CleanupWorkspace?.Invoke();
        session.Cancellation.Dispose();
    }

    private sealed class DownloadSession(ExtensionProgressiveTransfer transfer, TimeSpan remaining, TimeProvider clock)
    {
        public ExtensionProgressiveTransfer Transfer { get; } = transfer;
        public CancellationTokenSource Cancellation { get; } = new(remaining, clock);
        public Task<ProviderOutcome<ProviderStreamLease>>? Completion { get; set; }
        public Action? CleanupWorkspace { get; set; }
        public int Listeners { get; set; }
        public bool Finished { get; set; }
    }

    private sealed class Listener(Action close) : IDisposable
    {
        private int disposed;
        private int opened;
        private CancellationTokenRegistration registration;
        private CancellationTokenRegistration expiryRegistration;
        private CancellationTokenSource? responseCancellation;
        public void Register(CancellationToken token, CancellationToken expiry)
        {
            registration = token.Register(Dispose);
            expiryRegistration = expiry.Register(() => { if (Volatile.Read(ref opened) == 0) Dispose(); });
            if (Volatile.Read(ref disposed) != 0) { registration.Unregister(); expiryRegistration.Unregister(); }
        }
        public void MarkOpened()
        {
            Interlocked.Exchange(ref opened, 1);
            if (Volatile.Read(ref disposed) != 0) throw new OperationCanceledException("The prepared download lease has expired.");
        }
        public void SetCancellation(CancellationTokenSource cancellation)
        {
            Interlocked.Exchange(ref responseCancellation, cancellation)?.Dispose();
            if (Volatile.Read(ref disposed) != 0) Interlocked.Exchange(ref responseCancellation, null)?.Dispose();
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            registration.Unregister();
            expiryRegistration.Unregister();
            var cancellation = Interlocked.Exchange(ref responseCancellation, null);
            cancellation?.Cancel();
            cancellation?.Dispose();
            close();
        }
    }

    private sealed class ListenerContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly Listener listener;
        public ListenerContent(HttpContent inner, Listener listener)
        {
            this.inner = inner;
            this.listener = listener;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length)
        {
            length = inner.Headers.ContentLength ?? 0;
            return inner.Headers.ContentLength.HasValue;
        }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            try { await inner.CopyToAsync(stream); }
            finally { inner.Dispose(); listener.Dispose(); }
        }
        protected override async Task<Stream> CreateContentReadStreamAsync() =>
            new ListenerStream(await inner.ReadAsStreamAsync(), listener);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); listener.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class ListenerStream(Stream inner, Listener listener) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            inner.ReadAsync(buffer, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) { inner.Dispose(); listener.Dispose(); }
            base.Dispose(disposing);
        }
        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync();
            listener.Dispose();
            GC.SuppressFinalize(this);
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
