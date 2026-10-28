using System.Net;
using allstarr.Core.Capabilities;

namespace allstarr.Core.Extensions;

public sealed partial class ExtensionDownloadStreamingCapabilityAdapter
{
    private readonly TimeProvider clock;
    private readonly bool allowsProgressive;
    private readonly bool allowsImmutableFlac;
    private readonly ExtensionDeezerSpeculation? deezerSpeculation;
    private readonly object sessionsGate = new();
    private readonly Dictionary<SessionKey, DownloadSession> sessions = [];
    private bool disposed;
    private Task? disposal;

    private sealed record SessionKey(ProviderActorContext Actor, ProviderAccountContext? Account, string Policy,
        string Track, ProviderAudioQuality Quality);

    public async Task<ProviderOutcome<ProviderStreamLease>> GetStreamLeaseAsync(
        ProviderExecutionContext context, ProviderStreamLeaseRequest request)
    {
        context.RequireResourceOwner(request.TrackId, ProviderResourceKind.Track);
        if (!context.Policy.AllowManagedDownloads || context.ProviderId != ProviderId || !context.Policy.AllowsProvider(ProviderId))
            return Failure<ProviderStreamLease>(ProviderErrorKind.Forbidden);
        if (context.CancellationToken.IsCancellationRequested || context.PlaybackAbandonment.IsCancellationRequested)
            return Failure<ProviderStreamLease>(ProviderErrorKind.Canceled);
        if (context.IsExpired(clock.GetUtcNow())) return Failure<ProviderStreamLease>(ProviderErrorKind.CapabilityUnavailable);
        var sessionPolicy = new ProviderExecutionPolicy(context.Policy.Quality, context.Policy.ExplicitContent,
            context.Policy.AllowFallback, context.Policy.AllowSharedAccount, context.Policy.AllowManagedDownloads, [ProviderId]);
        var key = new SessionKey(context.Actor, context.Account, System.Text.Json.JsonSerializer.Serialize(sessionPolicy), request.TrackId.Value, request.RequestedQuality);
        DownloadSession session;
        Listener listener;
        lock (sessionsGate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!sessions.TryGetValue(key, out session!))
            {
                session = new DownloadSession(new ExtensionProgressiveTransfer(clock, allowsProgressive, deezerSpeculation, allowsImmutableFlac),
                    context.Remaining(clock.GetUtcNow()), context.PlaybackTransferDeadline, clock);
                var created = session;
                session.IdleTimer = clock.CreateTimer(_ => Abandon(key, created), null,
                    Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                sessions.Add(key, session);
            }
            session.IdleTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            session.IdleScheduled = false;
            session.Listeners++;
            listener = new Listener(() => Release(key, session));
            if (session.Completion == null)
            {
                var downloadContext = new ProviderExecutionContext(context.Actor, context.ProviderId, context.Account, context.Policy,
                    context.OperationId, context.CorrelationId, session.Deadline ?? context.Deadline, session.Cancellation.Token, context.IdempotencyKey);
                session.Completion = RunSessionAsync(downloadContext, request, key, session);
            }
        }
        listener.Register(context.CancellationToken, session.Cancellation.Token, context.PlaybackAbandonment,
            () => Abandon(key, session));
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, context.PlaybackAbandonment);
        try
        {
            await Task.WhenAny(session.Transfer.Ready.Task, session.Completion).WaitAsync(waiting.Token);
            ProviderStreamLease prepared;
            if (session.Transfer.Published && !session.Completion.IsCompleted)
            {
                prepared = new ProviderStreamLease($"download-{Guid.CreateVersion7():N}",
                    new Uri("https://extension-download.invalid/progressive"), clock.GetUtcNow().AddMinutes(5),
                    true, true,
                    new ProviderMediaFormat(session.Transfer.MimeType, session.Transfer.Container,
                        session.Transfer.Container is "flac" or "mp3" ? session.Transfer.Container : "unknown"),
                    ProviderStreamRetryBehavior.DoNotRetry,
                    (message, token) => session.Transfer.OpenAsync(message, token));
            }
            else
            {
                var result = await session.Completion.WaitAsync(waiting.Token);
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
            if (context.PlaybackAbandonment.IsCancellationRequested) Abandon(key, session);
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
            session.Succeeded = result.IsSuccess;
            if (session.Listeners == 0)
            {
                if (!result.IsSuccess || session.Cancellation.IsCancellationRequested) CleanupSession(key, session);
                else RetainIdleSession(session);
            }
        }
        return result;
    }

