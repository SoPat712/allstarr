using System.Text.Json;
using allstarr.Core.ClientApiV2;
using allstarr.Core.Identity;
using allstarr.Core.Protocols;
using Microsoft.AspNetCore.Http;

namespace allstarr.Tests.Protocols;

public sealed class ServiceLyricsSearchTests
{
    private const string Query = "Something in the way that you walk";

    [Fact]
    public void NetEaseHitsUseTheMarkedLineAndItsHighlight()
    {
        using var json = JsonDocument.Parse("""
            {"result":{"songs":[
              {"name":"Dreamstate","ar":[{"name":"Stonefox"}],"lyrics":["intro\r","<b>something in the way that you walk</b> and you talk and\r"]},
              {"name":"No marks","ar":[{"name":"Someone"}],"lyrics":["nothing here"]},
              {"name":"","ar":[{"name":"Nameless"}],"lyrics":["<b>x</b>"]}]}}
            """);
        var hit = Assert.Single(V2ServiceLyricsSearch.NetEase(json.RootElement, Query));
        Assert.Equal(("Dreamstate", "Stonefox", "neteaselyrics"), (hit.Title, hit.Artist, hit.ProviderId));
        Assert.Equal("something in the way that you walk and you talk and", hit.Line);
        var highlight = Assert.Single(hit.Highlights);
        Assert.Equal("something in the way that you walk", hit.Line.Substring(highlight.Start, highlight.Length));
    }

    [Fact]
    public void QqHitsFindTheLineHoldingThePhraseInTheWholeLyrics()
    {
        using var json = JsonDocument.Parse("""
            {"data":{"lyric":{"list":[
              {"songname":"Walk","singer":[{"name":"Griff"}],"content":"Walk - Griff\\n Composed by：Griff\\n Something in the way that you walk, oh\\n You won't remember"},
              {"songname":"Higher Than This","singer":[{"name":"Vallis Alps"}],"content":"Higher Than This\\n Nothing alike"}]}}}
            """);
        var hit = Assert.Single(V2ServiceLyricsSearch.Qq(json.RootElement, Query));
        Assert.Equal(("Walk", "Griff", "qqlyrics"), (hit.Title, hit.Artist, hit.ProviderId));
        Assert.Equal("Something in the way that you walk, oh", hit.Line);
        Assert.Equal(new LyricsSearchHighlightValue(0, Query.Length), Value(Assert.Single(hit.Highlights)));
    }

    [Fact]
    public void WholePhraseHitsComeFirstAndTheSameSongAppearsOnce()
    {
        var ranked = V2ServiceLyricsSearch.Rank([
            new("Next To Me", "Imagine Dragons", "the way that you", [], "neteaselyrics"),
            new("Walk", "Griff", "something in the way that you walk", [], "neteaselyrics"),
            new("Walk (In The Room)", "Griff", "Something in the way that you walk", [], "qqlyrics"),
            new("Dreamstate", "Stonefox, Tep No", "something in the way that you walk and you talk", [], "qqlyrics")], Query).ToArray();
        Assert.Equal(["Walk", "Dreamstate", "Next To Me"], ranked.Select(hit => hit.Title));
    }

    [Theory]
    [InlineData("Walk", "Griff", true)]
    [InlineData("Walk (Acoustic)", "Griff & Friends", true)]
    [InlineData("Walk", "Someone Else", false)]
    [InlineData("Walking", "Griff", false)]
    public void ASongMatchesByTitleAndFirstArtist(string title, string artist, bool same) =>
        Assert.Equal(same, V2ServiceLyricsSearch.Same(new("Walk", "Griff, Ines Dunn", "line", [], "qqlyrics"), title, artist));

    [Fact]
    public async Task HitsCanComeFirstWithoutTracksAndTheServicesAreAskedOnce()
    {
        var asked = 0;
        var handler = new AnswerHandler(request =>
        {
            Interlocked.Increment(ref asked);
            return request.RequestUri!.Host == "music.163.com"
                ? """{"result":{"songs":[{"name":"Walk","ar":[{"name":"Griff"}],"lyrics":["<b>something in the way that you walk</b>"]}]}}"""
                : """{"data":{"lyric":{"list":[]}}}""";
        });
        var search = new V2ServiceLyricsSearch(new Clients(handler), new TestMemoryApplicationCache(), null!, null!, null!, null!);
        var http = new DefaultHttpContext();
        http.Request.QueryString = new QueryString("?q=walk&resolve=false");
        var owner = Guid.CreateVersion7();
        var execution = new ProtocolExecutionContext(ProtocolKind.Jellyfin, "backend", owner.ToString("N"),
            new AllstarrPrincipal(owner, "jellyfin", "backend", owner.ToString("N"), "Owner", false), "lyrics", DateTimeOffset.UtcNow.AddMinutes(1), default);

        var first = await search.SearchAsync(http, execution, Query, 10, resolve: false);
        var again = await search.SearchAsync(http, execution, Query, 10, resolve: false);

        var body = Assert.IsType<V2ServiceLyricsSearchResponse>(first.Body);
        Assert.False(body.Resolved);
        var hit = Assert.Single(body.Results);
        Assert.Equal(("Walk", "Griff"), (hit.Title, hit.Artist));
        Assert.Null(hit.Track);
        Assert.Equal("pending", hit.MatchState);
        Assert.Equal(["ok", "ok"], body.Providers.Select(provider => provider.State));
        Assert.Equal(200, again.Status);
        Assert.Equal(2, asked);
    }

    private sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private sealed class AnswerHandler(Func<HttpRequestMessage, string> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(answer(request)) });
    }

    private sealed record LyricsSearchHighlightValue(int Start, int Length);
    private static LyricsSearchHighlightValue Value(allstarr.Core.ClientApi.LyricsSearchHighlight highlight) => new(highlight.Start, highlight.Length);
}
