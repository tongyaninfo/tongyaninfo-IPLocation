using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class DatabaseUploadTaskConfiguration : IEntityTypeConfiguration<DatabaseUploadTask>
{
    public void Configure(EntityTypeBuilder<DatabaseUploadTask> builder)
    {
        builder.ToTable("DATABASE_UPLOAD_TASK", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_DATABASE_UPLOAD_TASK_ID_POSITIVE", "\"ID\" > 0");
            table.HasCheckConstraint("CK_DATABASE_UPLOAD_TASK_BYTES", "\"RECEIVED_BYTES\" >= 0");
            table.HasCheckConstraint("CK_DATABASE_UPLOAD_TASK_SHA256", "\"SHA256\" IS NULL OR \"SHA256\" ~ '^[0-9a-fA-F]{64}$'");
            table.HasCheckConstraint("CK_DATABASE_UPLOAD_TASK_TIME", "\"FINISHED_AT_UTC\" IS NULL OR \"FINISHED_AT_UTC\" >= \"STARTED_AT_UTC\"");
        });
        builder.ToTable(table => table.HasComment("库文件上传和校验进度；任务成功不代表版本已经激活"));
        builder.HasKey(entity => entity.Id).HasName("PK_DATABASE_UPLOAD_TASK");
        builder.Property(entity => entity.Id).HasColumnName("ID").BusinessId("上传任务ID，由统一YiteIdService生成");
        builder.Property(entity => entity.ProviderCode).HasColumnName("PROVIDER_CODE").HasMaxLength(16).IsRequired().HasComment("目标来源代码");
        builder.Property(entity => entity.State).HasColumnName("STATE").HasMaxLength(32).IsRequired().HasComment("任务受控状态");
        builder.Property(entity => entity.ReceivedBytes).HasColumnName("RECEIVED_BYTES").HasComment("已接收字节数");
        builder.Property(entity => entity.Sha256).HasColumnName("SHA256").HasMaxLength(64).HasComment("接收完成后的文件摘要，未完成可为空");
        builder.Property(entity => entity.VersionId).HasColumnName("VERSION_ID").HasComment("登记的数据版本ID，尚未登记可为空");
        builder.Property(entity => entity.Phase).HasColumnName("PHASE").HasMaxLength(32).IsRequired().HasComment("接收、校验、建索引等任务阶段");
        builder.Property(entity => entity.ErrorCode).HasColumnName("ERROR_CODE").HasMaxLength(64).HasComment("脱敏机器错误码，不保存原始异常");
        builder.Property(entity => entity.OperatorId).HasColumnName("OPERATOR_ID").HasComment("操作管理员ID");
        builder.Property(entity => entity.StartedAtUtc).HasColumnName("STARTED_AT_UTC").Utc("开始时间，UTC");
        builder.Property(entity => entity.FinishedAtUtc).HasColumnName("FINISHED_AT_UTC").Utc("完成或失败时间，UTC");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.OperatorId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_DATABASE_UPLOAD_TASK_OPERATOR");
        builder.HasOne<IpDatabaseVersion>().WithMany().HasForeignKey(entity => new { entity.VersionId, entity.ProviderCode })
            .HasPrincipalKey(entity => new { entity.Id, entity.ProviderCode }).OnDelete(DeleteBehavior.Restrict)
            .HasConstraintName("FK_DATABASE_UPLOAD_TASK_VERSION_PROVIDER");
        builder.HasIndex(entity => new { entity.State, entity.StartedAtUtc }).HasDatabaseName("IX_DATABASE_UPLOAD_TASK_STATE_STARTED");
    }
}
