using System.Text.Json;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed partial class DurableBackupServiceTests
{
    [Fact]
    public async Task Api_ReportsConfiguredDatabaseAndWalBytesWithoutOpeningFiles()
    {
        var service = Service();
        var artifact = await service.CreateAsync();
        _options.DatabaseFileName = "configured-size.db";
        await File.WriteAllBytesAsync(_options.DatabasePath, new byte[37]);
        await File.WriteAllBytesAsync(_options.DatabasePath + "-wal", new byte[19]);
        await File.WriteAllBytesAsync(_options.DatabasePath + "-shm", new byte[11]);
        using var database = File.Open(_options.DatabasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var wal = File.Open(_options.DatabasePath + "-wal", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var result = Assert.IsType<OkObjectResult>(await Controller(true, service).Get());

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(56, payload.RootElement.GetProperty("databaseBytes").GetInt64());
        var backup = Assert.Single(payload.RootElement.GetProperty("backups").EnumerateArray());
        Assert.Equal(artifact.Bytes, backup.GetProperty("Bytes").GetInt64());
        Assert.Equal(37, database.Length);
        Assert.Equal(19, wal.Length);
    }

    [Fact]
    public async Task Api_ReportsDatabaseBytesWhenWalIsAbsent()
    {
        _options.DatabaseFileName = "configured-size.db";
        await File.WriteAllBytesAsync(_options.DatabasePath, new byte[37]);

        var result = Assert.IsType<OkObjectResult>(await Controller(true).Get());

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(37, payload.RootElement.GetProperty("databaseBytes").GetInt64());
        Assert.False(File.Exists(_options.DatabasePath + "-wal"));
    }

    [Fact]
    public async Task Api_ReportsUnknownDatabaseBytesWhenDatabaseIsAbsent()
    {
        _options.DatabaseFileName = "missing-size.db";
        await File.WriteAllBytesAsync(_options.DatabasePath + "-wal", new byte[19]);

        var result = Assert.IsType<OkObjectResult>(await Controller(true).Get());

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("databaseBytes").ValueKind);
        Assert.False(File.Exists(_options.DatabasePath));
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    public async Task Api_ReportsUnknownDatabaseBytesWhenAStorageFileCannotBeMeasured(string suffix)
    {
        _options.DatabaseFileName = "configured-size.db";
        if (suffix.Length != 0) await File.WriteAllBytesAsync(_options.DatabasePath, new byte[37]);
        Directory.CreateDirectory(_options.DatabasePath + suffix);

        var result = Assert.IsType<OkObjectResult>(await Controller(true).Get());

        using var payload = JsonDocument.Parse(JsonSerializer.Serialize(result.Value));
        Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("databaseBytes").ValueKind);
    }
}
