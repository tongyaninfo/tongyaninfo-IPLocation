using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class AccessLogConfiguration : IEntityTypeConfiguration<AccessLog>
{
    public void Configure(EntityTypeBuilder<AccessLog> builder)
    {
        builder.ToTable("ACCESS_LOG", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_ACCESS_LOG_ID_POSITIVE", "\"ID\" > 0");
            table.HasCheckConstraint("CK_ACCESS_LOG_EVENT_KEY", "\"EVENT_KEY\" ~ '^[0-9a-fA-F]{64}$'");
            table.HasCheckConstraint("CK_ACCESS_LOG_STATUS_CODE", "\"STATUS_CODE\" BETWEEN 100 AND 599");
            table.HasCheckConstraint("CK_ACCESS_LOG_ELAPSED", "\"ELAPSED_MILLISECONDS\" >= 0 AND \"ELAPSED_MILLISECONDS\" < 'Infinity'::double precision");
        });
        builder.ToTable(table => table.HasComment("公共访问明细；完整来源IP仅用于获准后台读取，不保存查询目标或完整URL"));
        builder.HasKey(entity => entity.Id).HasName("PK_ACCESS_LOG");
        builder.Property(entity => entity.Id).HasColumnName("ID").BusinessId("访问记录ID，由后台消费者使用统一YiteIdService生成");
        builder.Property(entity => entity.EventKey).HasColumnName("EVENT_KEY").HasMaxLength(64).IsRequired().HasComment("稳定事件摘要，批次重试通过唯一约束去重");
        builder.Property(entity => entity.OccurredAtUtc).HasColumnName("OCCURRED_AT_UTC").Utc("请求开始时间，UTC");
        builder.Property(entity => entity.TraceId).HasColumnName("TRACE_ID").HasMaxLength(128).IsRequired().HasComment("请求链路标识，不作为主键");
        builder.Property(entity => entity.RouteCategory).HasColumnName("ROUTE_CATEGORY").HasMaxLength(32).IsRequired().HasComment("服务端固定路由类别，不保存原始路径");
        builder.Property(entity => entity.Method).HasColumnName("METHOD").HasMaxLength(16).IsRequired().HasComment("HTTP方法");
        builder.Property(entity => entity.Protocol).HasColumnName("PROTOCOL").HasMaxLength(16).IsRequired().HasComment("HTTP协议版本");
        builder.Property(entity => entity.Format).HasColumnName("FORMAT").HasMaxLength(16).IsRequired().HasComment("实际响应格式");
        builder.Property(entity => entity.Database).HasColumnName("DATABASE").HasMaxLength(16).HasComment("固定来源代码或all；纯IP请求可为空");
        builder.Property(entity => entity.DatabaseVersion).HasColumnName("DATABASE_VERSION").HasMaxLength(128).HasComment("实际查询的数据版本摘要");
        builder.Property(entity => entity.StatusCode).HasColumnName("STATUS_CODE").HasComment("HTTP响应状态码");
        builder.Property(entity => entity.ElapsedMilliseconds).HasColumnName("ELAPSED_MILLISECONDS").HasColumnType("double precision").HasComment("请求耗时，毫秒");
        builder.Property(entity => entity.IsCancelled).HasColumnName("IS_CANCELLED").HasComment("客户端是否取消或断开");
        builder.Property(entity => entity.SourceAddress).HasColumnName("SOURCE_ADDRESS").HasColumnType("inet").HasComment("获准保存的完整IP；关闭完整留存时可保存脱敏网段");
        builder.Property(entity => entity.ClientCategory).HasColumnName("CLIENT_CATEGORY").HasMaxLength(16).IsRequired().HasComment("客户端粗分类，不保存完整User-Agent");
        builder.Property(entity => entity.RequestHeadersJson).HasColumnName("REQUEST_HEADERS_JSON").HasColumnType("jsonb").HasComment("经允许名单清洗及限额的请求头快照；历史未采集为空");
        builder.Property(entity => entity.SourceLocationJson).HasColumnName("SOURCE_LOCATION_JSON").HasColumnType("jsonb").HasComment("后台解析日志来源IP的位置快照；历史未采集为空");
        builder.Property(entity => entity.McpContextJson).HasColumnName("MCP_CONTEXT_JSON").HasColumnType("jsonb").HasComment("经白名单清洗及限额的MCP调用上下文；非MCP和历史记录为空");
        builder.HasIndex(entity => entity.EventKey).IsUnique().HasDatabaseName("UX_ACCESS_LOG_EVENT_KEY");
        builder.HasIndex(entity => entity.OccurredAtUtc).HasDatabaseName("IX_ACCESS_LOG_OCCURRED");
        builder.HasIndex(entity => new { entity.RouteCategory, entity.OccurredAtUtc }).HasDatabaseName("IX_ACCESS_LOG_ROUTE_OCCURRED");
        builder.HasIndex(entity => new { entity.Database, entity.OccurredAtUtc }).HasDatabaseName("IX_ACCESS_LOG_DATABASE_OCCURRED");
    }
}
