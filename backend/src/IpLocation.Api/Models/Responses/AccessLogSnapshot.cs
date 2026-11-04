using IpLocation.Api.Models.Events;

namespace IpLocation.Api.Models.Responses;

/// <summary>进程内采集健康状态；计数用于观测，不表示已经可靠落盘。</summary>
public sealed record AccessQueueMetrics(
    long Received,
    long Accepted,
    long Written,
    long Dropped,
    long WriteFailures,
    int Queued,
    long InFlight,
    bool IsAccepting,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    long Suppressed = 0);

/// <summary>同一分钟的请求与延迟直方图；最后一个桶表示超过所有上界。</summary>
public sealed record AccessMinuteStats(
    DateTimeOffset MinuteUtc,
    long TotalRequests,
    long HomeHtmlRequests,
    long PlainIpRequests,
    long QueryRequests,
    long ClientErrors,
    long ServerErrors,
    long CancelledRequests,
    double TotalElapsedMilliseconds,
    double MinElapsedMilliseconds,
    double MaxElapsedMilliseconds,
    IReadOnlyList<long> LatencyHistogramCounts);

/// <summary>受限内存窗口，进程退出即失去；不能充当可靠管理审计或历史日志。</summary>
public sealed record AccessLogSnapshot(
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset StartedAtUtc,
    string StorageKind,
    bool IsDurable,
    int RecentEventCapacity,
    int? RecentEventRetentionDays,
    int MinuteBucketCapacity,
    IReadOnlyList<double> LatencyUpperBoundsMilliseconds,
    AccessQueueMetrics Metrics,
    IReadOnlyList<AccessEvent> RecentEvents,
    IReadOnlyList<AccessMinuteStats> Minutes);
