using System.Text.Json;

namespace IpLocation.Api.Models.Responses;

/// <summary>有界、可读的 MCP 入出参快照；null 表示未采集，与已采集的空对象或 JSON null 区分。</summary>
public sealed record McpPayloadSnapshot(JsonElement? Value, bool Truncated);
