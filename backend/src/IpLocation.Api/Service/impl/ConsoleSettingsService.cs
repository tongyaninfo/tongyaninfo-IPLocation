using System.Data;
using System.Net;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

/// <summary>仅允许固定登记的非敏感设置；白名单和审计同事务，禁止通过表单修改凭据。</summary>
public sealed class ConsoleSettingsService(IServiceProvider services, IConsoleRuntimeSettings runtime,
    IIpQueryService queries, IIdService ids, IClientIpService clientIp, IOptions<DatabaseOptions> database,
    IOptions<AccessLogOptions> logging, IOptions<ConsoleAuthOptions> auth, IOptions<TrustedProxyOptions> proxies,
    ILogger<ConsoleSettingsService> logger, IAccessEventQueue? queue = null) : IConsoleSettingsService
{
    public ConsoleSettingsResponse GetSettings()
    {
        var snapshot = runtime.Current;
        if (!snapshot.IsReady) throw new InvalidOperationException("运行设置尚未加载。");
        var basic = snapshot.Basic ?? ConsoleBasicSettingsRules.FromOptions(logging.Value, auth.Value);
        var effectiveQueueCapacity = queue?.Capacity ?? logging.Value.QueueCapacity;
        return new(snapshot.Revision, snapshot.Query, snapshot.AllowedNetworks,
            new(database.Value.Enabled, basic.RetainFullSourceIp, basic.AccessLogRetentionDays,
                effectiveQueueCapacity, basic.SessionMinutes,
                proxies.Value.KnownProxies.Length + proxies.Value.KnownIPNetworks.Length, proxies.Value.ForwardLimit,
                basic.QueueCapacity != effectiveQueueCapacity),
            queries.GetProviders(), Basic: basic);
    }

    public async Task<ConsoleOperationResponse<ConsoleSettingsResponse>> SaveAsync(HttpContext context, ConsoleSettingsRequest request)
    {
        if (request.Query is null || !ConsoleRuntimeSettings.IsValidQuery(request.Query) || request.Revision < 1 ||
            !TryNormalizeNetworks(request.AllowedNetworks, out var networks) ||
            (request.Basic is not null && !ConsoleBasicSettingsRules.IsValid(request.Basic)))
            return Failure(400, "INVALID_SETTINGS", "查询、基础配置或白名单无效；保留天数须留空或为0至365天，会话5至1440分钟，队列1至1000000条。");
        request.Query.DefaultDatabase = ProviderCodes.Public(request.Query.DefaultDatabase);
        var address = clientIp.GetClientIp(context);
        // 使用同一可信地址检查，阻止误操作把当前管理连接锁在门外；无需信任客户端声明的IP。
        if (address is null || !networks.Any(network => network.Contains(address)))
            return Failure(400, "CONSOLE_SELF_LOCKOUT", "新白名单必须继续允许当前管理连接；请保留当前 IP 或所属网段。");
        var factory = services.GetService<IDbContextFactory<IpLocationDbContext>>();
        if (factory is null) return ConsoleIdentityService.StorageUnavailable<ConsoleSettingsResponse>();
        try
        {
            await using var db = await factory.CreateDbContextAsync(context.RequestAborted);
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, context.RequestAborted);
            var actor = (AdminAccount)context.Items[ConsoleAuthentication.AccountKey]!;
            if (!await ConsoleMutation.LockActorAsync(db, actor, context.RequestAborted, context))
                return Failure(401, "CONSOLE_UNAUTHENTICATED", "管理员会话已失效。");
            var rows = await db.SystemSettings.Where(row => row.Key.StartsWith("query.") || row.Key.StartsWith("console.") || row.Key.StartsWith("basic."))
                .ToDictionaryAsync(row => row.Key, context.RequestAborted);
            var revision = rows.TryGetValue("console.settingsRevision", out var savedRevision) ? savedRevision.Revision : 1;
            if (revision != request.Revision)
                return Failure(409, "SETTINGS_CONFLICT", "设置已被其他操作修改，请刷新后重试。");

            // 全局管理事务锁下刷新来源设置，避免在停用并发中接受过时的默认库。
            var baseline = runtime.Current;
            var beforeBasic = ConsoleBasicSettingsRules.Read(rows.ToDictionary(pair => pair.Key, pair => pair.Value.ValueJson),
                baseline.Basic ?? ConsoleBasicSettingsRules.FromOptions(logging.Value, auth.Value));
            var basic = request.Basic ?? beforeBasic;
            T Read<T>(string key, T fallback) => rows.TryGetValue(key, out var row)
                ? JsonSerializer.Deserialize<T>(row.ValueJson)! : fallback;
            var beforeQuery = new QueryOptions
            {
                DefaultDatabase = ProviderCodes.Public(Read("query.defaultDatabase", baseline.Query.DefaultDatabase)),
                ProviderTimeoutMs = Read("query.providerTimeoutMs", baseline.Query.ProviderTimeoutMs),
                TotalTimeoutMs = Read("query.totalTimeoutMs", baseline.Query.TotalTimeoutMs),
                MaxConcurrentSources = Read("query.maxConcurrentSources", baseline.Query.MaxConcurrentSources)
            };
            var beforeNetworks = baseline.AllowedNetworks;
            if (Read("console.allowlistManaged", false))
                beforeNetworks = (await db.AdminIpAllowlists.AsNoTracking().Where(row => row.Enabled)
                    .Select(row => row.Network).ToListAsync(context.RequestAborted)).Select(network => network.ToString()).ToArray();
            var selected = queries.GetProviders().FirstOrDefault(provider => provider.Id == request.Query.DefaultDatabase);
            var providerKey = ProviderCodes.EnabledSettingKey(request.Query.DefaultDatabase);
            var enabledRow = await db.SystemSettings.AsNoTracking().SingleOrDefaultAsync(row => row.Key == providerKey, context.RequestAborted);
            var enabled = enabledRow is null ? selected?.Enabled == true : JsonSerializer.Deserialize<bool>(enabledRow.ValueJson);
            if (selected is null || !enabled || !selected.Ready)
                return Failure(400, "DEFAULT_DATABASE_NOT_READY", "默认数据库必须已启用且实际就绪。");

            var now = DateTimeOffset.UtcNow;
            void Set<T>(string key, T value)
            {
                if (!rows.TryGetValue(key, out var row))
                {
                    row = new SystemSetting { Key = key, Revision = 1 };
                    rows.Add(key, row); db.SystemSettings.Add(row);
                }
                else row.Revision = checked(row.Revision + 1);
                row.ValueJson = JsonSerializer.Serialize(value);
                row.UpdatedAtUtc = now; row.UpdatedById = actor.Id;
            }
            Set("query.defaultDatabase", request.Query.DefaultDatabase);
            Set("query.providerTimeoutMs", request.Query.ProviderTimeoutMs);
            Set("query.totalTimeoutMs", request.Query.TotalTimeoutMs);
            Set("query.maxConcurrentSources", request.Query.MaxConcurrentSources);
            Set("basic.accessLogRetentionDays", basic.AccessLogRetentionDays);
            Set("basic.retainFullSourceIp", basic.RetainFullSourceIp);
            Set("basic.sessionMinutes", basic.SessionMinutes);
            Set("basic.queueCapacity", basic.QueueCapacity);
            Set("console.allowlistManaged", true);
            Set("console.settingsRevision", revision + 1);
            rows["console.settingsRevision"].Revision = checked(revision + 1);

            var allowlist = await db.AdminIpAllowlists.ToListAsync(context.RequestAborted);
            var desired = networks.Select(network => network.ToString()).ToHashSet(StringComparer.Ordinal);
            foreach (var row in allowlist)
            {
                var enable = desired.Remove(row.Network.ToString());
                row.Enabled = enable; row.UpdatedById = actor.Id; row.UpdatedAtUtc = now;
            }
            foreach (var network in networks.Where(network => desired.Contains(network.ToString())))
                db.AdminIpAllowlists.Add(new AdminIpAllowlist
                {
                    Id = ids.NewId(), Network = network, Enabled = true, CreatedById = actor.Id, UpdatedById = actor.Id,
                    CreatedAtUtc = now, UpdatedAtUtc = now, Note = "后台系统设置"
                });
            ConsoleMutation.AddAudit(db, ids, clientIp, context, "Settings.Update", "SystemSetting", null,
                new { before = new { query = beforeQuery, basic = beforeBasic, allowedNetworks = beforeNetworks },
                    after = new { query = request.Query, basic, allowedNetworks = networks.Select(network => network.ToString()).ToArray() } });
            await db.SaveChangesAsync(context.RequestAborted);
            await transaction.CommitAsync(context.RequestAborted);
            // 发布刚提交的已验证配置，无需再次连库；数据库瞬时故障不能延迟白名单收紧。
            runtime.Publish(new(revision + 1, new QueryOptions
            {
                DefaultDatabase = request.Query.DefaultDatabase, ProviderTimeoutMs = request.Query.ProviderTimeoutMs,
                TotalTimeoutMs = request.Query.TotalTimeoutMs, MaxConcurrentSources = request.Query.MaxConcurrentSources
            }, networks.Select(network => network.ToString()).ToArray(), true, basic));
            return new(200, ApiResult<ConsoleSettingsResponse>.Ok(GetSettings(), "设置已保存；日志策略即时生效，会话时长用于新登录，队列容量变更重启后生效。"));
        }
        catch (DbUpdateConcurrencyException) { return Failure(409, "SETTINGS_CONFLICT", "设置已变化，请刷新后重试。"); }
        catch (Exception exception) when (exception is not OperationCanceledException || !context.RequestAborted.IsCancellationRequested)
        {
            logger.LogWarning("保存系统设置失败，类型：{Type}", exception.GetType().Name);
            return ConsoleIdentityService.StorageUnavailable<ConsoleSettingsResponse>();
        }
    }

    public static bool TryNormalizeNetworks(string[]? values, out IPNetwork[] networks)
    {
        networks = [];
        if (values is null || values.Length is 0 or > 128) return false;
        var result = new Dictionary<string, IPNetwork>(StringComparer.Ordinal);
        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 64) return false;
            IPNetwork network;
            if (IpAddressParser.TryParse(value.Trim(), out var ip))
                network = new IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            else
            {
                var text = value.Trim();
                var separator = text.IndexOf('/');
                if (!IPNetwork.TryParse(text, out network) || separator < 1 ||
                    !IpAddressParser.TryParse(text[..separator], out var baseAddress) || !baseAddress.Equals(network.BaseAddress))
                    return false;
            }
            // IPv4映射地址规范化由现有解析器完成；CIDR必须显式填写规范网络起点。
            result[network.ToString()] = network;
        }
        networks = result.Values.ToArray();
        return true;
    }

    private static ConsoleOperationResponse<ConsoleSettingsResponse> Failure(int status, string code, string message) =>
        new(status, ApiResult<ConsoleSettingsResponse>.Fail(code, message));
}
