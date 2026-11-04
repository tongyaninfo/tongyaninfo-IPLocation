using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class AdminAccountConfiguration : IEntityTypeConfiguration<AdminAccount>
{
    public void Configure(EntityTypeBuilder<AdminAccount> builder)
    {
        builder.ToTable("ADMIN_ACCOUNT", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_ADMIN_ACCOUNT_ID_POSITIVE", "\"ID\" > 0");
            table.HasCheckConstraint("CK_ADMIN_ACCOUNT_SECURITY_VERSION", "\"SECURITY_VERSION\" > 0");
            table.HasCheckConstraint("CK_ADMIN_ACCOUNT_REQUIRED_TEXT", "btrim(\"ACCOUNT\") <> '' AND btrim(\"NORMALIZED_ACCOUNT\") <> '' AND btrim(\"PASSWORD_HASH\") <> ''");
        });
        builder.ToTable(table => table.HasComment("管理员账户；禁止默认账户种子和明文密码"));
        builder.HasKey(entity => entity.Id).HasName("PK_ADMIN_ACCOUNT");
        builder.Property(entity => entity.Id).HasColumnName("ID").BusinessId("管理员ID，由统一YiteIdService生成");
        builder.Property(entity => entity.Account).HasColumnName("ACCOUNT").HasMaxLength(128).IsRequired().HasComment("保留显示形式的登录账号");
        builder.Property(entity => entity.NormalizedAccount).HasColumnName("NORMALIZED_ACCOUNT").HasMaxLength(128).IsRequired().HasComment("服务统一规范化后的唯一登录账号");
        builder.Property(entity => entity.DisplayName).HasColumnName("DISPLAY_NAME").HasMaxLength(128).IsRequired().HasComment("管理员显示名");
        builder.Property(entity => entity.PasswordHash).HasColumnName("PASSWORD_HASH").HasMaxLength(1024).IsRequired().HasComment("带算法信息的安全密码散列，不保存明文");
        builder.Property(entity => entity.Enabled).HasColumnName("ENABLED").HasComment("是否允许登录");
        builder.Property(entity => entity.SecurityVersion).HasColumnName("SECURITY_VERSION").IsConcurrencyToken().HasComment("身份安全版本；变更时使已有会话失效");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("CREATED_AT_UTC").Utc("创建时间，UTC");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC").Utc("最后修改时间，UTC");
        builder.HasIndex(entity => entity.NormalizedAccount).IsUnique().HasDatabaseName("UX_ADMIN_ACCOUNT_NORMALIZED_ACCOUNT");
    }
}
