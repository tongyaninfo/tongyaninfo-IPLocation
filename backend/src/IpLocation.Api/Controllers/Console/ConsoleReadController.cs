using IpLocation.Api.Common;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace IpLocation.Api.Controllers.Console;

[ApiController]
[Route("api/console")]
public sealed class ConsoleReadController(IConsoleReadService reads, IConsoleIdentityService identities,
    IAntiforgery antiforgery, ILogger<ConsoleReadController> logger) : ControllerBase
{
    [HttpGet("overview")]
    public Task<IActionResult> Overview([FromQuery] ConsoleReadWindowRequest request) =>
        ReadAsync(() => reads.GetOverviewAsync(request, HttpContext.RequestAborted));

    [HttpGet("access-logs")]
    public Task<IActionResult> AccessLogs([FromQuery] ConsoleAccessLogReadRequest request) =>
        ReadAsync(() => reads.GetAccessLogsAsync(request, HttpContext.RequestAborted));

    [HttpGet("mcp-logs")]
    public Task<IActionResult> McpLogs([FromQuery] ConsoleAccessLogReadRequest request)
    {
        // 独立入口只读取 MCP 公共调用；仍经过相同后台白名单和管理员身份检查。
        request.RouteCategory = "mcp";
        return ReadAsync(() => reads.GetAccessLogsAsync(request, HttpContext.RequestAborted));
    }

    [HttpGet("audits")]
    public Task<IActionResult> Audits([FromQuery] ConsoleAuditReadRequest request) =>
        ReadAsync(() => reads.GetAuditsAsync(request, HttpContext.RequestAborted));

    private async Task<IActionResult> ReadAsync<T>(Func<Task<T>> operation)
    {
        Response.Headers.CacheControl = "no-store";
        var rejected = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, false);
        if (rejected is not null) return rejected;
        try { return Ok(ApiResult<T>.Ok(await operation(), "后台数据读取完成。")); }
        catch (ConsoleReadValidationException exception)
        {
            return BadRequest(ApiResult<object>.Fail("CONSOLE_READ_INVALID", exception.Message));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            // 不向接口或应用日志输出连接字符串、SQL及服务器文件位置。
            logger.LogWarning("后台数据读取失败，类型：{ExceptionType}", exception.GetType().Name);
            return StatusCode(503, ApiResult<object>.Fail("CONSOLE_STORAGE_UNAVAILABLE", "后台持久化暂不可用，请稍后重试。"));
        }
    }
}
