using IpLocation.Api.Common;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Controllers;

[ApiController]
public sealed class IpController(IIpQueryService queries, IClientIpService clientIp, IOptions<QueryOptions> queryOptions,
    IConsoleRuntimeSettings? runtimeSettings = null) : ControllerBase
{
    [HttpGet("/")]
    public async Task<IActionResult> Root([FromQuery] string? ip, [FromQuery] string? db,
        [FromQuery] string? format, [FromServices] FrontendHost frontend, CancellationToken cancellationToken)
    {
        Response.Headers.Vary = "Accept";
        Response.Headers.CacheControl = "no-store";
        // 只有约定查询键才切换协议；发布号等无关参数仍按原 Accept 协商首页或纯 IP。
        if (!PublicQueryParameters.IsRootQuery(Request))
        {
            if (FrontendHost.PrefersHtml(Request))
            {
                HttpContext.Items["AccessLog.Format"] = "html";
                await frontend.WriteIndexAsync(HttpContext);
                return new EmptyResult();
            }

            HttpContext.Items["AccessLog.Format"] = "plain";
            var address = clientIp.GetClientIp(HttpContext);
            if (address is null)
            {
                Response.StatusCode = 503;
                return Content("无法识别当前请求地址。\n", "text/plain; charset=utf-8");
            }
            // 纯 IP 保持快路径，不调用定位服务，也不等待日志消费者。
            return Content(address + "\n", "text/plain; charset=utf-8");
        }

        format = (format ?? "json").ToLowerInvariant();
        if (PublicQueryParameters.HasInvalidSingleValue(Request.Query, "format") || !QueryFormatter.IsSupported(format))
            return InvalidFormatResponse();
        HttpContext.Items["AccessLog.Format"] = format;
        if (PublicQueryParameters.HasInvalidSingleValue(Request.Query, "ip"))
            return QueryFormatter.Format(new(400, ApiResult<QueryResult>.Fail("INVALID_IP",
                "ip 必须是单个非空的 IPv4 或 IPv6 地址。")), format);

        // 显式 ip 空值已经拒绝；只有完全省略时才允许通过同一可信连接识别自身。
        return await ExecuteQueryAsync(ip, db, format, cancellationToken);
    }

    [HttpGet("/api/ip/me")]
    public Task<IActionResult> Me([FromQuery] string? db, CancellationToken cancellationToken) =>
        ExecuteQueryAsync(null, db, "json", cancellationToken);

    [HttpGet("/search/{ip}")]
    [HttpGet("/search/{ip}/{format}")]
    public async Task<IActionResult> Search([FromRoute] string ip, [FromRoute] string? format,
        [FromQuery] string? db, CancellationToken cancellationToken)
    {
        format ??= "json";
        if (!QueryFormatter.IsSupported(format)) return InvalidFormatResponse();
        return await ExecuteQueryAsync(ip, db, format, cancellationToken);
    }

    [HttpGet("/api/providers")]
    public IActionResult Providers()
    {
        Response.Headers.CacheControl = "no-store";
        return Ok(ApiResult<object>.Ok(queries.GetProviders(), "来源登记不代表已接入或已启用。"));
    }

    private void SetMetadata(string? database, QueryResponse response)
    {
        HttpContext.Items["AccessLog.Database"] = ProviderCodes.Public(database ?? runtimeSettings?.Current.Query.DefaultDatabase ?? queryOptions.Value.DefaultDatabase);
        var versions = response.Result.Data?.Sources.Where(source => source.Version is not null)
            .Select(source => $"{source.Id}:{source.Version}");
        if (versions is not null) HttpContext.Items["AccessLog.Version"] = string.Join(',', versions);
    }

    private async Task<IActionResult> ExecuteQueryAsync(string? ip, string? database, string format,
        CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        HttpContext.Items["AccessLog.Format"] = format;
        // 只有完全省略 db 才使用默认来源，防止 MVC 将显式空值绑定成 null 后静默换库。
        if (PublicQueryParameters.HasInvalidSingleValue(Request.Query, "db")) return InvalidDatabaseResponse(format);
        ip ??= clientIp.GetClientIp(HttpContext)?.ToString();
        if (ip is null)
            return QueryFormatter.Format(new(503, ApiResult<QueryResult>.Fail("CLIENT_IP_UNAVAILABLE",
                "无法识别当前请求地址。")), format);

        var response = await queries.QueryAsync(ip, database, cancellationToken);
        SetMetadata(database, response);
        return QueryFormatter.Format(response, format);
    }

    private IActionResult InvalidFormatResponse()
    {
        Response.Headers.CacheControl = "no-store";
        HttpContext.Items["AccessLog.Format"] = "json";
        return QueryFormatter.Format(new(400, ApiResult<QueryResult>.Fail("INVALID_FORMAT",
            "格式仅支持 json、xml、txt，且只能提供一个非空值。")), "json");
    }

    private static IActionResult InvalidDatabaseResponse(string format) => QueryFormatter.Format(new(400,
        ApiResult<QueryResult>.Fail("UNKNOWN_DATABASE", "db 必须是一个非空的已登记来源标识。")), format);
}
