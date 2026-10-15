using allstarr.Core.Capabilities;
using allstarr.Core.ManagedFiles;
using allstarr.Core.Protocols;
using allstarr.Core.Routing;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace allstarr.Tests;

public sealed partial class RecordingKeptFilesTests
{
    [Theory]
    [InlineData(ProtocolKind.Jellyfin, null, false, 0, 31)]
    [InlineData(ProtocolKind.Subsonic, "bytes=5-9", false, 5, 5)]
    [InlineData(ProtocolKind.Jellyfin, "bytes=-5", false, 26, 5)]
    [InlineData(ProtocolKind.Subsonic, "bytes=99-", false, 0, 0)]
    [InlineData(ProtocolKind.Jellyfin, "bytes=1-2", true, 0, 0)]
    public async Task KeptPlaybackReadsOnlyTheRequestedAudio(ProtocolKind protocol, string? range, bool head, int start, int count)
    {
        var published = await Service().PublishAsync(Request());
        await SetListenerBackendAsync(protocol);
        await DisableSourceAccountAsync();
        var reads = await CountKeptReadsAsync();
        using var activity = new PlaybackDeliveryActivityStore();
        var gateway = KeptGateway(new Mock<IProviderRouter>(MockBehavior.Strict).Object, playbackActivity: activity);
        var context = PlaybackContext(protocol, other, CanonicalItemId());
        if (range != null && !head)
        {
            var initial = await gateway.OpenStreamAsync(context, "allstarr", recordingId.ToString("D"), ProviderAudioQuality.Any, null);
            Assert.NotNull(initial);
            initial.Response.Dispose();
            reads.Bytes = 0;
            reads.FullFileReads = 0;
        }
        var response = await gateway.OpenStreamAsync(context,
            "allstarr", recordingId.ToString("D"), ProviderAudioQuality.Any, range, head);
        Assert.NotNull(response);
        Assert.Equal(published.ManagedFileId, response.ServingManagedFileId);
        Assert.Equal(0, reads.Bytes);
        var result = await ExecuteResponseAsync(response.Response, head ? HttpMethods.Head : HttpMethods.Get, range);
        Assert.Equal(audio.Skip(start).Take(count), result.Body);
        Assert.Equal(count, reads.Bytes);
        Assert.Equal(0, reads.FullFileReads);
    }

    [Fact]
    public async Task OrdinaryKeptOwnershipLookupStillHashesTheEntireFile()
    {
        await Service().PublishAsync(Request());
        var reads = await CountKeptReadsAsync();
        Assert.NotNull(await Service().FindAsync(Actor(other), recordingId));
        Assert.Equal(1, reads.FullFileReads);
        Assert.Equal(audio.Length, reads.Bytes);
    }

    [Theory]
    [Trait("Lane", "ReleaseCritical")]
    [InlineData("missing")]
    [InlineData("length")]
    [InlineData("replacement")]
    [InlineData("file-link")]
    [InlineData("parent-link")]
    public async Task KeptPlaybackRejectsUnavailableOrReplacedOwnedFilesWithoutReadingAudio(string change)
    {
        var published = await Service().PublishAsync(Request());
        var reads = await CountKeptReadsAsync();
        if (change == "parent-link")
        {
            var parent = Path.GetDirectoryName(published.Path)!;
            var moved = Path.Combine(root, "moved-parent");
            Directory.Move(parent, moved);
            Directory.CreateSymbolicLink(parent, moved);
        }
        else if (change == "length") await File.AppendAllTextAsync(published.Path, "changed");
        else
        {
            File.Delete(published.Path);
            if (change == "replacement") await File.WriteAllBytesAsync(published.Path, audio);
            else if (change == "file-link") File.CreateSymbolicLink(published.Path, artifact.SourcePath);
        }
        var opened = await Service().OpenForPlaybackAsync(Actor(other),
            new("allstarr", ProviderResourceKind.Track, recordingId.ToString("D")), false);
        Assert.Null(opened);
        Assert.Equal(0, reads.Bytes);
        Assert.Equal(0, reads.FullFileReads);
        Assert.Equal(audio, await File.ReadAllBytesAsync(artifact.SourcePath));
    }

    [Fact]
    [Trait("Lane", "ReleaseCritical")]
    public async Task KeptPlaybackChecksTheActualOpenedFileHandle()
    {
        await Service().PublishAsync(Request());
        var wrongFile = Path.Combine(root, "different-file.flac");
        await File.WriteAllBytesAsync(wrongFile, audio);
        var reads = await CountKeptReadsAsync();
        reads.OpenOverride = _ => File.OpenRead(wrongFile);
        Assert.Null(await Service().OpenForPlaybackAsync(Actor(other),
            new("allstarr", ProviderResourceKind.Track, recordingId.ToString("D")), false));
        Assert.Equal(0, reads.Bytes);
    }

    private async Task<CountedKeptFiles> CountKeptReadsAsync()
    {
        await services.DisposeAsync();
        var reads = new CountedKeptFiles(new PhysicalManagedFileOperations());
        var collection = new ServiceCollection();
        collection.AddDbContext<AllstarrDbContext>(builder => builder.UseSqlite(database.ConnectionString));
        collection.AddManagedFilePlacement();
        collection.AddSingleton<IManagedFileOperations>(reads);
        services = collection.BuildServiceProvider();
        return reads;
    }

    private sealed class CountedKeptFiles(IManagedFileOperations inner) : IManagedFileOperations
    {
        public long Bytes;
        public int FullFileReads;
        public Func<string, Stream>? OpenOverride;
        public Stream OpenRead(string path, bool sequential = false)
        {
            if (sequential) FullFileReads++;
            return new CountedReadStream(!sequential && OpenOverride != null ? OpenOverride(path) : inner.OpenRead(path, sequential), size => Bytes += size);
        }
        public bool TryCreateHardLink(string link, string source) => inner.TryCreateHardLink(link, source);
        public bool TryCreateReflink(string destination, string source) => inner.TryCreateReflink(destination, source);
        public bool TryGetFileIdentity(string path, out ManagedFileSystemIdentity identity) => inner.TryGetFileIdentity(path, out identity);
        public bool TryGetFileIdentity(Stream stream, out ManagedFileSystemIdentity identity) =>
            inner.TryGetFileIdentity(stream is CountedReadStream counted ? counted.Inner : stream, out identity);
        public Task CopyAsync(string source, string destination, CancellationToken token) => inner.CopyAsync(source, destination, token);
        public void MoveNoReplace(string source, string destination) => inner.MoveNoReplace(source, destination);
    }

    private sealed class CountedReadStream(Stream inner, Action<int> counted) : Stream
    {
        public Stream Inner => inner;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            counted(read);
            return read;
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            var read = await inner.ReadAsync(buffer, token);
            counted(read);
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) =>
            ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
        public override ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
