using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using allstarr.Core.ClientApi;
using allstarr.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed partial class ProtocolRouteFixtureTests
{
    [Fact]
    public async Task ClientApiV2_LibraryFileSchemasDescribeBinaryAndConflictResponses()
    {
        using var factory = ClientApiFactory(_ => throw new InvalidOperationException("Unexpected upstream request"));
        using var client = CreateClientApiClient(factory);
        using var response = await client.GetAsync("/allstarr/v2/openapi.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var paths = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("paths");
        foreach (var path in new[] { "mixes/{id}/cover", "releases/{id}/cover", "models/karaoke/{version}/file", "providers/{id}/icon" })
        {
            var operation = paths.GetProperty("/allstarr/v2/" + path).GetProperty("get");
            var content = operation.GetProperty("responses").GetProperty("200").GetProperty("content");
            Assert.False(content.TryGetProperty("application/json", out _));
            Assert.All(content.EnumerateObject(), type => Assert.Equal("binary", type.Value.GetProperty("schema").GetProperty("format").GetString()));
        }
        Assert.EndsWith("/ClientApiError", paths.GetProperty("/allstarr/v2/library/additions").GetProperty("post")
            .GetProperty("responses").GetProperty("409").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
    }

    [Fact]
    public async Task ClientApiV2_KaraokeMetadataAndFileUseNativeUrlsAndValidators()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        var directory = Path.Combine(factory.Services.GetRequiredService<StorageOptions>().DataDirectory, "models", "karaoke", "model-1");
        Directory.CreateDirectory(directory);
        byte[] bytes = [1, 2, 3, 4, 5, 6];
        var sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
        await File.WriteAllBytesAsync(Path.Combine(directory, "model.aar"), bytes);
        await File.WriteAllTextAsync(Path.Combine(directory, "LICENSE"), "MIT License");
        await File.WriteAllTextAsync(Path.Combine(directory, "manifest.json"), JsonSerializer.Serialize(new
        { version = "model-1", sha256, sizeBytes = bytes.Length, computeUnits = "gpu", file = "model.aar", format = "appleArchive", license = "MIT", source = "Fixture" }));
        await factory.Services.GetRequiredService<IKaraokeModelService>().ScanAsync();
        using var metadata = await client.GetAsync("/allstarr/v2/models/karaoke");
        Assert.Equal(HttpStatusCode.OK, metadata.StatusCode);
        var body = await metadata.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(sha256, body.GetProperty("sha256").GetString());
        Assert.Equal(bytes.Length, body.GetProperty("sizeBytes").GetInt64());
        Assert.Equal("gpu", body.GetProperty("computeUnits").GetString());
        var path = body.GetProperty("url").GetString()!;
        Assert.Equal("/allstarr/v2/models/karaoke/model-1/file", path);
        await AssertV2LibraryConditionalAsync(client, "/allstarr/v2/models/karaoke", metadata);
        await AssertV2LibraryBatchAsync(client, "/allstarr/v2/models/karaoke", metadata);
        using var file = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, file.StatusCode);
        Assert.Equal(bytes, await file.Content.ReadAsByteArrayAsync());
        Assert.Equal("application/octet-stream", file.Content.Headers.ContentType?.MediaType);
        Assert.Equal('"' + sha256 + '"', file.Headers.ETag?.Tag);
        Assert.Contains("immutable", file.Headers.CacheControl!.ToString());
        await AssertV2LibraryConditionalAsync(client, path, file);
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Range = new RangeHeaderValue(1, 3);
        using var range = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(bytes[1..4], await range.Content.ReadAsByteArrayAsync());
        using var batch = await client.PostAsJsonAsync("/allstarr/v2/batch", new { requests = new[] { new { id = "file", path } } });
        Assert.Equal(404, (await batch.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("responses").GetProperty("file").GetProperty("status").GetInt32());
    }

    [Fact]
    public async Task ClientApiV2_ProviderIconsUseTheSameAssetsAndNativeTrackUrls()
    {
        using var factory = ClientDetailsFactory(request => request.RequestUri!.AbsolutePath == "/Users/Me"
            ? Json(200, """{"Id":"listener"}""") : Json(200, V2LibrarySong));
        using var fixtures = new ProviderIconFiles();
        using var client = CreateClientApiClient(factory);
        fixtures.Use(factory.Services);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        foreach (var variant in new[] { "color", "glyph" })
        {
            var path = $"/allstarr/v2/providers/deezer/icon?variant={variant}&format=png&size=32";
            using var icon = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.OK, icon.StatusCode);
            Assert.Equal("image/png", icon.Content.Headers.ContentType?.MediaType);
            Assert.NotEmpty(await icon.Content.ReadAsByteArrayAsync());
            await AssertV2LibraryConditionalAsync(client, path, icon);
        }
        using var track = await client.GetAsync("/allstarr/v2/tracks/native1");
        Assert.Equal(HttpStatusCode.OK, track.StatusCode);
        var provider = (await track.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("track").GetProperty("provider");
        Assert.StartsWith("/allstarr/v2/providers/", provider.GetProperty("iconUrl").GetString());
        Assert.StartsWith("/allstarr/v2/providers/", provider.GetProperty("glyphUrl").GetString());
        using var unknown = await client.GetAsync("/allstarr/v2/providers/no-such-provider/icon");
        await AssertClientApiError(unknown, 404, "not_found");
    }

    [Theory]
    [InlineData("providers/deezer/icon?variant=invalid")]
    [InlineData("providers/deezer/icon?format=")]
    [InlineData("providers/deezer/icon?format=jpeg")]
    [InlineData("providers/deezer/icon?size=15")]
    [InlineData("providers/deezer/icon?size=257")]
    [InlineData("providers/deezer/icon?size=bad")]
    [InlineData("providers/not%20valid/icon")]
    [InlineData("models/karaoke/bad%5Cversion/file")]
    public async Task ClientApiV2_FileResourcesRejectInvalidParameters(string path)
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        using var response = await client.GetAsync("/allstarr/v2/" + path);
        await AssertClientApiError(response, 400, "invalid_request");
    }

    [Fact]
    public async Task ClientApiV2_UnavailableKaraokeResourcesReturnNotFound()
    {
        using var factory = ClientApiFactory(_ => Json(200, """{"Id":"listener"}"""));
        using var client = CreateClientApiClient(factory);
        client.DefaultRequestHeaders.Authorization = new("Bearer", "listener-token");
        foreach (var path in new[] { "models/karaoke", "models/karaoke/missing/file" })
        {
            using var response = await client.GetAsync("/allstarr/v2/" + path);
            await AssertClientApiError(response, 404, "not_found");
        }
    }
}
