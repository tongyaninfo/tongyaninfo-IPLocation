using IpLocation.Api.Common;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace IpLocation.Api.Controllers.Console;

[ApiController]
[Route("api/console/settings")]
public sealed class ConsoleSettingsController(IConsoleIdentityService identities, IAntiforgery antiforgery,
    IConsoleSettingsService settings, IConsoleRuntimeSettings runtime, IClientIpService clientIp) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var denied = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, false);
        if (denied is not null) return denied;
        try
        {
            await runtime.ReloadAsync(HttpContext.RequestAborted);
            return Ok(ApiResult<object>.Ok(settings.GetSettings() with { CurrentSourceAddress = clientIp.GetClientIp(HttpContext)?.ToString() }));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        { return StatusCode(503, ApiResult<object>.Fail("CONSOLE_STORAGE_UNAVAILABLE", "系统设置暂时不可读取。")); }
    }

    [HttpPost]
    [RequestSizeLimit(32_768)]
    public async Task<IActionResult> Save(ConsoleSettingsRequest request)
    {
        var denied = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, true);
        if (denied is not null) return denied;
        var response = await settings.SaveAsync(HttpContext, request);
        if (response.StatusCode is >= 400 and < 500 && !await ConsoleManagementGuard.RecordDeniedAsync(HttpContext, response.Result.Code))
            return StatusCode(503, ApiResult<object>.Fail("CONSOLE_AUDIT_UNAVAILABLE", "管理审计暂不可用，操作未执行。"));
        return StatusCode(response.StatusCode, response.Result.Data is null ? response.Result : response.Result with
        { Data = response.Result.Data with { CurrentSourceAddress = clientIp.GetClientIp(HttpContext)?.ToString() } });
    }
}
