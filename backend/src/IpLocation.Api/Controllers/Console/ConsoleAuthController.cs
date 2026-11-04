using IpLocation.Api.Common;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace IpLocation.Api.Controllers.Console;

[ApiController]
[Route("api/console")]
public sealed class ConsoleAuthController(IConsoleIdentityService identities, IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet("access")]
    public async Task<IActionResult> Access()
    {
        var response = await identities.GetAccessAsync(HttpContext);
        return StatusCode(response.StatusCode, response.Result);
    }

    [HttpGet("auth/csrf")]
    public IActionResult Csrf()
    {
        var tokens = antiforgery.GetAndStoreTokens(HttpContext);
        return Ok(ApiResult<object>.Ok(new { token = tokens.RequestToken! }, "防伪令牌已生成。"));
    }

    [HttpPost("auth/login")]
    [EnableRateLimiting("console-login")]
    [RequestSizeLimit(16_384)]
    public async Task<IActionResult> Login(ConsoleLoginRequest request)
    {
        if (!await ValidCsrfAsync()) return CsrfRejected();
        var response = await identities.LoginAsync(HttpContext, request);
        return StatusCode(response.StatusCode, response.Result);
    }

    [HttpPost("auth/logout")]
    public async Task<IActionResult> Logout()
    {
        if (!await ValidCsrfAsync()) return CsrfRejected();
        var response = await identities.LogoutAsync(HttpContext);
        return StatusCode(response.StatusCode, response.Result);
    }

    private async Task<bool> ValidCsrfAsync()
    {
        try
        {
            await antiforgery.ValidateRequestAsync(HttpContext);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            return false;
        }
    }

    private ObjectResult CsrfRejected() => StatusCode(400, ApiResult<object>.Fail("CONSOLE_CSRF_INVALID",
        "防伪令牌无效，请刷新登录页面后重试。"));
}