    private void Release(SessionKey key, DownloadSession session)
    {
        lock (sessionsGate)
        {
            if (--session.Listeners != 0) return;
            if (session.Finished && !session.Succeeded) CleanupSession(key, session);
            else RetainIdleSession(session);
        }
    }

    private static void RetainIdleSession(DownloadSession session)
    {
        if (session.IdleScheduled) return;
        session.IdleScheduled = true;
        session.IdleTimer?.Change(TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
    }

    private void Abandon(SessionKey key, DownloadSession session)
    {
        lock (sessionsGate)
        {
            if (sessions.TryGetValue(key, out var current) && ReferenceEquals(current, session) && session.Listeners == 0)
            {
                session.IdleScheduled = false;
                if (session.Finished) CleanupSession(key, session);
                else session.Cancellation.Cancel();
            }
        }
    }

    private void CleanupSession(SessionKey key, DownloadSession session)
    {
        if (!sessions.TryGetValue(key, out var current) || !ReferenceEquals(current, session)) return;
        sessions.Remove(key);
        session.Transfer.Cleanup();
        session.CleanupWorkspace?.Invoke();
        session.IdleTimer?.Dispose();
        session.ReadinessTimer?.Dispose();
        session.Cancellation.Dispose();
    }

    public ValueTask DisposeAsync()
    {
        lock (sessionsGate)
        {
            if (disposal == null)
            {
                disposed = true;
                var active = sessions.Values.ToArray();
                foreach (var session in active)
                {
                    session.IdleTimer?.Dispose();
                    session.ReadinessTimer?.Dispose();
                    session.Cancellation.Cancel();
                }
                disposal = FinishDisposalAsync(active);
            }
            return new ValueTask(disposal);
        }
    }

    private async Task FinishDisposalAsync(DownloadSession[] active)
    {
        await Task.WhenAll(active.Select(session => session.Completion!)).ConfigureAwait(false);
        lock (sessionsGate)
            foreach (var (key, session) in sessions.ToArray()) CleanupSession(key, session);
    }

    private sealed class DownloadSession
    {
        public DownloadSession(ExtensionProgressiveTransfer transfer, TimeSpan remaining,
            DateTimeOffset? transferDeadline, TimeProvider clock)
        {
            Transfer = transfer;
            if (transferDeadline is { } fullDeadline && fullDeadline - clock.GetUtcNow() > remaining)
            {
                Deadline = fullDeadline;
                Cancellation = new(fullDeadline - clock.GetUtcNow(), clock);
                ReadinessTimer = clock.CreateTimer(_ =>
                {
                    if (!Transfer.Published && !Volatile.Read(ref Finished))
                    {
                        try { Cancellation.Cancel(); }
                        catch (ObjectDisposedException) { }
                    }
                }, null, remaining, Timeout.InfiniteTimeSpan);
            }
            else Cancellation = new(remaining, clock);
        }

        public ExtensionProgressiveTransfer Transfer { get; }
        public DateTimeOffset? Deadline { get; }
        public CancellationTokenSource Cancellation { get; }
        public ITimer? ReadinessTimer { get; }
        public Task<ProviderOutcome<ProviderStreamLease>>? Completion { get; set; }
        public Action? CleanupWorkspace { get; set; }
        public ITimer? IdleTimer { get; set; }
        public int Listeners { get; set; }
        public bool Finished;
        public bool Succeeded { get; set; }
        public bool IdleScheduled { get; set; }
    }

    private sealed class Listener(Action close) : IDisposable
    {
        private int disposed;
        private int opened;
        private CancellationTokenRegistration registration;
        private CancellationTokenRegistration expiryRegistration;
        private CancellationTokenRegistration abandonmentRegistration;
        private CancellationTokenSource? responseCancellation;
        public void Register(CancellationToken token, CancellationToken expiry, CancellationToken abandonment, Action abandon)
        {
            registration = token.Register(Dispose);
            expiryRegistration = expiry.Register(() => { if (Volatile.Read(ref opened) == 0) Dispose(); });
            abandonmentRegistration = abandonment.Register(() => { Dispose(); abandon(); });
            if (Volatile.Read(ref disposed) != 0)
            { registration.Unregister(); expiryRegistration.Unregister(); abandonmentRegistration.Unregister(); }
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
            abandonmentRegistration.Unregister();
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
