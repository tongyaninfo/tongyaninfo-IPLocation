using IpLocation.Api.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace IpLocation.Api.Data;

/// <summary>离线迁移专用：不读取私密配置、不创建 Web 宿主，也不打开数据库连接。</summary>
public sealed class IpLocationDesignTimeDbContextFactory : IDesignTimeDbContextFactory<IpLocationDbContext>
{
    public IpLocationDbContext CreateDbContext(string[] args)
    {
        // 仅供 Npgsql 建立模型和生成 SQL；端口1不是运行数据库配置，不含账号或密码。
        const string designConnection = "Host=127.0.0.1;Port=1;Database=iplocation;Timeout=1;Pooling=false";
        var options = new DbContextOptionsBuilder<IpLocationDbContext>();
        PostgreSqlPersistenceExtensions.ConfigureDatabase(options, designConnection);
        return new IpLocationDbContext(options.Options);
    }
}
