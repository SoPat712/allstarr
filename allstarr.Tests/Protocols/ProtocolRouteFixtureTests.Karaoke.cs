using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApi_Karaoke_ValidModel_CapabilityOn_MetadataCorrect_AndStreamsFileByteExactWithRangeAndETag()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        var storage = factory.Services.GetRequiredService<StorageOptions>();
        var modelsDir = Path.Combine(storage.DataDirectory, "models", "karaoke");
        var versionDir = Path.Combine(modelsDir, "htdemucs-ane-1");
        Directory.CreateDirectory(versionDir);

        var modelBytes = new byte[1024];
        for (var i = 0; i < modelBytes.Length; i++) modelBytes[i] = (byte)(i % 256);
        var modelFilePath = Path.Combine(versionDir, "HTDemucsCoreANE.aar");
        await File.WriteAllBytesAsync(modelFilePath, modelBytes);

        var sha256Hex = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
        var manifestJson = JsonSerializer.Serialize(new
        {
            version = "htdemucs-ane-1",
            sha256 = sha256Hex,
            sizeBytes = 1024,
            computeUnits = "neuralEngine",
            file = "HTDemucsCoreANE.aar",
            format = "appleArchive",
            license = "MIT",
            source = "htdemucs (Meta), converted to Core ML for the Neural Engine by Songbird"
        });
        await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), manifestJson);
        await File.WriteAllTextAsync(Path.Combine(versionDir, "LICENSE"), "MIT License");

        var karaokeService = factory.Services.GetRequiredService<IKaraokeModelService>();
        await karaokeService.ScanAsync();

        // 1. Capability check
        using (var capResponse = await client.GetAsync("/allstarr/v1/capabilities"))
        {
            Assert.Equal(HttpStatusCode.OK, capResponse.StatusCode);
            using var capDoc = JsonDocument.Parse(await capResponse.Content.ReadAsStringAsync());
            Assert.True(capDoc.RootElement.GetProperty("features").GetProperty("karaokeModel").GetBoolean());
        }

        // 2. Metadata check
        using (var metaResponse = await client.GetAsync("/allstarr/v1/models/karaoke"))
        {
            Assert.Equal(HttpStatusCode.OK, metaResponse.StatusCode);
            using var metaDoc = JsonDocument.Parse(await metaResponse.Content.ReadAsStringAsync());
            var root = metaDoc.RootElement;
            Assert.Equal("htdemucs-ane-1", root.GetProperty("version").GetString());
            Assert.Equal(sha256Hex, root.GetProperty("sha256").GetString());
            Assert.Equal(1024, root.GetProperty("sizeBytes").GetInt64());
            Assert.Equal("neuralEngine", root.GetProperty("computeUnits").GetString());
            Assert.Equal("appleArchive", root.GetProperty("format").GetString());
            Assert.Equal("/allstarr/v1/models/karaoke/htdemucs-ane-1/file", root.GetProperty("url").GetString());
            Assert.Equal("MIT", root.GetProperty("license").GetString());
        }

        // 3. Full download check
        using (var fileResponse = await client.GetAsync("/allstarr/v1/models/karaoke/htdemucs-ane-1/file"))
        {
            Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
            Assert.Equal("application/octet-stream", fileResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal($"\"{sha256Hex}\"", fileResponse.Headers.ETag?.Tag);
            Assert.True(fileResponse.Headers.CacheControl?.Private);
            Assert.Equal(TimeSpan.FromSeconds(31536000), fileResponse.Headers.CacheControl?.MaxAge);
            Assert.Contains("immutable", fileResponse.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
            Assert.Equal("attachment", fileResponse.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("HTDemucsCoreANE.aar", fileResponse.Content.Headers.ContentDisposition?.FileName);

            var downloadedBytes = await fileResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(modelBytes, downloadedBytes);
        }

        // 4. HEAD request check
        using (var headRequest = new HttpRequestMessage(HttpMethod.Head, "/allstarr/v1/models/karaoke/htdemucs-ane-1/file"))
        using (var headResponse = await client.SendAsync(headRequest))
        {
            Assert.Equal(HttpStatusCode.OK, headResponse.StatusCode);
            Assert.Equal("application/octet-stream", headResponse.Content.Headers.ContentType?.MediaType);
            Assert.Equal($"\"{sha256Hex}\"", headResponse.Headers.ETag?.Tag);
            Assert.True(headResponse.Headers.CacheControl?.Private);
            Assert.Equal(TimeSpan.FromSeconds(31536000), headResponse.Headers.CacheControl?.MaxAge);
            Assert.Contains("immutable", headResponse.Headers.CacheControl?.Extensions.Select(e => e.Name) ?? []);
            Assert.Equal(1024, headResponse.Content.Headers.ContentLength);
            var headBytes = await headResponse.Content.ReadAsByteArrayAsync();
            Assert.Empty(headBytes);
        }

        // 5. Ranged download check (206)
        using (var rangeRequest = new HttpRequestMessage(HttpMethod.Get, "/allstarr/v1/models/karaoke/htdemucs-ane-1/file"))
        {
            rangeRequest.Headers.Range = new RangeHeaderValue(10, 29);
            using var rangeResponse = await client.SendAsync(rangeRequest);
            Assert.Equal(HttpStatusCode.PartialContent, rangeResponse.StatusCode);
            Assert.Equal("bytes 10-29/1024", rangeResponse.Content.Headers.ContentRange?.ToString());
            Assert.Equal(20, rangeResponse.Content.Headers.ContentLength);
            var rangeBytes = await rangeResponse.Content.ReadAsByteArrayAsync();
            Assert.Equal(modelBytes[10..30], rangeBytes);
        }

        // 6. Invalid range check (416)
        using (var invalidRangeRequest = new HttpRequestMessage(HttpMethod.Get, "/allstarr/v1/models/karaoke/htdemucs-ane-1/file"))
        {
            invalidRangeRequest.Headers.Range = new RangeHeaderValue(2000, 3000);
            using var invalidRangeResponse = await client.SendAsync(invalidRangeRequest);
            Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalidRangeResponse.StatusCode);
        }
    }

    [Fact]
    public async Task ClientApi_Karaoke_MultipleVersions_ServesMostRecentlyWrittenManifest()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        var storage = factory.Services.GetRequiredService<StorageOptions>();
        var modelsDir = Path.Combine(storage.DataDirectory, "models", "karaoke");

        // Version 1
        var v1Dir = Path.Combine(modelsDir, "v1");
        Directory.CreateDirectory(v1Dir);
        var v1Bytes = Encoding.UTF8.GetBytes("version 1 model");
        await File.WriteAllBytesAsync(Path.Combine(v1Dir, "model.bin"), v1Bytes);
        var v1Sha = Convert.ToHexString(SHA256.HashData(v1Bytes)).ToLowerInvariant();
        var v1ManifestPath = Path.Combine(v1Dir, "manifest.json");
        await File.WriteAllTextAsync(v1ManifestPath, JsonSerializer.Serialize(new
        {
            version = "v1",
            sha256 = v1Sha,
            sizeBytes = v1Bytes.Length,
            computeUnits = "cpu",
            file = "model.bin",
            format = "appleArchive",
            license = "MIT"
        }));
        File.SetLastWriteTimeUtc(v1ManifestPath, DateTime.UtcNow.AddMinutes(-10));

        // Version 2 (newer)
        var v2Dir = Path.Combine(modelsDir, "v2");
        Directory.CreateDirectory(v2Dir);
        var v2Bytes = Encoding.UTF8.GetBytes("version 2 model with more bytes");
        await File.WriteAllBytesAsync(Path.Combine(v2Dir, "model2.bin"), v2Bytes);
        var v2Sha = Convert.ToHexString(SHA256.HashData(v2Bytes)).ToLowerInvariant();
        var v2ManifestPath = Path.Combine(v2Dir, "manifest.json");
        await File.WriteAllTextAsync(v2ManifestPath, JsonSerializer.Serialize(new
        {
            version = "v2",
            sha256 = v2Sha,
            sizeBytes = v2Bytes.Length,
            computeUnits = "neuralEngine",
            file = "model2.bin",
            format = "appleArchive",
            license = "MIT"
        }));
        File.SetLastWriteTimeUtc(v2ManifestPath, DateTime.UtcNow.AddMinutes(-5));

        var karaokeService = factory.Services.GetRequiredService<IKaraokeModelService>();
        await karaokeService.ScanAsync();

        // Should serve v2 as it's the newer manifest
        using (var response = await client.GetAsync("/allstarr/v1/models/karaoke"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("v2", doc.RootElement.GetProperty("version").GetString());
        }

        // Both files can still be downloaded directly
        using (var v1File = await client.GetAsync("/allstarr/v1/models/karaoke/v1/file"))
        {
            Assert.Equal(HttpStatusCode.OK, v1File.StatusCode);
            Assert.Equal(v1Bytes, await v1File.Content.ReadAsByteArrayAsync());
        }
        using (var v2File = await client.GetAsync("/allstarr/v1/models/karaoke/v2/file"))
        {
            Assert.Equal(HttpStatusCode.OK, v2File.StatusCode);
            Assert.Equal(v2Bytes, await v2File.Content.ReadAsByteArrayAsync());
        }

        // Touch v1 to make it newer
        File.SetLastWriteTimeUtc(v1ManifestPath, DateTime.UtcNow.AddMinutes(5));
        await karaokeService.ScanAsync();

        // Now v1 should be served
        using (var response = await client.GetAsync("/allstarr/v1/models/karaoke"))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("v1", doc.RootElement.GetProperty("version").GetString());
        }
    }

    [Theory]
    [InlineData("missing-file")]
    [InlineData("size-mismatch")]
    [InlineData("sha-mismatch")]
    [InlineData("invalid-json")]
    public async Task ClientApi_Karaoke_InvalidOrCorruptModel_CapabilityOff_AndReturns404(string failureMode)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        var storage = factory.Services.GetRequiredService<StorageOptions>();
        var versionDir = Path.Combine(storage.DataDirectory, "models", "karaoke", "v1");
        Directory.CreateDirectory(versionDir);

        var modelBytes = Encoding.UTF8.GetBytes("valid content");
        var realSha = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();

        switch (failureMode)
        {
            case "missing-file":
                await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), JsonSerializer.Serialize(new
                {
                    version = "v1",
                    sha256 = realSha,
                    sizeBytes = modelBytes.Length,
                    computeUnits = "cpu",
                    file = "nonexistent.bin",
                    format = "appleArchive",
                    license = "MIT"
                }));
                break;
            case "size-mismatch":
                await File.WriteAllBytesAsync(Path.Combine(versionDir, "model.bin"), modelBytes);
                await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), JsonSerializer.Serialize(new
                {
                    version = "v1",
                    sha256 = realSha,
                    sizeBytes = modelBytes.Length + 999,
                    computeUnits = "cpu",
                    file = "model.bin",
                    format = "appleArchive",
                    license = "MIT"
                }));
                break;
            case "sha-mismatch":
                await File.WriteAllBytesAsync(Path.Combine(versionDir, "model.bin"), modelBytes);
                await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), JsonSerializer.Serialize(new
                {
                    version = "v1",
                    sha256 = "0000000000000000000000000000000000000000000000000000000000000000",
                    sizeBytes = modelBytes.Length,
                    computeUnits = "cpu",
                    file = "model.bin",
                    format = "appleArchive",
                    license = "MIT"
                }));
                break;
            case "invalid-json":
                await File.WriteAllBytesAsync(Path.Combine(versionDir, "model.bin"), modelBytes);
                await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), "{ invalid json");
                break;
        }

        var karaokeService = factory.Services.GetRequiredService<IKaraokeModelService>();
        await karaokeService.ScanAsync();

        // Capability off
        using (var capResponse = await client.GetAsync("/allstarr/v1/capabilities"))
        {
            Assert.Equal(HttpStatusCode.OK, capResponse.StatusCode);
            using var capDoc = JsonDocument.Parse(await capResponse.Content.ReadAsStringAsync());
            Assert.False(capDoc.RootElement.GetProperty("features").GetProperty("karaokeModel").GetBoolean());
        }

        // Metadata 404
        using (var metaResponse = await client.GetAsync("/allstarr/v1/models/karaoke"))
        {
            await AssertClientApiError(metaResponse, 404, "not_found");
        }

        // File download 404
        using (var fileResponse = await client.GetAsync("/allstarr/v1/models/karaoke/v1/file"))
        {
            await AssertClientApiError(fileResponse, 404, "not_found");
        }
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../other")]
    [InlineData("foo/bar")]
    [InlineData("foo\\bar")]
    [InlineData(".")]
    public async Task ClientApi_Karaoke_UnsafeVersionSegment_Returns404(string unsafeVersion)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        using var response = await client.GetAsync($"/allstarr/v1/models/karaoke/{Uri.EscapeDataString(unsafeVersion)}/file");
        await AssertClientApiError(response, 404, "not_found");
    }

    [Fact]
    public async Task ClientApi_Karaoke_Unauthenticated_Returns401()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        // Do not add X-Emby-Token

        using var metaResponse = await client.GetAsync("/allstarr/v1/models/karaoke");
        await AssertClientApiError(metaResponse, 401, "unauthorized");

        using var fileResponse = await client.GetAsync("/allstarr/v1/models/karaoke/htdemucs-ane-1/file");
        await AssertClientApiError(fileResponse, 401, "unauthorized");
    }

    [Fact]
    public async Task ClientApi_Karaoke_RequestNeverHashesFile()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Add("X-Emby-Token", "listener-token");

        var storage = factory.Services.GetRequiredService<StorageOptions>();
        var versionDir = Path.Combine(storage.DataDirectory, "models", "karaoke", "htdemucs-ane-1");
        Directory.CreateDirectory(versionDir);

        var modelBytes = new byte[512];
        Random.Shared.NextBytes(modelBytes);
        var modelFilePath = Path.Combine(versionDir, "HTDemucsCoreANE.aar");
        await File.WriteAllBytesAsync(modelFilePath, modelBytes);

        var initialSha = Convert.ToHexString(SHA256.HashData(modelBytes)).ToLowerInvariant();
        await File.WriteAllTextAsync(Path.Combine(versionDir, "manifest.json"), JsonSerializer.Serialize(new
        {
            version = "htdemucs-ane-1",
            sha256 = initialSha,
            sizeBytes = 512,
            computeUnits = "neuralEngine",
            file = "HTDemucsCoreANE.aar",
            format = "appleArchive",
            license = "MIT"
        }));

        var karaokeService = factory.Services.GetRequiredService<IKaraokeModelService>();
        await karaokeService.ScanAsync();

        // Mutate the file on disk without updating manifest or triggering re-scan,
        // and keep mtime identical so cheap mtime check sees no folder change.
        var originalMtime = File.GetLastWriteTimeUtc(modelFilePath);
        var mutatedBytes = new byte[512];
        for (var i = 0; i < 512; i++) mutatedBytes[i] = (byte)(255 - modelBytes[i]);
        await File.WriteAllBytesAsync(modelFilePath, mutatedBytes);
        File.SetLastWriteTimeUtc(modelFilePath, originalMtime);

        // A request to the metadata endpoint or file endpoint serves the cached SHA-256 ETag without re-hashing
        using var metaResponse = await client.GetAsync("/allstarr/v1/models/karaoke");
        Assert.Equal(HttpStatusCode.OK, metaResponse.StatusCode);
        using var metaDoc = JsonDocument.Parse(await metaResponse.Content.ReadAsStringAsync());
        Assert.Equal(initialSha, metaDoc.RootElement.GetProperty("sha256").GetString());

        using var fileResponse = await client.GetAsync("/allstarr/v1/models/karaoke/htdemucs-ane-1/file");
        Assert.Equal(HttpStatusCode.OK, fileResponse.StatusCode);
        Assert.Equal($"\"{initialSha}\"", fileResponse.Headers.ETag?.Tag);
    }
}
