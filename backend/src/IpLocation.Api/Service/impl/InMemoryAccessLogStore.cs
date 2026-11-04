using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.Extensions.Options;
using IpLocation.Api.Common;

namespace IpLocation.Api.Service.impl;

/// <summary>初始化阶段的受限内存存储；不建表、不落盘，也不提供可靠审计承诺。</summary>
public sealed class InMemoryAccessLogStore : IAccessLogStore
{
    private static readonly double[] HistogramUpperBounds = [1, 5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000];
    private readonly object _gate = new();
    private readonly Queue<AccessEvent> _recent = new();
    private readonly SortedDictionary<DateTimeOffset, MinuteAccumulator> _minutes = new();
    private readonly AccessLogOptions _options;
    private readonly IAccessEventQueue _queue;
    private readonly TimeProvider _timeProvider;
    private readonly DateTimeOffset _startedAtUtc;
    private readonly IConsoleRuntimeSettings? _runtime;

    public InMemoryAccessLogStore(IOptions<AccessLogOptions> options, IAccessEventQueue queue, TimeProvider timeProvider,
        IConsoleRuntimeSettings? runtime = null)
    {
        _options = options.Value;
        _queue = queue;
        _timeProvider = timeProvider;
        _startedAtUtc = timeProvider.GetUtcNow();
        _runtime = runtime;
    }

    public ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ConsoleBasicSettingsRules.IsAccessLoggingEnabled(_runtime, _options)) return ValueTask.FromResult(0);
        lock (_gate)
        {
            // 内存批次在同一锁内提交；避免取消发生在一半后被误报为整批未写入。
            cancellationToken.ThrowIfCancellationRequested();
            var now = _timeProvider.GetUtcNow();
            var recentCutoff = ConsoleBasicSettingsRules.ReadCutoff(now, ConsoleBasicSettingsRules.RetentionDays(_runtime, _options));
            var minuteCutoff = ToMinute(now).AddMinutes(1 - _options.MinuteBucketCapacity);
            PruneExpired(recentCutoff, minuteCutoff);
            foreach (var incoming in events)
            {
                var item = incoming with { McpContextJson = McpContexts.Normalize(incoming.McpContextJson) };
                // 排队期间已过期的事件不能通过迟到批次重新进入明细窗口。
                if (!recentCutoff.HasValue || item.OccurredAtUtc > recentCutoff.Value) _recent.Enqueue(item);
                while (_recent.Count > _options.RecentEventCapacity)
                    _recent.Dequeue();

                var minute = ToMinute(item.OccurredAtUtc);
                if (minute < minuteCutoff) continue;
                if (!_minutes.TryGetValue(minute, out var accumulator))
                    _minutes.Add(minute, accumulator = new MinuteAccumulator());
                accumulator.Add(item);
                while (_minutes.Count > _options.MinuteBucketCapacity)
                    _minutes.Remove(_minutes.First().Key);
            }
        }

        return ValueTask.FromResult(events.Count);
    }

    public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60)
    {
        lock (_gate)
        {
            var now = _timeProvider.GetUtcNow();
            var retention = ConsoleBasicSettingsRules.RetentionDays(_runtime, _options);
            PruneExpired(ConsoleBasicSettingsRules.ReadCutoff(now, retention),
                ToMinute(now).AddMinutes(1 - _options.MinuteBucketCapacity));
            return new AccessLogSnapshot(
                now, _startedAtUtc, "inMemory", false,
                _options.RecentEventCapacity, retention, _options.MinuteBucketCapacity,
                HistogramUpperBounds.ToArray(), _queue.GetMetrics(),
                _recent.Reverse().Take(Math.Clamp(recentLimit, 0, _options.RecentEventCapacity)).ToArray(),
                _minutes.TakeLast(Math.Clamp(minuteLimit, 0, _options.MinuteBucketCapacity))
                    .Select(pair => pair.Value.ToSnapshot(pair.Key)).ToArray());
        }
    }

    private void PruneExpired(DateTimeOffset? recentCutoff, DateTimeOffset minuteCutoff)
    {
        // 并发请求可能乱序完成，不能只检查队首，否则后方过期 IP 会被新记录挡住。
        for (var remaining = _recent.Count; remaining > 0; remaining--)
        {
            var item = _recent.Dequeue();
            if (!recentCutoff.HasValue || item.OccurredAtUtc > recentCutoff.Value) _recent.Enqueue(item);
        }
        while (_minutes.Count > 0 && _minutes.First().Key < minuteCutoff)
            _minutes.Remove(_minutes.First().Key);
    }

    private static DateTimeOffset ToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private sealed class MinuteAccumulator
    {
        private long _count;
        private long _home;
        private long _plainIp;
        private long _query;
        private long _clientErrors;
        private long _serverErrors;
        private long _cancelled;
        private double _total;
        private double _min = double.MaxValue;
        private double _max;
        private readonly long[] _histogram = new long[HistogramUpperBounds.Length + 1];

        public void Add(AccessEvent item)
        {
            _count++;
            if (item.RouteCategory == "homeHtml") _home++;
            if (item.RouteCategory == "plainIp") _plainIp++;
            if (AccessLogPersistencePlan.IsLocationQuery(item)) _query++;
            if (item.StatusCode is >= 400 and < 500) _clientErrors++;
            if (item.StatusCode >= 500) _serverErrors++;
            if (item.IsCancelled) _cancelled++;
            var elapsed = double.IsFinite(item.ElapsedMilliseconds) ? Math.Max(0, item.ElapsedMilliseconds) : 0;
            _total += elapsed;
            _min = Math.Min(_min, elapsed);
            _max = Math.Max(_max, elapsed);
            var index = 0;
            while (index < HistogramUpperBounds.Length && elapsed > HistogramUpperBounds[index]) index++;
            _histogram[index]++;
        }

        public AccessMinuteStats ToSnapshot(DateTimeOffset minute) => new(minute, _count, _home, _plainIp,
            _query, _clientErrors, _serverErrors, _cancelled, _total,
            _count == 0 ? 0 : _min, _max, _histogram.ToArray());
    }
}
