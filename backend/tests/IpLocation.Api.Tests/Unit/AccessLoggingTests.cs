using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Middleware;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class AccessLoggingTests
{
    [Fact]
    public void FullQueueRejectsWithoutPretendingTheEventWasAccepted()
    {
        var options = CreateOptions(new AccessLogOptions { QueueCapacity = 1 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        queue.RecordReceived();
        queue.RecordReceived();

        Assert.True(queue.TryEnqueue(Event()));
        Assert.False(queue.TryEnqueue(Event()));

        var metrics = queue.GetMetrics();
        Assert.Equal(2, metrics.Received);
        Assert.Equal(1, metrics.Accepted);
        Assert.Equal(1, metrics.Dropped);
        Assert.Equal(1, metrics.Queued);
        Assert.True(queue.TryRead(out _));
        queue.RecordWritten(1);
        Assert.Equal(0, queue.GetMetrics().InFlight);
        queue.Complete();
        Assert.False(queue.TryEnqueue(Event()));
        Assert.False(queue.GetMetrics().IsAccepting);
    }

    [Theory]
    [InlineData("203.0.113.42", "203.0.113.0/24")]
    [InlineData("::ffff:203.0.113.42", "203.0.113.0/24")]
    [InlineData("2001:db8:abcd:1234::99", "2001:db8:abcd::/48")]
    public async Task PublicMetadataMasksIpAndKeepsOnlySanitizedHeaderSnapshot(string ip, string expected)
    {
        var options = CreateOptions();
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var context = Context("/search/1.2.3.4", ip);
        context.Request.QueryString = new QueryString("?db=dbip&token=never-retain-this");
        context.Request.Headers.UserAgent = "curl/8.0 controlled-agent";
        context.Request.Headers.Referer = "https://example.com/from?token=never-retain-this#fragment";
        context.Request.Headers.Authorization = "Bearer never-retain-this";
        var middleware = new AccessLogMiddleware(http =>
        {
            http.Response.ContentType = "application/json";
            return Task.CompletedTask;
        }, options, TimeProvider.System);

        await middleware.InvokeAsync(context, queue);

        Assert.True(queue.TryRead(out var recorded));
        Assert.Equal(expected, recorded.SourceAddress);
        Assert.Equal("dbip", recorded.Database);
        Assert.Equal("curl", recorded.ClientCategory);
        Assert.Equal("query", recorded.RouteCategory);
        var headers = AccessRequestHeaders.Read(recorded.RequestHeadersJson)!;
        Assert.Equal("curl/8.0 controlled-agent", headers.Headers["user-agent"].Single());
        Assert.Equal("https://example.com/from", headers.Headers["referer"].Single());
        Assert.Equal("/search/1.2.3.4?db=dbip&token=[REDACTED]", headers.RequestPath);
        Assert.False(headers.RequestPathTruncated);
        Assert.DoesNotContain("never-retain-this", recorded.ToString());
    }

    [Fact]
    public async Task ExplicitFullSourceIpPolicyRetainsNormalizedAddress()
    {
        var options = CreateOptions(new AccessLogOptions { RetainFullSourceIp = true });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var middleware = new AccessLogMiddleware(_ => Task.CompletedTask, options, TimeProvider.System);

        await middleware.InvokeAsync(Context("/", "::ffff:203.0.113.42"), queue);

        Assert.True(queue.TryRead(out var recorded));
        Assert.Equal("203.0.113.42", recorded.SourceAddress);
    }

    [Theory]
    [InlineData("chunzhen")]
    [InlineData("CZ88")]
    [InlineData("cz88")]
    public async Task PureSourceAliasesWriteCanonicalLogCode(string code)
    {
        var options = CreateOptions();
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var context = Context("/search/8.8.8.8", "203.0.113.42");
        context.Request.QueryString = new QueryString("?db=" + code);
        var middleware = new AccessLogMiddleware(_ => Task.CompletedTask, options, TimeProvider.System);
        await middleware.InvokeAsync(context, queue);
        Assert.True(queue.TryRead(out var recorded));
        Assert.Equal("CZ88", recorded.Database);
    }

    [Fact]
    public async Task FullIpRetentionExpiresWithoutNewTrafficAndHandlesOutOfOrderEvents()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var options = CreateOptions(new AccessLogOptions { RetainFullSourceIp = true, RecentEventRetentionDays = 1 });
        var queue = new AccessEventQueue(options, clock);
        var store = new InMemoryAccessLogStore(options, queue, clock);
        await store.AppendBatchAsync([
            Event() with { OccurredAtUtc = now, SourceAddress = "203.0.113.42" },
            Event() with { OccurredAtUtc = now.AddHours(-23), SourceAddress = "203.0.113.43" },
            Event() with { OccurredAtUtc = now.AddDays(-2), SourceAddress = "203.0.113.44" }
        ], CancellationToken.None);
        Assert.Equal(2, store.GetSnapshot().RecentEvents.Count);

        clock.Advance(TimeSpan.FromHours(2));
        var retained = store.GetSnapshot();
        Assert.Single(retained.RecentEvents);
        Assert.Equal("203.0.113.42", retained.RecentEvents[0].SourceAddress);

        // 达到恰好一天的边界即过期，且读接口自身执行清理，无需等待下一次访问。
        clock.Advance(TimeSpan.FromHours(22));
        var expired = store.GetSnapshot();
        Assert.Empty(expired.RecentEvents);
        Assert.Empty(expired.Minutes);
        Assert.Equal(1, expired.RecentEventRetentionDays);
    }

    [Fact]
    public async Task StaticHealthAndConsolePollingAreExcludedFromPublicCounts()
    {
        var options = CreateOptions();
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var middleware = new AccessLogMiddleware(_ => Task.CompletedTask, options, TimeProvider.System);
        foreach (var path in new[] { "/assets/app.js", "/health", "/api/console/overview", "/console" })
            await middleware.InvokeAsync(Context(path, "127.0.0.1"), queue);

        Assert.Equal(0, queue.GetMetrics().Received);
        Assert.Equal(0, queue.GetMetrics().Queued);
    }

    [Fact]
    public async Task SlowStoreDoesNotBlockPlainIpRequestsAndOverflowIsVisible()
    {
        var options = CreateOptions(new AccessLogOptions { QueueCapacity = 1, BatchSize = 1 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var store = new BlockingStore();
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance);
        var middleware = new AccessLogMiddleware(http =>
        {
            http.Response.ContentType = "text/plain";
            return Task.CompletedTask;
        }, options, TimeProvider.System);
        await worker.StartAsync(CancellationToken.None);
        HttpContext HeaderContext(string ip)
        {
            var context = Context("/", ip);
            context.Request.Headers.UserAgent = "curl/8.0 " + new string('x', 3000);
            context.Request.Headers.Referer = "https://example.com/origin?token=excluded";
            return context;
        }
        try
        {
            // 存储始终被阻塞；公共响应仍应独立完成，而非等待后台批次。
            await middleware.InvokeAsync(HeaderContext("203.0.113.1"), queue).WaitAsync(TimeSpan.FromSeconds(3));
            await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await middleware.InvokeAsync(HeaderContext("203.0.113.2"), queue).WaitAsync(TimeSpan.FromSeconds(3));
            await middleware.InvokeAsync(HeaderContext("203.0.113.3"), queue).WaitAsync(TimeSpan.FromSeconds(3));

            Assert.Equal(3, queue.GetMetrics().Received);
            Assert.Equal(1, queue.GetMetrics().Dropped);
            Assert.Equal(0, queue.GetMetrics().Written);
        }
        finally
        {
            store.Release.TrySetResult();
            await worker.StopAsync(CancellationToken.None);
        }
        Assert.Equal(2, queue.GetMetrics().Written);
    }

    [Fact]
    public async Task FailingStoreIsCountedAsLostAndDoesNotRetryUnknownBatch()
    {
        var options = CreateOptions(new AccessLogOptions { BatchSize = 1 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var store = new FailingStore();
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        queue.TryEnqueue(Event());
        await WaitUntilAsync(() => queue.GetMetrics().WriteFailures == 1);
        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(1, store.Attempts);
        Assert.Equal(0, queue.GetMetrics().Written);
        Assert.Equal(1, queue.GetMetrics().Dropped);
        Assert.NotNull(queue.GetMetrics().LastFailureUtc);
    }

    [Fact]
    public async Task StoreIgnoringCancellationCannotAccumulateUnboundedWrites()
    {
        var options = CreateOptions(new AccessLogOptions { BatchSize = 1, WriteTimeoutMilliseconds = 25 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var store = new IgnoringCancellationStore();
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            queue.TryEnqueue(Event());
            await WaitUntilAsync(() => queue.GetMetrics().WriteFailures == 1);
            for (var index = 0; index < 5; index++) queue.TryEnqueue(Event());
            await WaitUntilAsync(() => queue.GetMetrics().Dropped == 6);

            Assert.Equal(1, store.Attempts);
            Assert.Equal(0, queue.GetMetrics().Written);

            store.Release.TrySetResult(1);
            queue.TryEnqueue(Event());
            await WaitUntilAsync(() => queue.GetMetrics().Written == 1);
            Assert.Equal(2, store.Attempts);
        }
        finally
        {
            store.Release.TrySetResult(1);
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task StopHasDrainDeadlineAndMarksUnwrittenEvents()
    {
        var options = CreateOptions(new AccessLogOptions { BatchSize = 1, ShutdownDrainMilliseconds = 25 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var store = new BlockingStore();
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance);
        await worker.StartAsync(CancellationToken.None);
        queue.TryEnqueue(Event());
        queue.TryEnqueue(Event());
        await store.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));

        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await WaitUntilAsync(() => queue.GetMetrics().Dropped == 2);

        Assert.Equal(0, queue.GetMetrics().Written);
        Assert.Equal(0, queue.GetMetrics().Queued);
        Assert.Equal(0, queue.GetMetrics().InFlight);
    }

    [Fact]
    public async Task MemoryWindowsAreBoundedAndMinuteHistogramsRemainMergeable()
    {
        var options = CreateOptions(new AccessLogOptions { RecentEventCapacity = 2, MinuteBucketCapacity = 2 });
        var minute = new DateTimeOffset(2026, 11, 2, 1, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(minute.AddMinutes(2));
        var queue = new AccessEventQueue(options, clock);
        var store = new InMemoryAccessLogStore(options, queue, clock);
        await store.AppendBatchAsync([
            Event() with { OccurredAtUtc = minute, RouteCategory = "plainIp" },
            Event() with { OccurredAtUtc = minute.AddMinutes(1), RouteCategory = "query", ElapsedMilliseconds = 8 },
            Event() with { OccurredAtUtc = minute.AddMinutes(2), RouteCategory = "homeHtml", ElapsedMilliseconds = 6 }
        ], CancellationToken.None);

        var snapshot = store.GetSnapshot(100, 100);
        Assert.False(snapshot.IsDurable);
        Assert.Equal("inMemory", snapshot.StorageKind);
        Assert.Equal(2, snapshot.RecentEvents.Count);
        Assert.Equal("homeHtml", snapshot.RecentEvents[0].RouteCategory);
        Assert.Equal(2, snapshot.Minutes.Count);
        Assert.Equal(minute.AddMinutes(1), snapshot.Minutes[0].MinuteUtc);
        Assert.Equal(1, snapshot.Minutes[0].QueryRequests);
        Assert.Equal(1, snapshot.Minutes[1].HomeHtmlRequests);
        Assert.Equal(snapshot.LatencyUpperBoundsMilliseconds.Count + 1, snapshot.Minutes[0].LatencyHistogramCounts.Count);
        Assert.Equal(1, snapshot.Minutes[0].LatencyHistogramCounts.Sum());
    }

    [Fact]
    public async Task MinuteWindowExpiresByTimeEvenWhenCapacityWasNotFilled()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);
        var clock = new MutableTimeProvider(now);
        var options = CreateOptions(new AccessLogOptions { MinuteBucketCapacity = 2 });
        var queue = new AccessEventQueue(options, clock);
        var store = new InMemoryAccessLogStore(options, queue, clock);
        await store.AppendBatchAsync([Event() with { OccurredAtUtc = now }], CancellationToken.None);
        Assert.Single(store.GetSnapshot().Minutes);

        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Empty(store.GetSnapshot().Minutes);
        Assert.Single(store.GetSnapshot().RecentEvents);
    }

    private static IOptions<AccessLogOptions> CreateOptions(AccessLogOptions? value = null) =>
        Microsoft.Extensions.Options.Options.Create(value ?? new AccessLogOptions());

    private static DefaultHttpContext Context(string path, string ip)
    {
        var context = new DefaultHttpContext();
        context.Request.Path = path;
        context.Request.Method = "GET";
        context.Request.Protocol = "HTTP/1.1";
        context.Connection.RemoteIpAddress = IPAddress.Parse(ip);
        return context;
    }

    private static AccessEvent Event() => new(DateTimeOffset.UtcNow, "test-trace", "plainIp", "GET",
        "HTTP/1.1", "txt", null, null, 200, 1.5, false, "203.0.113.0/24", "curl");

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private sealed class BlockingStore : IAccessLogStore
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return events.Count;
        }

        public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60) =>
            throw new NotSupportedException();
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private long _utcTicks = initialUtc.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed class FailingStore : IAccessLogStore
    {
        public int Attempts { get; private set; }
        public ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        {
            Attempts++;
            return ValueTask.FromException<int>(new InvalidOperationException("controlled failure"));
        }

        public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60) =>
            throw new NotSupportedException();
    }

    private sealed class IgnoringCancellationStore : IAccessLogStore
    {
        private int _attempts;
        public int Attempts => Volatile.Read(ref _attempts);
        public TaskCompletionSource<int> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _attempts);
            return new ValueTask<int>(Release.Task);
        }

        public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60) =>
            throw new NotSupportedException();
    }
}
