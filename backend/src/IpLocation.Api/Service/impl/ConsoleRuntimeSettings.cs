using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

/// <summary>启动读取、管理提交后刷新；数据库读取失败时不发布半份配置。</summary>
public sealed class ConsoleRuntimeSettings : IConsoleRuntimeSettings
{
    private readonly IServiceProvider _services;
    private readonly QueryOptions _baseline;
    private readonly string[] _baselineNetworks;
    private readonly ConsoleBasicSettings _baselineBasic;
    private readonly SemaphoreSlim _reload = new(1, 1);
    private ConsoleRuntimeSnapshot _current;

    public ConsoleRuntimeSettings(IServiceProvider services, IOptions<QueryOptions> query,
        IOptionsMonitor<ConsoleOptions> console, IOptions<DatabaseOptions> database,
        IOptions<AccessLogOptions>? logging = null, IOptions<ConsoleAuthOptions>? auth = null)
    {
        _services = services;
        _baseline = query.Value;
        _baselineNetworks = console.CurrentValue.AllowedNetworks.ToArray();
        _baselineBasic = ConsoleBasicSettingsRules.FromOptions(logging?.Value ?? new(), auth?.Value ?? new());
        // 启用持久化后，必须先加载白名单；未加载时后台入口保持关闭。
        _current = new(1, Copy(_baseline), _baselineNetworks, !database.Value.Enabled, _baselineBasic);
    }

    public ConsoleRuntimeSnapshot Current => Volatile.Read(ref _current);

    public void Publish(ConsoleRuntimeSnapshot snapshot)
    {
        // 旧调用方只发布查询/白名单时继续保留基础设置，不能把明确的 null 保留期重置为默认值。
        snapshot = snapshot with { Query = Copy(snapshot.Query), Basic = snapshot.Basic ?? Current.Basic ?? _baselineBasic };
        if (!ConsoleBasicSettingsRules.IsValid(snapshot.Basic)) throw new InvalidOperationException("运行基础设置无效。");
        // 慢读取不能覆盖已经提交的新版本；发布内容来自已校验的完整快照。
        while (true)
        {
            var previous = Current;
            if (previous.IsReady && snapshot.Revision < previous.Revision) return;
            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, snapshot, previous), previous)) return;
        }
    }

    public async Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        await _reload.WaitAsync(cancellationToken);
        try
        {
            var factory = _services.GetService<IDbContextFactory<IpLocationDbContext>>();
            if (factory is null)
            {
                Publish(new(1, Copy(_baseline), _baselineNetworks.ToArray(), true, _baselineBasic));
                return;
            }
            await using var db = await factory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
            var values = await db.SystemSettings.AsNoTracking()
                .Where(row => row.Key.StartsWith("query.") || row.Key.StartsWith("basic.") || row.Key == "console.settingsRevision" || row.Key == "console.allowlistManaged")
                .ToDictionaryAsync(row => row.Key, cancellationToken);
            var query = Copy(_baseline);
            T Get<T>(string key, T fallback) => values.TryGetValue(key, out var row)
                ? JsonSerializer.Deserialize<T>(row.ValueJson) ?? throw new InvalidOperationException("已保存的设置内容无效。") : fallback;
            query.DefaultDatabase = ProviderCodes.Public(Get("query.defaultDatabase", query.DefaultDatabase));
            query.ProviderTimeoutMs = Get("query.providerTimeoutMs", query.ProviderTimeoutMs);
            query.TotalTimeoutMs = Get("query.totalTimeoutMs", query.TotalTimeoutMs);
            query.MaxConcurrentSources = Get("query.maxConcurrentSources", query.MaxConcurrentSources);
            if (!IsValidQuery(query)) throw new InvalidOperationException("已保存的查询设置超出允许范围。");
            var networks = _baselineNetworks.ToArray();
            if (Get("console.allowlistManaged", false))
                networks = (await db.AdminIpAllowlists.AsNoTracking().Where(row => row.Enabled)
                    .OrderBy(row => row.Id).Select(row => row.Network).ToListAsync(cancellationToken))
                    .Select(network => network.ToString()).ToArray();
            var revision = values.TryGetValue("console.settingsRevision", out var version) ? version.Revision : 1;
            var basic = ConsoleBasicSettingsRules.Read(values.ToDictionary(pair => pair.Key, pair => pair.Value.ValueJson), _baselineBasic);
            await transaction.CommitAsync(cancellationToken);
            Publish(new(revision, query, networks, true, basic));
        }
        finally { _reload.Release(); }
    }

    public static bool IsValidQuery(QueryOptions query) =>
        !string.IsNullOrWhiteSpace(query.DefaultDatabase) && query.DefaultDatabase.Length <= 16 &&
        query.ProviderTimeoutMs is > 0 and <= 60_000 && query.TotalTimeoutMs is > 0 and <= 120_000 &&
        query.TotalTimeoutMs >= query.ProviderTimeoutMs && query.MaxConcurrentSources is > 0 and <= 6;

    private static QueryOptions Copy(QueryOptions query) => new()
    {
        DefaultDatabase = ProviderCodes.Public(query.DefaultDatabase), ProviderTimeoutMs = query.ProviderTimeoutMs,
        TotalTimeoutMs = query.TotalTimeoutMs, MaxConcurrentSources = query.MaxConcurrentSources
    };
}
