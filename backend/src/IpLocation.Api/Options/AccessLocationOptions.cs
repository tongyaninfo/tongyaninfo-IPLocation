namespace IpLocation.Api.Options;

/// <summary>来源定位仅在访问消费者执行；串行有界，每批和每条都有截止时间，不查询其他来源。</summary>
public sealed class AccessLocationOptions
{
    public bool Enabled { get; set; } = true;
    public int BatchTimeoutMilliseconds { get; set; } = 500;
    public int LookupTimeoutMilliseconds { get; set; } = 25;
}
