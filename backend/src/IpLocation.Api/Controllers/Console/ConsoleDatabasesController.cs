using IpLocation.Api.Common;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;

namespace IpLocation.Api.Controllers.Console;

[ApiController]
[Route("api/console/databases")]
public sealed class ConsoleDatabasesController(DatabaseManagementService databases, IConsoleIdentityService identities,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public Task<IActionResult> List(CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        var rejected = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, false);
        if (rejected is not null) return rejected;
        return Ok(ApiResult<object>.Ok(await databases.GetAsync(cancellationToken)));
    });

    [HttpPost("{id}/state")]
    [RequestSizeLimit(32_768)]
    public Task<IActionResult> State(string id, DatabaseStateRequest request, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        var rejected = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, true);
        if (rejected is not null) return rejected;
        var response = await databases.SetStateAsync(id, request, Actor, HttpContext, cancellationToken);
        await AuditDeniedAsync(id, response, cancellationToken);
        return StatusCode(response.StatusCode, response.Result);
    });

    [HttpPost("order")]
    [RequestSizeLimit(32_768)]
    public Task<IActionResult> Order(DatabaseOrderRequest request, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        var rejected = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, true);
        if (rejected is not null) return rejected;
        var response = await databases.ReorderAsync(request, Actor, HttpContext, cancellationToken);
        if (!response.Result.Success)
            await databases.RecordDeniedAsync("displayOrder", response.Result.Code, HttpContext, cancellationToken, "providerOrder");
        return StatusCode(response.StatusCode, response.Result);
    });

    [HttpPost("{id}/uploads")]
    [RequestSizeLimit(1_073_807_360)]
    [DisableFormValueModelBinding]
    public Task<IActionResult> Upload(string id, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        var rejected = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, true);
        if (rejected is not null) return rejected;
        var response = await databases.UploadAsync(id, Actor, HttpContext, cancellationToken);
        await AuditDeniedAsync(id, response, cancellationToken);
        return StatusCode(response.StatusCode, response.Result);
    });

    [HttpPost("{id}/activate")]
    [RequestSizeLimit(32_768)]
    public Task<IActionResult> Activate(string id, DatabaseActivationRequest request, CancellationToken cancellationToken) => SafeAsync(async () =>
    {
        var rejected = await ConsoleManagementGuard.CheckAsync(HttpContext, identities, antiforgery, true);
        if (rejected is not null) return rejected;
        var response = await databases.ActivateAsync(id, request, Actor, HttpContext, cancellationToken);
        await AuditDeniedAsync(id, response, cancellationToken);
        return StatusCode(response.StatusCode, response.Result);
    });

    private async Task<IActionResult> SafeAsync(Func<Task<IActionResult>> action)
    {
        try { return await action(); }
        catch (Exception exception) when (exception is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            return StatusCode(503, ApiResult<object>.Fail("DATABASE_MANAGEMENT_UNAVAILABLE", "数据库管理存储或运行状态暂不可用，请稍后重试。"));
        }
    }

    private Task AuditDeniedAsync(string id, Models.Responses.DatabaseManagementResponse response, CancellationToken cancellationToken) =>
        response.Result.Success || response.Result.Code == "DATABASE_CHANGE_COMMITTED_REFRESH_PENDING" ? Task.CompletedTask
            : databases.RecordDeniedAsync(id, response.Result.Code, HttpContext, cancellationToken);

    private AdminAccount Actor => (AdminAccount)HttpContext.Items[ConsoleAuthentication.AccountKey]!;
}
