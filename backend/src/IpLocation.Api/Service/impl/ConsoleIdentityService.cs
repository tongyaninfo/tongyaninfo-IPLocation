using System.Globalization;
using System.Net;
using System.Security.Claims;
using IpLocation.Api.Common;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

public sealed class ConsoleIdentityService(IConsoleIdentityStore store, IPasswordHasher<AdminAccount> passwords,
    IIdService ids, IClientIpService clientIp, IOptions<ConsoleAuthOptions> options,
    TimeProvider time, ConsoleLoginAttemptLimiter attempts, ILogger<ConsoleIdentityService> logger,
    IConsoleRuntimeSettings? runtime = null) : IConsoleIdentityService
{
    // 未知账号也执行一次同算法校验，降低通过响应耗时枚举管理员的机会。
    private static readonly AdminAccount Dummy = new();
    private static readonly string DummyHash = new PasswordHasher<AdminAccount>(Microsoft.Extensions.Options.Options.Create(
        new PasswordHasherOptions { IterationCount = ConsoleAuthentication.PasswordIterations })).HashPassword(Dummy,
        "This is a controlled timing fixture, never an account credential.");

    public async Task<ConsoleOperationResponse<ConsoleAccessResponse>> GetAccessAsync(HttpContext context)
    {
        try
        {
            if (context.Items.ContainsKey(ConsoleAuthentication.StorageFailureKey)) return StorageUnavailable<ConsoleAccessResponse>();
            if (!await store.HasAccountsAsync(context.RequestAborted))
                return AccessFailure(503, "CONSOLE_NOT_INITIALIZED", false, "管理后台尚未初始化。");
            if (context.User.Identity?.IsAuthenticated != true)
                return AccessFailure(401, "CONSOLE_UNAUTHENTICATED", true, "需要管理员身份验证。");

            var account = context.Items[ConsoleAuthentication.AccountKey] as AdminAccount;
            if (account is null || !account.Enabled)
                return AccessFailure(401, "CONSOLE_UNAUTHENTICATED", true, "管理员会话已失效。");
            return new(200, ApiResult<ConsoleAccessResponse>.Ok(Access(account), "已通过管理员身份验证。"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            LogStorageFailure(exception);
            return StorageUnavailable<ConsoleAccessResponse>();
        }
    }

    public async Task<ConsoleOperationResponse<ConsoleAccessResponse>> LoginAsync(HttpContext context, ConsoleLoginRequest request)
    {
        try
        {
            if (!await store.HasAccountsAsync(context.RequestAborted))
                return AccessFailure(503, "CONSOLE_NOT_INITIALIZED", false, "管理后台尚未初始化。");
            var normalized = NormalizeAccount(request.Account);
            var bounded = normalized.Length is > 0 and <= 128 && request.Password is { Length: > 0 and <= 1024 };
            if (!attempts.TryAcquire(normalized.Length is > 0 and <= 128 ? normalized : "invalid"))
            {
                context.Response.Headers.RetryAfter = "300";
                return AccessFailure(429, "CONSOLE_LOGIN_RATE_LIMITED", true, "登录尝试过于频繁，请稍后重试。");
            }
            var account = bounded ? await store.FindByAccountAsync(normalized, context.RequestAborted) : null;
            var verification = passwords.VerifyHashedPassword(account ?? Dummy, account?.PasswordHash ?? DummyHash,
                bounded ? request.Password : "invalid");
            if (account is null || !account.Enabled || verification == PasswordVerificationResult.Failed)
            {
                await store.WriteAuditAsync(Audit(context, "Admin.Login", "denied", account?.Id), context.RequestAborted);
                return AccessFailure(401, "CONSOLE_LOGIN_FAILED", true, "账号或密码不正确。");
            }

            var replacement = verification == PasswordVerificationResult.SuccessRehashNeeded
                ? passwords.HashPassword(account, request.Password) : null;
            if (!await store.CommitLoginAsync(account, replacement, Audit(context, "Admin.Login", "success", account.Id),
                    context.RequestAborted))
            {
                await store.WriteAuditAsync(Audit(context, "Admin.Login", "conflict", account.Id), context.RequestAborted);
                return AccessFailure(401, "CONSOLE_LOGIN_FAILED", true, "账号或密码不正确。");
            }

            // 只有可靠审计提交成功，才签发浏览器会话；不把密码或散列写进票据。
            var identity = new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, account.Id.ToString(CultureInfo.InvariantCulture)),
                new Claim(ClaimTypes.Name, account.Account),
                new Claim(ConsoleAuthentication.SecurityVersionClaim, account.SecurityVersion.ToString(CultureInfo.InvariantCulture))
            ], ConsoleAuthentication.Scheme);
            var now = time.GetUtcNow();
            await context.SignInAsync(ConsoleAuthentication.Scheme, new ClaimsPrincipal(identity), new AuthenticationProperties
            {
                IssuedUtc = now,
                // 已有票据保持原到期时间；新登录使用刚提交的非敏感会话配置。
                ExpiresUtc = now.AddMinutes(runtime?.Current.Basic?.SessionMinutes ?? options.Value.SessionMinutes),
                IsPersistent = false,
                AllowRefresh = false
            });
            return new(200, ApiResult<ConsoleAccessResponse>.Ok(Access(account), "登录成功。"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            LogStorageFailure(exception);
            return StorageUnavailable<ConsoleAccessResponse>();
        }
    }

    public async Task<ConsoleOperationResponse<object>> LogoutAsync(HttpContext context)
    {
        var access = await GetAccessAsync(context);
        if (access.StatusCode != 200)
            return new(access.StatusCode, ApiResult<object>.Fail(access.Result.Code, access.Result.Message));
        try
        {
            var account = (AdminAccount)context.Items[ConsoleAuthentication.AccountKey]!;
            if (!await store.LogoutAsync(account.Id, account.SecurityVersion,
                    Audit(context, "Admin.Logout", "success", account.Id), context.RequestAborted))
                return new(401, ApiResult<object>.Fail("CONSOLE_UNAUTHENTICATED", "管理员会话已失效。"));
            await context.SignOutAsync(ConsoleAuthentication.Scheme);
            return new(200, ApiResult<object>.Ok(new { }, "已退出，当前账号的旧会话均已撤销。"));
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            LogStorageFailure(exception);
            return StorageUnavailable<object>();
        }
    }

    public static string NormalizeAccount(string? account) => (account ?? "").Trim().ToUpperInvariant();

    private static ConsoleAccessResponse Access(AdminAccount account) => new(true, true, true, "管理员会话有效。",
        new(account.Id.ToString(CultureInfo.InvariantCulture), account.Account, account.DisplayName));

    private static ConsoleOperationResponse<ConsoleAccessResponse> AccessFailure(int status, string code, bool initialized,
        string message) => new(status, ApiResult<ConsoleAccessResponse>.Fail(code, message,
        new(true, initialized, false, message, null)));

    public static ConsoleOperationResponse<T> StorageUnavailable<T>() => new(503,
        ApiResult<T>.Fail("CONSOLE_STORAGE_UNAVAILABLE", "管理员持久化暂不可用，请联系管理员检查数据库配置。"));

    private AdminAuditEvent Audit(HttpContext context, string action, string result, long? actorId) => new()
    {
        Id = ids.NewId(), ActorId = actorId, SourceAddress = clientIp.GetClientIp(context), Action = action,
        TargetType = "AdminAccount", TargetId = actorId?.ToString(CultureInfo.InvariantCulture),
        DifferenceJson = "{}", Result = result, TraceId = context.TraceIdentifier, OccurredAtUtc = time.GetUtcNow()
    };

    private void LogStorageFailure(Exception exception) =>
        logger.LogWarning("管理员身份存储操作失败，类型：{ExceptionType}", exception.GetType().Name);
}
