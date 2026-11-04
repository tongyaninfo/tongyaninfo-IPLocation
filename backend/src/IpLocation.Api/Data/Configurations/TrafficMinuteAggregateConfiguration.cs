using IpLocation.Api.Models.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace IpLocation.Api.Data.Configurations;

public sealed class TrafficMinuteAggregateConfiguration : IEntityTypeConfiguration<TrafficMinuteAggregate>
{
    public void Configure(EntityTypeBuilder<TrafficMinuteAggregate> builder)
    {
        builder.ToTable("TRAFFIC_MINUTE_AGGREGATE", IpLocationDbContext.SchemaName, table =>
        {
            table.HasCheckConstraint("CK_TRAFFIC_MINUTE_REVISION", "\"REVISION\" > 0");
            table.HasCheckConstraint("CK_TRAFFIC_MINUTE_ALIGNMENT", "date_trunc('minute', \"MINUTE_UTC\" AT TIME ZONE 'UTC') = \"MINUTE_UTC\" AT TIME ZONE 'UTC'");
            table.HasCheckConstraint("CK_TRAFFIC_MINUTE_COUNT_KIND", "\"COUNT_KIND\" IN ('request', 'source')");
            table.HasCheckConstraint("CK_TRAFFIC_MINUTE_COUNTS", "\"TOTAL_REQUESTS\" >= 0 AND \"HOME_HTML_REQUESTS\" >= 0 AND \"PLAIN_IP_REQUESTS\" >= 0 AND \"QUERY_REQUESTS\" >= 0 AND \"CLIENT_ERRORS\" >= 0 AND \"SERVER_ERRORS\" >= 0 AND \"CANCELLED_REQUESTS\" >= 0 AND \"SUCCESS_COUNT\" >= 0 AND (\"NOT_FOUND_COUNT\" IS NULL OR \"NOT_FOUND_COUNT\" >= 0)");
            table.HasCheckConstraint("CK_TRAFFIC_MINUTE_ELAPSED", "\"TOTAL_ELAPSED_MILLISECONDS\" >= 0 AND \"TOTAL_ELAPSED_MILLISECONDS\" < 'Infinity'::double precision AND \"MIN_ELAPSED_MILLISECONDS\" >= 0 AND \"MIN_ELAPSED_MILLISECONDS\" < 'Infinity'::double precision AND \"MAX_ELAPSED_MILLISECONDS\" >= \"MIN_ELAPSED_MILLISECONDS\" AND \"MAX_ELAPSED_MILLISECONDS\" < 'Infinity'::double precision");
            table.HasCheckConstraint("CK_TRAFFIC_MINUTE_HISTOGRAM", "CASE WHEN jsonb_typeof(\"LATENCY_HISTOGRAM_JSON\") = 'array' THEN jsonb_array_length(\"LATENCY_HISTOGRAM_JSON\") = 13 ELSE false END");
        });
        builder.ToTable(table => table.HasComment("UTC分钟固定维度统计；只聚合已去重事件，直方图合并后计算分位数"));
        builder.HasKey(entity => new { entity.MinuteUtc, entity.RouteCategory, entity.Database, entity.Format, entity.CountKind })
            .HasName("PK_TRAFFIC_MINUTE_AGGREGATE");
        builder.Property(entity => entity.MinuteUtc).HasColumnName("MINUTE_UTC").ValueGeneratedNever().Utc("UTC分钟起点，自然复合键的一部分");
        builder.Property(entity => entity.RouteCategory).HasColumnName("ROUTE_CATEGORY").HasMaxLength(32).ValueGeneratedNever().HasComment("服务端固定路由分类");
        builder.Property(entity => entity.Database).HasColumnName("DATABASE").HasMaxLength(16).ValueGeneratedNever().HasComment("固定来源代码、all或无来源时的空串");
        builder.Property(entity => entity.Format).HasColumnName("FORMAT").HasMaxLength(16).ValueGeneratedNever().HasComment("固定响应格式维度");
        builder.Property(entity => entity.CountKind).HasColumnName("COUNT_KIND").HasMaxLength(16).ValueGeneratedNever().HasComment("request为客户端请求，source为实际来源调用");
        builder.Property(entity => entity.TotalRequests).HasColumnName("TOTAL_REQUESTS").HasComment("本维度计数；由CountKind区分请求或来源调用");
        builder.Property(entity => entity.HomeHtmlRequests).HasColumnName("HOME_HTML_REQUESTS").HasComment("首页HTML请求计数");
        builder.Property(entity => entity.PlainIpRequests).HasColumnName("PLAIN_IP_REQUESTS").HasComment("根路径纯IP调用计数");
        builder.Property(entity => entity.QueryRequests).HasColumnName("QUERY_REQUESTS").HasComment("逻辑查询请求计数");
        builder.Property(entity => entity.ClientErrors).HasColumnName("CLIENT_ERRORS").HasComment("HTTP4xx计数");
        builder.Property(entity => entity.ServerErrors).HasColumnName("SERVER_ERRORS").HasComment("HTTP5xx计数");
        builder.Property(entity => entity.CancelledRequests).HasColumnName("CANCELLED_REQUESTS").HasComment("取消或连接中断计数，可与错误状态重叠");
        builder.Property(entity => entity.SuccessCount).HasColumnName("SUCCESS_COUNT").HasComment("HTTP2xx成功计数，不代表数据库命中");
        builder.Property(entity => entity.NotFoundCount).HasColumnName("NOT_FOUND_COUNT").HasComment("业务无匹配计数；尚未采集该状态时为空");
        builder.Property(entity => entity.TotalElapsedMilliseconds).HasColumnName("TOTAL_ELAPSED_MILLISECONDS").HasColumnType("double precision").HasComment("同一口径耗时总和，毫秒");
        builder.Property(entity => entity.MinElapsedMilliseconds).HasColumnName("MIN_ELAPSED_MILLISECONDS").HasColumnType("double precision").HasComment("耗时最小值，毫秒");
        builder.Property(entity => entity.MaxElapsedMilliseconds).HasColumnName("MAX_ELAPSED_MILLISECONDS").HasColumnType("double precision").HasComment("耗时最大值，毫秒");
        builder.Property(entity => entity.LatencyHistogramJson).HasColumnName("LATENCY_HISTOGRAM_JSON").Json("13个固定直方图桶计数；写入端另验证每桶非负和总数");
        builder.Property(entity => entity.Revision).HasColumnName("REVISION").IsConcurrencyToken().HasComment("统计行并发版本，初始1，合并后递增");
        builder.Property(entity => entity.UpdatedAtUtc).HasColumnName("UPDATED_AT_UTC").Utc("最后成功合并时间，UTC");
    }
}
