namespace IpLocation.Api.Options;

/// <summary>持久日志只读查询的资源和时间边界；不授权任何自动清理。</summary>
public sealed class PersistentAccessLogOptions
{
    public const string SectionName = "AccessLogging:PersistentReader";
    public int ReadTimeoutMilliseconds { get; set; } = 5_000;
    public int MaxRecentRows { get; set; } = 1_000;
    public int MaxMinuteRows { get; set; } = 1_440;
    public int MaxReadRangeDays { get; set; } = 31;
    public int MinuteRetentionDays { get; set; } = 180;
}
