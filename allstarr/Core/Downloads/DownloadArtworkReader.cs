using allstarr.Core.Protocols;
using allstarr.Core.Releases;
using allstarr.Services.Common;
using allstarr.Services.Local;
using AudioTagReader = TagLib.File;

namespace allstarr.Core.Downloads;

public sealed class DownloadArtworkReader(IProtocolProviderGateway gateway, IMediaAssetResolver media,
    IHttpClientFactory clients, ILogger logger, IEnumerable<IPlaybackMetadataResolver>? metadataResolvers = null)
{
    private const int MaximumBytes = 8 * 1024 * 1024;

    public async Task<MediaAssetSource?> ReadAsync(Guid owner, ProtocolExecutionContext protocol,
        string provider, string trackId, IEnumerable<string> paths, CancellationToken token)
    {
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var source = new ArtworkTagFile(path, token);
                using var file = AudioTagReader.Create(source, TagLib.ReadStyle.None);
                var picture = file.Tag.Pictures.OrderByDescending(item => item.Type == TagLib.PictureType.FrontCover)
                    .FirstOrDefault(item => item.Data.Count is > 0 and <= MaximumBytes &&
                        item.MimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true);
                if (picture != null) return new(picture.Data.Data, picture.MimeType);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Embedded download artwork could not be read");
            }
        }

        var asset = await media.ResolveAsync(new MediaAssetIdentity(owner, null, provider, "track", $"ext-{provider}-song-{trackId}",
                Revision: "cover-v2", Width: 96),
            cancel => ReadCatalogAsync(owner, protocol, provider, trackId, cancel), MaximumBytes, token);
        return asset == null ? null : new(asset.Bytes, asset.ContentType);
    }

    private async Task<MediaAssetSource?> ReadCatalogAsync(Guid owner, ProtocolExecutionContext protocol,
        string provider, string trackId, CancellationToken token)
    {
        try
        {
            var song = await gateway.GetStoredSongAsync(protocol, provider, trackId);
            var cover = await ReadCoverAsync(provider, [song?.CoverArtUrlLarge, song?.CoverArtUrl], token);
            if (cover != null) return cover;
            foreach (var resolver in metadataResolvers ?? [])
            {
                var metadata = await resolver.ResolveAsync($"ext-{provider}-song-{trackId}", protocol, token);
                cover = await ReadCoverAsync(provider, [metadata?.CoverArtUrl], token);
                if (cover != null) return cover;
            }
            song = await gateway.GetSongAsync(protocol, provider, trackId) ?? song;
            cover = await ReadCoverAsync(provider, [song?.CoverArtUrlLarge, song?.CoverArtUrl], token);
            if (cover != null) return cover;
            if (string.IsNullOrWhiteSpace(song?.AlbumId)) return null;
            var albumId = song.AlbumId;
            var album = LocalLibraryService.ParseExternalResource(albumId);
            if (album.isExternal && album.type == "album") albumId = album.externalId!;
            var asset = await new ReleaseArtworkReader(gateway, media, clients, logger)
                .ReadAsync(owner, protocol, provider, albumId, token);
            return asset == null ? null : new(asset.Bytes, asset.ContentType);
        }
        catch (Exception exception) when (!token.IsCancellationRequested)
        {
            logger.LogDebug(exception, "Download artwork lookup failed for provider {ProviderId}", provider);
            return null;
        }
    }

    private async Task<MediaAssetSource?> ReadCoverAsync(string provider, IEnumerable<string?> urls, CancellationToken token)
    {
        foreach (var url in urls.Distinct(StringComparer.Ordinal))
        {
            if (ProviderArtworkFallback.IsFallback(provider, url, "track") ||
                !OutboundRequestGuard.TryCreateSafeHttpUri(url, out var uri, out _) || uri == null) continue;
            try
            {
                using var response = await clients.CreateClient().GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, token);
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (!response.IsSuccessStatusCode || contentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) != true ||
                    response.Content.Headers.ContentLength > MaximumBytes) continue;
                await response.Content.LoadIntoBufferAsync(MaximumBytes, token);
                return new(await response.Content.ReadAsByteArrayAsync(token), contentType);
            }
            catch (Exception exception) when (!token.IsCancellationRequested)
            {
                logger.LogDebug(exception, "Download song artwork could not be read");
            }
        }
        return null;
    }

    private sealed class ArtworkTagFile(string path, CancellationToken token) : Stream, TagLib.File.IFileAbstraction
    {
        private readonly FileStream source = System.IO.File.OpenRead(path);
        private int remaining = MaximumBytes;
        public string Name => path;
        public Stream ReadStream => this;
        public Stream WriteStream => throw new NotSupportedException();
        public void CloseStream(Stream stream) { }
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => source.Length;
        public override long Position { get => source.Position; set => source.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => source.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count)
        {
            token.ThrowIfCancellationRequested();
            if (count > remaining) throw new IOException("Embedded artwork metadata exceeds the read limit.");
            var read = source.Read(buffer, offset, count);
            remaining -= read;
            return read;
        }
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) source.Dispose();
            base.Dispose(disposing);
        }
    }
}
