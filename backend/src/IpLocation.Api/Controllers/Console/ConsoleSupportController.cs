using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Service;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Tongyan.Support.Client.Models;
using Tongyan.Support.Client.Service;

namespace IpLocation.Api.Controllers.Console;

/// <summary>安装身份与遥测设置沿用后台白名单、会话及防伪边界；公共反馈不开放安装级列表。</summary>
[ApiController, Route("api/console/support")]
public sealed class ConsoleSupportController(ProductSupportClient support, IConsoleIdentityService identities,
    IAntiforgery antiforgery, IConsoleIdentityStore audit, IIdService ids, IClientIpService clientIp) : ControllerBase
{
    [HttpGet("status")]
    public async Task<IActionResult> Status(CancellationToken ct)
    {
        var denied = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, false);
        if (denied is not null) return denied;
        try { return Ok(ApiResult<SupportStatus>.Ok(await support.StatusAsync(true, ct))); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return StatusCode(503, ApiResult<object>.Fail("SUPPORT_IDENTITY_UNAVAILABLE", "安装身份暂不可读取，原身份不会被重置。")); }
    }

    [HttpPut("telemetry")]
    public async Task<IActionResult> Telemetry(TelemetryInput input, CancellationToken ct)
    {
        var denied = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, true);
        if (denied is not null) return denied;
        var intentWritten = false;
        try
        {
            var before = await support.StatusAsync(true, ct);
            // 文件与数据库没有共同事务：先可靠记意图，再写本地状态，最后记实际结果。
            await RecordAuditAsync("intent", before.TelemetryEnabled, input.Enabled, ct);
            intentWritten = true;
            var after = await support.SetTelemetryAsync(input.Enabled, ct);
            await RecordAuditAsync("success", before.TelemetryEnabled, after.TelemetryEnabled, ct);
            return Ok(ApiResult<SupportStatus>.Ok(after));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return StatusCode(503, ApiResult<object>.Fail(intentWritten ? "SUPPORT_CHANGE_UNCONFIRMED" : "SUPPORT_CHANGE_NOT_APPLIED",
                intentWritten ? "遥测设置结果尚未确认，请刷新支持状态核对；原操作意图已记录。" : "支持状态或可靠审计暂不可用，遥测设置未执行。"));
        }
    }

    private Task RecordAuditAsync(string result, bool before, bool after, CancellationToken ct)
    {
        var actor = (AdminAccount)HttpContext.Items[ConsoleAuthentication.AccountKey]!;
        return audit.WriteAuditAsync(new AdminAuditEvent
        {
            Id = ids.NewId(), ActorId = actor.Id, SourceAddress = clientIp.GetClientIp(HttpContext),
            Action = "Support.Telemetry.Update", TargetType = "SupportSetting", TargetId = "telemetryEnabled",
            DifferenceJson = JsonSerializer.Serialize(new { before, after }), Result = result,
            TraceId = HttpContext.TraceIdentifier, OccurredAtUtc = DateTimeOffset.UtcNow
        }, ct);
    }
}
