using IpLocation.Api.Service;
using IpLocation.Api.Models.Entities;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace IpLocation.Api.Common;

/// <summary>全部后台业务接口沿用相同身份与防伪边界，HTTP 状态及业务合同保持一致。</summary>
public static class ConsoleManagementGuard
{
    public static async Task<IActionResult?> CheckAsync(HttpContext context, IConsoleIdentityService identities,
        IAntiforgery antiforgery, bool write)
    {
        context.Response.Headers.CacheControl = "no-store";
        var access = await identities.GetAccessAsync(context);
        if (access.StatusCode != 200) return new ObjectResult(access.Result) { StatusCode = access.StatusCode };
        if (!write) return null;
        try
        {
            // 写接口合同只接受显式请求头；缺头时直接拒绝，避免防伪组件为寻找表单令牌预读大型上传正文。
            var header = context.Request.Headers["X-CSRF-TOKEN"];
            if (header.Count != 1 || string.IsNullOrWhiteSpace(header[0]) || header[0]!.Length > 4096)
                throw new AntiforgeryValidationException("缺少有效防伪请求头。");
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            if (!await RecordDeniedAsync(context, "CONSOLE_CSRF_INVALID"))
                return new ObjectResult(ApiResult<object>.Fail("CONSOLE_AUDIT_UNAVAILABLE", "管理审计暂不可用，操作未执行。")) { StatusCode = 503 };
            return new BadRequestObjectResult(ApiResult<object>.Fail("CONSOLE_CSRF_INVALID", "防伪令牌无效，请刷新页面后重试。"));
        }
        return null;
    }

    public static async Task<bool> RecordDeniedAsync(HttpContext context, string reasonCode)
    {
        try
        {
            var actor = context.Items[ConsoleAuthentication.AccountKey] as AdminAccount;
            if (actor is null) return true;
            // 只记录固定原因代码，不存请求体、文件名、路径或未验证输入；与可丢弃队列分离。
            var store = context.RequestServices.GetRequiredService<IConsoleIdentityStore>();
            await store.WriteAuditAsync(new AdminAuditEvent
            {
                Id = context.RequestServices.GetRequiredService<IIdService>().NewId(), ActorId = actor.Id,
                SourceAddress = context.RequestServices.GetRequiredService<IClientIpService>().GetClientIp(context),
                Action = "Management.Denied", TargetType = "ConsoleOperation", Result = "denied",
                TraceId = context.TraceIdentifier, OccurredAtUtc = DateTimeOffset.UtcNow,
                DifferenceJson = System.Text.Json.JsonSerializer.Serialize(new { reasonCode })
            }, context.RequestAborted);
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        { return false; }
    }
}
