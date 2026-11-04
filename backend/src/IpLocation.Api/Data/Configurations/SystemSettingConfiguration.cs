using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class SystemSettingConfiguration : IEntityTypeConfiguration<SystemSetting>
{
    public void Configure(EntityTypeBuilder<SystemSetting> builder)
    {
        builder.ToTable("SYSTEM_SETTING", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_SYSTEM_SETTING_KEY", "btrim(\"KEY\") <> ''");
            table.HasCheckConstraint("CK_SYSTEM_SETTING_REVISION", "\"REVISION\" > 0");
        });
        builder.ToTable(table => table.HasComment("非敏感系统设置；禁止保存明文密码或供应商Token"));
        builder.HasKey(entity => entity.Key).HasName("PK_SYSTEM_SETTING");
        builder.Property(entity => entity.Key).HasColumnName("KEY").HasMaxLength(128).ValueGeneratedNever().HasComment("服务端固定登记的唯一设置键");
        builder.Property(entity => entity.ValueJson).HasColumnName("VALUE_JSON").Json("经验证的非敏感JSON配置值");
        builder.Property(entity => entity.Revision).HasColumnName("REVISION").IsConcurrencyToken().HasComment("乐观并发版本，初始1，每次成功修改递增");
        builder.Property(entity => entity.UpdatedById).HasColumnName("UPDATED_BY_ID").HasComment("修改管理员ID；受控本地初始化可为空");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC").Utc("最后修改时间，UTC");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.UpdatedById).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_SYSTEM_SETTING_UPDATED_BY");
    }
}
