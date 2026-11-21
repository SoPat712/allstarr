using System.Net;
using allstarr.Core.ClientApiV2;

namespace allstarr.Tests.Api;

public sealed class V2ConvertedDownloadsTests : IDisposable
{
    private static readonly Uri Address = new("http://127.0.0.1:8080/allstarr/v2/stream/token");
    private readonly string directory = Path.Combine(Path.GetTempPath(), "allstarr-converted-" + Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider time = new(DateTimeOffset.UtcNow);

    public void Dispose()
    {
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }

    [Fact]
    public async Task ACopyIsMadeOnceAndThenFound()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var source = new Converter(async _ => { await release.Task; return Audio(40); });
        var copies = Copies(source);
        Assert.Null(copies.Find(Key(1)));

        // Two requests for the same copy while it is being made share one read of the source.
        var first = copies.MakeAsync(Key(1), Address, default);
        var second = copies.MakeAsync(Key(1), Address, default);
        release.SetResult();
        var made = await first;
        Assert.Equal(200, made.Status);
        Assert.Equal(40, made.File!.Length);
        Assert.Equal(made.File.FullName, (await second).File!.FullName);
        Assert.Equal(1, source.Calls);

        Assert.Equal(40, copies.Find(Key(1))!.Length);
        Assert.Equal(40, (await copies.MakeAsync(Key(1), Address, default)).File!.Length);
        Assert.Equal(1, source.Calls);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.part"));
    }

    [Fact]
    public async Task ABusySourceLeavesNoCopyAndPassesItsWaitOn()
    {
        var copies = Copies(new Converter(_ =>
        {
            var busy = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            busy.Headers.RetryAfter = new(TimeSpan.FromSeconds(7));
            return Task.FromResult(busy);
        }));
        var made = await copies.MakeAsync(Key(1), Address, default);
        Assert.Null(made.File);
        Assert.Equal(503, made.Status);
        Assert.Equal(TimeSpan.FromSeconds(7), made.RetryAfter);
        Assert.Null(copies.Find(Key(1)));
    }

    [Fact]
    public async Task AConversionThatStopsPartWayIsNeverKeptAsTheWholeSong()
    {
        var copies = Copies(new Converter(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Breaking()) })));
        var made = await copies.MakeAsync(Key(1), Address, default);
        Assert.Null(made.File);
        Assert.Equal(503, made.Status);
        Assert.NotNull(made.RetryAfter);
        Assert.Null(copies.Find(Key(1)));
        Assert.Empty(Directory.EnumerateFiles(directory));
    }

    [Fact]
    public async Task TheOldestCopiesMakeRoomButNeverOneThatMayStillBeDownloading()
    {
        var copies = Copies(new Converter(_ => Task.FromResult(Audio(40))), maximumBytes: 100);
        foreach (var number in new[] { 1, 2 })
        {
            Assert.NotNull((await copies.MakeAsync(Key(number), Address, default)).File);
            File.SetLastAccessTimeUtc(Path.Combine(directory, Key(number) + ".audio"), time.GetUtcNow().UtcDateTime);
            time.Advance(TimeSpan.FromMinutes(1));
        }
        // Twenty minutes on, the first copy is read again: a download of it is resuming.
        time.Advance(TimeSpan.FromMinutes(20));
        Assert.NotNull(copies.Find(Key(1)));

        Assert.NotNull((await copies.MakeAsync(Key(3), Address, default)).File);
        Assert.NotNull(copies.Find(Key(1)));
        Assert.Null(copies.Find(Key(2)));
        Assert.NotNull(copies.Find(Key(3)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../etc/passwd")]
    [InlineData("0123456789abcdeg")]
    public async Task OnlyAKeyResolveIssuedNamesACopy(string key)
    {
        var source = new Converter(_ => Task.FromResult(Audio(40)));
        var copies = Copies(source);
        Assert.Null(copies.Find(key));
        Assert.Equal(404, (await copies.MakeAsync(key, Address, default)).Status);
        Assert.Equal(0, source.Calls);
    }

    private V2ConvertedDownloads Copies(Converter source, long maximumBytes = 1 << 20) => new(directory, maximumBytes, new Clients(source), time);

    private static string Key(int number) => number.ToString("x64");

    private static HttpResponseMessage Audio(int length) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[length]) };

    private sealed class Converter(Func<HttpRequestMessage, Task<HttpResponseMessage>> answer) : HttpMessageHandler
    {
        private int calls;
        public int Calls => calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            return answer(request);
        }
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private sealed class Breaking : Stream
    {
        private bool sent;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (sent) throw new IOException("The conversion stopped.");
            sent = true;
            buffer[offset] = 1;
            return 1;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
