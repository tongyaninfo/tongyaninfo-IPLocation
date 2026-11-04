using IpLocation.Api.Models.Events;

namespace IpLocation.Api.Models.Responses;

/// <summary>业务主键以字符串返回，避免前端丢失 long 精度。</summary>
public sealed record PersistentAccessLogEntry(string Id, AccessEvent Event);

/// <summary>固定自然维度的分钟数据；HTTP 成功不等于定位命中，null 表示未采集无匹配。</summary>
public sealed record PersistentTrafficMinute(
    string RouteCategory,
    string Database,
    string Format,
    string CountKind,
    AccessMinuteStats Stats,
    long HttpSuccessCount,
    long? NotFoundCount,
    long Revision);

/// <summary>数据库历史快照与进程内窗口分离；截断和有效保留边界均显式返回。</summary>
public sealed record PersistentAccessLogSnapshot(
    DateTimeOffset CapturedAtUtc,
    DateTimeOffset RequestedFromUtc,
    DateTimeOffset RequestedToUtc,
    DateTimeOffset MinuteFromUtc,
    DateTimeOffset MinuteToUtc,
    DateTimeOffset? AccessRetentionCutoffUtc,
    DateTimeOffset MinuteRetentionCutoffUtc,
    string StorageKind,
    int RecentLimit,
    int MinuteLimit,
    bool RecentTruncated,
    bool MinutesTruncated,
    IReadOnlyList<double> LatencyUpperBoundsMilliseconds,
    IReadOnlyList<PersistentAccessLogEntry> RecentEvents,
    IReadOnlyList<PersistentTrafficMinute> Minutes);
