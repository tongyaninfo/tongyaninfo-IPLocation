using System.Net;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Middleware;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using IpLocation.Api.Workers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>基础设置与普通访问管线的边界测试；不连接数据库，不清理任何历史记录。</summary>
public sealed class ConsoleBasicSettingsTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ExplicitPersistedNullIsUnlimitedWhileMissingKeyUsesStartupDefault()
    {
        var baseline = new ConsoleBasicSettings(30, true, 480, 100);
        Assert.Equal(30, ConsoleBasicSettingsRules.Read(new Dictionary<string, string>(), baseline).AccessLogRetentionDays);
        var restored = ConsoleBasicSettingsRules.Read(new Dictionary<string, string>
        {
            ["basic.accessLogRetentionDays"] = "null",
            ["basic.retainFullSourceIp"] = "false",
            ["basic.sessionMinutes"] = "17",
            ["basic.queueCapacity"] = "200"
        }, baseline);
        Assert.Null(restored.AccessLogRetentionDays);
        Assert.False(restored.RetainFullSourceIp);
        Assert.Equal(17, restored.SessionMinutes);
        Assert.Equal(200, restored.QueueCapacity);
        Assert.Equal("null", JsonSerializer.Serialize(restored.AccessLogRetentionDays));
    }

    [Theory]
    [InlineData(null, 17, 1, true)]
    [InlineData(0, 5, 1_000_000, true)]
    [InlineData(365, 1440, 100, true)]
    [InlineData(-1, 17, 100, false)]
    [InlineData(366, 17, 100, false)]
    [InlineData(30, 4, 100, false)]
    [InlineData(30, 1441, 100, false)]
    [InlineData(30, 17, 0, false)]
    [InlineData(30, 17, 1_000_001, false)]
    public void BasicSettingsEnforceIndependentNonSensitiveBounds(int? days, int session, int capacity, bool valid) =>
        Assert.Equal(valid, ConsoleBasicSettingsRules.IsValid(new(days, true, session, capacity)));

    [Fact]
    public void ExistingPublishCallersKeepBasicPolicyAndQueueOnlyResizesOnRecreation()
    {
        using var services = new ServiceCollection().AddOptions().BuildServiceProvider();
        var monitor = new Microsoft.Extensions.Options.OptionsMonitor<ConsoleOptions>(
            new Microsoft.Extensions.Options.OptionsFactory<ConsoleOptions>([], []), [], new Microsoft.Extensions.Options.OptionsCache<ConsoleOptions>());
        using (monitor)
        {
            var runtime = new ConsoleRuntimeSettings(services, OptionsFactory.Create(new QueryOptions()), monitor,
                OptionsFactory.Create(new DatabaseOptions()), OptionsFactory.Create(new AccessLogOptions { QueueCapacity = 10 }));
            var options = OptionsFactory.Create(new AccessLogOptions { QueueCapacity = 10 });
            var currentQueue = new AccessEventQueue(options, TimeProvider.System, runtime);
            runtime.Publish(runtime.Current with { Revision = 2, Basic = new(null, true, 17, 20) });
            // 来源管理等旧发布调用没有 basic，不能覆盖已保存的显式 null 或重建队列。
            runtime.Publish(new(3, new QueryOptions(), ["127.0.0.1"], true));
            Assert.Null(runtime.Current.Basic!.AccessLogRetentionDays);
            Assert.Equal(17, runtime.Current.Basic.SessionMinutes);
            Assert.Equal(10, currentQueue.Capacity);
            Assert.Equal(20, new AccessEventQueue(options, TimeProvider.System, runtime).Capacity);
        }
    }

    [Fact]
    public async Task DisabledPolicyDoesNotCaptureOrEnqueueNewPublicRequests()
    {
        var runtime = Runtime(0);
        var options = OptionsFactory.Create(new AccessLogOptions { RetainFullSourceIp = true });
        var queue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var reached = false;
        var middleware = new AccessLogMiddleware(_ => { reached = true; return Task.CompletedTask; }, options, TimeProvider.System, runtime);
        var context = Context();
        context.Request.Headers.UserAgent = "controlled user agent";
        await middleware.InvokeAsync(context, queue);
        Assert.True(reached);
        Assert.Equal(0, queue.GetMetrics().Received);
        Assert.Equal(0, queue.GetMetrics().Accepted);
        Assert.False(queue.TryRead(out _));
    }

    [Fact]
    public async Task UnknownPersistedPolicyFailsClosedUntilRuntimeSettingsAreReady()
    {
        var runtime = Runtime(30);
        runtime.Publish(runtime.Current with { IsReady = false });
        var options = OptionsFactory.Create(new AccessLogOptions { RecentEventRetentionDays = 30 });
        var queue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var reached = 0;
        var middleware = new AccessLogMiddleware(_ => { reached++; return Task.CompletedTask; }, options, TimeProvider.System, runtime);

        Assert.True(ConsoleBasicSettingsRules.IsAccessLoggingEnabled(null, options.Value));
        Assert.False(ConsoleBasicSettingsRules.IsAccessLoggingEnabled(runtime, options.Value));
        await middleware.InvokeAsync(Context(), queue);
        Assert.Equal(1, reached);
        Assert.Equal(0, queue.GetMetrics().Received);
        Assert.Equal(0, queue.GetMetrics().Accepted);
        Assert.False(queue.TryRead(out _));

        runtime.Publish(runtime.Current with { Revision = 2, IsReady = true });
        Assert.True(ConsoleBasicSettingsRules.IsAccessLoggingEnabled(runtime, options.Value));
        await middleware.InvokeAsync(Context(), queue);
        Assert.Equal(2, reached);
        Assert.Equal(1, queue.GetMetrics().Received);
        Assert.Equal(1, queue.GetMetrics().Accepted);
        Assert.True(queue.TryRead(out var recorded));
        Assert.Equal("203.0.113.42", recorded.SourceAddress);
    }

    [Fact]
    public async Task PolicyChangeDuringRequestPreventsLateEnqueueAndFullIpToggleAppliesImmediately()
    {
        var runtime = Runtime(30);
        var options = OptionsFactory.Create(new AccessLogOptions { RetainFullSourceIp = false });
        var queue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var middleware = new AccessLogMiddleware(_ => Task.CompletedTask, options, TimeProvider.System, runtime);
        await middleware.InvokeAsync(Context(), queue);
        Assert.True(queue.TryRead(out var full));
        Assert.Equal("203.0.113.42", full.SourceAddress);

        runtime.Set(runtime.Current.Basic! with { RetainFullSourceIp = false });
        await middleware.InvokeAsync(Context(), queue);
        Assert.True(queue.TryRead(out var masked));
        Assert.Equal("203.0.113.0/24", masked.SourceAddress);

        var disabling = new AccessLogMiddleware(_ =>
        {
            runtime.Set(runtime.Current.Basic! with { AccessLogRetentionDays = 0 });
            return Task.CompletedTask;
        }, options, TimeProvider.System, runtime);
        await disabling.InvokeAsync(Context(), queue);
        Assert.False(queue.TryRead(out _));
    }

    [Fact]
    public async Task ZeroPreservesMemoryHistoryButAddsNeitherDetailsNorMinutes()
    {
        var runtime = Runtime(null);
        var clock = new FixedTime();
        var options = OptionsFactory.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(options, clock, runtime);
        var store = new InMemoryAccessLogStore(options, queue, clock, runtime);
        Assert.Equal(1, await store.AppendBatchAsync([Event("old")], default));
        runtime.Set(runtime.Current.Basic! with { AccessLogRetentionDays = 0 });
        Assert.Equal(0, await store.AppendBatchAsync([Event("new")], default));
        var snapshot = store.GetSnapshot();
        Assert.Equal("old", Assert.Single(snapshot.RecentEvents).TraceId);
        Assert.Equal(1, Assert.Single(snapshot.Minutes).TotalRequests);
        runtime.Set(runtime.Current.Basic! with { AccessLogRetentionDays = null });
        Assert.Equal("old", Assert.Single(store.GetSnapshot().RecentEvents).TraceId);
    }

    [Fact]
    public async Task UnlimitedMemoryWindowStillHasCapacityAndRetainsOldEventsWithoutAgeCutoff()
    {
        var runtime = Runtime(null);
        var clock = new FixedTime();
        var options = OptionsFactory.Create(new AccessLogOptions { RecentEventCapacity = 2 });
        var queue = new AccessEventQueue(options, clock, runtime);
        var store = new InMemoryAccessLogStore(options, queue, clock, runtime);
        await store.AppendBatchAsync([Event("one") with { OccurredAtUtc = Now.AddDays(-800) },
            Event("two") with { OccurredAtUtc = Now.AddDays(-700) }, Event("three") with { OccurredAtUtc = Now.AddDays(-600) }], default);
        Assert.Null(store.GetSnapshot().RecentEventRetentionDays);
        Assert.Equal(new[] { "three", "two" }, store.GetSnapshot().RecentEvents.Select(item => item.TraceId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    public void UnlimitedAndDisabledPoliciesAllowHistoricalReadsWithBoundedRequestWindows(int? retention)
    {
        var from = Now.AddDays(-800);
        var to = from.AddDays(1);
        var window = ConsoleReadRules.NormalizeWindow(new() { FromUtc = from, ToUtc = to }, Now, retention);
        Assert.Equal(from, window.FromUtc);
        var persistent = AccessLogPersistencePlan.NormalizeReadWindow(new(from, to), Now,
            new AccessLogOptions { RecentEventRetentionDays = retention }, new());
        Assert.Null(persistent.AccessRetentionCutoffUtc);
        Assert.Equal(Now.AddDays(-180), persistent.MinuteRetentionCutoffUtc);
        Assert.Throws<ConsoleReadValidationException>(() => ConsoleReadRules.NormalizeWindow(new() { FromUtc = Now.AddDays(-32) }, Now, retention));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessLogPersistencePlan.NormalizeReadWindow(new(Now.AddDays(-32), Now), Now,
            new AccessLogOptions { RecentEventRetentionDays = retention }, new()));
    }

    [Fact]
    public async Task DisabledPostgresStoreDoesNotGenerateIdsOrCreateDatabaseContext()
    {
        var options = OptionsFactory.Create(new AccessLogOptions());
        var runtime = Runtime(0);
        var contexts = new UnavailableContexts();
        var ids = new CountingIds();
        var queue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var store = new PostgreSqlAccessLogStore(contexts, ids, options, OptionsFactory.Create(new PersistentAccessLogOptions()),
            queue, TimeProvider.System, runtime);
        Assert.Equal(0, await store.AppendBatchAsync([Event("pending")], default));
        Assert.Equal(0, contexts.Calls);
        Assert.Equal(0, ids.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConsumerSuppressionIsNotReportedAsSuccessfulWritingOrStorageFailure(bool disabledBeforeRead)
    {
        var runtime = Runtime(disabledBeforeRead ? 0 : 30);
        var options = OptionsFactory.Create(new AccessLogOptions { BatchSize = 1, FlushIntervalMilliseconds = 1 });
        var queue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var store = new CountingStore { Suppress = !disabledBeforeRead };
        var enricher = new CountingEnricher();
        queue.TryEnqueue(Event("pending"));
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance, enricher, runtime: runtime);
        await worker.StartAsync(default);
        await Wait(() => queue.GetMetrics().Suppressed == 1);
        await worker.StopAsync(default);
        var metrics = queue.GetMetrics();
        Assert.Equal(0, metrics.Written);
        Assert.Equal(0, metrics.Dropped);
        Assert.Equal(0, metrics.WriteFailures);
        Assert.Equal(0, metrics.InFlight);
        Assert.Null(metrics.LastSuccessUtc);
        Assert.Equal(disabledBeforeRead ? 0 : 1, enricher.Calls);
        Assert.Equal(disabledBeforeRead ? 0 : 1, store.Calls);
    }

    [Fact]
    public async Task DisableDuringEnrichmentSkipsStoreAndStartedTransactionMayFinish()
    {
        var runtime = Runtime(30);
        var options = OptionsFactory.Create(new AccessLogOptions { BatchSize = 1, FlushIntervalMilliseconds = 1 });
        var queue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var store = new CountingStore();
        var enricher = new CountingEnricher { OnCall = () => runtime.Set(runtime.Current.Basic! with { AccessLogRetentionDays = 0 }) };
        queue.TryEnqueue(Event("enriching"));
        using var worker = new AccessLogWorker(queue, store, options, NullLogger<AccessLogWorker>.Instance, enricher, runtime: runtime);
        await worker.StartAsync(default);
        await Wait(() => queue.GetMetrics().Suppressed == 1);
        Assert.Equal(0, store.Calls);
        await worker.StopAsync(default);

        runtime.Set(runtime.Current.Basic! with { AccessLogRetentionDays = 30 });
        var nextQueue = new AccessEventQueue(options, TimeProvider.System, runtime);
        var inFlight = new CountingStore { Block = true };
        using var started = new AccessLogWorker(nextQueue, inFlight, options, NullLogger<AccessLogWorker>.Instance, runtime: runtime);
        nextQueue.TryEnqueue(Event("committing"));
        await started.StartAsync(default);
        await inFlight.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        runtime.Set(runtime.Current.Basic! with { AccessLogRetentionDays = 0 });
        inFlight.Release.TrySetResult();
        await Wait(() => nextQueue.GetMetrics().Written == 1);
        nextQueue.TryEnqueue(Event("old-queue"));
        await Wait(() => nextQueue.GetMetrics().Suppressed == 1);
        await started.StopAsync(default);
        Assert.Equal(1, inFlight.Calls);
        Assert.Equal(1, nextQueue.GetMetrics().Written);
    }

    [Fact]
    public void BasicChangesRemainVisibleInSanitizedReliableAudit()
    {
        var json = JsonSerializer.Serialize(new { before = new { basic = new ConsoleBasicSettings(30, true, 480, 100) },
            after = new { basic = new ConsoleBasicSettings(null, false, 17, 200), password = "never-return" } }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var sanitized = ConsoleReadRules.SanitizeDifference(json);
        var after = sanitized["after"].GetProperty("basic");
        Assert.Equal(JsonValueKind.Null, after.GetProperty("accessLogRetentionDays").ValueKind);
        Assert.Equal(17, after.GetProperty("sessionMinutes").GetInt32());
        Assert.Equal(200, after.GetProperty("queueCapacity").GetInt32());
        Assert.DoesNotContain("never-return", JsonSerializer.Serialize(sanitized));
    }

    [Fact]
    public async Task InvalidOrFailedSaveDoesNotPublishUncommittedBasicSettings()
    {
        var runtime = Runtime(30);
        using var services = new ServiceCollection().BuildServiceProvider();
        var service = new ConsoleSettingsService(services, runtime, new NoQueries(), new CountingIds(), new LoopbackClient(),
            OptionsFactory.Create(new DatabaseOptions()), OptionsFactory.Create(new AccessLogOptions()),
            OptionsFactory.Create(new ConsoleAuthOptions()), OptionsFactory.Create(new TrustedProxyOptions()),
            NullLogger<ConsoleSettingsService>.Instance);
        var current = runtime.Current;
        var invalid = await service.SaveAsync(new DefaultHttpContext(), new(1, new QueryOptions(), ["127.0.0.1"], new(-1, true, 17, 100)));
        Assert.Equal(400, invalid.StatusCode);
        var missingStorage = await service.SaveAsync(new DefaultHttpContext(), new(1, new QueryOptions(), ["127.0.0.1"], new(null, false, 17, 200)));
        Assert.Equal(503, missingStorage.StatusCode);
        Assert.Same(current, runtime.Current);
        Assert.Equal(30, runtime.Current.Basic!.AccessLogRetentionDays);
    }

    private static DefaultHttpContext Context()
    {
        var context = new DefaultHttpContext();
        context.Request.Path = "/";
        context.Request.Method = "GET";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.42");
        return context;
    }

    private static AccessEvent Event(string trace) => new(Now, trace, "plainIp", "GET", "HTTP/1.1", "ip", null, null,
        200, 1, false, "203.0.113.42", "curl");

    private static MutableRuntime Runtime(int? days) => new(new(1, new QueryOptions(), ["127.0.0.1"], true, new(days, true, 480, 100)));

    private static async Task Wait(Func<bool> completed)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!completed()) await Task.Delay(10, timeout.Token);
    }

    private sealed class FixedTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class LoopbackClient : IClientIpService { public IPAddress GetClientIp(HttpContext context) => IPAddress.Loopback; }
    private sealed class NoQueries : IIpQueryService
    {
        public IReadOnlyList<IpLocation.Api.Models.Providers.ProviderDescriptor> GetProviders() => [];
        public Task<QueryResponse> QueryAsync(string ip, string? database, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class MutableRuntime(ConsoleRuntimeSnapshot initial) : IConsoleRuntimeSettings
    {
        private ConsoleRuntimeSnapshot _current = initial;
        public ConsoleRuntimeSnapshot Current => Volatile.Read(ref _current);
        public void Publish(ConsoleRuntimeSnapshot snapshot) => Volatile.Write(ref _current, snapshot);
        public Task ReloadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Set(ConsoleBasicSettings basic) => Publish(Current with { Revision = Current.Revision + 1, Basic = basic });
    }
    private sealed class CountingIds : IIdService { public int Calls; public long NewId() => ++Calls; }
    private sealed class UnavailableContexts : IDbContextFactory<IpLocationDbContext>
    {
        public int Calls;
        public IpLocationDbContext CreateDbContext() { Calls++; throw new InvalidOperationException("Controlled unavailable database."); }
        public Task<IpLocationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        { Calls++; throw new InvalidOperationException("Controlled unavailable database."); }
    }
    private sealed class CountingEnricher : IAccessLogEnricher
    {
        public int Calls;
        public Action? OnCall;
        public ValueTask<IReadOnlyList<AccessEvent>> EnrichAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); OnCall?.Invoke(); return ValueTask.FromResult(events); }
    }
    private sealed class CountingStore : IAccessLogStore
    {
        public int Calls;
        public bool Suppress;
        public bool Block;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult();
            if (Block) await Release.Task.WaitAsync(cancellationToken);
            return Suppress ? 0 : events.Count;
        }
        public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60) => throw new NotSupportedException();
    }
}
