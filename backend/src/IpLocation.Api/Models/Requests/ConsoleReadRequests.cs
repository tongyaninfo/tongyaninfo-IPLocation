namespace IpLocation.Api.Models.Requests;

/// <summary>后台读取限制时间范围，不允许无界历史扫描。</summary>
public class ConsoleReadWindowRequest
{
    public DateTimeOffset? FromUtc { get; set; }
    public DateTimeOffset? ToUtc { get; set; }
}

public sealed class ConsoleAccessLogReadRequest : ConsoleReadWindowRequest
{
    public string? SourceAddress { get; set; }
    public string? RouteCategory { get; set; }
    public string? Database { get; set; }
    public string? Format { get; set; }
    public int? StatusCode { get; set; }
    public string? ToolName { get; set; }
    public string? ResultCode { get; set; }
    public string? TraceId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}

public sealed class ConsoleAuditReadRequest : ConsoleReadWindowRequest
{
    public string? Actor { get; set; }
    public string? Action { get; set; }
    public string? Result { get; set; }
    public string? TraceId { get; set; }
    public int Page { get; set; } = 1;
    public int PageSize { get; set; } = 20;
}
