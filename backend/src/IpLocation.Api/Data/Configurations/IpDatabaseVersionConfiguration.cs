using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class IpDatabaseVersionConfiguration : IEntityTypeConfiguration<IpDatabaseVersion>
{
    public void Configure(EntityTypeBuilder<IpDatabaseVersion> builder)
    {
        builder.ToTable("IP_DATABASE_VERSION", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_IP_DB_VERSION_ID_POSITIVE", "\"ID\" > 0");
            table.HasCheckConstraint("CK_IP_DB_VERSION_SIZE", "\"SIZE_BYTES\" >= 0");
            table.HasCheckConstraint("CK_IP_DB_VERSION_SHA256", "\"SHA256\" ~ '^[0-9a-fA-F]{64}$'");
            table.HasCheckConstraint("CK_IP_DB_VERSION_FAMILY", "\"ADDRESS_FAMILY\" IN ('ipv4', 'ipv6', 'dual')");
            table.HasCheckConstraint("CK_IP_DB_VERSION_REQUIRED_TEXT", "btrim(\"PROVIDER_CODE\") <> '' AND btrim(\"VERSION\") <> '' AND btrim(\"FILE_FORMAT\") <> '' AND btrim(\"STORAGE_PATH\") <> '' AND btrim(\"STATUS\") <> ''");
        });
        builder.ToTable(table => table.HasComment("IP数据文件的不可变版本、校验与许可元数据；文件不存入本表"));
        builder.HasKey(entity => entity.Id).HasName("PK_IP_DATABASE_VERSION");
        builder.HasAlternateKey(entity => new { entity.Id, entity.ProviderCode }).HasName("AK_IP_DB_VERSION_ID_PROVIDER");
        builder.Property(entity => entity.Id).HasColumnName("ID").BusinessId("数据版本ID，由统一YiteIdService生成");
        builder.Property(entity => entity.ProviderCode).HasColumnName("PROVIDER_CODE").HasMaxLength(16).IsRequired().HasComment("服务固定登记的来源代码");
        builder.Property(entity => entity.Version).HasColumnName("VERSION").HasMaxLength(128).IsRequired().HasComment("本次文件版本标识");
        builder.Property(entity => entity.FileFormat).HasColumnName("FILE_FORMAT").HasMaxLength(16).IsRequired().HasComment("经校验的文件格式");
        builder.Property(entity => entity.AddressFamily).HasColumnName("ADDRESS_FAMILY").HasMaxLength(8).IsRequired().HasComment("实际覆盖地址族：ipv4、ipv6或dual");
        builder.Property(entity => entity.Sha256).HasColumnName("SHA256").HasMaxLength(64).IsRequired().HasComment("原始文件SHA-256十六进制摘要");
        builder.Property(entity => entity.SizeBytes).HasColumnName("SIZE_BYTES").HasComment("文件字节数");
        builder.Property(entity => entity.StoragePath).HasColumnName("STORAGE_PATH").HasMaxLength(2048).IsRequired().HasComment("仓库外私有存储定位，不对外展示");
        builder.Property(entity => entity.OfficialVersion).HasColumnName("OFFICIAL_VERSION").HasMaxLength(128).HasComment("供应商提供的官方版本，未知为空");
        builder.Property(entity => entity.PublishedOn).HasColumnName("PUBLISHED_ON").HasColumnType("date").HasComment("官方仅提供日期时保留原始日期精度");
        builder.Property(entity => entity.PublishedAtUtc).HasColumnName("PUBLISHED_AT_UTC").Utc("官方提供的精确发布时间，UTC，未知为空");
        builder.Property(entity => entity.OfficialReleaseInfoJson).HasColumnName("OFFICIAL_RELEASE_INFO_JSON").Json("官方发布信息及原始时间精度说明");
        builder.Property(entity => entity.LicenseEvidenceJson).HasColumnName("LICENSE_EVIDENCE_JSON").Json("许可依据及适用范围，不含密钥或Token");
        builder.Property(entity => entity.LicenseConfirmed).HasColumnName("LICENSE_CONFIRMED").HasComment("本项目用途的许可是否已核实");
        builder.Property(entity => entity.Status).HasColumnName("STATUS").HasMaxLength(32).IsRequired().HasComment("接收、校验、就绪或拒绝等受控版本状态");
        builder.Property(entity => entity.CreatedById).HasColumnName("CREATED_BY_ID").HasComment("上传该版本的管理员ID");
        builder.Property(entity => entity.CreatedAtUtc).HasColumnName("CREATED_AT_UTC").Utc("版本登记时间，UTC");
        builder.Property(entity => entity.ValidatedAtUtc).HasColumnName("VALIDATED_AT_UTC").Utc("最后完成校验时间，UTC");
        builder.HasOne<AdminAccount>().WithMany().HasForeignKey(entity => entity.CreatedById).OnDelete(DeleteBehavior.Restrict).HasConstraintName("FK_IP_DB_VERSION_CREATED_BY");
        builder.HasIndex(entity => new { entity.ProviderCode, entity.CreatedAtUtc }).HasDatabaseName("IX_IP_DB_VERSION_PROVIDER_CREATED");
        builder.HasIndex(entity => new { entity.ProviderCode, entity.Sha256 }).HasDatabaseName("IX_IP_DB_VERSION_PROVIDER_SHA256");
    }
}
