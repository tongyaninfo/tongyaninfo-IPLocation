using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Options;
using IpLocation.Api.Service;

namespace IpLocation.Api.Common;

/// <summary>无数据库副作用的批次规则，用于复用去重、分钟口径与读取边界。</summary>
public static class AccessLogPersistencePlan
{
    private static readonly double[] HistogramBounds = [1, 5, 10, 25, 50, 100, 250, 500, 1_000, 2_500, 5_000, 10_000];

    public static IReadOnlyList<double> LatencyUpperBoundsMilliseconds => HistogramBounds.ToArray();

    public static string GetEventKey(AccessEvent accessEvent)
    {
        // v1 规则属于持久合同；重试不能因重新发号、时区表示或序列化方式改变键。
        var trace = accessEvent.TraceId;
        var route = accessEvent.RouteCategory;
        var method = accessEvent.Method;
        var identity = string.Create(CultureInfo.InvariantCulture,
            $"v1|{trace.Length}:{trace}|{accessEvent.OccurredAtUtc.UtcTicks}|{route.Length}:{route}|{method.Length}:{method}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant();
    }

    public static IReadOnlyList<PreparedAccessRecord> Prepare(IReadOnlyList<AccessEvent> events, IIdService ids)
    {
        if (events.Count > 10_000) throw new ArgumentOutOfRangeException(nameof(events), "访问日志批次超过上限。");
        var unique = new Dictionary<string, AccessEvent>(StringComparer.Ordinal);
        foreach (var item in events)
        {
            ValidateEvent(item);
            // 扩展诊断字段不参与 v1 事件身份，旧事件重试不会因新增头字段而重复计数。
            unique.TryAdd(GetEventKey(item), item with
            {
                RequestHeadersJson = AccessRequestHeaders.Normalize(item.RequestHeadersJson),
                SourceLocationJson = AccessSourceLocations.Read(item.SourceLocationJson) is { } location ? AccessSourceLocations.Write(location) : null,
                McpContextJson = McpContexts.Normalize(item.McpContextJson)
            });
        }
        // 稳定插入顺序也减少多个消费者争用相同唯一键时的锁顺序差异。
        return unique.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair =>
        {
            var id = ids.NewId();
            if (id <= 0) throw new InvalidOperationException("统一发号服务返回了无效业务 ID。");
            return new PreparedAccessRecord(id, pair.Key, pair.Value);
        }).ToArray();
    }

    public static IReadOnlyList<AccessMinuteIncrement> AggregateInserted(
        IReadOnlyList<PreparedAccessRecord> records, IReadOnlySet<string> insertedKeys)
    {
        return records.Where(record => insertedKeys.Contains(record.EventKey))
            .GroupBy(record => (Minute: ToMinute(record.Event.OccurredAtUtc), record.Event.RouteCategory,
                Database: record.Event.Database ?? string.Empty, record.Event.Format))
            .OrderBy(group => group.Key.Minute)
            .ThenBy(group => group.Key.RouteCategory, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Database, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Format, StringComparer.Ordinal)
            .Select(group =>
            {
                var items = group.Select(record => record.Event).ToArray();
                var histogram = new long[HistogramBounds.Length + 1];
                foreach (var item in items)
                {
                    var index = 0;
                    while (index < HistogramBounds.Length && item.ElapsedMilliseconds > HistogramBounds[index]) index++;
                    histogram[index]++;
                }
                return new AccessMinuteIncrement(group.Key.Minute, group.Key.RouteCategory,
                    group.Key.Database, group.Key.Format, "request", items.LongLength,
                    items.LongCount(item => item.RouteCategory == "homeHtml"),
                    items.LongCount(item => item.RouteCategory == "plainIp"),
                    items.LongCount(IsLocationQuery),
                    items.LongCount(item => item.StatusCode is >= 400 and < 500),
                    items.LongCount(item => item.StatusCode >= 500),
                    items.LongCount(item => item.IsCancelled),
                    items.LongCount(item => item.StatusCode is >= 200 and < 300),
                    null, items.Sum(item => item.ElapsedMilliseconds),
                    items.Min(item => item.ElapsedMilliseconds), items.Max(item => item.ElapsedMilliseconds), histogram);
            }).ToArray();
    }

    public static AccessLogReadWindow NormalizeReadWindow(AccessLogReadRequest request, DateTimeOffset now,
        AccessLogOptions accessOptions, PersistentAccessLogOptions readerOptions, IConsoleRuntimeSettings? runtime = null)
    {
        if (request.RecentLimit < 1 || request.RecentLimit > readerOptions.MaxRecentRows)
            throw new ArgumentOutOfRangeException(nameof(request), "访问明细读取行数超出允许范围。");
        if (request.MinuteLimit < 1 || request.MinuteLimit > readerOptions.MaxMinuteRows)
            throw new ArgumentOutOfRangeException(nameof(request), "分钟统计读取行数超出允许范围。");
        now = now.ToUniversalTime();
        var to = (request.ToUtc ?? now).ToUniversalTime();
        if (to > now) to = now;
        var from = (request.FromUtc ?? to.AddDays(-1)).ToUniversalTime();
        if (from >= to || to - from > TimeSpan.FromDays(readerOptions.MaxReadRangeDays))
            throw new ArgumentOutOfRangeException(nameof(request), "读取时间范围无效或超过允许上限。");
        var minuteTo = ToMinute(to);
        if (minuteTo < to) minuteTo = minuteTo.AddMinutes(1);
        // 分钟统计按完整桶读取，单独返回取整边界，不冒充秒级精确范围。
        return new AccessLogReadWindow(now, from, to, ToMinute(from), minuteTo,
            ConsoleBasicSettingsRules.ReadCutoff(now, ConsoleBasicSettingsRules.RetentionDays(runtime, accessOptions)),
            now.AddDays(-readerOptions.MinuteRetentionDays), request.RecentLimit, request.MinuteLimit);
    }

    private static DateTimeOffset ToMinute(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, utc.Minute, 0, TimeSpan.Zero);
    }

    private static void ValidateEvent(AccessEvent item)
    {
        if (string.IsNullOrWhiteSpace(item.TraceId) || item.TraceId.Length > 128 ||
            string.IsNullOrWhiteSpace(item.RouteCategory) || item.RouteCategory.Length > 32 ||
            string.IsNullOrWhiteSpace(item.Method) || item.Method.Length > 16 ||
            item.Protocol.Length > 16 || item.Format.Length > 16 || item.ClientCategory.Length > 16 ||
            item.Database is { Length: > 16 } || item.DatabaseVersion is { Length: > 128 } || item.SourceAddress is { Length: > 64 } ||
            item.StatusCode is < 100 or > 599 || !double.IsFinite(item.ElapsedMilliseconds) || item.ElapsedMilliseconds < 0)
            throw new ArgumentException("访问事件元数据无效或超过允许长度。", nameof(item));
    }

    public static bool IsLocationQuery(AccessEvent item) => item.RouteCategory is "query" or "selfQuery" ||
        (item.RouteCategory == "mcp" && McpContexts.IsLocationQuery(item.McpContextJson));
}
