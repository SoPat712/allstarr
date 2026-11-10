using System.Collections.Concurrent;
using System.Diagnostics;
using allstarr.Middleware;
using allstarr.Core.Operations;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace allstarr.Tests;

[Collection("Request logging")]
public sealed class RequestLoggingMiddlewareTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TimingBreakdownIsStructuredAtDebugAndFailureWarningRemains(bool verbose)
    {
        var logger = new StructuredLogger();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Debug:LogAllRequests"] = verbose.ToString() }).Build();
        var context = new DefaultHttpContext();
        context.Request.Path = "/allstarr/v1/me/stats";
        context.Request.QueryString = new("?api_key=private-secret");
        await new RequestLoggingMiddleware(async http =>
        {
            using (RequestTiming.Current?.Stage("build")) await Task.Yield();
            RequestTiming.Current!.Add(RequestTimingStage.Db, TimeSpan.FromMilliseconds(1.2));
            http.Response.StatusCode = 503;
        }, logger, configuration).InvokeAsync(context);
        var entry = Assert.Single(logger.Entries, item => item.Level == LogLevel.Debug);
        Assert.Equal("stats", entry.Values["Route"]);
        Assert.Equal(1.2, entry.Values["DbMs"]);
        Assert.Equal(0, entry.Values["JellyfinCalls"]);
        Assert.Equal("none", entry.Values["Cache"]);
        Assert.True(Assert.IsType<double>(entry.Values["PreMs"]) >= 0);
        Assert.Equal(1, entry.Values["Inflight"]);
        Assert.Contains("build ", Assert.IsType<string>(entry.Values["Stages"]));
        Assert.Contains("stages build ", entry.Message);
        Assert.Contains(logger.Entries, item => item.Level == LogLevel.Warning);
        Assert.DoesNotContain(logger.Entries.SelectMany(item => item.Values.Values), value => value?.ToString()?.Contains("private-secret") == true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HostingOffsetAndPressureAreCapturedOnceBeforeWork(bool enabled)
    {
        using var hosting = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn")
            .SetStartTime(DateTime.UtcNow.AddMilliseconds(-400)).Start();
        hosting.SetTag("private-token", "private-activity-value");
        var logger = new StructuredLogger();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Diagnostics:ServerTimingEnabled"] = enabled.ToString() }).Build();
        var context = new DefaultHttpContext();
        context.Request.Path = "/allstarr/v1/me/stats";
        context.Request.QueryString = new("?token=private-query-value");
        RequestTiming.Result? measured = null;
        await new RequestLoggingMiddleware(async _ =>
        {
            await Task.Delay(10);
            measured = RequestTiming.Current?.Snapshot("stats");
        }, logger, configuration).InvokeAsync(context);

        var pressure = Assert.Single(logger.Entries, item => item.Values.ContainsKey("PendingWorkItemCount"));
        var pre = Assert.IsType<double>(pressure.Values["PreMs"]);
        Assert.True(pre >= 350);
        Assert.IsType<long>(pressure.Values["PendingWorkItemCount"]);
        Assert.IsType<int>(pressure.Values["ThreadCount"]);
        Assert.Equal(1, pressure.Values["Inflight"]);
        Assert.Equal("stats", pressure.Values["Route"]);
        Assert.Equal(enabled, measured != null);
        if (enabled)
        {
            Assert.Equal(pre, measured!.PreMs);
            var completed = Assert.Single(logger.Entries, item => item.Values.ContainsKey("TotalMs"));
            Assert.Equal(pre, completed.Values["PreMs"]);
            Assert.Contains(FormattableString.Invariant($", pre;dur={pre:0.0}, inflight;desc=\"1\""), measured.Header);
        }
        Assert.DoesNotContain(logger.Entries, item => item.Message.Contains("private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HostingParentIsUsedInsteadOfCurrentChild()
    {
        using var source = new ActivitySource("Microsoft.AspNetCore");
        using var listener = Listen(source);
        using var hosting = source.StartActivity("Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server,
            default(ActivityContext), startTime: DateTimeOffset.UtcNow.AddMilliseconds(-500));
        Assert.NotNull(hosting);
        using var child = new Activity("private-child-work").Start();
        var logger = new StructuredLogger();
        await new RequestLoggingMiddleware(_ => Task.CompletedTask, logger, new ConfigurationBuilder().Build())
            .InvokeAsync(new DefaultHttpContext());

        var entry = Assert.Single(logger.Entries, item => item.Values.ContainsKey("TotalMs"));
        Assert.True(Assert.IsType<double>(entry.Values["PreMs"]) >= 450);
        Assert.DoesNotContain(logger.Entries, item => item.Message.Contains("private", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Other", "Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Server)]
    [InlineData("Microsoft.AspNetCore", "private-work", ActivityKind.Server)]
    [InlineData("Microsoft.AspNetCore", "Microsoft.AspNetCore.Hosting.HttpRequestIn", ActivityKind.Internal)]
    public async Task NonHostingActivitiesDoNotProducePreDelay(string sourceName, string operation, ActivityKind kind)
    {
        using var source = new ActivitySource(sourceName);
        using var listener = Listen(source);
        using var activity = source.StartActivity(operation, kind, default(ActivityContext),
            startTime: DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.NotNull(activity);
        var logger = new StructuredLogger();
        await new RequestLoggingMiddleware(_ => Task.CompletedTask, logger, new ConfigurationBuilder().Build())
            .InvokeAsync(new DefaultHttpContext());

        var entry = Assert.Single(logger.Entries, item => item.Level == LogLevel.Debug);
        Assert.Equal(0d, entry.Values["PreMs"]);
    }

    [Fact]
    public async Task FutureHostingStartProducesZeroPreDelay()
    {
        using var hosting = new Activity("Microsoft.AspNetCore.Hosting.HttpRequestIn")
            .SetStartTime(DateTime.UtcNow.AddMinutes(1)).Start();
        var logger = new StructuredLogger();
        await new RequestLoggingMiddleware(_ => Task.CompletedTask, logger, new ConfigurationBuilder().Build())
            .InvokeAsync(new DefaultHttpContext());

        var entry = Assert.Single(logger.Entries, item => item.Level == LogLevel.Debug);
        Assert.Equal(0d, entry.Values["PreMs"]);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ConcurrentRequestsCountAtEntryAndLeaveAfterFailure(bool enabled, bool cancelled)
    {
        var logger = new StructuredLogger();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Diagnostics:ServerTimingEnabled"] = enabled.ToString() }).Build();
        var blocked = new RequestLoggingMiddleware(async _ =>
        {
            entered.SetResult();
            await release.Task;
            if (cancelled) throw new OperationCanceledException(new CancellationToken(true));
            throw new InvalidOperationException("Unavailable");
        }, logger, configuration);
        var request = blocked.InvokeAsync(new DefaultHttpContext());
        await entered.Task;
        var other = new RequestLoggingMiddleware(_ => Task.CompletedTask, logger, new ConfigurationBuilder().Build());
        try
        {
            var context = new DefaultHttpContext();
            context.Request.Path = "/allstarr/v1/me/sound";
            await other.InvokeAsync(context);
        }
        finally { release.SetResult(); }
        if (cancelled) await Assert.ThrowsAsync<OperationCanceledException>(() => request);
        else await Assert.ThrowsAsync<InvalidOperationException>(() => request);
        var next = new DefaultHttpContext();
        next.Request.Path = "/allstarr/v1/me/sound";
        await other.InvokeAsync(next);

        var entries = logger.Entries.Where(item => item.Values.TryGetValue("Route", out var route) &&
            Equals(route, "sound") && item.Values.ContainsKey("TotalMs")).ToArray();
        Assert.Equal(2, entries.Length);
        Assert.Equal(2, entries[0].Values["Inflight"]);
        Assert.Equal(1, entries[1].Values["Inflight"]);
    }

    [Fact]
    public async Task FailedAndAbandonedRequestsAreNotedWithoutTheirQueryWhileOthersStayQuiet()
    {
        var messages = new List<string>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        async Task Run(int status, bool abandoned = false)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = "GET";
            context.Request.Path = "/System/Info/Public";
            context.Request.QueryString = new QueryString("?api_key=fixture-private");
            if (abandoned) context.RequestAborted = new CancellationToken(true);
            await new RequestLoggingMiddleware(http =>
            {
                http.Response.StatusCode = status;
                return Task.CompletedTask;
            }, new CollectingLogger<RequestLoggingMiddleware>(messages), configuration).InvokeAsync(context);
        }

        await Run(200);
        Assert.DoesNotContain(messages, message => message.Contains("/System/Info/Public", StringComparison.Ordinal));
        await Run(503);
        await Run(200, abandoned: true);
        Assert.Contains(messages, message => message.Contains("Request GET /System/Info/Public answered 503 after", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("Client gave up on GET /System/Info/Public after", StringComparison.Ordinal));
        Assert.DoesNotContain(messages, message => message.Contains("fixture-private", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NotificationDestinationInSettingsBodyIsNeverLogged()
    {
        var messages = new List<string>();
        var exceptions = new List<Exception>();
        var context = new DefaultHttpContext();
        context.Request.Method = "POST";
        context.Request.Path = "/api/admin/alerts/channels";
        context.Request.ContentType = "application/json";
        context.Request.Body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"destination\":\"https://notification.test/private-topic?key=fixture-private\"}"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Debug:LogAllRequests"] = "true" }).Build();
        var middleware = new RequestLoggingMiddleware(_ => Task.CompletedTask,
            new CollectingLogger<RequestLoggingMiddleware>(messages, exceptions), configuration);
        await middleware.InvokeAsync(context);
        var log = string.Join('\n', messages);
        Assert.DoesNotContain("private-topic", log);
        Assert.DoesNotContain("fixture-private", log);
        Assert.Empty(exceptions);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MusixmatchQueryTokenIsNeverWrittenByRequestLogging(bool fail)
    {
        const string token = "fixture-musixmatch-token+&=private";
        var messages = new List<string>();
        var exceptions = new List<Exception>();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Debug:LogAllRequests"] = "true" }).Build();
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("allstarr.test");
        context.Request.Path = "/ws/1.1/macro.subtitles.get";
        context.Request.QueryString = new QueryString("?usertoken=" + Uri.EscapeDataString(token) + "&q_track=Fixture");
        var middleware = new RequestLoggingMiddleware(_ => fail
                ? throw new HttpRequestException("Failed upstream request " + context.Request.QueryString)
                : Task.CompletedTask,
            new CollectingLogger<RequestLoggingMiddleware>(messages, exceptions), configuration);
        if (fail) await Assert.ThrowsAsync<HttpRequestException>(() => middleware.InvokeAsync(context));
        else await middleware.InvokeAsync(context);
        var text = string.Join('\n', messages);
        Assert.Contains("usertoken=<redacted>", text, StringComparison.Ordinal);
        Assert.DoesNotContain(token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(Uri.EscapeDataString(token), text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(exceptions);
    }

    [Fact]
    public async Task InvokeAsync_AlwaysRedactsCredentialsAndPreservesRepeatedQueryKeys()
    {
        var messages = new List<string>();
        var logger = new CollectingLogger<RequestLoggingMiddleware>(messages);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Debug:LogAllRequests"] = "true",
                ["Debug:RedactSensitiveRequestValues"] = "false"
            })
            .Build();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = StatusCodes.Status204NoContent;
                return Task.CompletedTask;
            },
            logger,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("allstarr.test");
        context.Request.Path = "/rest/search3";
        context.Request.QueryString = new QueryString("?token=query-secret&id=one&id=two");
        context.Request.Headers.Authorization = "Bearer bearer-secret";
        context.Request.Headers["X-Emby-Authorization"] =
            "MediaBrowser Client=\"fixture\", Token=\"jellyfin-secret\"";
        context.Request.Headers["X-Emby-Token"] = "header-secret";

        await middleware.InvokeAsync(context);

        var log = string.Join('\n', messages);
        Assert.DoesNotContain("query-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("bearer-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("jellyfin-secret", log, StringComparison.Ordinal);
        Assert.DoesNotContain("header-secret", log, StringComparison.Ordinal);
        Assert.Contains("?token=<redacted>&id=<redacted>&id=<redacted>", log, StringComparison.Ordinal);
        Assert.Contains("Authorization: Bearer ***", log, StringComparison.Ordinal);
        Assert.Contains("X-Emby-Token: ***", log, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_DoesNotAttachExceptionThatMayContainSensitiveUrl()
    {
        var messages = new List<string>();
        var exceptions = new List<Exception>();
        var logger = new CollectingLogger<RequestLoggingMiddleware>(messages, exceptions);
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Debug:LogAllRequests"] = "true"
            })
            .Build();
        var middleware = new RequestLoggingMiddleware(
            _ => throw new InvalidOperationException(
                "request https://provider.invalid/media?token=private-token failed"),
            logger,
            configuration);
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("allstarr.test");
        context.Request.Path = "/rest/stream";
        context.Request.QueryString = new QueryString("?token=query-secret");

        await Assert.ThrowsAsync<InvalidOperationException>(() => middleware.InvokeAsync(context));

        var log = string.Join('\n', messages);
        Assert.Empty(exceptions);
        Assert.DoesNotContain("private-token", log, StringComparison.Ordinal);
        Assert.DoesNotContain("provider.invalid", log, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), log, StringComparison.Ordinal);
    }

    private sealed class CollectingLogger<T>(
        List<string> messages,
        List<Exception>? exceptions = null) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            messages.Add(formatter(state, exception));
            if (exception != null)
            {
                exceptions?.Add(exception);
            }
        }
    }

    private sealed class StructuredLogger : ILogger<RequestLoggingMiddleware>
    {
        public ConcurrentQueue<(LogLevel Level, Dictionary<string, object?> Values, string Message)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue((logLevel, ((IEnumerable<KeyValuePair<string, object?>>)(object)state!).ToDictionary(item => item.Key, item => item.Value), formatter(state, exception)));
    }

    private static ActivityListener Listen(ActivitySource source)
    {
        var listener = new ActivityListener
        {
            ShouldListenTo = candidate => ReferenceEquals(candidate, source),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }
}

[CollectionDefinition("Request logging", DisableParallelization = true)]
public sealed class RequestLoggingCollection { }
