using System.Text;
using allstarr.Core.Enrichment;

namespace allstarr.Tests;

public sealed class PromotionNamingTests
{
    private const string DefaultScript = "$if2(%albumartist%,%artist%)/%album%$if(%year%, \\(%year%\\))/$if($gt(%totaldiscs%,1),%discnumber%-)$num(%tracknumber%,2) - %title%.%_extension%";

    private static Dictionary<string, string?> Fields() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["artist"] = "Singer",
        ["albumartist"] = "Various Artists",
        ["album"] = "Collection",
        ["title"] = "Song",
        ["year"] = "2024",
        ["tracknumber"] = "3",
        ["totaldiscs"] = "2",
        ["discnumber"] = "1",
        ["_extension"] = "flac"
    };

    [Fact]
    public void DefaultScript_HandlesCompilationAndMultipleDiscs()
    {
        var result = PromotionNaming.Render(DefaultScript, Fields());
        Assert.Empty(result.Errors);
        Assert.Equal("Various Artists/Collection (2024)/1-03 - Song.flac", result.RelativePath);
    }

    [Fact]
    public void DefaultScript_HandlesMissingArtistYearAndSingleDisc()
    {
        var fields = Fields();
        fields.Remove("albumartist");
        fields.Remove("year");
        fields["totaldiscs"] = "1";
        Assert.Equal("Singer/Collection/03 - Song.flac", PromotionNaming.Render(DefaultScript, fields).RelativePath);
    }

    [Theory]
    [InlineData("year", null)]
    [InlineData("tracknumber", null)]
    [InlineData("tracknumber", "0")]
    public void PromotionNaming_RequiresMetadataEvenForOptionalYearSuffix(string field, string? value)
    {
        var fields = Fields();
        fields[field] = value;
        var error = Assert.Throws<PromotionException>(() => PromotionNaming.RenderForPromotion(DefaultScript, fields));
        Assert.Equal(PromotionFailure.NamingMetadataMissing, error.Failure);
        Assert.Equal("naming-metadata-missing", error.Reason);
    }

    [Fact]
    public void PromotionNaming_AllowsAnExplicitPatternWithoutMissingFields()
    {
        var fields = Fields();
        fields.Remove("year");
        fields.Remove("tracknumber");
        Assert.Equal("Singer/Collection/Song.flac",
            PromotionNaming.RenderForPromotion("%artist%/%album%/%title%.%_extension%", fields).RelativePath);
        Assert.Equal("Various Artists/Collection (2024)/1-03 - Song.flac",
            PromotionNaming.RenderForPromotion(DefaultScript, Fields()).RelativePath);
    }

    [Theory]
    [InlineData("$if(yes,one,two)", "one")]
    [InlineData("$if(,one,two)", "two")]
    [InlineData("$if(,one)", "_")]
    [InlineData("$if2(,,three,four)", "three")]
    [InlineData("$not()", "1")]
    [InlineData("$not(yes)", "_")]
    [InlineData("$and(yes,yes)", "1")]
    [InlineData("$and(yes,)", "_")]
    [InlineData("$or(,yes)", "1")]
    [InlineData("$eq(a,a)", "1")]
    [InlineData("$eq(a,A)", "_")]
    [InlineData("$ne(a,A)", "1")]
    [InlineData("$gt(12,2)", "1")]
    [InlineData("$lt(2,12)", "1")]
    [InlineData("$left(abcdef,3)", "abc")]
    [InlineData("$right(abcdef,3)", "def")]
    [InlineData("$num(3,2)", "03")]
    [InlineData("$num(-3,3)", "-03")]
    [InlineData("$lower(ABC)", "abc")]
    [InlineData("$upper(abc)", "ABC")]
    [InlineData("$title(the long road)", "The Long Road")]
    [InlineData("$replace(a-b-a,a,x)", "x-b-x")]
    [InlineData("$rreplace(abc123,[0-9]+,x)", "abcx")]
    [InlineData("$rreplace(ab,\\(a\\)\\(b\\),\\\\2\\\\1)", "ba")]
    [InlineData("$trim(  title  )", "title")]
    [InlineData("$trim(--title--,-)", "title")]
    [InlineData("$noop(hidden)visible", "visible")]
    [InlineData("$set(prefix,hello)$get(prefix)", "hello")]
    [InlineData("$set(artist,Updated)%artist%", "Updated")]
    [InlineData("$get(artist)", "Singer")]
    [InlineData("$firstalphachar(étoile)", "É")]
    [InlineData("$firstalphachar(123)", "#")]
    [InlineData("$firstalphachar(123,other)", "other")]
    [InlineData("$initials(the long road)", "TLR")]
    public void SupportedFunctions_RenderExpectedValue(string script, string expected)
    {
        var result = PromotionNaming.Render(script, Fields());
        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.RelativePath);
    }

    [Fact]
    public void Escapes_RepresentPicardPunctuationLiterally()
    {
        Assert.Equal("$100% (live), encore_", PromotionNaming.Render("\\$100\\% \\(live\\)\\, encore\\\\", Fields()).RelativePath);
    }

    [Fact]
    public void FieldsAndFunctionReplacements_CannotInjectFolders()
    {
        var fields = Fields();
        fields["title"] = "Side A/B\\C: live";
        Assert.Equal("Singer/Side A_B_C_ live.flac", PromotionNaming.Render("%artist%/%title%.%_extension%", fields).RelativePath);
        Assert.Equal("a_b", PromotionNaming.Render("$replace(a-b,-,%title%)", new Dictionary<string, string?> { ["title"] = "/" }).RelativePath);
        Assert.Equal("one/two", PromotionNaming.Render("$if(yes,one/two,no)", fields).RelativePath);
    }

    [Theory]
    [InlineData("$replace(%title%,/, - )", "A/B", "A - B")]
    [InlineData("$rreplace(%title%,/, - )", "A/B", "A - B")]
    [InlineData("$replace(%title%,\\\\, - )", "A\\B", "A - B")]
    [InlineData("$rreplace(%title%,\\\\\\\\, - )", "A\\B", "A - B")]
    [InlineData("$replace(one/two,/, - )", "unused", "one - two")]
    [InlineData("$eq(%title%,/)", "/", "1")]
    [InlineData("$trim(%title%,/)", "/title/", "title")]
    [InlineData("$rreplace(%title%,\\(.+\\),\\\\1)", "A/B", "A_B")]
    [InlineData("$rreplace(one/two,\\(.+\\),\\\\1)", "unused", "one/two")]
    public void Functions_CanMatchMetadataSlashesAndPreserveFolderProvenance(string script, string title, string expected)
    {
        var result = PromotionNaming.Render(script, new Dictionary<string, string?> { ["title"] = title });
        Assert.Empty(result.Errors);
        Assert.Equal(expected, result.RelativePath);
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("nul.flac", "_nul.flac")]
    [InlineData("COM1.wav", "_COM1.wav")]
    [InlineData("LPT9", "_LPT9")]
    [InlineData("title. ", "title")]
    [InlineData("C:/title", "C__title")]
    [InlineData("a<b>c:d\"e|f?g*h", "a_b_c_d_e_f_g_h")]
    public void Sanitization_IsPlatformIndependent(string value, string expected)
    {
        Assert.Equal(expected, PromotionNaming.Render("%title%", new Dictionary<string, string?> { ["title"] = value }).RelativePath);
    }

    [Fact]
    public void Unicode_IsNormalizedAndTruncatedOnRuneBoundaries()
    {
        var fields = Fields();
        fields["title"] = "e\u0301 東京 🎵";
        Assert.Equal("é 東京 🎵.flac", PromotionNaming.Render("%title%.%_extension%", fields).RelativePath);
        fields["title"] = string.Concat(Enumerable.Repeat("東京🎵", 200));
        var result = PromotionNaming.Render("%title%.%_extension%", fields);
        Assert.Empty(result.Errors);
        Assert.EndsWith(".flac", result.RelativePath);
        Assert.True(Encoding.UTF8.GetByteCount(result.RelativePath!) <= 180);
        Assert.DoesNotContain('\uFFFD', result.RelativePath!);
    }

    [Fact]
    public void DeepLongPaths_RespectTotalByteBudget()
    {
        var result = PromotionNaming.Render(string.Join('/', Enumerable.Repeat("%title%", 12)) + ".flac",
            new Dictionary<string, string?> { ["title"] = new string('a', 400) });
        Assert.Empty(result.Errors);
        Assert.True(Encoding.UTF8.GetByteCount(result.RelativePath!) <= 1024);
        Assert.All(result.RelativePath!.Split('/'), part => Assert.True(Encoding.UTF8.GetByteCount(part) <= 180));
        Assert.EndsWith(".flac", result.RelativePath);
    }

    [Fact]
    public void Collisions_AreDeterministicCaseInsensitiveAndPreserveExtension()
    {
        var paths = new[] { "Singer/Song.flac", "SINGER/SONG (2).FLAC", "Singer/Song (4).flac" };
        Assert.Equal("Singer/Song (3).flac", PromotionNaming.Render("%artist%/%title%.%_extension%", Fields(), paths).RelativePath);
        var longFields = Fields();
        longFields["title"] = new string('x', 300);
        var first = PromotionNaming.Render("%title%.%_extension%", longFields).RelativePath!;
        var second = PromotionNaming.Render("%title%.%_extension%", longFields, [first]).RelativePath!;
        Assert.EndsWith(" (2).flac", second);
        Assert.True(Encoding.UTF8.GetByteCount(second) <= 180);
    }

    [Fact]
    public void CollisionOfFullLengthPath_KeepsItsFolders()
    {
        var script = string.Join('/', Enumerable.Repeat("%title%", 12)) + ".flac";
        var fields = new Dictionary<string, string?> { ["title"] = new string('a', 400) };
        var first = PromotionNaming.Render(script, fields).RelativePath!;
        var result = PromotionNaming.Render(script, fields, [first]);
        Assert.Empty(result.Errors);
        var relative = Assert.IsType<string>(result.RelativePath);
        Assert.Equal(first[..first.LastIndexOf('/')], relative[..relative.LastIndexOf('/')]);
        Assert.EndsWith(" (2).flac", result.RelativePath);
        Assert.True(Encoding.UTF8.GetByteCount(result.RelativePath!) <= 1024);
    }

    [Fact]
    public void Conditionals_EvaluateOnlyTheSelectedBranch()
    {
        Assert.Equal("safe", PromotionNaming.Render("$if(yes,safe,$num(3,999))", Fields()).RelativePath);
        Assert.Equal("safe", PromotionNaming.Render("$if2(safe,$num(3,999))", Fields()).RelativePath);
        Assert.Equal("safe", PromotionNaming.Render("$set(value,safe)$if(yes,$get(value),$set(value,other))", Fields()).RelativePath);
    }

    [Fact]
    public void TraversalCreatedByFunctions_IsRejectedAtRenderTime()
    {
        var result = PromotionNaming.Render("$replace(.x,x,.)/%title%", Fields());
        Assert.Null(result.RelativePath);
        Assert.NotEmpty(result.Errors);
    }

    [Theory]
    [InlineData("%unsupported%")]
    [InlineData("$unknown(value)")]
    [InlineData("$num(3)")]
    [InlineData("$if(a,b,c,d)")]
    [InlineData("$lower(unclosed")]
    [InlineData("%title")]
    [InlineData("trailing\\")]
    [InlineData("../%title%")]
    [InlineData("%artist%/../%title%")]
    [InlineData("/%title%")]
    [InlineData("C:/%title%")]
    [InlineData("$if(yes,../bad,good)")]
    public void InvalidScripts_ReturnPositionedErrors(string script)
    {
        var errors = PromotionNaming.Validate(script);
        Assert.NotEmpty(errors);
        Assert.All(errors, error =>
        {
            Assert.InRange(error.Position, 0, script.Length);
            Assert.InRange(error.Length, 0, script.Length - error.Position);
        });
        Assert.Null(PromotionNaming.Render(script, Fields()).RelativePath);
    }

    [Fact]
    public void Validation_ReportsZeroBasedVariableSpan()
    {
        var error = Assert.Single(PromotionNaming.Validate("ok/%unknown%"));
        Assert.Equal(3, error.Position);
        Assert.Equal(9, error.Length);
    }

    [Theory]
    [InlineData(" ")]
    [InlineData(" -  ")]
    [InlineData("/%title%")]
    [InlineData("$noop(extra)")]
    public void NothingMayFollowTheFileExtension(string trailing)
    {
        const string script = "%artist%/%title%.%_extension%";
        var error = Assert.Single(PromotionNaming.Validate(script + trailing));
        Assert.Equal(script.Length, error.Position);
        Assert.Equal(trailing.Length, error.Length);
        Assert.Equal("Nothing may follow the file extension", error.Message);
        Assert.Null(PromotionNaming.Render(script + trailing, Fields()).RelativePath);
        Assert.Empty(PromotionNaming.Validate(script));
    }

    [Fact]
    public void ResourceLimits_ReturnErrors()
    {
        Assert.NotEmpty(PromotionNaming.Validate(new string('x', 20000)));
        Assert.NotEmpty(PromotionNaming.Validate(string.Concat(Enumerable.Repeat("$lower(", 100)) + "x" + new string(')', 100)));
        Assert.NotEmpty(PromotionNaming.Render("$rreplace(value,[,x)", Fields()).Errors);
        Assert.NotEmpty(PromotionNaming.Validate("$rreplace(value,[,x)"));
        Assert.NotEmpty(PromotionNaming.Render("$num(1,1000)", Fields()).Errors);
        Assert.NotEmpty(PromotionNaming.Render("%title%", new Dictionary<string, string?> { ["title"] = new string('x', 100000) }).Errors);
    }

    [Fact]
    public void AllSupportedMetadataVariables_AreAccepted()
    {
        var names = new[] { "artist", "albumartist", "album", "title", "date", "originaldate", "year", "originalyear",
            "tracknumber", "totaltracks", "discnumber", "totaldiscs", "genre", "label", "catalognumber", "releasetype",
            "releasecountry", "media", "_extension", "_quality", "musicbrainz_recordingid", "musicbrainz_albumid" };
        Assert.Empty(PromotionNaming.Validate(string.Join(" - ", names.Select(name => $"%{name}%"))));
    }

    [Theory]
    [InlineData("../escape.flac")]
    [InlineData("/escape.flac")]
    [InlineData("C:/escape.flac")]
    [InlineData("folder/../../escape.flac")]
    [InlineData("folder\\escape.flac")]
    public void Containment_RejectsTraversalAndAbsolutePaths(string relativePath)
    {
        Assert.Throws<ArgumentException>(() => PromotionNaming.ContainedPath(Path.GetTempPath(), relativePath));
    }

    [Fact]
    public void Containment_IsPureLexicalAndKeepsSiblingNamesInsideRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "music");
        Assert.Equal(Path.Combine(root, "music-other", "song.flac"), PromotionNaming.ContainedPath(root, "music-other/song.flac"));
    }

    [Fact]
    public void Tokens_RoundtripOrderedFieldsAndEscapedLiterals()
    {
        PromotionNamingToken[] tokens = [
            new(PromotionNamingTokenKind.Field, "artist"),
            new(PromotionNamingTokenKind.Literal, "/$100% (live), \\"),
            new(PromotionNamingTokenKind.Field, "title"),
            new(PromotionNamingTokenKind.Literal, "."),
            new(PromotionNamingTokenKind.Field, "_extension")
        ];
        var script = PromotionNamingTokens.Compile(tokens);
        Assert.True(PromotionNamingTokens.TryParse(script, out var parsed));
        Assert.Equal(tokens, parsed);
        Assert.Equal(script, PromotionNamingTokens.Compile(parsed));
    }

    [Fact]
    public void Tokens_FunctionsRemainScriptOnlyAndMalformedInputIsRejected()
    {
        Assert.False(PromotionNamingTokens.TryParse("$lower(%artist%)/%title%", out var tokens));
        Assert.Empty(tokens);
        Assert.False(PromotionNamingTokens.TryParse("%unknown%", out _));
        Assert.Throws<ArgumentException>(() => PromotionNamingTokens.Compile([new(PromotionNamingTokenKind.Field, "unknown")]));
    }

    [Fact]
    public void Tokens_CanBeReorderedAndCompiledBackToAnEquivalentScript()
    {
        PromotionNamingToken[] tokens = [new(PromotionNamingTokenKind.Field, "title"),
            new(PromotionNamingTokenKind.Literal, " - "), new(PromotionNamingTokenKind.Field, "artist")];
        Assert.Equal("Song - Singer", PromotionNaming.Render(PromotionNamingTokens.Compile(tokens), Fields()).RelativePath);
        Assert.True(PromotionNamingTokens.TryParse(PromotionNamingTokens.Compile(tokens.Reverse()), out var reversed));
        Assert.Equal("Singer - Song", PromotionNaming.Render(PromotionNamingTokens.Compile(reversed), Fields()).RelativePath);
    }
}
