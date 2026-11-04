using System.Diagnostics;
using System.Net;
using IpLocation.Api.Common;
using IpLocation.Api.Middleware;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Providers;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Service.impl.Providers.Chunzhen;
using IpLocation.Api.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

public sealed class AccessSourceLocationTests
{
    [Fact]
    public void ExplicitThreeLevelRegionMapsWithoutGuessingUnstructuredOrCountyText()
    {
        var value = AccessSourceLocations.FromResult("2026-10-28", "中国–安徽–蚌埠", "电信");
        Assert.Equal("中国", value.Country); Assert.Equal("安徽", value.Province); Assert.Equal("蚌埠", value.City);
        Assert.Equal("电信", value.Isp); Assert.Equal("中国–安徽–蚌埠", value.RawLocation);
        var four = AccessSourceLocations.FromResult("2026-10-28", "美国–加利福尼亚州–圣克拉拉–山景城", "DNS网络描述");
        Assert.Equal("美国", four.Country); Assert.Equal("加利福尼亚州", four.Province); Assert.Null(four.City);
        Assert.Equal("美国–加利福尼亚州–圣克拉拉–山景城", four.RawLocation);
        var unstructured = AccessSourceLocations.FromResult(null, "中国安徽蚌埠", "网络描述");
        Assert.Null(unstructured.Country); Assert.Null(unstructured.Province); Assert.Null(unstructured.City);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-json")]
    [InlineData("[]")]
    [InlineData("{\"provider\":\"CZ88\",\"status\":\"invented\"}")]
    [InlineData("{\"provider\":\"other\",\"status\":\"success\"}")]
    [InlineData("{\"provider\":\"CZ88\",\"status\":\"success\",\"city\":{}}")]
    public void LegacyNullAndInvalidJsonDoNotBreakLogReading(string? json) => Assert.Null(AccessSourceLocations.Read(json));

    [Fact]
    public void BoundedJsonRoundTripsFieldsAndDropsUnknownProperties()
    {
        var value = AccessSourceLocations.FromResult("2026-10-28", "中国–安徽–蚌埠", "电信");
        Assert.Equal(value, AccessSourceLocations.Read(AccessSourceLocations.Write(value)));
        var longValue = value with { RawLocation = new string('中', 1024), Isp = new string('网', 512), Country = new string('国', 256), Province = new string('省', 256), City = new string('市', 256) };
        var json = AccessSourceLocations.Write(longValue);
        Assert.NotNull(json); Assert.InRange(json!.Length, 1, AccessSourceLocations.MaxJsonCharacters);
        Assert.Equal("success", AccessSourceLocations.Read(json)!.Status);
        Assert.Null(AccessSourceLocations.Read(new string('x', AccessSourceLocations.MaxJsonCharacters + 1)));
    }

    [Fact]
    public async Task BatchEnrichmentCachesSameSourceAndKeepsEventIdentityAndHeadersUnchanged()
    {
        var provider = new SourceProvider((_, _) => Task.FromResult(Success()));
        var enricher = Enricher(provider);
        var original = Event("1.1.1.1") with { RequestHeadersJson = "{\"headers\":{}}" };
        var records = await enricher.EnrichAsync([original, original with { TraceId = "second" }], default);
        Assert.Equal(1, provider.Calls);
        Assert.Equal(2, records.Count);
        Assert.Equal(AccessLogPersistencePlan.GetEventKey(original), AccessLogPersistencePlan.GetEventKey(records[0]));
        Assert.Equal(original.RequestHeadersJson, records[0].RequestHeadersJson);
        Assert.Equal(original.SourceAddress, records[0].SourceAddress);
        var location = AccessSourceLocations.Read(records[0].SourceLocationJson)!;
        Assert.Equal("CZ88", location.Provider); Assert.Equal("2026-10-28", location.Version);
        Assert.Equal("蚌埠", location.City);
        var repeated = await enricher.EnrichAsync(records, default);
        Assert.Equal(1, provider.Calls); Assert.Same(records[0], repeated[0]);
    }

    [Fact]
    public async Task SpecialInvalidMaskedAndIpv6SourcesNeverCallThePublicDatabase()
    {
        var provider = new SourceProvider((_, _) => throw new InvalidOperationException("must not query"));
        var events = new[] { Event("127.0.0.1"), Event("10.0.0.1"), Event("bad-ip"), Event("203.0.113.0/24"), Event("2001:4860:4860::8888") };
        var result = await Enricher(provider).EnrichAsync(events, default);
        Assert.Equal(new[] { "special", "special", "invalid", "unsupported", "unsupported" }, result.Select(item => AccessSourceLocations.Read(item.SourceLocationJson)!.Status));
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task FaultedSourcePreservesLogAndNeverFallsBackToAnotherProvider()
    {
        var provider = new SourceProvider((_, _) => throw new InvalidDataException("private error"));
        var original = Event("8.8.8.8");
        var result = await Enricher(provider).EnrichAsync([original], default);
        Assert.Single(result); Assert.Equal("unavailable", AccessSourceLocations.Read(result[0].SourceLocationJson)!.Status);
        Assert.Equal(original.TraceId, result[0].TraceId); Assert.DoesNotContain("private error", result[0].SourceLocationJson!);
    }

    [Fact]
    public async Task IgnoredLookupCancellationKeepsAtMostOnePendingOperationAcrossBatches()
    {
        var release = new TaskCompletionSource<ProviderQueryResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new SourceProvider((_, _) => release.Task);
        var enricher = Enricher(provider, lookupMilliseconds: 15);
        try
        {
            var first = await enricher.EnrichAsync([Event("8.8.8.8"), Event("1.1.1.1")], default);
            var second = await enricher.EnrichAsync([Event("9.9.9.9")], default);
            Assert.Equal(1, provider.Calls);
            Assert.All(first.Concat(second), item => Assert.Equal("timeout", AccessSourceLocations.Read(item.SourceLocationJson)!.Status));
        }
        finally { release.TrySetResult(Success()); }
    }

    [Fact]
    public async Task StopCancellationPropagatesInsteadOfInventingSuccessfulMetadata()
    {
        var provider = new SourceProvider(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Success(); });
        var enricher = Enricher(provider, lookupMilliseconds: 500);
        using var stop = new CancellationTokenSource();
        var operation = enricher.EnrichAsync([Event("8.8.8.8")], stop.Token).AsTask();
        await provider.Entered.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task RootResponsesNeverWaitForBlockedEnrichmentOrSlowStorageAndOverflowIsVisible()
    {
        var options = OptionsFactory.Create(new AccessLogOptions { QueueCapacity = 2, BatchSize = 1, FlushIntervalMilliseconds = 1, WriteTimeoutMilliseconds = 1000, ShutdownDrainMilliseconds = 1000 });
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var enricher = new BlockedEnricher();
        var store = new SlowStore();
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance, enricher,
            OptionsFactory.Create(new AccessLocationOptions { BatchTimeoutMilliseconds = 40, LookupTimeoutMilliseconds = 20 }));
        var middleware = new AccessLogMiddleware(async context =>
        { context.Response.ContentType = "text/plain"; await context.Response.WriteAsync("203.0.113.89\n"); }, options, TimeProvider.System);
        await worker.StartAsync(default);
        try
        {
            await SendRootAsync(middleware, queue).WaitAsync(TimeSpan.FromSeconds(1));
            await enricher.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            var timer = Stopwatch.StartNew();
            for (var index = 0; index < 20; index++) await SendRootAsync(middleware, queue).WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.True(timer.Elapsed < TimeSpan.FromSeconds(1));
            Assert.True(queue.GetMetrics().Dropped > 0);
            await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal("timeout", AccessSourceLocations.Read(store.First![0].SourceLocationJson)!.Status);
            await SendRootAsync(middleware, queue).WaitAsync(TimeSpan.FromMilliseconds(500));
            Assert.Equal(1, enricher.Calls);
        }
        finally { enricher.Release.TrySetResult(); store.Release.TrySetResult(); await worker.StopAsync(default); }
    }

    [Fact]
    public async Task ExplicitRealDatEnrichesTheSourceRecordWithoutUsingAQueryTarget()
    {
        var path = Environment.GetEnvironmentVariable("IPLOCATION_QQWRY_VALIDATION_PATH");
        if (string.IsNullOrWhiteSpace(path)) return;
        using var source = new ChunzhenDataProvider(OptionsFactory.Create(new ChunzhenOptions { Enabled = true, DatabasePath = path }));
        using var leaseProvider = new RuntimeProviderRegistry.RuntimeProvider(source, false);
        var result = await Enricher(leaseProvider).EnrichAsync([Event("203.0.113.89")], default);
        var location = AccessSourceLocations.Read(result[0].SourceLocationJson)!;
        Assert.Equal("success", location.Status); Assert.Equal("2026-10-28", location.Version);
        Assert.Equal("中国", location.Country); Assert.Equal("安徽", location.Province); Assert.Equal("蚌埠", location.City);
    }

    private static AccessLogEnricher Enricher(IIpDataProvider provider, int lookupMilliseconds = 25) => new(
        OptionsFactory.Create(new AccessLocationOptions { BatchTimeoutMilliseconds = 600, LookupTimeoutMilliseconds = lookupMilliseconds }), new Registry(provider));
    private static AccessEvent Event(string source) => new(DateTimeOffset.UtcNow, "controlled", "plainIp", "GET", "HTTP/1.1", "ip", null, null, 200, 1, false, source, "curl");
    private static ProviderQueryResult Success() => new("success", new IpFields { Region = "中国–安徽–蚌埠", Isp = "电信" }, "controlled", "2026-10-28");
    private static async Task SendRootAsync(AccessLogMiddleware middleware, IAccessEventQueue queue)
    {
        var context = new DefaultHttpContext(); context.Request.Path = "/"; context.Request.Method = "GET"; context.Request.Protocol = "HTTP/1.1";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.89"); context.Response.Body = new MemoryStream();
        await middleware.InvokeAsync(context, queue);
        context.Response.Body.Position = 0;
        Assert.Equal("203.0.113.89\n", await new StreamReader(context.Response.Body).ReadToEndAsync());
    }
    private sealed class SourceProvider(Func<IPAddress, CancellationToken, Task<ProviderQueryResult>> query) : IIpDataProvider
    {
        public ProviderDescriptor Descriptor => new("CZ88", "controlled", "ready", true, false, true, false, true, "2026-10-28", "controlled", "", "");
        public int Calls; public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProviderQueryResult> QueryAsync(IPAddress address, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); return query(address, cancellationToken); }
    }
    private sealed class Registry(IIpDataProvider provider) : IRuntimeProviderRegistry
    {
        public IReadOnlyList<IIpDataProvider> Providers { get; } = [provider];
        public ProviderOrderSnapshot Order => new(0, Providers);
        public long Revision(string id) => 0; public void PublishState(string id, bool enabled, long revision) { }
        public void PublishOrder(IReadOnlyList<string> providerIds, long revision) { }
        public Task ReloadAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class BlockedEnricher : IAccessLogEnricher
    {
        public int Calls; public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<IReadOnlyList<AccessEvent>> EnrichAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); Entered.TrySetResult(); await Release.Task; return events; }
    }
    private sealed class SlowStore : IAccessLogStore
    {
        public IReadOnlyList<AccessEvent>? First;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        { First ??= events; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return events.Count; }
        public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60) => throw new NotSupportedException();
    }
}
