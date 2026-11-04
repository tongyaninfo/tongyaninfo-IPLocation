namespace IpLocation.Api.Models.Entities;

/// <summary>UTC 分钟及固定维度的自然复合键；请求和来源调用不能混为同一口径。</summary>
public sealed class TrafficMinuteAggregate
{
    public DateTimeOffset MinuteUtc { get; set; }
    public string RouteCategory { get; set; } = string.Empty;
    /// <summary>没有来源的纯 IP/页面请求使用空串，避免可空自然键出现重复桶。</summary>
    public string Database { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string CountKind { get; set; } = "request";
    public long TotalRequests { get; set; }
    public long HomeHtmlRequests { get; set; }
    public long PlainIpRequests { get; set; }
    public long QueryRequests { get; set; }
    public long ClientErrors { get; set; }
    public long ServerErrors { get; set; }
    public long CancelledRequests { get; set; }
    /// <summary>当前表示 HTTP 2xx 次数，不冒充厂商结果命中次数。</summary>
    public long SuccessCount { get; set; }
    /// <summary>当前事件尚不含业务状态，null 表示未采集，不能当作已统计的零次无匹配。</summary>
    public long? NotFoundCount { get; set; }
    public double TotalElapsedMilliseconds { get; set; }
    public double MinElapsedMilliseconds { get; set; }
    public double MaxElapsedMilliseconds { get; set; }
    /// <summary>固定直方图的桶计数 JSON 数组，不存储各分钟 p95 的平均值。</summary>
    public string LatencyHistogramJson { get; set; } = "[]";
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAtUtc { get; set; }
}
