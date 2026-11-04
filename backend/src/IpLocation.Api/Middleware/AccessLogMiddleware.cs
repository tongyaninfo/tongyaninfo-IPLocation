using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http.Features;

namespace IpLocation.Api.Middleware;

/// <summary>可信转发头处理后使用；请求侧采集脱敏路径及有界元数据，不等待日志写入。</summary>
public sealed class AccessLogMiddleware
{
    private static readonly HashSet<string> DatabaseIds = new(StringComparer.OrdinalIgnoreCase)
        { "dbip", "ipinfo", "ip2region", ProviderCodes.Cz88, ProviderCodes.LegacyChunzhen, "geolite2", "ip2location", "all" };
    private readonly RequestDelegate _next;
    private readonly AccessLogOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly IConsoleRuntimeSettings? _runtime;

    public AccessLogMiddleware(RequestDelegate next, IOptions<AccessLogOptions> options, TimeProvider timeProvider,
        IConsoleRuntimeSettings? runtime = null)
    {
        _next = next;
        _options = options.Value;
        _timeProvider = timeProvider;
        _runtime = runtime;
    }

    public async Task InvokeAsync(HttpContext context, IAccessEventQueue queue)
    {
        var path = context.Request.Path;
        var isRoot = path == "/";
        var isRootQuery = PublicQueryParameters.IsRootQuery(context.Request);
        // TXT 查询仍属于定位查询；空/重复 ip 的失败请求也不能计作访问者自身查询。
        var hasExplicitIp = isRootQuery && context.Request.Query.ContainsKey("ip");
        var isQuery = path.StartsWithSegments("/search") || hasExplicitIp;
        var isSelfQuery = path.Equals(new PathString("/api/ip/me")) || (isRootQuery && !hasExplicitIp);
        var isProviderMetadata = path.Equals(new PathString("/api/providers"));
        var isMcp = PublicMcpExtensions.IsMcpPath(path);
        if (isMcp) context.Response.Headers.CacheControl = "no-store";
        var loggingEnabled = ConsoleBasicSettingsRules.IsAccessLoggingEnabled(_runtime, _options);
        if (isMcp) context.Items[PublicMcpExtensions.CaptureEnabledKey] = loggingEnabled;
        if (!loggingEnabled || (!isRoot && !isQuery && !isSelfQuery && !isProviderMetadata && !isMcp))
        {
            await _next(context);
            return;
        }

        queue.RecordReceived();
        // 在下游路由/静态文件重写之前保留原始编码，查询串不经字典重建，重复参数和顺序保持。
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var requestPath = rawTarget?.StartsWith('/') == true ? rawTarget
            : context.Request.PathBase.ToUriComponent() + context.Request.Path.ToUriComponent() + context.Request.QueryString.ToUriComponent();
        var started = Stopwatch.GetTimestamp();
        var occurred = _timeProvider.GetUtcNow();
        var failed = false;
        try
        {
            await _next(context);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            // 保存关闭策略后，尚未完成的旧请求也不再复制诊断头或新增排队事件。
            if (ConsoleBasicSettingsRules.IsAccessLoggingEnabled(_runtime, _options))
            {
                var format = ResolveFormat(context);
                var category = isMcp ? "mcp" : isSelfQuery ? "selfQuery" : isQuery ? "query"
                    : isRoot ? (format == "html" ? "homeHtml" : "plainIp") : "providerMetadata";
                var database = context.Items["AccessLog.Database"] as string;
                if (database is null && (isQuery || isSelfQuery) && context.Request.Query["db"].Count == 1)
                    database = context.Request.Query["db"][0];
                database = database is not null && DatabaseIds.Contains(database) ? ProviderCodes.Public(database.ToLowerInvariant()) : null;
                var status = isMcp && context.RequestAborted.IsCancellationRequested ? 499
                    : failed && !context.Response.HasStarted ? 500 : context.Response.StatusCode;
                var requestHeaders = AccessRequestHeaders.Capture(context.Request.Headers, requestPath);
                // TryEnqueue 立即返回；队列满的计数由队列实现负责，不影响原响应。
                queue.TryEnqueue(new AccessEvent(occurred, Limit(context.TraceIdentifier, 128), category,
                    Limit(context.Request.Method, 16), Limit(context.Request.Protocol, 16), format, database,
                    LimitOrNull(context.Items["AccessLog.Version"] as string, 128), status,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds, context.RequestAborted.IsCancellationRequested,
                    FormatSourceAddress(context.Connection.RemoteIpAddress), ResolveClientCategory(context),
                    requestHeaders, McpContextJson: isMcp ? McpContexts.Write(PublicMcpExtensions.ReadContext(context)) : null));
            }
        }
    }

    private static string ResolveFormat(HttpContext context)
    {
        var configured = context.Items["AccessLog.Format"] as string;
        if (configured == "plain") return "ip";
        if (configured is "json" or "xml" or "txt" or "html" or "ip") return configured;
        var contentType = context.Response.ContentType ?? string.Empty;
        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)) return "html";
        if (contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (contentType.StartsWith("text/event-stream", StringComparison.OrdinalIgnoreCase)) return "sse";
        if (contentType.StartsWith("application/xml", StringComparison.OrdinalIgnoreCase) || contentType.StartsWith("text/xml", StringComparison.OrdinalIgnoreCase)) return "xml";
        if (contentType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)) return "txt";
        return "unknown";
    }

    private string? FormatSourceAddress(IPAddress? address)
    {
        if (address is null) return null;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (_runtime?.Current.Basic?.RetainFullSourceIp ?? _options.RetainFullSourceIp) return address.ToString();
        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            bytes[3] = 0;
            return new IPAddress(bytes) + "/24";
        }
        Array.Clear(bytes, 6, bytes.Length - 6);
        return new IPAddress(bytes) + "/48";
    }

    private static string ResolveClientCategory(HttpContext context)
    {
        // 只输出粗分类，不保存完整 UA；UA 不参与响应格式或访问权限判断。
        var agent = context.Request.Headers.UserAgent.ToString();
        var sample = agent.AsSpan(0, Math.Min(agent.Length, 128));
        if (sample.Contains("curl/", StringComparison.OrdinalIgnoreCase)) return "curl";
        if (sample.Contains("Wget/", StringComparison.OrdinalIgnoreCase)) return "wget";
        if (sample.Contains("Mozilla/", StringComparison.OrdinalIgnoreCase)) return "browser";
        return "other";
    }

    private static string Limit(string value, int length) => value.Length <= length ? value : value[..length];
    private static string? LimitOrNull(string? value, int length) => value is null ? null : Limit(value, length);
}
