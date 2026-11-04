using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace IpLocation.Api.Data.Configurations;

/// <summary>统一列映射小工具，不注册独立发号器或数据库默认值。</summary>
internal static class MappingHelpers
{
    private static readonly ValueConverter<DateTimeOffset, DateTimeOffset> UtcConverter =
        new(value => value.ToUniversalTime(), value => value.ToUniversalTime());

    public static PropertyBuilder<long> BusinessId(this PropertyBuilder<long> property, string comment) =>
        property.HasColumnType("bigint").ValueGeneratedNever().HasComment(comment);

    public static PropertyBuilder<DateTimeOffset> Utc(this PropertyBuilder<DateTimeOffset> property, string comment) =>
        property.HasColumnType("timestamp with time zone").HasConversion(UtcConverter).HasComment(comment);

    public static PropertyBuilder<DateTimeOffset?> Utc(this PropertyBuilder<DateTimeOffset?> property, string comment) =>
        property.HasColumnType("timestamp with time zone").HasConversion(UtcConverter).HasComment(comment);

    public static PropertyBuilder<string> Json(this PropertyBuilder<string> property, string comment) =>
        property.HasColumnType("jsonb").IsRequired().HasComment(comment);
}
