namespace IpLocation.Api.Models.Requests;

/// <summary>后台限定窗口读取；不接受任意 SQL、排序表达式或无界分页。</summary>
public sealed record AccessLogReadRequest(
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int RecentLimit = 100,
    int MinuteLimit = 1_440);
