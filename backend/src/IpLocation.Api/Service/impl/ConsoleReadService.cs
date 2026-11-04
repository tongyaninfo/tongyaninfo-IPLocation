using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Entities;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using NpgsqlTypes;

namespace IpLocation.Api.Service.impl;

/// <summary>后台持久化读取；在数据库聚合完整窗口，不把受限明细或进程缓存当作历史总量。</summary>
public sealed class ConsoleReadService(IServiceProvider services, IAccessLogStore processWindow,
    IAccessEventQueue queue, IOptions<AccessLogOptions> accessOptions,
    IOptions<PersistentAccessLogOptions> readerOptions, TimeProvider time, IConsoleRuntimeSettings? runtime = null) : IConsoleReadService
{
    private static readonly HashSet<string> Routes = ["homeHtml", "plainIp", "query", "selfQuery", "providerMetadata", "mcp"];
    private static readonly HashSet<string> Formats = ["html", "ip", "json", "xml", "txt", "sse", "unknown"];
    private static readonly HashSet<string> Databases = ["geolite2", "ip2location", "dbip", "ipinfo", "ip2region", ProviderCodes.Cz88, "all"];

    public async Task<ConsoleOverviewResponse> GetOverviewAsync(ConsoleReadWindowRequest request, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var retention = readerOptions.Value.MinuteRetentionDays;
        var window = ConsoleReadRules.NormalizeWindow(request, now, retention, completeMinutes: true);
        using var timeout = ReadTimeout(cancellationToken);
        await using var db = await CreateContextAsync(timeout.Token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token);
        var totals = await ReadTotalsAsync(db, window.FromUtc, window.ToUtc, timeout.Token);
        var (trend, mcpTrend) = await ReadTrendAsync(db, window.FromUtc, window.ToUtc, timeout.Token);
        var mcpTotals = new ConsoleMcpOverviewTotals(mcpTrend.Sum(point => point.Requests),
            mcpTrend.Sum(point => point.QueryRequests), mcpTrend.Sum(point => point.Errors));
        var audits = await ReadAuditRowsAsync(AuditQuery(db).Where(item => item.Event.OccurredAtUtc >= now.AddDays(-180) &&
                item.Event.OccurredAtUtc < now).OrderByDescending(item => item.Event.OccurredAtUtc)
            .ThenByDescending(item => item.Event.Id).Take(5), timeout.Token);
        await transaction.CommitAsync(timeout.Token);
        var metrics = queue.GetMetrics();
        var snapshot = processWindow.GetSnapshot(0, 0);
        var notes = new List<string>
        {
            "统计仅包含已持久化的公共请求，按完整UTC分钟桶汇总；最新分钟可能仍在采集中。",
            "首页HTML、纯IP调用与逻辑查询分别计数；请求数不代表用户数，聚合查询不等于来源调用数。",
            "MCP请求包含协议发现、初始化和目录；MCP定位查询仅计query_ip/query_my_ip。MCP图表中的HTTP错误不等于业务错误。",
            "p95为合并延迟直方图后的桶上界估算；无样本或p95超过10000ms时为空。",
            "逐来源实际调用、命中、无匹配及超时统计尚未采集，不以零次代替。",
            "队列健康仅覆盖当前进程；异常退出前未落盘事件可能缺失，历史缺口无法完整重建。"
        };
        if (metrics.Dropped > 0 || metrics.WriteFailures > 0)
            notes.Add("当前进程已出现日志丢弃或写入失败，持久统计存在已知缺口。");
        if (!ConsoleBasicSettingsRules.IsAccessLoggingEnabled(runtime, accessOptions.Value))
            notes.Add("当前普通访问保存已关闭，明细和分钟统计均不再新增；历史数据暂留，关闭期间的请求不会补记。管理员可靠审计继续记录。");
        if (metrics.Suppressed > 0)
            notes.Add("已有队列事件按关闭策略跳过，未将其计为写入成功或存储故障。");
        if (metrics.Queued > 0 || metrics.InFlight > 0)
            notes.Add("仍有访问事件等待持久化，最近统计会有延迟。");
        return new(window.FromUtc, window.ToUtc, now, "postgresql", totals, trend, metrics,
            snapshot.StartedAtUtc, retention, notes, audits, mcpTotals, mcpTrend);
    }

    public async Task<ConsoleAccessLogPage> GetAccessLogsAsync(ConsoleAccessLogReadRequest request,
        CancellationToken cancellationToken)
    {
        var offset = ConsoleReadRules.PageOffset(request.Page, request.PageSize);
        var retention = ConsoleBasicSettingsRules.RetentionDays(runtime, accessOptions.Value);
        var window = ConsoleReadRules.NormalizeWindow(request, time.GetUtcNow(), retention);
        var route = FixedFilter(request.RouteCategory, Routes);
        var format = FixedFilter(request.Format, Formats);
        var requestedDatabase = ConsoleReadRules.Filter(request.Database, 32);
        var database = FixedFilter(requestedDatabase is null ? null : ProviderCodes.Public(requestedDatabase), Databases);
        var source = ConsoleReadRules.Filter(request.SourceAddress, 64);
        var tool = ConsoleReadRules.Filter(request.ToolName, 64);
        var resultCode = ConsoleReadRules.Filter(request.ResultCode, 64);
        var trace = ConsoleReadRules.Filter(request.TraceId, 128);
        NpgsqlInet? address = null;
        if (source is not null)
        {
            if (!IpAddressParser.TryParse(source, out var parsed)) throw new ConsoleReadValidationException("来源地址须为单个IPv4或IPv6地址。");
            address = new NpgsqlInet(parsed);
        }
        if (request.StatusCode is < 100 or > 599) throw new ConsoleReadValidationException("HTTP状态码须为100至599。");
        using var timeout = ReadTimeout(cancellationToken);
        await using var db = await CreateContextAsync(timeout.Token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token);
        var query = db.AccessLogs.AsNoTracking().Where(item => item.OccurredAtUtc >= window.FromUtc && item.OccurredAtUtc < window.ToUtc);
        if (route is not null) query = query.Where(item => item.RouteCategory == route);
        if (format is not null) query = query.Where(item => item.Format == format);
        if (database is not null)
        {
            var aliases = ProviderCodes.LogAliases(database);
            query = query.Where(item => aliases.Contains(item.Database!));
        }
        if (address.HasValue) query = query.Where(item => item.SourceAddress == address.Value);
        if (request.StatusCode.HasValue) query = query.Where(item => item.StatusCode == request.StatusCode.Value);
        // 使用参数化 jsonb 包含查询，筛选发生在分页之前，不下载整窗记录后内存过滤。
        if (tool is not null)
        {
            var filter = JsonSerializer.Serialize(new { toolName = tool });
            query = query.Where(item => item.McpContextJson != null && EF.Functions.JsonContains(item.McpContextJson, filter));
        }
        if (resultCode is not null)
        {
            var filter = JsonSerializer.Serialize(new { resultCode });
            query = query.Where(item => item.McpContextJson != null && EF.Functions.JsonContains(item.McpContextJson, filter));
        }
        if (trace is not null) query = query.Where(item => item.TraceId == trace);
        var total = await query.LongCountAsync(timeout.Token);
        var rows = await query.OrderByDescending(item => item.OccurredAtUtc).ThenByDescending(item => item.Id)
            .Skip(offset).Take(request.PageSize).ToListAsync(timeout.Token);
        await transaction.CommitAsync(timeout.Token);
        return new(rows.Select(item => new ConsoleAccessLogEntry(item.Id.ToString(CultureInfo.InvariantCulture),
            item.OccurredAtUtc, item.SourceAddress?.ToString(), item.RouteCategory, item.Method, item.Format,
            item.Database is null ? null : ProviderCodes.Public(item.Database), item.DatabaseVersion, item.StatusCode, item.ElapsedMilliseconds, item.IsCancelled,
            item.ClientCategory, item.TraceId, AccessRequestHeaders.Read(item.RequestHeadersJson), AccessSourceLocations.Read(item.SourceLocationJson),
            McpContexts.Read(item.McpContextJson))).ToArray(), total, request.Page, request.PageSize,
            window.FromUtc, window.ToUtc, retention, queue.GetMetrics());
    }

    public async Task<ConsoleAuditPage> GetAuditsAsync(ConsoleAuditReadRequest request, CancellationToken cancellationToken)
    {
        var offset = ConsoleReadRules.PageOffset(request.Page, request.PageSize);
        // 审计读取边界不是物理删除策略，本服务不会清理任何记录。
        var window = ConsoleReadRules.NormalizeWindow(request, time.GetUtcNow(), 180);
        var actor = ConsoleReadRules.Filter(request.Actor, 128)?.ToLowerInvariant();
        var action = ConsoleReadRules.Filter(request.Action, 64);
        var result = ConsoleReadRules.Filter(request.Result, 32);
        var trace = ConsoleReadRules.Filter(request.TraceId, 128);
        using var timeout = ReadTimeout(cancellationToken);
        await using var db = await CreateContextAsync(timeout.Token);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token);
        var query = AuditQuery(db).Where(item => item.Event.OccurredAtUtc >= window.FromUtc && item.Event.OccurredAtUtc < window.ToUtc);
        if (actor is not null) query = query.Where(item => item.Account != null &&
            (item.Account.Account.ToLower().Contains(actor) || item.Account.DisplayName.ToLower().Contains(actor)));
        if (action is not null) query = query.Where(item => item.Event.Action == action);
        if (result is not null) query = query.Where(item => item.Event.Result == result);
        if (trace is not null) query = query.Where(item => item.Event.TraceId == trace);
        var total = await query.LongCountAsync(timeout.Token);
        var rows = await ReadAuditRowsAsync(query.OrderByDescending(item => item.Event.OccurredAtUtc)
            .ThenByDescending(item => item.Event.Id).Skip(offset).Take(request.PageSize), timeout.Token);
        await transaction.CommitAsync(timeout.Token);
        return new(rows, total, request.Page, request.PageSize);
    }

    private async Task<IpLocationDbContext> CreateContextAsync(CancellationToken cancellationToken)
    {
        var factory = services.GetService<IDbContextFactory<IpLocationDbContext>>() ??
            throw new InvalidOperationException("后台持久化尚未配置。");
        return await factory.CreateDbContextAsync(cancellationToken);
    }

    private CancellationTokenSource ReadTimeout(CancellationToken cancellationToken)
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(readerOptions.Value.ReadTimeoutMilliseconds);
        return timeout;
    }

    private static string? FixedFilter(string? value, HashSet<string> choices)
    {
        value = ConsoleReadRules.Filter(value, 32);
        if (value is not null && !choices.Contains(value)) throw new ConsoleReadValidationException("筛选项不在允许范围内。");
        return value;
    }

    private static IQueryable<AuditRow> AuditQuery(IpLocationDbContext db) =>
        from audit in db.AdminAuditEvents.AsNoTracking()
        join account in db.AdminAccounts.AsNoTracking() on audit.ActorId equals (long?)account.Id into accounts
        from account in accounts.DefaultIfEmpty()
        select new AuditRow { Event = audit, Account = account };

    private static async Task<IReadOnlyList<ConsoleAuditEntry>> ReadAuditRowsAsync(IQueryable<AuditRow> query,
        CancellationToken cancellationToken)
    {
        var rows = await query.Select(item => new
        {
            item.Event.Id, item.Event.OccurredAtUtc,
            Actor = item.Account == null ? null : item.Account.DisplayName == "" ? item.Account.Account : item.Account.DisplayName,
            item.Event.SourceAddress, item.Event.Action, item.Event.TargetType, item.Event.TargetId,
            item.Event.Result, item.Event.TraceId, item.Event.DifferenceJson
        }).ToListAsync(cancellationToken);
        return rows.Select(item => new ConsoleAuditEntry(item.Id.ToString(CultureInfo.InvariantCulture),
            item.OccurredAtUtc, item.Actor, item.SourceAddress?.ToString(), item.Action, item.TargetType, item.TargetId,
            item.Result, item.TraceId, ConsoleReadRules.SanitizeDifference(item.DifferenceJson))).ToArray();
    }

    private static async Task<ConsoleOverviewTotals> ReadTotalsAsync(IpLocationDbContext db,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // 13个固定桶在DB端求和；只返回一行，不加载整窗分钟明细，也不平均各分钟p95。
        var histogramColumns = string.Join(", ", Enumerable.Range(0, 13).Select(index =>
            $"COALESCE(SUM((\"LATENCY_HISTOGRAM_JSON\"->>{index})::bigint), 0)"));
        var sql = "SELECT COALESCE(SUM(\"TOTAL_REQUESTS\"),0)::bigint, COALESCE(SUM(\"HOME_HTML_REQUESTS\"),0)::bigint, " +
            "COALESCE(SUM(\"PLAIN_IP_REQUESTS\"),0)::bigint, COALESCE(SUM(\"QUERY_REQUESTS\"),0)::bigint, " +
            "COALESCE(SUM(\"CLIENT_ERRORS\"),0)::bigint, COALESCE(SUM(\"SERVER_ERRORS\"),0)::bigint, " +
            "COALESCE(SUM(\"CANCELLED_REQUESTS\"),0)::bigint, COALESCE(SUM(\"TOTAL_ELAPSED_MILLISECONDS\"),0), " +
            histogramColumns + " FROM iplocation.\"TRAFFIC_MINUTE_AGGREGATE\" " +
            "WHERE \"MINUTE_UTC\" >= @from AND \"MINUTE_UTC\" < @to AND \"COUNT_KIND\" = 'request'";
        await using var command = Command(db, sql, from, to);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        var total = reader.GetInt64(0);
        var counts = Enumerable.Range(8, 13).Select(index => Convert.ToInt64(reader.GetValue(index), CultureInfo.InvariantCulture)).ToArray();
        return new(total, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4),
            reader.GetInt64(5), reader.GetInt64(6), total == 0 ? null : reader.GetDouble(7) / total,
            counts.Sum() == total ? ConsoleReadRules.Percentile95(counts) : null);
    }

    private static async Task<(IReadOnlyList<ConsoleTrafficPoint> Public, IReadOnlyList<ConsoleTrafficPoint> Mcp)> ReadTrendAsync(IpLocationDbContext db,
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        // 趋势最多约360点；日桶固定UTC，客户端负责按Asia/Shanghai展示时间。
        var bucket = to - from <= TimeSpan.FromHours(6) ? "minute" : to - from <= TimeSpan.FromDays(7) ? "hour" : "day";
        const string sql = "SELECT date_trunc(@bucket, \"MINUTE_UTC\" AT TIME ZONE 'UTC') AT TIME ZONE 'UTC' AS bucket, " +
            "SUM(\"TOTAL_REQUESTS\")::bigint, SUM(\"QUERY_REQUESTS\")::bigint, SUM(\"CLIENT_ERRORS\" + \"SERVER_ERRORS\")::bigint, " +
            // 复用同一次分钟汇总查询，不扫描访问明细，也不更改已存统计字段的HTTP语义。
            "COALESCE(SUM(\"TOTAL_REQUESTS\") FILTER (WHERE \"ROUTE_CATEGORY\" = 'mcp'),0)::bigint, " +
            "COALESCE(SUM(\"QUERY_REQUESTS\") FILTER (WHERE \"ROUTE_CATEGORY\" = 'mcp'),0)::bigint, " +
            "COALESCE(SUM(\"CLIENT_ERRORS\" + \"SERVER_ERRORS\") FILTER (WHERE \"ROUTE_CATEGORY\" = 'mcp'),0)::bigint " +
            "FROM iplocation.\"TRAFFIC_MINUTE_AGGREGATE\" WHERE \"MINUTE_UTC\" >= @from AND \"MINUTE_UTC\" < @to " +
            "AND \"COUNT_KIND\" = 'request' GROUP BY bucket ORDER BY bucket";
        await using var command = Command(db, sql, from, to);
        var parameter = command.CreateParameter();
        parameter.ParameterName = "bucket";
        parameter.Value = bucket;
        command.Parameters.Add(parameter);
        var result = new List<ConsoleTrafficPoint>();
        var mcp = new List<ConsoleTrafficPoint>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var timestamp = reader.GetFieldValue<DateTimeOffset>(0);
            result.Add(new(timestamp, reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3)));
            if (reader.GetInt64(4) > 0) mcp.Add(new(timestamp, reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6)));
        }
        return (result, mcp);
    }

    private static DbCommand Command(IpLocationDbContext db, string sql, DateTimeOffset from, DateTimeOffset to)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = sql;
        command.CommandTimeout = db.Database.GetCommandTimeout() ?? 5;
        foreach (var pair in new[] { ("from", from), ("to", to) })
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = pair.Item1;
            parameter.Value = pair.Item2;
            command.Parameters.Add(parameter);
        }
        return command;
    }

    private sealed class AuditRow
    {
        public AdminAuditEvent Event { get; init; } = null!;
        public AdminAccount? Account { get; init; }
    }
}
