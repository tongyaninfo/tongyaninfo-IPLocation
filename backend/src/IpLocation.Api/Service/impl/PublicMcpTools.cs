using System.ComponentModel;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace IpLocation.Api.Service.impl;

/// <summary>只做 MCP 适配，IP 规范化、特殊用途分类、来源选择和超时均复用既有查询服务。</summary>
[McpServerToolType]
public sealed class PublicMcpTools(IIpQueryService queries, IClientIpService clientIp, IHttpContextAccessor accessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { Encoder = ApiJsonEncoding.Encoder };

    [McpServerTool(Name = "query_ip", Title = "查询指定 IP", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("查询单个 IPv4 或 IPv6 的分类及各来源地理/网络信息。db 默认为 all，也可指定 list_providers 返回的来源 ID。坐标和城市并非精确位置。" + PublicMcpExtensions.AttributionInstructions)]
    public Task<CallToolResult> QueryIpAsync([Description("单个 IPv4 或 IPv6，不接受域名、URL、端口或 CIDR。")] string ip,
        [Description("来源 ID，默认为 all；停用或未就绪来源不会静默替换。")] string db = "all", CancellationToken cancellationToken = default) =>
        QueryAsync(ip, db, cancellationToken);

    [McpServerTool(Name = "query_my_ip", Title = "查询调用方出口 IP", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("识别本次 MCP HTTP 连接的出口 IP 并查询。云端客户端可能返回云服务器出口，不能据此识别终端用户浏览器 IP。" + PublicMcpExtensions.AttributionInstructions)]
    public Task<CallToolResult> QueryMyIpAsync([Description("来源 ID，默认为 all。")] string db = "all", CancellationToken cancellationToken = default) =>
        QueryAsync(clientIp.GetClientIpString(CurrentHttp()), db, cancellationToken);

    [McpServerTool(Name = "list_providers", Title = "获取数据源目录", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("按后台顺序获取已登记来源、启用/就绪状态、IPv4/IPv6 支持、实际版本及署名。登记不代表启用，不包含私有文件路径。" + PublicMcpExtensions.AttributionInstructions)]
    public CallToolResult ListProviders()
    {
        var http = CurrentHttp();
        var providers = queries.GetProviders();
        PublicMcpExtensions.WriteContext(http, PublicMcpExtensions.ReadContext(http) with
        {
            ResultCode = "OK", ResultStatus = "success", Sources = providers.Select(p => new McpSourceStatus(p.Id, p.Version, p.Status)).ToArray()
        });
        return Format(ApiResult<object>.Ok(providers, "来源登记不代表已接入或已启用。"));
    }

    private async Task<CallToolResult> QueryAsync(string? ip, string? database, CancellationToken cancellationToken)
    {
        var http = CurrentHttp();
        QueryResponse response;
        if (ip is null) response = new(503, ApiResult<QueryResult>.Fail("CLIENT_IP_UNAVAILABLE", "无法识别当前请求地址。"));
        else if (string.IsNullOrWhiteSpace(database)) response = new(400, ApiResult<QueryResult>.Fail("UNKNOWN_DATABASE", "db 必须是一个非空的已登记来源标识。"));
        else response = await queries.QueryAsync(ip, database, cancellationToken);
        var data = response.Result.Data;
        http.Items["AccessLog.Database"] = database is null ? null : ProviderCodes.Public(database);
        var snapshot = PublicMcpExtensions.ReadContext(http);
        PublicMcpExtensions.WriteContext(http, snapshot with
        {
            Ip = data?.Ip ?? (IpAddressParser.TryParse(ip, out var address) ? address.ToString() : null),
            Database = database is null ? null : ProviderCodes.Public(database), ResultCode = response.Result.Code,
            ResultStatus = data?.Status ?? "error", IsError = !response.Result.Success,
            Sources = data?.Sources.Select(source => new McpSourceStatus(source.Id, source.Version, source.Status)).ToArray()
        });
        if (data is not null) http.Items["AccessLog.Version"] = string.Join(',', data.Sources.Where(s => s.Version is not null).Select(s => $"{s.Id}:{s.Version}"));
        return Format(response.Result);
    }

    private HttpContext CurrentHttp() => accessor.HttpContext ?? throw new InvalidOperationException("MCP HTTP 请求上下文不可用。");

    private static CallToolResult Format<T>(ApiResult<T> result)
    {
        // 来源只追加到 MCP 响应副本，不修改查询服务原结果或普通 HTTP 的三格式合同。
        // 两种 MCP 内容复用同一份序列化，保持 ApiResult 四字段和单个 JSON 文本回退。
        var attributed = result with { Message = result.Message + "\n" + PublicMcpExtensions.AttributionLine };
        var content = JsonSerializer.SerializeToElement(attributed, JsonOptions);
        return new CallToolResult { IsError = !attributed.Success, StructuredContent = content, Content = [new TextContentBlock { Text = content.GetRawText() }] };
    }
}
