using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class AdminIpAllowlistConfiguration : IEntityTypeConfiguration<AdminIpAllowlist>
{
    public void Configure(EntityTypeBuilder<AdminIpAllowlist> builder)
    {
        builder.ToTable("ADMIN_IP_ALLOWLIST", IpLocationDbContext.SchemaName, table =>
            table.HasCheckConstraint("CK_ADMIN_IP_ALLOWLIST_ID_POSITIVE", "\"ID\" > 0"));
        builder.ToTable(table => table.HasComment("管理员入口IP白名单；无启用规则时拒绝访问"));
        builder.HasKey(entity => entity.Id).HasName("PK_ADMIN_IP_ALLOWLIST");
        builder.Property(entity => entity.Id).HasColumnName("ID").BusinessId("白名单规则ID，由统一YiteIdService生成");
        // Npgsql EF Core 10 原生映射 System.Net.IPNetwork 为 cidr，不转成字符串冒充网络类型。
        builder.Property(entity => entity.Network).HasColumnName("NETWORK").HasColumnType("cidr").HasComment("允许网段；精确IPv4/IPv6分别为32/128位前缀");
        builder.Property(entity => entity.Enabled).HasColumnName("ENABLED").HasComment("规则是否启用");
        builder.Property(entity => entity.Note).HasColumnName("NOTE").HasMaxLength(512).HasComment("规则用途说明");
        builder.Property(entity => entity.CreatedById).HasColumnName("CREATED_BY_ID").HasComment("创建管理员ID；受控本地初始化可为空");
        builder.Property(entity => entity.UpdatedById).HasColumnName("UPDATED_BY_ID").HasComment("最后修改管理员ID");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("CREATED_AT_UTC").Utc("创建时间，UTC");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC").Utc("最后修改时间，UTC");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.CreatedById).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ADMIN_ALLOWLIST_CREATED_BY");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.UpdatedById).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ADMIN_ALLOWLIST_UPDATED_BY");
        builder.HasIndex(entity => entity.Enabled).HasDatabaseName("IX_ADMIN_IP_ALLOWLIST_ENABLED");
    }
}
