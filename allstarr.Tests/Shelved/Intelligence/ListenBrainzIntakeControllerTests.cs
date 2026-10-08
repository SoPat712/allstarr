using allstarr.Controllers;
using allstarr.Core.Intelligence;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;

namespace allstarr.Tests;

public sealed class ListenBrainzIntakeControllerTests
{
    [Fact]
    public void IntakeTokenFormatRoundTripsAndRejectsAlteredSecrets()
    {
        var id = Guid.CreateVersion7();
        var expected = Enumerable.Range(0, 32).Select(value => (byte)value).ToArray();
        var token = ListeningIntakeTokenService.Format(id, expected);

        Assert.True(ListeningIntakeTokenService.TryParse(token, out var parsedId, out var parsed));
        Assert.Equal(id, parsedId);
        Assert.Equal(expected, parsed);
        Assert.False(ListeningIntakeTokenService.TryParse(token[..^1] + "z", out _, out _));
    }

    [Fact]
    public void ListenBrainzPayloadNormalizesSupportedRowsAndOptionalMetadata()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var request = Request("import", 2, now.AddMinutes(-2).ToUnixTimeSeconds());
        request.Payload![0]!.TrackMetadata!.AdditionalInfo = new()
        {
            DurationMilliseconds = 183_000,
            RecordingMusicBrainzId = "12345678-1234-1234-1234-123456789abc",
            TrackNumber = 4,
            MediaPlayer = "Koito"
        };

        Assert.True(ListenBrainzIntakeController.TryNormalize(request, now, out var rows, out var error), error);
        Assert.Equal(2, rows.Count);
        Assert.Equal("Song", rows[0].Title);
        Assert.Equal(183_000, rows[0].DurationMilliseconds);
        Assert.Equal("12345678-1234-1234-1234-123456789abc", rows[0].RecordingMusicBrainzId);
        Assert.Null(rows[1].Album);
    }

    [Fact]
    public void ListenBrainzPayloadRejectsOversizedInvalidOrFutureSubmissions()
    {
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        Assert.False(ListenBrainzIntakeController.TryNormalize(Request("import", 101, now.ToUnixTimeSeconds()), now, out _, out _));
        Assert.False(ListenBrainzIntakeController.TryNormalize(Request("single", 2, now.ToUnixTimeSeconds()), now, out _, out _));
        Assert.False(ListenBrainzIntakeController.TryNormalize(Request("single", 1, now.AddMinutes(6).ToUnixTimeSeconds()), now, out _, out _));
        var invalid = Request("single", 1, now.ToUnixTimeSeconds());
        invalid.Payload![0]!.TrackMetadata!.AdditionalInfo = new() { RecordingMusicBrainzId = "not-an-id" };
        Assert.False(ListenBrainzIntakeController.TryNormalize(invalid, now, out _, out _));
    }

    [Fact]
    public void IntakeUsesPublicListenBrainzRouteAndOneMegabyteBodyLimit()
    {
        var route = Assert.Single(typeof(ListenBrainzIntakeController)
            .GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>());
        var submit = typeof(ListenBrainzIntakeController).GetMethod(nameof(ListenBrainzIntakeController.SubmitListens))!;
        var limit = Assert.Single(submit.GetCustomAttributes(typeof(RequestSizeLimitAttribute), false)
            .Cast<RequestSizeLimitAttribute>());

        Assert.Equal("apis/listenbrainz/1", route.Template);
        Assert.Equal(1_048_576, ((IRequestSizeLimitMetadata)limit).MaxRequestBodySize);
    }

    private static ListenBrainzSubmitRequest Request(string type, int count, long timestamp) => new()
    {
        ListenType = type,
        Payload = Enumerable.Range(0, count).Select(_ => (ListenBrainzListen?)new ListenBrainzListen
        {
            ListenedAt = timestamp,
            TrackMetadata = new()
            {
                ArtistName = "Artist",
                TrackName = "Song"
            }
        }).ToList()
    };
}
