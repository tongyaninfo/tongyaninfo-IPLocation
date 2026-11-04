namespace IpLocation.Api.Options;

/// <summary>访问采集容量有明确上限；真实容量与性能须经压测确认。</summary>
public sealed class AccessLogOptions
{
    public const string SectionName = "AccessLogging";

    public bool Enabled { get; set; } = true;
    public int QueueCapacity { get; set; } = 10_000;
    public int BatchSize { get; set; } = 500;
    public int FlushIntervalMilliseconds { get; set; } = 1_000;
    public int WriteTimeoutMilliseconds { get; set; } = 5_000;
    public int ShutdownDrainMilliseconds { get; set; } = 5_000;
    public int RecentEventCapacity { get; set; } = 1_000;
    /// <summary>null 不限制历史读取时间；0 关闭新普通访问保存；正数限制可读天数，不授权物理删除。</summary>
    public int? RecentEventRetentionDays { get; set; } = 30;
    /// <summary>聚合仅保留包含当前分钟在内的指定分钟窗口，不只限制桶数量。</summary>
    public int MinuteBucketCapacity { get; set; } = 1_440;

    /// <summary>默认仅保存 IPv4 /24 或 IPv6 /48；修改留存策略前先确认范围。</summary>
    public bool RetainFullSourceIp { get; set; }
}
