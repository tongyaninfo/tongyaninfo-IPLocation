namespace IpLocation.Api.Models.Events;

/// <summary>同一事件的去重键固定，业务 ID 仅由后台消费者生成。</summary>
public sealed record PreparedAccessRecord(long Id, string EventKey, AccessEvent Event);

/// <summary>仅针对数据库实际新插入事件计算的分钟增量，不承载厂商调用统计。</summary>
public sealed record AccessMinuteIncrement(
    DateTimeOffset MinuteUtc,
    string RouteCategory,
    string Database,
    string Format,
    string CountKind,
    long TotalRequests,
    long HomeHtmlRequests,
    long PlainIpRequests,
    long QueryRequests,
    long ClientErrors,
    long ServerErrors,
    long CancelledRequests,
    long SuccessCount,
    long? NotFoundCount,
    double TotalElapsedMilliseconds,
    double MinElapsedMilliseconds,
    double MaxElapsedMilliseconds,
    long[] LatencyHistogramCounts);

/// <summary>规范化的只读范围；保留边界独立于用户请求窗口。</summary>
public sealed record AccessLogReadWindow(
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset FromUtc,
    DateTimeOffset ToUtc,
    DateTimeOffset MinuteFromUtc,
    DateTimeOffset MinuteToUtc,
    DateTimeOffset? AccessRetentionCutoffUtc,
    DateTimeOffset MinuteRetentionCutoffUtc,
    int RecentLimit,
    int MinuteLimit);
