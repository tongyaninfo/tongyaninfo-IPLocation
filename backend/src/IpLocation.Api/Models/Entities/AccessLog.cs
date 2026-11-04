using NpgsqlTypes;
using System.Text.Json.Serialization;

namespace IpLocation.Api.Models.Entities;

/// <summary>公共访问明细；事件键用于重试去重，业务主键由消费者使用统一发号服务生成。</summary>
public sealed class AccessLog
{
    [JsonNumberHandling(JsonNumberHandling.WriteAsString)]
    public long Id { get; set; }
    public string EventKey { get; set; } = string.Empty;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string TraceId { get; set; } = string.Empty;
    public string RouteCategory { get; set; } = string.Empty;
    public string Method { get; set; } = string.Empty;
    public string Protocol { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public string? Database { get; set; }
    public string? DatabaseVersion { get; set; }
    public int StatusCode { get; set; }
    public double ElapsedMilliseconds { get; set; }
    public bool IsCancelled { get; set; }
    /// <summary>inet 兼容完整 IP 与按配置脱敏后的网络前缀，避免丢失 /24 或 /48 的含义。</summary>
    public NpgsqlInet? SourceAddress { get; set; }
    public string ClientCategory { get; set; } = string.Empty;
    /// <summary>有界、非秘密请求头及脱敏请求路径快照；历史未采集记录保持 null。</summary>
    public string? RequestHeadersJson { get; set; }
    /// <summary>后台按日志来源 IP 解析的位置快照；历史未采集记录保持 null。</summary>
    public string? SourceLocationJson { get; set; }
    /// <summary>MCP 协议及工具调用的有界上下文；非 MCP 和历史记录保持 null。</summary>
    public string? McpContextJson { get; set; }
}
