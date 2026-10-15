using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using allstarr.Core.Capabilities;
using allstarr.Core.Providers.Lyrics;
using allstarr.Core.Providers.Spotify;
using allstarr.Core.Storage;
using allstarr.Services.Common;
using Microsoft.Extensions.DependencyInjection;

namespace allstarr.Tests;

public sealed class MusixmatchLyricsCapabilityAdapterTests
{
    private static readonly Guid RecordingId =
        Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserId =
        Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid AccountA =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AccountB =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid SecretA =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid SecretB =
        Guid.Parse("44444444-4444-4444-4444-444444444444");

    private const string TimedMacroPayload = """
        {
          "message": {
            "header": {
              "status_code": 200
            },
            "body": {
              "macro_calls": {
                "matcher.track.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "track": {
                        "track_id": "track-42",
                        "track_name": "Fixture track",
                        "artist_name": "Fixture artist"
                      }
                    }
                  }
                },
                "track.richsync.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "richsync": {
                        "richsync_body": "[{\"ts\":1.234,\"x\":\"First\"},{\"ts\":2.5,\"x\":\"Second\"}]"
                      }
                    }
                  }
                },
                "track.subtitles.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "subtitle_list": [
                        {
                          "subtitle": {
                            "subtitle_body": "[00:01.234]First\n[00:02.500]Second",
                            "subtitle_language": "en"
                          }
                        }
                      ]
                    }
                  }
                },
                "track.lyrics.get": {
                  "message": {
                    "header": {
                      "status_code": 200
                    },
                    "body": {
                      "lyrics": {
                        "lyrics_body": "First\nSecond"
                      }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    private const string MissingMacroPayload =
        """{"message":{"header":{"status_code":200},"body":{"macro_calls":{}}}}""";

    [Fact]
    public void Registration_RequiresAccountDeclaresBothScopesTokenAndFixedOrigin()
    {
        var adapter = CreateAdapter(new FixtureHttpHandler(), new FixtureSecretAccessor());
        var registration = MusixmatchLyricsCapabilityAdapter.CreateRegistration(adapter);
        var validated = ProviderRegistrationValidator.Validate(registration);
        var descriptor = validated.Descriptor;
        var lyrics = Assert.Single(descriptor.Capabilities);
        var token = Assert.Single(descriptor.Settings);

        Assert.Equal(MusixmatchLyricsCapabilityAdapter.StableProviderId, descriptor.Id);
        Assert.Equal(ProviderAccountRequirement.Required, lyrics.AccountRequirement);
        Assert.Contains(ProviderAccountScope.Personal, lyrics.AllowedAccountScopes);
        Assert.Contains(ProviderAccountScope.Shared, lyrics.AllowedAccountScopes);
        Assert.Contains("fetchLyrics", lyrics.Hooks);
        Assert.Contains(
            descriptor.Permissions.NetworkOrigins,
            origin => origin == new Uri("https://apic-desktop.musixmatch.com/"));
        Assert.Equal(["userToken"], descriptor.Permissions.SecretSettingKeys);
        Assert.False(descriptor.Permissions.Cache);
        Assert.Equal("userToken", token.Key);
        Assert.Equal(ProviderSettingValueKind.Secret, token.ValueKind);
        Assert.True(token.Required);
        Assert.Equal(
            "Copy the user token from the Musixmatch desktop app's debug information. Allstarr encrypts it.",
            token.HelpText);
        Assert.False(lyrics.SupportsPublicRead);
    }

    [Fact]
    public async Task TimedMacro_UsesExactAccountRequestAndPreservesMilliseconds()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor("desktop-user-token");
        var adapter = CreateAdapter(handler, secrets);

        var outcome = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(preferredFormat: ProviderLyricsFormat.LineTimed));

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        var result = outcome.RequireValue();
        Assert.Equal(ProviderLyricsAvailabilityState.Available, result.Availability);
        Assert.Equal(ProviderLyricsFormat.LineTimed, result.Format);
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.Content);
        Assert.StartsWith("sha256:", result.Revision, StringComparison.Ordinal);
        Assert.Equal(1, handler.Requests);
        Assert.Equal(HttpMethod.Get, handler.Method);
        Assert.Equal("apic-desktop.musixmatch.com", handler.RequestUri!.Host);
        Assert.Equal(
            "/ws/1.1/macro.subtitles.get",
            handler.RequestUri.AbsolutePath);

        var query = ParseQuery(handler.RequestUri);
        Assert.Equal("web-desktop-app-v1.0", query["app_id"]);
        Assert.Equal("json", query["format"]);
        Assert.Equal("lyrics_richsynced", query["namespace"]);
        Assert.Equal("track.richsync", query["optional_calls"]);
        Assert.Equal("lrc", query["subtitle_format"]);
        Assert.Equal("Fixture artist", query["q_artist"]);
        Assert.Equal("Fixture track", query["q_track"]);
        Assert.Equal("desktop-user-token", query["usertoken"]);
        Assert.Equal("180", query["f_subtitle_length"]);
        Assert.Equal("3", query["f_subtitle_length_max_deviation"]);
        Assert.False(query.ContainsKey("duration"));
        Assert.False(query.ContainsKey("deviation"));
        Assert.True(long.TryParse(query["t"], out var timestamp) && timestamp > 0);
        Assert.Equal("en", handler.AcceptLanguage);
        Assert.Equal("AWSELBCORS=0; AWSELB=0", handler.Cookie);
        var secret = Assert.Single(secrets.Accounts);
        Assert.Equal(AccountA, secret.AccountId);
        Assert.Equal(SecretA, secret.SecretReferenceId);
    }

    [Fact]
    public async Task PlainPreference_ProjectsThePlainMacroWithoutInventingTiming()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var outcome = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsFormat.PlainText, outcome.RequireValue().Format);
        Assert.Equal("First\nSecond", outcome.RequireValue().Content);
    }

    [Fact]
    public async Task AvailabilityOnly_ProjectsFullCachedResult()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var content = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));
        var availability = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(availabilityOnly: true, preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(content.IsSuccess, content.Error?.SafeMessage);
        Assert.True(availability.IsSuccess, availability.Error?.SafeMessage);
        Assert.Equal("First\nSecond", content.RequireValue().Content);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, availability.RequireValue().Availability);
        Assert.Null(availability.RequireValue().Content);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task AvailabilityThenContent_UsesOneFullResponseFetch()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var availability = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(availabilityOnly: true, preferredFormat: ProviderLyricsFormat.PlainText));
        var content = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));

        Assert.True(availability.IsSuccess, availability.Error?.SafeMessage);
        Assert.True(content.IsSuccess, content.Error?.SafeMessage);
        Assert.Null(availability.RequireValue().Content);
        Assert.Equal("First\nSecond", content.RequireValue().Content);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Cache_IsolatedByAccountRevisionSecretMetadataAndRequestedFormat()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest(preferredFormat: ProviderLyricsFormat.PlainText));
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 2, secretReferenceId: SecretA),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretB),
            LyricsRequest());
        await adapter.FetchLyricsAsync(
            Context(AccountB, revision: 1, secretReferenceId: SecretA),
            LyricsRequest());

        handler.Payload = TimedMacroPayload.Replace(
            "Fixture track",
            "Alternate track",
            StringComparison.Ordinal);
        await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 1, secretReferenceId: SecretA),
            LyricsRequest(title: "Alternate track"));

        Assert.Equal(6, handler.Requests);
        var keys = cache.GetKeysByPattern("lyrics:v2:*").ToArray();
        Assert.Equal(6, keys.Length);
        Assert.Contains(keys, key => key.Contains(AccountA.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(AccountB.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(SecretA.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(SecretB.ToString("N"), StringComparison.Ordinal));
        Assert.Contains(keys, key => key.Contains(":2:", StringComparison.Ordinal));
        Assert.DoesNotContain(keys, key => key.Contains("desktop-user-token", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SharedAccount_UsesTheSelectedSecretWithoutPublicFallback()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);

        var outcome = await adapter.FetchLyricsAsync(
            Context(AccountA, scope: ProviderAccountScope.Shared),
            LyricsRequest());

        Assert.True(outcome.IsSuccess, outcome.Error?.SafeMessage);
        Assert.Equal(1, handler.Requests);
        var account = Assert.Single(secrets.Accounts);
        Assert.Equal(AccountA, account.AccountId);
        Assert.Null(account.OwnerUserId);
    }

    [Fact]
    public async Task PublicAndMissingAccountContextsAreDeniedBeforeSecretOrHttp()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);

        var publicResult = await adapter.FetchLyricsAsync(PublicContext(), LyricsRequest());
        var missingAccount = await adapter.FetchLyricsAsync(
            MissingAccountContext(),
            LyricsRequest());

        Assert.False(publicResult.IsSuccess);
        Assert.Equal(ProviderErrorKind.Forbidden, publicResult.Error!.Kind);
        Assert.False(missingAccount.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, missingAccount.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task MissingSecretReferenceIsConfigurationFailureBeforeHttp()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);

        var result = await adapter.FetchLyricsAsync(
            MissingSecretContext(),
            LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("{}")]
    [InlineData("{\"userToken\":\"\"}")]
    [InlineData("{\"userToken\":")]
    public async Task MissingOrMalformedSecretBundlesAreConfigurationFailures(string bundle)
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = FixtureSecretAccessor.FromBundle(bundle);
        var adapter = CreateAdapter(handler, secrets);

        var result = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.AccountNeedsConfiguration, result.Error!.Kind);
        Assert.Equal(0, handler.Requests);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, ProviderErrorKind.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, ProviderErrorKind.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, ProviderErrorKind.RateLimited)]
    [InlineData(HttpStatusCode.BadGateway, ProviderErrorKind.TransientFailure)]
    [InlineData(HttpStatusCode.ServiceUnavailable, ProviderErrorKind.TransientFailure)]
    public async Task UpstreamStatusesBecomeTypedSafeOutcomes(
        HttpStatusCode status,
        ProviderErrorKind expected)
    {
        var handler = new FixtureHttpHandler
        {
            StatusCode = status,
            Payload = "{\"message\":{\"header\":{\"status_code\":500}}}"
        };
        if (status == HttpStatusCode.TooManyRequests)
            handler.RetryAfter = TimeSpan.FromSeconds(7);
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var result = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(expected, result.Error!.Kind);
        Assert.DoesNotContain("musixmatch", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("http", result.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        if (expected == ProviderErrorKind.RateLimited)
            Assert.Equal(TimeSpan.FromSeconds(7), result.Error.RetryAfter);
    }

    [Fact]
    public async Task NotFoundAndEmptyMacroAreSafeUnavailableResults()
    {
        var handler = new FixtureHttpHandler
        {
            StatusCode = HttpStatusCode.NotFound,
            Payload = MissingMacroPayload
        };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var notFound = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());
        handler.StatusCode = HttpStatusCode.OK;
        handler.Payload = MissingMacroPayload;
        var empty = await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 2),
            LyricsRequest());

        Assert.True(notFound.IsSuccess, notFound.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, notFound.RequireValue().Availability);
        Assert.True(empty.IsSuccess, empty.Error?.SafeMessage);
        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, empty.RequireValue().Availability);
    }

    [Fact]
    public async Task IdentityMismatchIsAnUnavailableMissWithoutProviderDetails()
    {
        var handler = new FixtureHttpHandler
        {
            Payload = TimedMacroPayload.Replace(
                "Fixture track",
                "Unrelated recording",
                StringComparison.Ordinal)
        };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var result = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.True(result.IsSuccess, result.Error?.SafeMessage);
        Assert.Equal(
            ProviderLyricsAvailabilityState.Unavailable,
            result.RequireValue().Availability);
    }

    [Fact]
    public async Task MetadataLookupKeyIsNotAnUpstreamMusixmatchTrackId()
    {
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = TimedMacroPayload }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest(trackId: new string('a', 64)));
        Assert.Equal("[00:01.234]First\n[00:02.500]Second", result.RequireValue().Content);
    }

    [Fact]
    public async Task UntimedSubtitleCannotPretendToBeTimedAndFallsBackToPlain()
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        calls.Remove("track.richsync.get");
        calls["track.subtitles.get"]!["message"]!["body"]!["subtitle_list"]![0]!["subtitle"]!["subtitle_body"] = "Synthetic untimed subtitle";
        var result = await CreateAdapter(new FixtureHttpHandler { Payload = node.ToJsonString() }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.Equal(ProviderLyricsFormat.PlainText, result.RequireValue().Format);
        Assert.Equal("First\nSecond", result.RequireValue().Content);
    }

    [Fact]
    public async Task MalformedApiStatusIsACompatibilityFailure()
    {
        var result = await CreateAdapter(new FixtureHttpHandler
        { Payload = "{\"message\":{\"header\":{\"status_code\":\"200\"},\"body\":{}}}" }, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());
        Assert.Equal("provider-contract-changed", result.Error?.Code);
    }

    [Fact]
    public async Task RichsyncBeyondOneDayIsAContractFailure()
    {
        var node = JsonNode.Parse(TimedMacroPayload)!;
        var calls = node["message"]!["body"]!["macro_calls"]!.AsObject();
        calls.Remove("track.subtitles.get");
        calls.Remove("track.lyrics.get");
        calls["track.richsync.get"]!["message"]!["body"]!["richsync"]!["richsync_body"] = "[{\"ts\":86400.001,\"x\":\"Synthetic overflow\"}]";
        var handler = new FixtureHttpHandler { Payload = node.ToJsonString() };
        var result = await CreateAdapter(handler, new FixtureSecretAccessor())
            .FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.False(result.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, result.Error!.Kind);
    }

    [Fact]
    public async Task UnavailableResultsAreNotCached()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { StatusCode = HttpStatusCode.NotFound };
        var cache = new TestMemoryApplicationCache();
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor(), cache);

        var first = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());
        handler.StatusCode = HttpStatusCode.OK;
        handler.Payload = TimedMacroPayload;
        var second = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());

        Assert.Equal(ProviderLyricsAvailabilityState.Unavailable, first.RequireValue().Availability);
        Assert.Equal(ProviderLyricsAvailabilityState.Available, second.RequireValue().Availability);
        Assert.Equal(2, handler.Requests);
    }

    [Fact]
    public async Task MalformedOrOversizedResponsesBecomeContractFailures()
    {
        var handler = new FixtureHttpHandler { Payload = "{\"message\":" };
        var adapter = CreateAdapter(handler, new FixtureSecretAccessor());

        var malformed = await adapter.FetchLyricsAsync(Context(AccountA), LyricsRequest());
        handler.Payload = new string('x', 2_000_001);
        var oversized = await adapter.FetchLyricsAsync(
            Context(AccountA, revision: 2),
            LyricsRequest());

        Assert.False(malformed.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, malformed.Error!.Kind);
        Assert.False(oversized.IsSuccess);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, oversized.Error!.Kind);
        Assert.DoesNotContain("message", malformed.Error.SafeMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("x", oversized.Error.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CanceledExpiredAndForeignRequestsNeverUseHttp()
    {
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        var canceledResult = await adapter.FetchLyricsAsync(
            Context(AccountA, cancellationToken: canceled.Token),
            LyricsRequest());
        var expiredResult = await adapter.FetchLyricsAsync(
            Context(AccountA, deadline: DateTimeOffset.UtcNow.AddSeconds(-1)),
            LyricsRequest());
        var foreign = await adapter.FetchLyricsAsync(
            Context(AccountA),
            LyricsRequest(providerId: "other-provider"));

        Assert.Equal(ProviderErrorKind.Canceled, canceledResult.Error!.Kind);
        Assert.Equal(ProviderErrorKind.CapabilityUnavailable, expiredResult.Error!.Kind);
        Assert.Equal(ProviderErrorKind.Forbidden, foreign.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task CancellationDuringWarmCacheReadReturnsCanceled()
    {
        EnsureCacheSettings();
        var handler = new FixtureHttpHandler { Payload = TimedMacroPayload };
        var secrets = new FixtureSecretAccessor();
        var adapter = CreateAdapter(handler, secrets, new BlockingApplicationCache());
        using var canceled = new CancellationTokenSource();

        var pending = adapter.FetchLyricsAsync(
            Context(AccountA, cancellationToken: canceled.Token),
            LyricsRequest());
        await Task.Yield();
        canceled.Cancel();
        var result = await pending;

        Assert.Equal(ProviderErrorKind.Canceled, result.Error!.Kind);
        Assert.Empty(secrets.Accounts);
        Assert.Equal(0, handler.Requests);
    }

    private static MusixmatchLyricsCapabilityAdapter CreateAdapter(
        FixtureHttpHandler handler,
        FixtureSecretAccessor secrets,
        IApplicationCache? cache = null) =>
        new(
            new HttpClient(handler),
            secrets,
            cache ?? new TestMemoryApplicationCache());

    private static ProviderLyricsRequest LyricsRequest(
        bool availabilityOnly = false,
        ProviderLyricsFormat? preferredFormat = ProviderLyricsFormat.LineTimed,
        string title = "Fixture track",
        string artist = "Fixture artist",
        string providerId = MusixmatchLyricsCapabilityAdapter.StableProviderId,
        string trackId = "track-42") =>
        new(
            RecordingId,
            new ProviderExternalResourceId(
                providerId,
                ProviderResourceKind.Track,
                trackId),
            availabilityOnly,
            preferredFormat,
            title,
            [artist],
            "Fixture album",
            180);

    private static ProviderExecutionContext Context(
        Guid accountId = default,
        long revision = 1,
        Guid secretReferenceId = default,
        ProviderAccountScope scope = ProviderAccountScope.Personal,
        DateTimeOffset? deadline = null,
        CancellationToken cancellationToken = default)
    {
        var selectedAccount = accountId == Guid.Empty ? AccountA : accountId;
        var selectedSecret = secretReferenceId == Guid.Empty ? SecretA : secretReferenceId;
        return BuildContext(
            selectedAccount,
            revision,
            selectedSecret,
            scope,
            deadline,
            cancellationToken);
    }

    private static ProviderExecutionContext MissingAccountContext() =>
        BuildContext(
            accountId: null,
            revision: 1,
            secretReferenceId: SecretA,
            scope: ProviderAccountScope.Personal,
            deadline: null,
            cancellationToken: default);

    private static ProviderExecutionContext MissingSecretContext() =>
        BuildContext(
            accountId: AccountA,
            revision: 1,
            secretReferenceId: null,
            scope: ProviderAccountScope.Personal,
            deadline: null,
            cancellationToken: default);

    private static ProviderExecutionContext BuildContext(
        Guid? accountId,
        long revision,
        Guid? secretReferenceId,
        ProviderAccountScope scope,
        DateTimeOffset? deadline,
        CancellationToken cancellationToken)
    {
        var account = accountId.HasValue
            ? new ProviderAccountContext(
                accountId.Value,
                MusixmatchLyricsCapabilityAdapter.StableProviderId,
                scope,
                revision,
                ownerUserId: scope == ProviderAccountScope.Personal ? UserId : null,
                secretReferenceId: secretReferenceId)
            : null;
        return new(
            new ProviderActorContext(
                ProviderActorKind.User,
                UserId,
                new ProviderBackendPrincipal("jellyfin", "fixture", "fixture-user")),
            MusixmatchLyricsCapabilityAdapter.StableProviderId,
            account,
            new ProviderExecutionPolicy(
                new ProviderQualityPolicy(
                    ProviderAudioQuality.Any,
                    ProviderAudioQuality.HighResolution,
                    allowTranscode: true),
                ProviderExplicitContentPolicy.Allow,
                allowFallback: true,
                allowSharedAccount: true,
                allowManagedDownloads: false,
                allowedProviderIds: [MusixmatchLyricsCapabilityAdapter.StableProviderId]),
            "lyrics-read",
            "lyrics-correlation",
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(1),
            cancellationToken);
    }

    private static ProviderExecutionContext PublicContext() => new(
        new ProviderActorContext(ProviderActorKind.PublicRead, null),
        MusixmatchLyricsCapabilityAdapter.StableProviderId,
        account: null,
        new ProviderExecutionPolicy(
            new ProviderQualityPolicy(
                ProviderAudioQuality.Any,
                ProviderAudioQuality.HighResolution,
                allowTranscode: true),
            ProviderExplicitContentPolicy.Allow,
            allowFallback: true,
            allowSharedAccount: true,
            allowManagedDownloads: false,
            allowedProviderIds: [MusixmatchLyricsCapabilityAdapter.StableProviderId]),
        "lyrics-public",
        "lyrics-public-correlation",
        DateTimeOffset.UtcNow.AddMinutes(1),
        CancellationToken.None);

    private static IReadOnlyDictionary<string, string> ParseQuery(Uri uri) =>
        uri.Query
            .TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                parts => Uri.UnescapeDataString(parts[0]),
                parts => Uri.UnescapeDataString(parts.Length == 1 ? string.Empty : parts[1].Replace('+', ' ')),
                StringComparer.Ordinal);

    private static void EnsureCacheSettings() =>
        CacheExtensions.InitializeCacheSettings(new ServiceCollection().BuildServiceProvider());

    private sealed class FixtureHttpHandler : HttpMessageHandler
    {
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.OK;
        public string Payload { get; set; } = TimedMacroPayload;
        public TimeSpan? RetryAfter { get; set; }
        public int Requests { get; private set; }
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? Cookie { get; private set; }
        public string? AcceptLanguage { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Method = request.Method;
            RequestUri = request.RequestUri;
            Cookie = request.Headers.TryGetValues("Cookie", out var values)
                ? values.SingleOrDefault()
                : null;
            AcceptLanguage = request.Headers.TryGetValues("Accept-Language", out var languages)
                ? languages.SingleOrDefault()
                : null;
            var response = new HttpResponseMessage(StatusCode)
            {
                Content = new StringContent(Payload, Encoding.UTF8, "application/json")
            };
            if (RetryAfter is { } retry)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(retry);
            return Task.FromResult(response);
        }
    }

    private sealed class FixtureSecretAccessor : IProviderAccountSecretAccessor
    {
        private readonly string _bundle;

        private FixtureSecretAccessor(string bundle, bool rawBundle)
        {
            _ = rawBundle;
            _bundle = bundle;
        }

        public FixtureSecretAccessor(string token = "desktop-user-token")
            : this(JsonSerializer.Serialize(new { userToken = token }), rawBundle: false)
        {
        }

        public static FixtureSecretAccessor FromBundle(string bundle) => new(bundle, rawBundle: true);

        public List<ProviderAccountContext> Accounts { get; } = [];

        public async Task<T> UseAsync<T>(
            ProviderAccountContext account,
            Func<ReadOnlyMemory<byte>, Task<T>> operation,
            CancellationToken cancellationToken)
        {
            Accounts.Add(account);
            cancellationToken.ThrowIfCancellationRequested();
            var bytes = Encoding.UTF8.GetBytes(_bundle);
            try
            {
                return await operation(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private sealed class BlockingApplicationCache : IApplicationCache
    {
        public bool IsEnabled => true;

        public Task<string?> GetStringAsync(string key) => Never<string>();

        public Task<T?> GetAsync<T>(string key) where T : class => Never<T>();

        public Task<bool> SetStringAsync(string key, string value, TimeSpan? expiry = null) =>
            Task.FromResult(false);

        public Task<bool> SetAsync<T>(string key, T value, TimeSpan? expiry = null) where T : class =>
            Task.FromResult(false);

        public Task<bool> DeleteAsync(string key) => Task.FromResult(false);

        public Task<bool> ExistsAsync(string key) => Task.FromResult(false);

        public Task<int> DeleteByPatternAsync(string pattern) => Task.FromResult(0);

        public Task<int> PurgeAllAsync() => Task.FromResult(0);

        private static Task<T?> Never<T>() where T : class =>
            new TaskCompletionSource<T?>(
                TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
}
