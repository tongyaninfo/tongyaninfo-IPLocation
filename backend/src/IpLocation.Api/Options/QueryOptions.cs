namespace IpLocation.Api.Options;

/// <summary>所有截止时间与并发均有明确上限，缺库不触发下载。</summary>
public sealed class QueryOptions
{
    public string DefaultDatabase { get; set; } = "dbip";
    public int ProviderTimeoutMs { get; set; } = 2_500;
    public int TotalTimeoutMs { get; set; } = 5_000;
    public int MaxConcurrentSources { get; set; } = 4;
}
