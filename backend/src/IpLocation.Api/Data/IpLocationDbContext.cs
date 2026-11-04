using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace IpLocation.Api.Data;

/// <summary>仅定义模型；构造和普通服务启动都不建库、不迁移、不创建账号。</summary>
public sealed class IpLocationDbContext(DbContextOptions<IpLocationDbContext> options) : DbContext(options)
{
    public const string SchemaName = "iplocation";

    public DbSet<AdminAccount> AdminAccounts => Set<AdminAccount>();
    public DbSet<AdminIpAllowlist> AdminIpAllowlists => Set<AdminIpAllowlist>();
    public DbSet<IpDatabaseVersion> IpDatabaseVersions => Set<IpDatabaseVersion>();
    public DbSet<IpDatabaseActivation> IpDatabaseActivations => Set<IpDatabaseActivation>();
    public DbSet<DatabaseUploadTask> DatabaseUploadTasks => Set<DatabaseUploadTask>();
    public DbSet<AdminAuditEvent> AdminAuditEvents => Set<AdminAuditEvent>();
    public DbSet<AccessLog> AccessLogs => Set<AccessLog>();
    public DbSet<TrafficMinuteAggregate> TrafficMinuteAggregates => Set<TrafficMinuteAggregate>();
    public DbSet<SystemSetting> SystemSettings => Set<SystemSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(SchemaName);
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IpLocationDbContext).Assembly);
    }
}
