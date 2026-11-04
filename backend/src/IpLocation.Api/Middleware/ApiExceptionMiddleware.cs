using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;

namespace IpLocation.Api.Middleware;

/// <summary>公开错误不包含供应商异常、堆栈或运行路径。</summary>
public sealed class ApiExceptionMiddleware(RequestDelegate next, ILogger<ApiExceptionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端已断开，不向已经失效的连接写入伪造完成结果。
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception exception) when (!context.Response.HasStarted)
        {
            logger.LogError("请求处理发生未预期异常，类型：{ExceptionType}", exception.GetType().Name);
            var format = context.GetRouteValue("format")?.ToString() ?? "json";
            if (!QueryFormatter.IsSupported(format)) format = "json";
            var content = QueryFormatter.Format(new QueryResponse(500,
                ApiResult<QueryResult>.Fail("INTERNAL_ERROR", "服务暂时无法完成请求。")), format);
            context.Response.Clear();
            context.Response.StatusCode = content.StatusCode ?? 500;
            context.Response.ContentType = content.ContentType;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.WriteAsync(content.Content ?? "", context.RequestAborted);
        }
    }
}
