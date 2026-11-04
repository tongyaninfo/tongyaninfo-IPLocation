using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.EntityFrameworkCore;
using OptionsFactory = Microsoft.Extensions.Options.Options;

namespace IpLocation.Api.Tests.Unit;

/// <summary>无数据库验证纯规则和失败边界；不能替代获准隔离 PG 的事务与并发验收。</summary>
public sealed class PostgreSqlAccessLogStoreTests
{
    [Fact]
    public void EventKeyRemainsStableAcrossTimezoneRepresentationAndNewBusinessIds()
    {
        var item = Event("trace-a");
        var offsetRepresentation = item with { OccurredAtUtc = item.OccurredAtUtc.ToOffset(TimeSpan.FromHours(8)) };
        var ids = new CountingIdService();

        var first = Assert.Single(AccessLogPersistencePlan.Prepare([item], ids));
        var retry = Assert.Single(AccessLogPersistencePlan.Prepare([offsetRepresentation], ids));

        Assert.Equal(first.EventKey, retry.EventKey);
        Assert.Equal(64, first.EventKey.Length);
        Assert.NotEqual(first.Id, retry.Id);
        Assert.NotEqual(first.EventKey, AccessLogPersistencePlan.GetEventKey(item with { TraceId = "trace-b" }));
        Assert.Equal(first.EventKey, AccessLogPersistencePlan.GetEventKey(item with
        {
            RequestHeadersJson = "{\"headers\":{\"user-agent\":[\"new metadata\"]},\"truncated\":false}",
            SourceLocationJson = "{\"provider\":\"CZ88\",\"status\":\"notFound\"}"
        }));
    }

    [Fact]
    public void DamagedDiagnosticMetadataDoesNotDropAnOtherwiseValidEventOrChangeItsIdentity()
    {
        var item = Event("trace-diagnostics") with { RequestHeadersJson = "{", SourceLocationJson = "[" };
        var record = Assert.Single(AccessLogPersistencePlan.Prepare([item], new CountingIdService()));
        Assert.Equal(AccessLogPersistencePlan.GetEventKey(item), record.EventKey);
        Assert.Null(record.Event.RequestHeadersJson);
        Assert.Null(record.Event.SourceLocationJson);
    }

    [Fact]
    public async Task ProcessWindowKeepsCollectedHeaderMetadataAndLegacyNullSeparately()
    {
        var options = OptionsFactory.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(options, TimeProvider.System);
        var memory = new InMemoryAccessLogStore(options, queue, TimeProvider.System);
        var collected = Event("new") with
        {
            OccurredAtUtc = DateTimeOffset.UtcNow,
            RequestHeadersJson = "{\"headers\":{\"user-agent\":[\"controlled\"]},\"truncated\":false}"
        };
        var legacy = collected with { TraceId = "legacy", RequestHeadersJson = null };
        await memory.AppendBatchAsync([collected, legacy], default);
        var records = memory.GetSnapshot().RecentEvents;
        Assert.Equal("controlled", AccessRequestHeaders.Read(records.Single(item => item.TraceId == "new").RequestHeadersJson)!.Headers["user-agent"].Single());
        Assert.Null(records.Single(item => item.TraceId == "legacy").RequestHeadersJson);
    }

    [Fact]
    public void DuplicateEventsWithinBatchReceiveOnlyOneBusinessId()
    {
        var ids = new CountingIdService();
        var item = Event("trace-a");

        var records = AccessLogPersistencePlan.Prepare([item, item, item with { ElapsedMilliseconds = 99 }], ids);

        Assert.Single(records);
        Assert.Equal(1, ids.Calls);
    }

    [Fact]
    public void OnlyDatabaseReturnedNewKeysContributeToRequestHistogram()
    {
        var first = Event("trace-first") with { ElapsedMilliseconds = 5 };
        var existing = Event("trace-existing") with { ElapsedMilliseconds = 2_000, StatusCode = 503 };
        var records = AccessLogPersistencePlan.Prepare([first, existing], new CountingIdService());
        var insertedKeys = new HashSet<string>(StringComparer.Ordinal) { AccessLogPersistencePlan.GetEventKey(first) };

        var minute = Assert.Single(AccessLogPersistencePlan.AggregateInserted(records, insertedKeys));

        Assert.Equal("request", minute.CountKind);
        Assert.Equal(1, minute.TotalRequests);
        Assert.Equal(1, minute.PlainIpRequests);
        Assert.Equal(1, minute.SuccessCount);
        Assert.Equal(0, minute.ServerErrors);
        Assert.Null(minute.NotFoundCount);
        Assert.Equal(5, minute.TotalElapsedMilliseconds);
        Assert.Equal(1, minute.LatencyHistogramCounts.Sum());
        Assert.Equal(1, minute.LatencyHistogramCounts[1]);
        Assert.Empty(AccessLogPersistencePlan.AggregateInserted(records, new HashSet<string>()));
    }

