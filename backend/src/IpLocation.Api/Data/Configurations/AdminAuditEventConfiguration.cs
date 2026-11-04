using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class AdminAuditEventConfiguration : IEntityTypeConfiguration<AdminAuditEvent>
{
    public void Configure(EntityTypeBuilder<AdminAuditEvent> builder)
    {
        builder.ToTable("ADMIN_AUDIT_EVENT", IpLocationDbContext.SchemaName, table =>
            table.HasCheckConstraint("CK_ADMIN_AUDIT_EVENT_ID_POSITIVE", "\"ID\" > 0"));
        builder.ToTable(table => table.HasComment("管理操作的可靠审计，不通过可丢弃访问队列写入"));
        builder.HasKey(entity => entity.Id).HasName("PK_ADMIN_AUDIT_EVENT");
        builder.Property(entity => entity.Id).HasColumnName("ID").BusinessId("审计事件ID，由统一YiteIdService生成");
        builder.Property(entity => entity.ActorId).HasColumnName("ACTOR_ID").HasComment("操作管理员ID；身份未知的登录失败可为空");
        builder.Property(entity => entity.SourceAddress).HasColumnName("SOURCE_ADDRESS").HasColumnType("inet").HasComment("经可信代理规则识别的操作来源地址");
        builder.Property(entity => entity.Action).HasColumnName("ACTION").HasMaxLength(64).IsRequired().HasComment("服务固定定义的管理动作");
        builder.Property(entity => entity.TargetType).HasColumnName("TARGET_TYPE").HasMaxLength(64).HasComment("被操作对象类型");
        builder.Property(entity => entity.TargetId).HasColumnName("TARGET_ID").HasMaxLength(128).HasComment("被操作对象标识，不包含秘密");
        builder.Property(entity => entity.DifferenceJson).HasColumnName("DIFFERENCE_JSON").Json("已脱敏的变更差异，不含密码、Token或文件正文");
        builder.Property(entity => entity.Result).HasColumnName("RESULT").HasMaxLength(32).IsRequired().HasComment("管理操作结果");
        builder.Property(entity => entity.TraceId).HasColumnName("TRACE_ID").HasMaxLength(128).IsRequired().HasComment("请求链路关联标识，不充当业务主键");
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("OCCURRED_AT_UTC").Utc("操作发生时间，UTC");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.ActorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_ADMIN_AUDIT_EVENT_ACTOR");
        builder.HasIndex(entity => entity.OccurredAtUtc).HasDatabaseName("IX_ADMIN_AUDIT_EVENT_OCCURRED");
        builder.HasIndex(entity => new { entity.ActorId, entity.OccurredAtUtc }).HasDatabaseName("IX_ADMIN_AUDIT_EVENT_ACTOR_OCCURRED");
    }
}
