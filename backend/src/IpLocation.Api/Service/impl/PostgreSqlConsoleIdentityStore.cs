using System.Data;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Service;
using Microsoft.EntityFrameworkCore;

namespace IpLocation.Api.Service.impl;

/// <summary>每次操作独立上下文；身份变更和审计同事务提交，失败时不会假报成功。</summary>
public sealed class PostgreSqlConsoleIdentityStore(IServiceProvider services) : IConsoleIdentityStore
{
    private IDbContextFactory<IpLocationDbContext> Factory =>
        services.GetService<IDbContextFactory<IpLocationDbContext>>() ??
        throw new InvalidOperationException("管理员持久化尚未配置。");

    public async Task<bool> HasAccountsAsync(CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        return await db.AdminAccounts.AnyAsync(cancellationToken);
    }

    public async Task<AdminAccount?> FindByAccountAsync(string normalizedAccount, CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        return await db.AdminAccounts.AsNoTracking().SingleOrDefaultAsync(
            account => account.NormalizedAccount == normalizedAccount, cancellationToken);
    }

    public async Task<AdminAccount?> FindByIdAsync(long id, CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        return await db.AdminAccounts.AsNoTracking().SingleOrDefaultAsync(account => account.Id == id, cancellationToken);
    }

    public async Task WriteAuditAsync(AdminAuditEvent audit, CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        db.AdminAuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> CommitLoginAsync(AdminAccount expectedAccount, string? replacementHash, AdminAuditEvent audit,
        CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var account = await LockAccountAsync(db, expectedAccount.Id, cancellationToken);
        if (account is null || !account.Enabled || account.SecurityVersion != expectedAccount.SecurityVersion ||
            account.PasswordHash != expectedAccount.PasswordHash) return false;
        if (replacementHash is not null)
        {
            account.PasswordHash = replacementHash;
            account.UpdatedAtUtc = audit.OccurredAtUtc;
        }
        db.AdminAuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> LogoutAsync(long accountId, long expectedVersion, AdminAuditEvent audit,
        CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var account = await LockAccountAsync(db, accountId, cancellationToken);
        if (account is null || !account.Enabled || account.SecurityVersion != expectedVersion) return false;
        // 退出撤销该账号的所有旧票据，旧 Cookie 副本也不能继续使用。
        account.SecurityVersion = checked(account.SecurityVersion + 1);
        account.UpdatedAtUtc = audit.OccurredAtUtc;
        db.AdminAuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> InitializeAsync(AdminAccount account, AdminAuditEvent audit, CancellationToken cancellationToken)
    {
        var stage = "create-context";
        try
        {
            await using var db = await Factory.CreateDbContextAsync(cancellationToken);
            stage = "begin-transaction";
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
            // 首管理员初始化跨进程串行；锁随事务释放，不新增锁表或永久标记。
            stage = "acquire-initialization-lock";
            await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(4933277946142571)", cancellationToken);
            stage = "check-existing-admin";
            if (await db.AdminAccounts.AnyAsync(cancellationToken)) return false;
            stage = "save-admin-and-audit";
            db.AdminAccounts.Add(account);
            db.AdminAuditEvents.Add(audit);
            await db.SaveChangesAsync(cancellationToken);
            stage = "commit-transaction";
            await transaction.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            exception.Data["ConsoleInitStage"] = stage;
            throw;
        }
    }

    public async Task<bool> ResetCredentialsAsync(AdminAccount expectedAccount, string accountName, string passwordHash,
        AdminAuditEvent audit, CancellationToken cancellationToken)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var account = await LockAccountAsync(db, expectedAccount.Id, cancellationToken);
        // 与登录、退出共用身份行锁；检查读取快照，避免覆盖刚发生的其他身份变更。
        if (account is null || account.SecurityVersion != expectedAccount.SecurityVersion ||
            account.NormalizedAccount != expectedAccount.NormalizedAccount || account.PasswordHash != expectedAccount.PasswordHash)
            return false;
        var normalized = ConsoleIdentityService.NormalizeAccount(accountName);
        if (await db.AdminAccounts.AnyAsync(other => other.Id != account.Id && other.NormalizedAccount == normalized, cancellationToken))
            return false;

        account.Account = accountName;
        account.NormalizedAccount = normalized;
        account.DisplayName = accountName;
        account.PasswordHash = passwordHash;
        account.SecurityVersion = checked(account.SecurityVersion + 1);
        account.UpdatedAtUtc = audit.OccurredAtUtc;
        // 账号原ID、创建时间、启用状态保留；重名并发仍由已有唯一约束兜底，异常将回滚整个事务。
        db.AdminAuditEvents.Add(audit);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    private static async Task<AdminAccount?> LockAccountAsync(IpLocationDbContext db, long id,
        CancellationToken cancellationToken)
    {
        // 参数化查询锁定身份行，防止验证密码期间被禁用、退出或更改安全版本。
        var accounts = await db.AdminAccounts.FromSqlInterpolated(
            $"SELECT * FROM iplocation.\"ADMIN_ACCOUNT\" WHERE \"ID\" = {id} FOR UPDATE")
            .ToListAsync(cancellationToken);
        return accounts.SingleOrDefault();
    }
}