    [Fact]
    public void MinuteNaturalDimensionsRemainSeparateAndLatencyCountsAreMergeable()
    {
        var records = AccessLogPersistencePlan.Prepare([
            Event("trace-1") with { RouteCategory = "query", Database = "dbip", Format = "json", ElapsedMilliseconds = 1 },
            Event("trace-2") with { RouteCategory = "query", Database = "dbip", Format = "xml", ElapsedMilliseconds = 50 },
            Event("trace-3") with { RouteCategory = "query", Database = "dbip", Format = "json", ElapsedMilliseconds = 15_000 },
            Event("trace-4") with { RouteCategory = "selfQuery", Database = "all", Format = "json", ElapsedMilliseconds = 10 }
        ], new CountingIdService());
        var increments = AccessLogPersistencePlan.AggregateInserted(records, records.Select(record => record.EventKey).ToHashSet());

        Assert.Equal(3, increments.Count);
        Assert.All(increments, row => Assert.Equal("request", row.CountKind));
        Assert.Equal(4, increments.Sum(row => row.TotalRequests));
        Assert.Equal(4, increments.Sum(row => row.LatencyHistogramCounts.Sum()));
        var combined = Assert.Single(increments, row => row.RouteCategory == "query" && row.Format == "json");
        Assert.Equal(2, combined.QueryRequests);
        Assert.Equal(15_001, combined.TotalElapsedMilliseconds);
        Assert.Equal(1, combined.MinElapsedMilliseconds);
        Assert.Equal(15_000, combined.MaxElapsedMilliseconds);
        Assert.Equal(1, combined.LatencyHistogramCounts[0]);
        Assert.Equal(1, combined.LatencyHistogramCounts[^1]);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidElapsedIsRejectedBeforeIdGeneration(double elapsed)
    {
        var ids = new CountingIdService();

        Assert.Throws<ArgumentException>(() => AccessLogPersistencePlan.Prepare(
            [Event("trace-invalid") with { ElapsedMilliseconds = elapsed }], ids));

        Assert.Equal(0, ids.Calls);
    }

    [Fact]
    public void HistoryReadLimitsAndRetentionAreIndependentOfRequestedRange()
    {
        var now = new DateTimeOffset(2026, 11, 2, 12, 0, 30, TimeSpan.Zero);
        var request = new AccessLogReadRequest(now.AddDays(-31), now.AddHours(1), 10, 20);
        var window = AccessLogPersistencePlan.NormalizeReadWindow(request, now,
            new AccessLogOptions { RecentEventRetentionDays = 30 }, new PersistentAccessLogOptions());

        Assert.Equal(now, window.ToUtc);
        Assert.Equal(now.AddDays(-30), window.AccessRetentionCutoffUtc);
        Assert.Equal(now.AddDays(-180), window.MinuteRetentionCutoffUtc);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 12, 1, 0, TimeSpan.Zero), window.MinuteToUtc);
        Assert.Equal(10, window.RecentLimit);
        Assert.Equal(20, window.MinuteLimit);
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessLogPersistencePlan.NormalizeReadWindow(
            request with { RecentLimit = 100_000 }, now, new AccessLogOptions(), new PersistentAccessLogOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => AccessLogPersistencePlan.NormalizeReadWindow(
            request with { FromUtc = now.AddDays(-32) }, now, new AccessLogOptions(), new PersistentAccessLogOptions()));
    }

    [Fact]
    public async Task DatabaseFailureDoesNotBecomeMemorySuccess()
    {
        var factory = new UnavailableContextFactory();
        var accessOptions = OptionsFactory.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(accessOptions, TimeProvider.System);
        var store = new PostgreSqlAccessLogStore(factory, new CountingIdService(), accessOptions,
            OptionsFactory.Create(new PersistentAccessLogOptions()), queue, TimeProvider.System);

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await store.AppendBatchAsync([Event("trace-failure")], CancellationToken.None));

        Assert.Equal(1, factory.Calls);
        var snapshot = store.GetSnapshot();
        Assert.Empty(snapshot.RecentEvents);
        Assert.False(snapshot.IsDurable);
        Assert.Equal("postgresqlProcessWindow", snapshot.StorageKind);
    }

    [Fact]
    public async Task InvalidReadIsRejectedBeforeCreatingAnyDatabaseContext()
    {
        var factory = new UnavailableContextFactory();
        var accessOptions = OptionsFactory.Create(new AccessLogOptions());
        var queue = new AccessEventQueue(accessOptions, TimeProvider.System);
        var store = new PostgreSqlAccessLogStore(factory, new CountingIdService(), accessOptions,
            OptionsFactory.Create(new PersistentAccessLogOptions()), queue, TimeProvider.System);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => store.GetSnapshotAsync(
            new AccessLogReadRequest(RecentLimit: int.MaxValue), CancellationToken.None));

        Assert.Equal(0, factory.Calls);
    }

    private static AccessEvent Event(string trace) => new(new DateTimeOffset(2026, 11, 2, 12, 0, 0, TimeSpan.Zero),
        trace, "plainIp", "GET", "HTTP/1.1", "ip", null, null, 200, 1, false, "203.0.113.42", "curl");

    private sealed class CountingIdService : IIdService
    {
        public int Calls { get; private set; }
        public long NewId() => ++Calls;
    }

    private sealed class UnavailableContextFactory : IDbContextFactory<IpLocationDbContext>
    {
        public int Calls { get; private set; }
        public IpLocationDbContext CreateDbContext()
        {
            Calls++;
            throw new InvalidOperationException("controlled database unavailable");
        }

        public Task<IpLocationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<IpLocationDbContext>(new InvalidOperationException("controlled database unavailable"));
        }
    }
}
