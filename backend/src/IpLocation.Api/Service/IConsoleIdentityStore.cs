using IpLocation.Api.Models.Entities;

namespace IpLocation.Api.Service;

/// <summary>管理员身份与审计直接使用可靠存储，禁止回退普通访问记录队列。</summary>
public interface IConsoleIdentityStore
{
    Task<bool> HasAccountsAsync(CancellationToken cancellationToken);
    Task<AdminAccount?> FindByAccountAsync(string normalizedAccount, CancellationToken cancellationToken);
    Task<AdminAccount?> FindByIdAsync(long id, CancellationToken cancellationToken);
    Task WriteAuditAsync(AdminAuditEvent audit, CancellationToken cancellationToken);
    Task<bool> CommitLoginAsync(AdminAccount expectedAccount, string? replacementHash, AdminAuditEvent audit,
        CancellationToken cancellationToken);
    Task<bool> LogoutAsync(long accountId, long expectedVersion, AdminAuditEvent audit, CancellationToken cancellationToken);
    Task<bool> InitializeAsync(AdminAccount account, AdminAuditEvent audit, CancellationToken cancellationToken);
    Task<bool> ResetCredentialsAsync(AdminAccount expectedAccount, string accountName, string passwordHash,
        AdminAuditEvent audit, CancellationToken cancellationToken);
}
