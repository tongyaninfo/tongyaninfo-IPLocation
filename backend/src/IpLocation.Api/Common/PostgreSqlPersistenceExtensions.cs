using IpLocation.Api.Data;
using IpLocation.Api.Options;
using IpLocation.Api.Service;
using IpLocation.Api.Service.impl;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace IpLocation.Api.Common;

/// <summary>仅配置 PostgreSQL 和发号依赖；不连接、建库、应用迁移或初始化账号。</summary>
public static class PostgreSqlPersistenceExtensions
{
    public const string Schema = "iplocation";
    public const string MigrationsHistoryTable = "__EFMigrationsHistory";

    public static IServiceCollection AddPostgreSqlPersistence(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<IdGenerationOptions>().Bind(configuration.GetSection(IdGenerationOptions.SectionName))
            .Validate(options => options.WorkerId <= 63, "WorkerId 必须在 0 到 63 之间。")
            .ValidateOnStart();
        services.TryAddSingleton<IIdService, YiteIdService>();

        var options = configuration.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new();
        services.AddOptions<DatabaseOptions>().Bind(configuration.GetSection(DatabaseOptions.SectionName))
            .Validate(value => value.CommandTimeoutSeconds is > 0 and <= 60, "数据库命令超时必须在 1 到 60 秒之间。")
            .ValidateOnStart();
        if (!options.Enabled) return services;

        var connection = configuration.GetConnectionString(options.ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connection))
            throw new InvalidOperationException("PostgreSQL 已启用，但未提供获准的连接配置。不会回退到内存存储。");

        services.AddDbContextFactory<IpLocationDbContext>(builder => ConfigureDatabase(builder, connection, options.CommandTimeoutSeconds));
        services.AddPostgreSqlAccessLogStore();
        return services;
    }

    public static void ConfigureDatabase(DbContextOptionsBuilder builder, string connection, int timeoutSeconds = 10) =>
        builder.UseNpgsql(connection, postgres => postgres
            .MigrationsHistoryTable(MigrationsHistoryTable, Schema)
            .CommandTimeout(timeoutSeconds));
}
