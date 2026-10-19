using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace allstarr.Tests;

public sealed partial class ExtensionProgressivePlaybackTests
{
    private const string Decrypt = """
        var digest=utils.md5('123'), secret='g4el58wc0zvf9na1', key='';
        for(var i=0;i<16;i++) key+=(digest.charCodeAt(i)^digest.charCodeAt(i+16)^secret.charCodeAt(i)).toString(16).padStart(2,'0');
        var clear=file.transformPatternedBlocks(encrypted,path+'.flac',{
          algorithm:'blowfish',mode:'cbc',operation:'decrypt',key:key,keyEncoding:'hex',
          iv:'0001020304050607',ivEncoding:'hex',padding:'none',segmentSize:2048,
          transformEvery:3,transformOffset:0,transformPartial:false});
        if(!clear.success) throw new Error('decryption failed');
        file.delete(encrypted);
        path=clear.path;
        """;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playback_PatternedFlacReadsBeforeCompletionAndMatchesCommittedArtifact(bool knownLength)
    {
        var (adapter, context, track) = Setup(knownLength, Decrypt, deezer: true);
        var encrypted = EncryptedFlac();
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        Assert.True(lease.SupportsSeeking);
        Assert.True(lease.SupportsByteRanges);
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal("audio/flac", response.Content.Headers.ContentType!.MediaType);
        Assert.Equal(knownLength ? encrypted.Length : null, response.Content.Headers.ContentLength);
        using var body = await response.Content.ReadAsStreamAsync();
        var received = new byte[prefix.Length];
        await body.ReadExactlyAsync(received).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(prefix, received);
        var tail = new byte[4];
        var reading = body.ReadAsync(tail).AsTask();
        Assert.False(reading.IsCompleted);
        source.Release(encrypted[^4..]);
        source.Complete();
        var count = await reading.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.InRange(count, 1, 4);
        await body.ReadExactlyAsync(tail.AsMemory(count));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, tail);
        Assert.Equal(0, await body.ReadAsync(new byte[1]));
        Assert.Equal(prefix.Concat(tail), File.ReadAllBytes(Assert.Single(Directory.GetFiles(options.RootPath, "*.flac", SearchOption.AllDirectories))));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playback_PatternedFlacRangesWaitForFutureBytes(bool knownLength)
    {
        var (adapter, context, track) = Setup(knownLength, Decrypt, deezer: true);
        var encrypted = EncryptedFlac();
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(prefix.Length, prefix.Length + 3);
        var opening = lease.ProtectedResponseFactory!(request, default);
        if (!knownLength) Assert.False(opening.IsCompleted);
        async Task<byte[]> ReadAsync()
        {
            using var response = await opening;
            Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
            Assert.Equal($"bytes {prefix.Length}-{prefix.Length + 3}/{encrypted.Length}",
                response.Content.Headers.ContentRange?.ToString());
            Assert.Equal(4, response.Content.Headers.ContentLength);
            Assert.Contains("bytes", response.Headers.AcceptRanges);
            return await response.Content.ReadAsByteArrayAsync();
        }
        var reading = ReadAsync();
        Assert.False(reading.IsCompleted);
        source.Release(encrypted[^4..]);
        source.Complete();
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, await reading.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Theory]
    [InlineData("key:key", "key:'00000000000000000000000000000000'")]
    [InlineData("transformEvery:3", "transformEvery:2")]
    [InlineData("iv:'0001020304050607'", "iv:'0706050403020100'")]
    [InlineData("transformPartial:false", "transformPartial:true")]
    [InlineData("var digest=", "file.writeBytes(encrypted,'AQIDBA=='); var digest=")]
    [InlineData("path=clear.path;", "path=clear.path; file.writeBytes(path,'AQIDBA==');")]
    [InlineData("path=clear.path;", "path=clear.path; var bytes=file.readBytes(path).data; file.writeBytes(path,'A'+bytes.slice(1));")]
    public async Task Playback_PatternedMismatchFailsWithoutCommitAndDisablesVersion(string before, string after)
    {
        // A stored flag makes the next invocation use correct parameters in the same source version.
        var processing = Decrypt.Replace(before, after);
        processing = "if(storage.get('retry')) {" + Decrypt + "} else { storage.set('retry','yes'); " + processing + "}";
        var (adapter, context, track) = Setup(true, processing, deezer: true);
        var encrypted = EncryptedFlac();
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using (var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri))
        using (var response = await lease.ProtectedResponseFactory!(request, default))
        using (var body = await response.Content.ReadAsStreamAsync())
        {
            await body.ReadExactlyAsync(new byte[prefix.Length]);
            var reading = body.ReadAsync(new byte[5]).AsTask();
            source.Release(encrypted[^4..]);
            source.Complete();
            try { await reading.WaitAsync(TimeSpan.FromSeconds(10)); }
            catch (IOException) { }
            await Assert.ThrowsAnyAsync<IOException>(() => body.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.False(sandbox.DeezerSpeculation!.Enabled, string.Join("\n", logger.Messages));
            Assert.Contains(logger.Messages, message => message.StartsWith("Progressive patterned decryption disabled until restart:", StringComparison.Ordinal));
            Assert.Empty(Directory.GetFiles(options.RootPath, "*.flac", SearchOption.AllDirectories));
        }
        source = new();
        // Reloading the sandbox must not restore speculation before process restart.
        (adapter, context, track) = Setup(true, processing, deezer: true);
        Assert.False(sandbox.DeezerSpeculation!.Enabled);
        pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        source.Release(encrypted[^4..]);
        source.Complete();
        lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var retryRequest = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var retryResponse = await lease.ProtectedResponseFactory!(retryRequest, default);
        Assert.Equal(prefix.Concat(new byte[] { 1, 2, 3, 4 }), await retryResponse.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Playback_PatternedRangeWithholdsItsLastByteUntilVerification()
    {
        var processing = Decrypt.Replace("var clear=", "utils.sleep(1000); var clear=");
        var (adapter, context, track) = Setup(true, processing, deezer: true);
        var encrypted = EncryptedFlac();
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        request.Headers.Range = new RangeHeaderValue(0, 31);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        Assert.Equal(31, await body.ReadAsync(new byte[32]));
        var last = body.ReadAsync(new byte[1]).AsTask();
        Assert.False(last.IsCompleted);
        source.Release(encrypted[^4..]);
        source.Complete();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(1));
        Assert.False(last.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await last.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(0, await body.ReadAsync(new byte[1]));
    }

    [Fact]
    public async Task Playback_PatternedTransformCancellationKeepsSpeculationEnabled()
    {
        var processing = Decrypt.Replace("transformPartial:false});", "transformPartial:false},function(){utils.sleep(1000);});");
        var (adapter, context, track) = Setup(true, processing, deezer: true);
        using var cancellation = new CancellationTokenSource();
        context = new(context.Actor, context.ProviderId, context.Account, context.Policy, context.OperationId,
            context.CorrelationId, context.Deadline, cancellation.Token, context.IdempotencyKey);
        var encrypted = EncryptedFlac();
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        source.Release(encrypted[^4..]);
        source.Complete();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        // A subsequent hook acquires the same interpreter lock after the cancelled transform exits.
        await Task.Run(() => sandbox.InvokeJson("checkAvailability", "{}")).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(sandbox.DeezerSpeculation!.Enabled);
    }

    [Fact]
    public async Task Playback_PatternedExpiredUnopenedLeaseReleasesItsListener()
    {
        var (adapter, context, track) = Setup(true, Decrypt, deezer: true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(EncryptedFlac()[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        clock.Advance(TimeSpan.FromSeconds(61));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lease.ProtectedResponseFactory!(request, default));
    }

    [Fact]
    public async Task Playback_PatternedListenersShareTransferAndSurviveOneBodyDisposal()
    {
        var (adapter, context, track) = Setup(true, Decrypt, deezer: true);
        var encrypted = EncryptedFlac();
        var first = adapter.GetStreamLeaseAsync(context, new(track));
        var second = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        var leases = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        using var request = new HttpRequestMessage(HttpMethod.Get, leases[0].RequireValue().ProtectedSourceUri);
        using var firstResponse = await leases[0].RequireValue().ProtectedResponseFactory!(request, default);
        using var secondResponse = await leases[1].RequireValue().ProtectedResponseFactory!(request, default);
        (await firstResponse.Content.ReadAsStreamAsync()).Dispose();
        Assert.False(source.Canceled.Task.IsCompleted);
        source.Release(encrypted[^4..]);
        source.Complete();
        Assert.Equal(prefix.Concat(new byte[] { 1, 2, 3, 4 }), await secondResponse.Content.ReadAsByteArrayAsync());
        Assert.Equal(1, requests);
    }

    [Fact]
    public async Task Playback_PatternedDisposingLastBodyStopsEncryptedTransfer()
    {
        var (adapter, context, track) = Setup(true, Decrypt, deezer: true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(EncryptedFlac()[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        var body = await response.Content.ReadAsStreamAsync();
        body.Dispose();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.False(source.Canceled.Task.IsCompleted);
        clock.Advance(TimeSpan.FromSeconds(1));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Playback_PatternedDeadlinesFailAnIncompleteListener(bool downloadDeadline)
    {
        var (adapter, context, track) = Setup(true, Decrypt, deezer: true);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(EncryptedFlac()[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        using var body = await response.Content.ReadAsStreamAsync();
        await body.ReadExactlyAsync(new byte[prefix.Length]);
        var reading = body.ReadAsync(new byte[1]).AsTask();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(30));
        clock.Advance(TimeSpan.FromSeconds(downloadDeadline ? 61 : 31));
        await Assert.ThrowsAnyAsync<IOException>(() => reading.WaitAsync(TimeSpan.FromSeconds(10)));
        if (downloadDeadline) await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Playback_PatternedLastListenerCancellationStopsEncryptedTransfer()
    {
        var (adapter, context, track) = Setup(true, Decrypt, deezer: true);
        using var cancellation = new CancellationTokenSource();
        context = new(context.Actor, context.ProviderId, context.Account, context.Policy, context.OperationId,
            context.CorrelationId, context.Deadline, cancellation.Token, context.IdempotencyKey);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(EncryptedFlac()[..prefix.Length]);
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        cancellation.Cancel();
        await clock.WaitForTimersAsync(TimeSpan.FromSeconds(10));
        clock.Advance(TimeSpan.FromSeconds(10));
        await source.Canceled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Playback_PatternedInvalidFlacHeaderWaitsForCompletedArtifact()
    {
        var (adapter, context, track) = Setup(true, Decrypt, deezer: true);
        var encrypted = EncryptedFlac(validHeader: false);
        var pending = adapter.GetStreamLeaseAsync(context, new(track));
        source.Release(encrypted[..prefix.Length]);
        await source.PrefixConsumed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        source.Release(encrypted[^4..]);
        source.Complete();
        var lease = (await pending.WaitAsync(TimeSpan.FromSeconds(10))).RequireValue();
        using var request = new HttpRequestMessage(HttpMethod.Get, lease.ProtectedSourceUri);
        using var response = await lease.ProtectedResponseFactory!(request, default);
        Assert.Equal(prefix.Concat(new byte[] { 1, 2, 3, 4 }), await response.Content.ReadAsByteArrayAsync());
    }

    private byte[] EncryptedFlac(bool validHeader = true)
    {
        if (validHeader)
        {
            "fLaC"u8.CopyTo(prefix);
            prefix[4] = 0x80;
            prefix[5] = 0;
            prefix[6] = 0;
            prefix[7] = 34;
        }
        var bytes = prefix.Concat(new byte[] { 1, 2, 3, 4 }).ToArray();
        var digest = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes("123"))).ToLowerInvariant();
        var key = Enumerable.Range(0, 16).Select(i => (byte)(digest[i] ^ digest[i + 16] ^ "g4el58wc0zvf9na1"[i])).ToArray();
        var cipher = new CbcBlockCipher(new BlowfishEngine());
        for (var segment = 0; segment + 2048 <= bytes.Length; segment += 6144)
        {
            cipher.Init(true, new ParametersWithIV(new KeyParameter(key), new byte[] { 0, 1, 2, 3, 4, 5, 6, 7 }));
            for (var offset = segment; offset < segment + 2048; offset += 8) cipher.ProcessBlock(bytes, offset, bytes, offset);
        }
        return bytes;
    }
}
