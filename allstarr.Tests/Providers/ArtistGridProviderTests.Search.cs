using System.Net;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.ArtistGrid;

namespace allstarr.Tests.Providers;

public sealed partial class ArtistGridProviderTests
{
    [Theory]
    [InlineData("communicate ariana grande")]
    [InlineData("ariana grande communicate")]
    [InlineData("Communicate")]
    public async Task SearchReturnsEveryPlayableLengthAndRanksCompleteVersionsFirst(string query)
    {
        using var client = CommunicateClient();
        var metadata = Metadata(client);
        var page = (await metadata.SearchTracksAsync(Context(), new(query, new(50)))).RequireValue();

        Assert.Equal(10, page.Items.Count);
        Assert.Equal(10, page.Items.Select(item => item.Id.Value).Distinct().Count());
        Assert.Contains(page.Items, item => item.Title == "Diva Shit [V1]");
        Assert.Contains(page.Items, item => item.Title == "Communicate [V3] [Tagged]");
        Assert.Contains(page.Items, item => item.Title.EndsWith("[Snippet]", StringComparison.Ordinal));
        Assert.All(page.Items.Take(5), item => Assert.DoesNotContain(
            new[] { "[Partial]", "[Snippet]", "[Beat Only]", "[Stem Bounce]", "[Confirmed]" },
            label => item.Title.EndsWith(label, StringComparison.Ordinal)));
        Assert.All(page.Items.Skip(6), item => Assert.Contains(
            new[] { "[Partial]", "[Snippet]", "[Beat Only]", "[Stem Bounce]" },
            label => item.Title.EndsWith(label, StringComparison.Ordinal)));
        Assert.Null(page.NextCursor);
        Assert.DoesNotContain(page.Items, item => item.Title.Contains("Unavailable", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SearchPagesAndExactLookupsPreservePlayableVersionsAndLengthLabels()
    {
        using var client = CommunicateClient();
        var metadata = Metadata(client);
        var tracks = new List<ProviderTrackMetadata>();
        string? cursor = null;
        do
        {
            var page = (await metadata.SearchTracksAsync(Context(), new("Ariana Grande Communicate", new(3, cursor)))).RequireValue();
            tracks.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (cursor != null);

        Assert.Equal(10, tracks.Count);
        Assert.Equal(10, tracks.Select(item => item.Id.Value).Distinct().Count());
        foreach (var track in tracks)
        {
            var lookup = (await metadata.GetTrackAsync(Context(), new(track.Id))).RequireValue();
            Assert.Equal(track.Id, lookup.Id);
            Assert.Equal(track.Title, lookup.Title);
        }
    }

    [Fact]
    public async Task SearchKeepsDistinctPlayableSourcesWhenVersionsShareTheirFirstLink()
    {
        var payload = """
            {"name":"Artist","tab":{"gid":"g-1"},"tracks":[
              {"name":{"raw":"Communicate [V1]"},"available_length":"Full","links":[{"url":"https://pillows.su/f/common"},{"url":"https://pillows.su/f/version-1"}]},
              {"name":{"raw":"Communicate [V3]"},"available_length":"OG File","links":[{"url":"https://pillows.su/f/common"},{"url":"https://pillows.su/f/version-3"}]}
            ]}
            """;
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/artists.csv" => Response(Csv("Artist", "artist"), null, "text/csv"),
            "/sh/artist/" or "/sh/artist/gid/g-1" => Response(payload, null, "application/json"),
            _ => throw new InvalidOperationException("Unexpected fixture request.")
        });
        var metadata = Metadata(client);
        var tracks = (await metadata.SearchTracksAsync(Context(), new("Artist Communicate", new(50)))).RequireValue().Items;
        Assert.Equal(3, tracks.Count);
        Assert.Equal(3, tracks.Select(item => item.Id.Value).Distinct().Count());
        Assert.Contains(tracks, item => item.Title.StartsWith("Communicate [V3]", StringComparison.Ordinal));
        foreach (var track in tracks)
            Assert.Equal(track.Id, (await metadata.GetTrackAsync(Context(), new(track.Id))).RequireValue().Id);
    }

    [Fact]
    public async Task TitleSearchUsesCachedTrackersAndABoundedSelectionOfRecommendedTrackers()
    {
        var fetched = new List<string>();
        var index = "name,url,best\nAriana Grande,ariana,false\n" +
            string.Join('\n', Enumerable.Range(0, 30).Select(number => $"Artist {number:D2},artist-{number},true"));
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/artists.csv") return Response(index, null, "text/csv");
            fetched.Add(request.RequestUri.AbsolutePath);
            return Response(CommunicatePayload(), null, "application/json");
        });
        var metadata = Metadata(client);
        Assert.NotEmpty((await metadata.SearchTracksAsync(Context(), new("Artist 29 Communicate", new(50)))).RequireValue().Items);
        fetched.Clear();
        var result = (await metadata.SearchTracksAsync(Context(), new("Communicate", new(200)))).RequireValue();

        Assert.Contains(result.Items, item => item.Artists[0].Name == "Artist 29");
        Assert.Contains(result.Items, item => item.Artists[0].Name == "Ariana Grande");
        Assert.InRange(fetched.Count, 1, 8);
        Assert.Equal(fetched.Count, fetched.Distinct().Count());
    }

    [Theory]
    [InlineData("Ariana Grande Communicate V3", "Communicate [V3] [Tagged]")]
    [InlineData("Ariana Grande So Crazy", "Diva Shit [V1]")]
    public async Task SearchMatchesAliasesAndExplicitVersionTags(string query, string title)
    {
        using var client = CommunicateClient();
        var tracks = (await Metadata(client).SearchTracksAsync(Context(), new(query, new(50)))).RequireValue().Items;
        Assert.Equal(title, Assert.Single(tracks).Title);
    }

