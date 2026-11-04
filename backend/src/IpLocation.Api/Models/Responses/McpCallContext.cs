using System.Text.Json;

namespace IpLocation.Api.Models.Responses;

/// <summary>MCP 调用的有界上下文；客户端名称和版本属于自报信息，不代表已验证身份。</summary>
public sealed record McpCallContext(
    string? ProtocolVersion = null,
    string? Method = null,
    string? RequestId = null,
    string? ToolName = null,
    string? ClientName = null,
    string? ClientVersion = null,
    string? Ip = null,
    string? Database = null,
    string? ResultCode = null,
    string? ResultStatus = null,
    bool IsError = false,
    IReadOnlyList<McpSourceStatus>? Sources = null,
    bool Truncated = false,
    JsonElement? Input = null,
    JsonElement? Output = null,
    bool InputTruncated = false,
    bool OutputTruncated = false,
    bool InputCaptured = false,
    bool OutputCaptured = false);

/// <summary>保留各实际来源的状态和版本，不保存供应商原始响应。</summary>
public sealed record McpSourceStatus(string Id, string? Version, string Status);
