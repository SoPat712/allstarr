using System.Globalization;
using System.Text;
using System.Text.Json;
using allstarr.Core.Intelligence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class ListeningHistoryImporterMatrixTests
{
    private static readonly DateTimeOffset ExpectedListen =
        new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);

    public static TheoryData<string> Formats => new()
    {
        "spotify-extended-streaming-history",
        "lastfm-recent-tracks",
        "listenbrainz-export",
        "koito-export-v1",
        "maloja-export"
    };

    [Fact]
    public void Registration_ContainsExactlyEveryQualifiedImporterInStableOrder()
    {
        var services = new ServiceCollection();
        services.AddListeningHistoryImport(new ConfigurationBuilder().Build());

        Assert.Collection(
            services.Where(item => item.ServiceType == typeof(IListeningHistoryImporter)),
            item => Assert.Equal(typeof(SpotifyListeningHistoryImporter), item.ImplementationType),
            item => Assert.Equal(typeof(LastFmListeningHistoryImporter), item.ImplementationType),
            item => Assert.Equal(typeof(ListenBrainzListeningHistoryImporter), item.ImplementationType),
            item => Assert.Equal(typeof(KoitoListeningHistoryImporter), item.ImplementationType),
            item => Assert.Equal(typeof(MalojaListeningHistoryImporter), item.ImplementationType));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Scan_HandlesEmptyDuplicateMalformedUnicodeTimezoneAndCancellation(string format)
    {
        var importer = Importer(format);
        var accepted = new List<ListeningHistoryImportRow>();

        var scan = await importer.ScanAsync(
            Stream(Fixture(format, copies: 2, includeMalformed: true)),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            (row, _) => { accepted.Add(row); return ValueTask.CompletedTask; });

        Assert.NotNull(scan);
        Assert.Equal(format, scan.Format);
        Assert.Equal(3, scan.Rows);
        Assert.Equal(1, scan.MusicRows);
        Assert.Equal(1, scan.Completed);
        Assert.Equal(1, scan.Malformed);
        Assert.Equal(1, scan.Duplicate);
        var row = Assert.Single(accepted);
        Assert.Equal("Beyoncé – 東京", row.Title);
        Assert.Equal("Sigur Rós", row.Artist);
        Assert.Equal(ExpectedListen, row.ListenedAt);

        var empty = await importer.ScanAsync(
            Stream(EmptyFixture(format)),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(format is not "spotify-extended-streaming-history" and not "listenbrainz-export", empty != null);
        Assert.Equal(0, empty?.Rows ?? 0);

        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => importer.ScanAsync(
            Stream(Fixture(format, copies: 1, includeMalformed: false)),
            new(new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero)),
            cancellationToken: cancellation.Token));
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public async Task Scan_RejectsGeneratedLargeInputAtTheConfiguredBound(string format)
    {
        var exception = await Assert.ThrowsAsync<ListeningHistoryImportException>(() =>
            Importer(format).ScanAsync(
                Stream(Fixture(format, copies: 1_001, includeMalformed: false)),
                new(DateTimeOffset.UtcNow, MaximumRows: 1_000)));

        Assert.Equal("history_import_row_limit", exception.Code);
    }

    private static IListeningHistoryImporter Importer(string format) => format switch
    {
        "spotify-extended-streaming-history" => new SpotifyListeningHistoryImporter(),
        "lastfm-recent-tracks" => new LastFmListeningHistoryImporter(),
        "listenbrainz-export" => new ListenBrainzListeningHistoryImporter(),
        "koito-export-v1" => new KoitoListeningHistoryImporter(),
        "maloja-export" => new MalojaListeningHistoryImporter(),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static byte[] Fixture(string format, int copies, bool includeMalformed)
    {
        var valid = ValidRow(format);
        var rows = Enumerable.Repeat(valid, copies).Cast<object>().ToList();
        if (includeMalformed) rows.Add(MalformedRow(format));
        return format switch
        {
            "lastfm-recent-tracks" => JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new Dictionary<string, object?> { ["track"] = rows }
            }),
            "listenbrainz-export" => Encoding.UTF8.GetBytes(
                string.Join('\n', rows.Select(row => JsonSerializer.Serialize(row)))),
            "koito-export-v1" => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["version"] = "1",
                ["user"] = "matrix-user",
                ["listens"] = rows
            }),
            "maloja-export" => JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object?>
            {
                ["scrobbles"] = rows
            }),
            _ => JsonSerializer.SerializeToUtf8Bytes(rows)
        };
    }

    private static byte[] EmptyFixture(string format) => format switch
    {
        "lastfm-recent-tracks" => JsonSerializer.SerializeToUtf8Bytes(new[] { new { track = Array.Empty<object>() } }),
        "listenbrainz-export" => Encoding.UTF8.GetBytes("\n"),
        "koito-export-v1" => JsonSerializer.SerializeToUtf8Bytes(new { version = "1", user = "matrix-user", listens = Array.Empty<object>() }),
        "maloja-export" => JsonSerializer.SerializeToUtf8Bytes(new { scrobbles = Array.Empty<object>() }),
        _ => JsonSerializer.SerializeToUtf8Bytes(Array.Empty<object>())
    };

    private static Dictionary<string, object?> ValidRow(string format) => format switch
    {
        "spotify-extended-streaming-history" => new()
        {
            ["ts"] = "2026-07-01T12:00:00Z",
            ["username"] = "matrix-user",
            ["platform"] = "desktop",
            ["ms_played"] = 180_000,
            ["master_metadata_track_name"] = "Beyoncé – 東京",
            ["master_metadata_album_artist_name"] = "Sigur Rós",
            ["master_metadata_album_album_name"] = "Ágætis byrjun",
            ["spotify_track_uri"] = "spotify:track:1111111111111111111111",
            ["reason_end"] = "trackdone"
        },
        "lastfm-recent-tracks" => new()
        {
            ["name"] = "Beyoncé – 東京",
            ["artist"] = Item("Sigur Rós"),
            ["album"] = Item("Ágætis byrjun"),
            ["mbid"] = "",
            ["date"] = new Dictionary<string, object?>
            {
                ["uts"] = ExpectedListen.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)
            }
        },
        "listenbrainz-export" => new()
        {
            ["listened_at"] = ExpectedListen.ToUnixTimeSeconds(),
            ["recording_msid"] = "11111111-1111-1111-1111-111111111111",
            ["track_metadata"] = new Dictionary<string, object?>
            {
                ["track_name"] = "Beyoncé – 東京",
                ["artist_name"] = "Sigur Rós",
                ["release_name"] = "Ágætis byrjun",
                ["additional_info"] = new Dictionary<string, object?> { ["duration_ms"] = 180_000 }
            }
        },
        "koito-export-v1" => new()
        {
            ["listened_at"] = "2026-07-01T08:00:00-04:00",
            ["client"] = "Koito",
            ["track"] = new Dictionary<string, object?>
            {
                ["duration"] = 180,
                ["aliases"] = Aliases("Beyoncé – 東京")
            },
            ["album"] = new Dictionary<string, object?> { ["aliases"] = Aliases("Ágætis byrjun") },
            ["artists"] = new[]
            {
                new Dictionary<string, object?> { ["is_primary"] = true, ["aliases"] = Aliases("Sigur Rós") }
            }
        },
        "maloja-export" => new()
        {
            ["time"] = ExpectedListen.ToUnixTimeSeconds(),
            ["track"] = new Dictionary<string, object?>
            {
                ["title"] = "Beyoncé – 東京",
                ["artists"] = new[] { "Sigur Rós" },
                ["album"] = new Dictionary<string, object?> { ["albumtitle"] = "Ágætis byrjun" }
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static Dictionary<string, object?> MalformedRow(string format) => format switch
    {
        "spotify-extended-streaming-history" => new()
        {
            ["ts"] = "invalid",
            ["ms_played"] = 1,
            ["master_metadata_track_name"] = "Broken"
        },
        "lastfm-recent-tracks" => new()
        {
            ["name"] = "Broken",
            ["artist"] = Item("Artist"),
            ["album"] = Item(null),
            ["mbid"] = "",
            ["date"] = new { uts = "invalid" }
        },
        "listenbrainz-export" => new()
        {
            ["listened_at"] = "invalid",
            ["track_metadata"] = new Dictionary<string, object?>
            {
                ["track_name"] = "Broken",
                ["artist_name"] = "Artist"
            }
        },
        "koito-export-v1" => new()
        {
            ["listened_at"] = "invalid",
            ["track"] = new Dictionary<string, object?> { ["duration"] = 1, ["aliases"] = Aliases("Broken") },
            ["artists"] = new[]
            {
                new Dictionary<string, object?> { ["is_primary"] = true, ["aliases"] = Aliases("Artist") }
            }
        },
        "maloja-export" => new()
        {
            ["time"] = "invalid",
            ["track"] = new Dictionary<string, object?> { ["title"] = "Broken", ["artists"] = new[] { "Artist" } }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };

    private static Dictionary<string, object?> Item(string? value) => new()
    {
        ["#text"] = value ?? "",
        ["mbid"] = ""
    };

    private static object[] Aliases(string value) =>
    [
        new Dictionary<string, object?> { ["alias"] = value, ["is_primary"] = true }
    ];

    private static Stream Stream(byte[] bytes) => new MemoryStream(bytes);
}
