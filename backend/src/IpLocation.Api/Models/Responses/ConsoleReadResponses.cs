using System.Text.Json;

namespace IpLocation.Api.Models.Responses;

/// <summary>请求级统计；空延迟表示没有样本或不能用有限直方图上界表达。</summary>
public sealed record ConsoleOverviewTotals(long Requests, long HomeHtmlRequests, long PlainIpRequests,
    long QueryRequests, long ClientErrors, long ServerErrors, long CancelledRequests,
    double? AverageMilliseconds, double? P95Milliseconds);

public sealed record ConsoleTrafficPoint(DateTimeOffset TimeUtc, long Requests, long QueryRequests, long Errors);

/// <summary>MCP 协议请求与定位查询分开计数；HTTP 错误不代表 HTTP 200 内的业务结果。</summary>
public sealed record ConsoleMcpOverviewTotals(long Requests, long QueryRequests, long HttpErrors);

public sealed record ConsoleAuditEntry(string Id, DateTimeOffset OccurredAtUtc, string? Actor,
    string? SourceAddress, string Action, string? TargetType, string? TargetId, string Result,
    string TraceId, IReadOnlyDictionary<string, JsonElement> Difference);

public sealed record ConsoleOverviewResponse(DateTimeOffset FromUtc, DateTimeOffset ToUtc,
    DateTimeOffset CapturedAtUtc, string StorageKind, ConsoleOverviewTotals Totals,
    IReadOnlyList<ConsoleTrafficPoint> Trend, AccessQueueMetrics Queue, DateTimeOffset StartedAtUtc,
    int RetentionDays, IReadOnlyList<string> Notes, IReadOnlyList<ConsoleAuditEntry> RecentAudits,
    ConsoleMcpOverviewTotals? McpTotals = null, IReadOnlyList<ConsoleTrafficPoint>? McpTrend = null);

public sealed record ConsoleAccessLogEntry(string Id, DateTimeOffset OccurredAtUtc, string? SourceAddress,
    string RouteCategory, string Method, string Format, string? Database, string? DatabaseVersion,
    int StatusCode, double ElapsedMilliseconds, bool IsCancelled, string ClientCategory, string TraceId,
    RequestHeadersSnapshot? RequestHeaders = null, AccessSourceLocation? SourceLocation = null,
    McpCallContext? McpContext = null)
{
    /// <summary>实际请求路径和查询串，凭据参数值已脱敏；历史未采集保持 null。</summary>
    public string? RequestPath => RequestHeaders?.RequestPath;
    public bool RequestPathTruncated => RequestHeaders?.RequestPathTruncated ?? false;
}

public sealed record ConsoleAccessLogPage(IReadOnlyList<ConsoleAccessLogEntry> Items, long Total,
    int Page, int PageSize, DateTimeOffset FromUtc, DateTimeOffset ToUtc, int? RetentionDays, AccessQueueMetrics Queue);

public sealed record ConsoleAuditPage(IReadOnlyList<ConsoleAuditEntry> Items, long Total, int Page, int PageSize);
