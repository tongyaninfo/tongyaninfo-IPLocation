using IpLocation.Api.Common;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Http.Features;

namespace IpLocation.Api.Middleware;

/// <summary>在页面、静态资源及 API 之前执行，同一白名单不能被路由大小写绕过。</summary>
public sealed class ConsoleAccessMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IClientIpService clientIp, IConsoleAccessService access)
    {
        // 文件提供器会折叠目录段；先拒绝有多种解释的路径，避免静态资源在折叠后绕过白名单。
        if (HasAmbiguousPath(context))
        {
            context.Response.Headers.CacheControl = "no-store";
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsJsonAsync(ApiResult<object>.Fail("AMBIGUOUS_PATH", "请求路径包含不允许的目录或分隔符。"));
            return;
        }

        if (IsConsolePath(context.Request.Path))
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!access.IsAllowed(clientIp.GetClientIp(context)))
            {
                // 未通过来源门禁时按不存在处理；不输出后台名称、白名单原因或登录提示。
                // no-store 避免共享缓存把本次隐藏响应返回给后续允许的来源。
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                context.Response.ContentLength = 0;
                return;
            }
        }

        await next(context);
    }

    private static bool HasAmbiguousPath(HttpContext context)
    {
        var path = context.Request.Path.Value ?? "";
        var target = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = string.IsNullOrEmpty(target) ? path : target.Split('?', 2)[0];
        try
        {
            return Ambiguous(path) || Ambiguous(rawPath) || Ambiguous(Uri.UnescapeDataString(rawPath));
        }
        catch (UriFormatException)
        {
            return true;
        }

        static bool Ambiguous(string value) => value.Contains('\\') || value.Contains("//", StringComparison.Ordinal) ||
            value.Contains("%2f", StringComparison.OrdinalIgnoreCase) || value.Contains("%5c", StringComparison.OrdinalIgnoreCase) ||
            value.Split('/').Any(segment => segment is "." or "..");
    }

    private static bool IsConsolePath(PathString path) =>
        path.StartsWithSegments("/console", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/api/console", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWithSegments("/console-assets", StringComparison.OrdinalIgnoreCase) ||
        (path.Value?.StartsWith("/assets/console-", StringComparison.OrdinalIgnoreCase) ?? false);
}
