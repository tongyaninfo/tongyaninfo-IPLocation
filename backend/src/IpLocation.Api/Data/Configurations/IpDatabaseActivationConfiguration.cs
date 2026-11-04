using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class IpDatabaseActivationConfiguration : IEntityTypeConfiguration<IpDatabaseActivation>
{
    public void Configure(EntityTypeBuilder<IpDatabaseActivation> builder)
    {
        builder.ToTable("IP_DATABASE_ACTIVATION", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_IP_DB_ACTIVATION_REVISION", "\"REVISION\" > 0");
            table.HasCheckConstraint("CK_IP_DB_ACTIVATION_FAMILY", "\"ADDRESS_FAMILY\" IN ('ipv4', 'ipv6')");
        });
        builder.ToTable(table => table.HasComment("每来源每地址族的唯一活动版本；切换须核验许可和期望Revision"));
        builder.HasKey(entity => new { entity.ProviderCode, entity.AddressFamily }).HasName("PK_IP_DATABASE_ACTIVATION");
        builder.Property(entity => entity.ProviderCode).HasColumnName("PROVIDER_CODE").HasMaxLength(16).ValueGeneratedNever().HasComment("来源代码，与引用版本保持一致");
        builder.Property(entity => entity.AddressFamily).HasColumnName("ADDRESS_FAMILY").HasMaxLength(8).ValueGeneratedNever().HasComment("此活动指针适用的地址族");
        builder.Property(entity => entity.VersionId).HasColumnName("VERSION_ID").HasComment("当前活动数据版本ID");
        builder.Property(entity => entity.Revision).HasColumnName("REVISION").IsConcurrencyToken().HasComment("乐观并发版本，初始1，每次成功切换递增");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC").Utc("活动指针更新时间，UTC");
        builder.Property(entity => entity.UpdatedById).HasColumnName("UPDATED_BY_ID").HasComment("最近切换管理员ID");
        // 复合外键同时保证版本存在和来源匹配；双栈覆盖与许可仍由激活服务核验。
        builder.HasOne<IpDatabaseVersion>().WithMany().HasForeignKey(entity => new { entity.VersionId, entity.ProviderCode })
            .HasPrincipalKey(entity => new { entity.Id, entity.ProviderCode }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_IP_DB_ACTIVATION_VERSION_PROVIDER");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.UpdatedById).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_IP_DB_ACTIVATION_UPDATED_BY");
    }
}