    [Theory]
    [InlineData("communicate")]
    [InlineData("communicate ariana grande")]
    [InlineData("ariana grande communicate")]
    public async Task SearchPreservesTheFourTrackerVersionsAndSeparatesNotesFromArtists(string query)
    {
        using var client = Client(request => request.RequestUri!.AbsolutePath switch
        {
            "/artists.csv" => Response("name,url,best\nAriana Grande,ariana,false", null, "text/csv"),
            "/sh/ariana/" or "/sh/ariana/gid/503467644" => Response(
                File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "ArtistGrid", "communicate.json")),
                null, "application/json"),
            _ => throw new InvalidOperationException("Unexpected fixture request.")
        });
        var metadata = Metadata(client);
        var tracks = (await metadata.SearchTracksAsync(Context(), new(query, new(50)))).RequireValue().Items;

        Assert.Equal(4, tracks.Count);
        Assert.Equal(4, tracks.Select(track => track.Id).Distinct().Count());
        Assert.All(tracks, track => Assert.Equal("Ariana Grande", Assert.Single(track.Artists).Name));
        Assert.All(tracks, track => Assert.Equal(track.Title.Trim(), track.Title));
        Assert.Equal("Communicate [V3] [OG File]", tracks[0].Title);
        Assert.Equal(TimeSpan.FromSeconds(115), tracks[0].Duration);
        Assert.True(tracks[0].IsBestVersion);
        Assert.All(tracks.Skip(1), track => Assert.False(track.IsBestVersion));
        Assert.True(allstarr.Core.Protocols.ProtocolProviderGateway.Map(tracks[0]).IsBestVersion);
        Assert.Contains(tracks, track => track.Title == "Diva Shit [V1]");
        Assert.Contains(tracks, track => track.Title == "Communicate [V2]");
        Assert.Contains(tracks, track => track.Title == "Communicate [V4] [OG File]");
        Assert.All(tracks, track => Assert.Contains(track.Contributors, credit => credit.StartsWith("prod. ", StringComparison.Ordinal)));
        Assert.Contains(tracks.Single(track => track.Title == "Communicate [V2]").Contributors, credit => credit == "ref. Tayla Parx");
        foreach (var track in tracks)
        {
            var lookup = (await metadata.GetTrackAsync(Context(), new(track.Id))).RequireValue();
            Assert.Equal(track.Title, lookup.Title);
            Assert.Equal(track.Artists, lookup.Artists);
            Assert.Equal(track.IsBestVersion, lookup.IsBestVersion);
        }
    }

    [Fact]
    public async Task SearchUsesOnlyFeaturedAndWithCreditsAsAdditionalArtists()
    {
        const string payload = """
            {"name":"Artist","tab":{"gid":"g-1"},"tracks":[
              {"name":{"raw":"⭐Song [V2]  \n(prod. Producer) (ref. Reference)\n(feat. Guest) (with Friend)\n(Alias)",
                "credits":["(prod. Producer) (ref. Reference)","(feat. Guest) (with Friend)","(Alias)"]},
               "links":[{"url":"https://pillows.su/f/song"}]}
            ]}
            """;
        using var client = Client(request => request.RequestUri!.AbsolutePath == "/artists.csv"
            ? Response(Csv("Artist", "artist"), null, "text/csv")
            : Response(payload, null, "application/json"));
        var track = Assert.Single((await Metadata(client).SearchTracksAsync(Context(), new("Artist Alias", new(20)))).RequireValue().Items);

        Assert.Equal("Song [V2]", track.Title);
        Assert.Equal(new[] { "Artist", "Guest", "Friend" }, track.Artists.Select(artist => artist.Name));
        Assert.Equal(new[] { "prod. Producer", "ref. Reference" }, track.Contributors);
    }

    private static HttpClient CommunicateClient() => Client(request => request.RequestUri!.AbsolutePath switch
    {
        "/artists.csv" => Response(Csv("Ariana Grande", "ariana"), null, "text/csv"),
        "/sh/ariana/" or "/sh/ariana/gid/g-1" => Response(CommunicatePayload(), null, "application/json"),
        _ => throw new InvalidOperationException("Unexpected fixture request.")
    });

    private static string CommunicatePayload()
    {
        var labels = new[] { "Full", "OG File", "Tagged", "Full (Tagged)", "Partial", "Snippet", "Beat Only", "Stem Bounce", "Confirmed", "" };
        var tracks = labels.Select((label, index) => new
        {
            name = new { raw = index == 0 ? "Diva Shit [V1]\n(prod. Someone)\n(So Crazy) (Communicate)" : $"Communicate [V{index + 1}]", title = "Communicate" },
            available_length = label,
            track_length = index == 5 ? "0:15" : "1:55",
            links = new[] { new { url = $"https://pillows.su/f/communicate-{index}" } }
        }).ToList();
        tracks.Add(new { name = new { raw = "Communicate Unavailable", title = "Communicate" }, available_length = "Full", track_length = "1:55", links = Array.Empty<string>().Select(url => new { url }).ToArray() });
        tracks.Add(new { name = new { raw = "Communicate Unavailable Video", title = "Communicate" }, available_length = "Confirmed", track_length = "1:55", links = new[] { new { url = "https://www.youtube.com/watch?v=unavailable" } } });
        return JsonSerializer.Serialize(new { name = "Ariana Grande", tab = new { name = "Demos", gid = "g-1" }, eras = new[] { new { name = "Demos", tracks } } });
    }
}
