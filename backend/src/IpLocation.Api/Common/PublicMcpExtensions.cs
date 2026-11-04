using System.Globalization;
using System.Text.Json;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Service.impl;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Protocol;

namespace IpLocation.Api.Common;

/// <summary>公开只读 MCP 与后台身份分离；所有请求采用无会话传输，不读取管理员 Cookie。</summary>
public static class PublicMcpExtensions
{
    public const string ContextKey = "AccessLog.McpContext";
    public const string CaptureEnabledKey = "AccessLog.McpCaptureEnabled";
    public const string CorsPolicy = "PublicMcp";
    public const string AttributionLine = "来源：https://ip.tongyaninfo.com";
    public const string AttributionInstructions = "引用本服务结果时，请在答复末尾附上“" + AttributionLine + "”。本服务来源不替代底层数据库的署名要求。";

    public static bool IsMcpPath(PathString path) => string.Equals(path.Value?.TrimEnd('/'), "/mcp", StringComparison.OrdinalIgnoreCase);

    public static IServiceCollection AddPublicMcp(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddCors(options => options.AddPolicy(CorsPolicy, policy =>
            policy.AllowAnyOrigin().AllowAnyHeader().WithMethods("POST", "OPTIONS")
                .WithExposedHeaders("MCP-Protocol-Version")));
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new Implementation { Name = "tongyan-ip", Version = "1.0.0", Title = "IP 聚合查询" };
            options.ServerInstructions = "公开只读 IP 查询。query_my_ip 返回 MCP 客户端连接出口 IP，不保证是终端用户 IP。地理信息为数据库推断，非精确位置。" + AttributionInstructions;
        }).WithHttpTransport(options =>
        {
            options.SessionMode = HttpServerSessionMode.Stateless;
            // 每个 HTTP 请求独立配置；闭包只关联本次请求，不跨访客缓存上下文。
            options.ConfigureSessionOptions = (http, serverOptions, _) =>
            {
                serverOptions.Filters.Message.OutgoingFilters.Add(next => async (message, cancellationToken) =>
                {
                    if (!ShouldCapture(http)) { await next(message, cancellationToken); return; }
                    var snapshot = ReadContext(http);
                    if (message.JsonRpcMessage is JsonRpcError error)
                        WriteContext(http, snapshot with { ResultCode = "RPC_" + error.Error.Code.ToString(CultureInfo.InvariantCulture),
                            ResultStatus = "protocolError", IsError = true, Output = JsonSerializer.SerializeToElement(error.Error) });
                    else if (message.JsonRpcMessage is JsonRpcResponse response)
                    {
                        var result = JsonSerializer.SerializeToElement(response.Result);
                        var failed = result.ValueKind == JsonValueKind.Object && result.TryGetProperty("isError", out var flag) && flag.ValueKind == JsonValueKind.True;
                        WriteContext(http, snapshot with { ResultCode = snapshot.ResultCode ?? (failed ? "MCP_TOOL_ERROR" : "OK"),
                            ResultStatus = snapshot.ResultStatus ?? (failed ? "toolError" : "success"), IsError = snapshot.IsError || failed,
                            Output = result });
                    }
                    await next(message, cancellationToken);
                });
                return Task.CompletedTask;
            };
        }).WithTools<PublicMcpTools>();
        return services;
    }

    public static bool ShouldCapture(HttpContext http) => http.Items[CaptureEnabledKey] is not false;

    public static McpCallContext ReadContext(HttpContext http) => http.Items[ContextKey] switch
    {
        McpCallContext context => context,
        string json => McpContexts.Read(json) ?? new(),
        _ => new()
    };

    public static void WriteContext(HttpContext http, McpCallContext value)
    {
        // 请求内保存强类型快照，统一在入队前清洗并序列化一次，避免多次复制完整出参。
        if (ShouldCapture(http)) http.Items[ContextKey] = value;
    }
}
