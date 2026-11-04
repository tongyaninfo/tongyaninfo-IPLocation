using System.Globalization;
using System.Text.Json;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.AspNetCore.Identity;

namespace IpLocation.Api.Common;

/// <summary>仅显式命令执行的本地管理员初始化/凭据重置；不由 Web 启动或普通请求触发。</summary>
public static class AdminInitializationCommand
{
    public static async Task<int> RunAsync(IConfiguration configuration, CancellationToken cancellationToken = default)
    {
        var stage = "validate-input";
        try
        {
            var initialize = configuration.GetValue<bool>("Admin:Initialize");
            var reset = configuration.GetValue<bool>("Admin:Reset");
            if (initialize == reset) throw new InvalidOperationException("必须明确且仅选择一种管理员操作。");
            var accountName = configuration["Admin:Account"]?.Trim() ?? "";
            var existingName = configuration["Admin:ExistingAccount"]?.Trim() ?? "";
            var file = configuration["Admin:PasswordFile"];
            if (accountName.Length is < 1 or > 128 || accountName.Any(char.IsControl) ||
                (reset && (existingName.Length is < 1 or > 128 || existingName.Any(char.IsControl))) ||
                string.IsNullOrWhiteSpace(file) || !Path.IsPathFullyQualified(file))
                throw new InvalidOperationException("初始化需要合法账号和绝对密码文件路径。");
            var info = new FileInfo(file);
            if (!info.Exists || info.LinkTarget is not null || info.Length is < 1 or > 4096)
                throw new InvalidOperationException("初始化密码文件无效。");
            if (!OperatingSystem.IsWindows())
            {
                var mode = File.GetUnixFileMode(file);
                if ((mode & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                             UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
                    throw new InvalidOperationException("初始化密码文件必须仅属主可访问。");
            }
            var password = (await File.ReadAllTextAsync(file, cancellationToken)).TrimEnd('\r', '\n');
            if (password.Length is < 8 or > 1024) throw new InvalidOperationException("初始密码长度必须为8到1024字符。");

            stage = "configure-services";
            var services = new ServiceCollection();
            services.AddSingleton(configuration);
            services.AddLogging();
            services.AddPostgreSqlPersistence(configuration);
            services.AddScoped<IConsoleIdentityStore, PostgreSqlConsoleIdentityStore>();
            services.AddScoped<IPasswordHasher<AdminAccount>, PasswordHasher<AdminAccount>>();
            services.Configure<PasswordHasherOptions>(options => options.IterationCount = ConsoleAuthentication.PasswordIterations);
            await using var provider = services.BuildServiceProvider();
            await using var scope = provider.CreateAsyncScope();
            stage = "resolve-id-service";
            var ids = scope.ServiceProvider.GetRequiredService<IIdService>();
            var now = DateTimeOffset.UtcNow;
            var store = scope.ServiceProvider.GetRequiredService<IConsoleIdentityStore>();
            if (reset)
            {
                stage = "find-existing-admin";
                var existing = await store.FindByAccountAsync(ConsoleIdentityService.NormalizeAccount(existingName), cancellationToken);
                if (existing is null)
                {
                    System.Console.Error.WriteLine("指定的原管理员不存在，未执行重置。");
                    return 2;
                }
                stage = "hash-reset-password";
                var hash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminAccount>>().HashPassword(existing, password);
                var resetAudit = new AdminAuditEvent
                {
                    Id = ids.NewId(), ActorId = existing.Id, Action = "Admin.ResetCredentials", Result = "success",
                    TargetType = "AdminAccount", TargetId = existing.Id.ToString(CultureInfo.InvariantCulture),
                    DifferenceJson = JsonSerializer.Serialize(new
                    {
                        account = new { before = existing.Account, after = accountName }, credentialsReset = true, sessionsRevoked = true
                    }),
                    TraceId = "local-admin-reset", OccurredAtUtc = now
                };
                stage = "commit-reset-and-audit";
                var updated = await store.ResetCredentialsAsync(existing, accountName, hash, resetAudit, cancellationToken);
                System.Console.WriteLine(updated ? "管理员账号、密码及重置审计已提交，旧会话已撤销。" : "账号重名或身份已变更，重置未提交。");
                return updated ? 0 : 2;
            }
            var account = new AdminAccount
            {
                Id = ids.NewId(), Account = accountName, NormalizedAccount = ConsoleIdentityService.NormalizeAccount(accountName),
                DisplayName = accountName, Enabled = true, SecurityVersion = 1, CreatedAtUtc = now, UpdatedAtUtc = now
            };
            stage = "hash-password";
            account.PasswordHash = scope.ServiceProvider.GetRequiredService<IPasswordHasher<AdminAccount>>().HashPassword(account, password);
            var audit = new AdminAuditEvent
            {
                Id = ids.NewId(), ActorId = account.Id, Action = "Admin.Initialize", Result = "success",
                TargetType = "AdminAccount", TargetId = account.Id.ToString(CultureInfo.InvariantCulture),
                DifferenceJson = "{}", TraceId = "local-admin-initialize", OccurredAtUtc = now
            };
            stage = "commit-admin-and-audit";
            var created = await store.InitializeAsync(account, audit, cancellationToken);
            System.Console.WriteLine(created ? "首管理员及初始化审计已提交。" : "已有管理员，拒绝覆盖或重复初始化。");
            return created ? 0 : 2;
        }
        catch (Exception exception)
        {
            // 不打印异常正文、配置或文件内容，避免连接密码或初始凭据进入终端记录。
            var storageStage = exception.Data["ConsoleInitStage"] as string ?? "not-entered";
            System.Console.Error.WriteLine($"管理员本地操作失败（阶段：{stage}/{storageStage}，类型：{exception.GetType().Name}），未报告操作成功。");
            return 1;
        }
    }
}
