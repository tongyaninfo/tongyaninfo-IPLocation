using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Common;

public static class ConsoleAuthentication
{
    public const string Scheme = "ConsoleAdmin";
    public const int PasswordIterations = 210_000;
    public const string SecurityVersionClaim = "console_security_version";
    public const string AccountKey = "Console.ValidatedAccount";
    public const string StorageFailureKey = "Console.StorageFailure";

    public static IServiceCollection AddConsoleAuthentication(this IServiceCollection services, IConfiguration configuration,
        IHostEnvironment environment)
    {
        services.AddOptions<ConsoleAuthOptions>().Bind(configuration.GetSection(ConsoleAuthOptions.SectionName))
            .Validate(options => options.SessionMinutes is >= 5 and <= 1440, "管理员会话时长必须为5到1440分钟。")
            .ValidateOnStart();
        var keyPath = configuration[$"{ConsoleAuthOptions.SectionName}:KeyRingPath"];
        var protection = services.AddDataProtection().SetApplicationName("Tongyan.IpLocation.Console.v1");
        if (!string.IsNullOrWhiteSpace(keyPath))
            protection.PersistKeysToFileSystem(new DirectoryInfo(Path.GetFullPath(keyPath, environment.ContentRootPath)));
        else protection.UseEphemeralDataProtectionProvider();
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddScoped<IPasswordHasher<AdminAccount>, PasswordHasher<AdminAccount>>();
        services.Configure<PasswordHasherOptions>(options => options.IterationCount = PasswordIterations);
        services.AddScoped<IConsoleIdentityStore, PostgreSqlConsoleIdentityStore>();
        services.AddScoped<IConsoleIdentityService, ConsoleIdentityService>();
        services.AddSingleton<ConsoleLoginAttemptLimiter>();
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "Iplocation.Console.Csrf";
            options.Cookie.Path = "/api/console";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });
        services.AddAuthentication(Scheme).AddCookie(Scheme, options =>
        {
            options.Cookie.Name = "Iplocation.Console.Auth";
            options.Cookie.Path = "/api/console";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Strict;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.SlidingExpiration = false;
            options.Events = new CookieAuthenticationEvents
            {
                OnRedirectToLogin = context => JsonAuthenticationFailure(context.HttpContext, 401, "CONSOLE_UNAUTHENTICATED"),
                OnRedirectToAccessDenied = context => JsonAuthenticationFailure(context.HttpContext, 403, "CONSOLE_FORBIDDEN"),
                OnValidatePrincipal = async context =>
                {
                    var idText = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                    var versionText = context.Principal?.FindFirstValue(SecurityVersionClaim);
                    if (!long.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ||
                        !long.TryParse(versionText, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
                    {
                        context.RejectPrincipal();
                        return;
                    }
                    try
                    {
                        var store = context.HttpContext.RequestServices.GetRequiredService<IConsoleIdentityStore>();
                        var account = await store.FindByIdAsync(id, context.HttpContext.RequestAborted);
                        if (account is null || !account.Enabled || account.SecurityVersion != version)
                            context.RejectPrincipal();
                        else context.HttpContext.Items[AccountKey] = account;
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException ||
                                                       !context.HttpContext.RequestAborted.IsCancellationRequested)
                    {
                        context.HttpContext.Items[StorageFailureKey] = true;
                        context.RejectPrincipal();
                    }
                }
            };
        });
        services.AddAuthorization();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("support-feedback", context => RateLimitPartition.GetFixedWindowLimiter(
                context.RequestServices.GetRequiredService<IClientIpService>().GetClientIp(context)?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            // 仅对登录入口限流；分区使用已处理的可信地址，不能用客户端伪造头新建分区。
            options.AddPolicy("console-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.RequestServices.GetRequiredService<IClientIpService>().GetClientIp(context)?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                context.HttpContext.Response.StatusCode = 429;
                context.HttpContext.Response.Headers.RetryAfter = "60";
                var support = context.HttpContext.Request.Path.StartsWithSegments("/api/support");
                await context.HttpContext.Response.WriteAsJsonAsync(ApiResult<object>.Fail(support ? "FEEDBACK_RATE_LIMITED" : "CONSOLE_LOGIN_RATE_LIMITED",
                    support ? "反馈请求过于频繁，请稍后重试。" : "登录尝试过于频繁，请稍后重试。"), cancellationToken);
            };
        });
        return services;
    }

    private static Task JsonAuthenticationFailure(HttpContext context, int status, string code)
    {
        context.Response.StatusCode = status;
        return context.Response.WriteAsJsonAsync(ApiResult<object>.Fail(code, "管理员身份无效或权限不足。"));
    }
}
