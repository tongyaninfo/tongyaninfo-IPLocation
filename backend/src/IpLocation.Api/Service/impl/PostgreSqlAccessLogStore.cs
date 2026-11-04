using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using IpLocation.Api.Common;
using IpLocation.Api.Data;
using IpLocation.Api.Models.Events;
using IpLocation.Api.Models.Requests;
using IpLocation.Api.Models.Responses;
using IpLocation.Api.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace IpLocation.Api.Service.impl;

/// <summary>后台消费者专用 PG 存储：明细去重及分钟累加同事务，不回退为内存成功。</summary>
public sealed class PostgreSqlAccessLogStore : IAccessLogStore, IPersistentAccessLogReader
{
    private readonly IDbContextFactory<IpLocationDbContext> _contexts;
    private readonly IIdService _ids;
    private readonly AccessLogOptions _accessOptions;
    private readonly PersistentAccessLogOptions _readerOptions;
    private readonly TimeProvider _timeProvider;
    private readonly InMemoryAccessLogStore _processWindow;
    private readonly IConsoleRuntimeSettings? _runtime;

    public PostgreSqlAccessLogStore(IDbContextFactory<IpLocationDbContext> contexts, IIdService ids,
        IOptions<AccessLogOptions> accessOptions, IOptions<PersistentAccessLogOptions> readerOptions,
        IAccessEventQueue queue, TimeProvider timeProvider, IConsoleRuntimeSettings? runtime = null)
    {
        _contexts = contexts;
        _ids = ids;
        _accessOptions = accessOptions.Value;
        _readerOptions = readerOptions.Value;
        _timeProvider = timeProvider;
        _runtime = runtime;
        _processWindow = new InMemoryAccessLogStore(accessOptions, queue, timeProvider, runtime);
    }

    public async ValueTask<int> AppendBatchAsync(IReadOnlyList<AccessEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0 || !ConsoleBasicSettingsRules.IsAccessLoggingEnabled(_runtime, _accessOptions)) return 0;
        cancellationToken.ThrowIfCancellationRequested();
        var records = AccessLogPersistencePlan.Prepare(events, _ids);
        await using var context = await _contexts.CreateDbContextAsync(cancellationToken);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);
        var connection = context.Database.GetDbConnection();
        var dbTransaction = transaction.GetDbTransaction();
        var payload = JsonSerializer.Serialize(records.Select(record => new
        {
            id = record.Id,
            event_key = record.EventKey,
            occurred_at_utc = record.Event.OccurredAtUtc.ToUniversalTime(),
            trace_id = record.Event.TraceId,
            route_category = record.Event.RouteCategory,
            method = record.Event.Method,
            protocol = record.Event.Protocol,
            format = record.Event.Format,
            database = record.Event.Database,
            database_version = record.Event.DatabaseVersion,
            status_code = record.Event.StatusCode,
            elapsed_milliseconds = record.Event.ElapsedMilliseconds,
            is_cancelled = record.Event.IsCancelled,
            source_address = record.Event.SourceAddress,
            client_category = record.Event.ClientCategory,
            request_headers_json = record.Event.RequestHeadersJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(record.Event.RequestHeadersJson),
            source_location_json = record.Event.SourceLocationJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(record.Event.SourceLocationJson),
            mcp_context_json = record.Event.McpContextJson is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(record.Event.McpContextJson)
        }));

        var inserted = new HashSet<string>(StringComparer.Ordinal);
        await using (var command = CreateCommand(connection, dbTransaction, InsertAccessSql, payload))
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            while (await reader.ReadAsync(cancellationToken)) inserted.Add(reader.GetString(0));

        var increments = AccessLogPersistencePlan.AggregateInserted(records, inserted);
        if (increments.Count > 0)
        {
            var updatedAt = _timeProvider.GetUtcNow();
            var minutePayload = JsonSerializer.Serialize(increments.Select(item => new
            {
                minute_utc = item.MinuteUtc,
                route_category = item.RouteCategory,
                database = item.Database,
                format = item.Format,
                count_kind = item.CountKind,
                total_requests = item.TotalRequests,
                home_html_requests = item.HomeHtmlRequests,
                plain_ip_requests = item.PlainIpRequests,
                query_requests = item.QueryRequests,
                client_errors = item.ClientErrors,
                server_errors = item.ServerErrors,
                cancelled_requests = item.CancelledRequests,
                success_count = item.SuccessCount,
                not_found_count = item.NotFoundCount,
                total_elapsed_milliseconds = item.TotalElapsedMilliseconds,
                min_elapsed_milliseconds = item.MinElapsedMilliseconds,
                max_elapsed_milliseconds = item.MaxElapsedMilliseconds,
                latency_histogram_json = item.LatencyHistogramCounts,
                updated_at_utc = updatedAt
            }));
            await using var command = CreateCommand(connection, dbTransaction, UpsertMinuteSql, minutePayload);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        // 同步入口只缓存已确认提交的新事件。缓存不是 PG 历史，也不参与提交成败判断。
        await _processWindow.AppendBatchAsync(records.Where(record => inserted.Contains(record.EventKey))
            .Select(record => record.Event).ToArray(), CancellationToken.None);
        // 已进入事务的批次允许完成；后续批次在创建上下文及发号前再次检查保存开关。
        return events.Count;
    }

    public AccessLogSnapshot GetSnapshot(int recentLimit = 100, int minuteLimit = 60) =>
        _processWindow.GetSnapshot(recentLimit, minuteLimit) with { StorageKind = "postgresqlProcessWindow", IsDurable = false };

    public async Task<PersistentAccessLogSnapshot> GetSnapshotAsync(AccessLogReadRequest request, CancellationToken cancellationToken)
    {
        var window = AccessLogPersistencePlan.NormalizeReadWindow(request, _timeProvider.GetUtcNow(), _accessOptions, _readerOptions, _runtime);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_readerOptions.ReadTimeoutMilliseconds);
        await using var context = await _contexts.CreateDbContextAsync(timeout.Token);
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, timeout.Token);

        var recentQuery = context.AccessLogs.AsNoTracking()
            .Where(row => row.OccurredAtUtc >= window.FromUtc && row.OccurredAtUtc < window.ToUtc);
        if (window.AccessRetentionCutoffUtc.HasValue)
            recentQuery = recentQuery.Where(row => row.OccurredAtUtc > window.AccessRetentionCutoffUtc.Value);
        var recent = await recentQuery
            .OrderByDescending(row => row.OccurredAtUtc).ThenByDescending(row => row.Id)
            .Take(window.RecentLimit + 1).ToListAsync(timeout.Token);
        var minutes = await context.TrafficMinuteAggregates.AsNoTracking()
            .Where(row => row.MinuteUtc >= window.MinuteFromUtc && row.MinuteUtc < window.MinuteToUtc &&
                          row.MinuteUtc > window.MinuteRetentionCutoffUtc)
            .OrderByDescending(row => row.MinuteUtc).ThenBy(row => row.RouteCategory).ThenBy(row => row.Database)
            .ThenBy(row => row.Format).ThenBy(row => row.CountKind)
            .Take(window.MinuteLimit + 1).ToListAsync(timeout.Token);
        await transaction.CommitAsync(timeout.Token);

        return new PersistentAccessLogSnapshot(window.CapturedAtUtc, window.FromUtc, window.ToUtc,
            window.MinuteFromUtc, window.MinuteToUtc, window.AccessRetentionCutoffUtc, window.MinuteRetentionCutoffUtc,
            "postgresql", window.RecentLimit, window.MinuteLimit,
            recent.Count > window.RecentLimit, minutes.Count > window.MinuteLimit,
            AccessLogPersistencePlan.LatencyUpperBoundsMilliseconds,
            recent.Take(window.RecentLimit).Select(row => new PersistentAccessLogEntry(row.Id.ToString(CultureInfo.InvariantCulture),
                new AccessEvent(row.OccurredAtUtc, row.TraceId, row.RouteCategory, row.Method, row.Protocol, row.Format,
                    row.Database, row.DatabaseVersion, row.StatusCode, row.ElapsedMilliseconds, row.IsCancelled,
                    row.SourceAddress?.ToString(), row.ClientCategory, AccessRequestHeaders.Normalize(row.RequestHeadersJson),
                    AccessSourceLocations.Write(AccessSourceLocations.Read(row.SourceLocationJson)), McpContexts.Normalize(row.McpContextJson)))).ToArray(),
            minutes.Take(window.MinuteLimit).Select(row => new PersistentTrafficMinute(row.RouteCategory, row.Database,
                row.Format, row.CountKind, new AccessMinuteStats(row.MinuteUtc, row.TotalRequests, row.HomeHtmlRequests,
                    row.PlainIpRequests, row.QueryRequests, row.ClientErrors, row.ServerErrors, row.CancelledRequests,
                    row.TotalElapsedMilliseconds, row.MinElapsedMilliseconds, row.MaxElapsedMilliseconds,
                    ReadHistogram(row.LatencyHistogramJson)), row.SuccessCount, row.NotFoundCount, row.Revision)).ToArray());
    }

    private DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql, string payload)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.CommandTimeout = Math.Max(1, (int)Math.Ceiling(_accessOptions.WriteTimeoutMilliseconds / 1_000d));
        var parameter = command.CreateParameter();
        parameter.ParameterName = "payload";
        parameter.DbType = DbType.String;
        parameter.Value = payload;
        command.Parameters.Add(parameter);
        return command;
    }

    private static long[] ReadHistogram(string json)
    {
        var values = JsonSerializer.Deserialize<long[]>(json);
        if (values is null || values.Length != 13 || values.Any(value => value < 0))
            throw new InvalidOperationException("已保存的分钟直方图无效，不能生成误导统计。");
        return values;
    }

    private const string InsertAccessSql = """
        INSERT INTO iplocation."ACCESS_LOG"
            ("ID", "EVENT_KEY", "OCCURRED_AT_UTC", "TRACE_ID", "ROUTE_CATEGORY", "METHOD", "PROTOCOL", "FORMAT",
             "DATABASE", "DATABASE_VERSION", "STATUS_CODE", "ELAPSED_MILLISECONDS", "IS_CANCELLED", "SOURCE_ADDRESS", "CLIENT_CATEGORY", "REQUEST_HEADERS_JSON", "SOURCE_LOCATION_JSON", "MCP_CONTEXT_JSON")
        SELECT id, event_key, occurred_at_utc, trace_id, route_category, method, protocol, format,
               database, database_version, status_code, elapsed_milliseconds, is_cancelled, CAST(source_address AS inet), client_category, request_headers_json, source_location_json, mcp_context_json
        FROM jsonb_to_recordset(CAST(@payload AS jsonb)) AS incoming(
            id bigint, event_key text, occurred_at_utc timestamptz, trace_id text, route_category text, method text,
            protocol text, format text, database text, database_version text, status_code integer,
            elapsed_milliseconds double precision, is_cancelled boolean, source_address text, client_category text, request_headers_json jsonb, source_location_json jsonb, mcp_context_json jsonb)
        ORDER BY event_key
        ON CONFLICT ("EVENT_KEY") DO NOTHING
        RETURNING "EVENT_KEY";
        """;

    private const string UpsertMinuteSql = """
        INSERT INTO iplocation."TRAFFIC_MINUTE_AGGREGATE" AS current_minute
            ("MINUTE_UTC", "ROUTE_CATEGORY", "DATABASE", "FORMAT", "COUNT_KIND", "TOTAL_REQUESTS", "HOME_HTML_REQUESTS",
             "PLAIN_IP_REQUESTS", "QUERY_REQUESTS", "CLIENT_ERRORS", "SERVER_ERRORS", "CANCELLED_REQUESTS", "SUCCESS_COUNT",
             "NOT_FOUND_COUNT", "TOTAL_ELAPSED_MILLISECONDS", "MIN_ELAPSED_MILLISECONDS", "MAX_ELAPSED_MILLISECONDS",
             "LATENCY_HISTOGRAM_JSON", "REVISION", "UPDATED_AT_UTC")
        SELECT minute_utc, route_category, database, format, count_kind, total_requests, home_html_requests,
               plain_ip_requests, query_requests, client_errors, server_errors, cancelled_requests, success_count,
               not_found_count, total_elapsed_milliseconds, min_elapsed_milliseconds, max_elapsed_milliseconds,
               latency_histogram_json, 1, updated_at_utc
        FROM jsonb_to_recordset(CAST(@payload AS jsonb)) AS incoming(
            minute_utc timestamptz, route_category text, database text, format text, count_kind text,
            total_requests bigint, home_html_requests bigint, plain_ip_requests bigint, query_requests bigint,
            client_errors bigint, server_errors bigint, cancelled_requests bigint, success_count bigint,
            not_found_count bigint, total_elapsed_milliseconds double precision, min_elapsed_milliseconds double precision,
            max_elapsed_milliseconds double precision, latency_histogram_json jsonb, updated_at_utc timestamptz)
        ORDER BY minute_utc, route_category, database, format, count_kind
        ON CONFLICT ("MINUTE_UTC", "ROUTE_CATEGORY", "DATABASE", "FORMAT", "COUNT_KIND") DO UPDATE SET
            "TOTAL_REQUESTS" = current_minute."TOTAL_REQUESTS" + EXCLUDED."TOTAL_REQUESTS",
            "HOME_HTML_REQUESTS" = current_minute."HOME_HTML_REQUESTS" + EXCLUDED."HOME_HTML_REQUESTS",
            "PLAIN_IP_REQUESTS" = current_minute."PLAIN_IP_REQUESTS" + EXCLUDED."PLAIN_IP_REQUESTS",
            "QUERY_REQUESTS" = current_minute."QUERY_REQUESTS" + EXCLUDED."QUERY_REQUESTS",
            "CLIENT_ERRORS" = current_minute."CLIENT_ERRORS" + EXCLUDED."CLIENT_ERRORS",
            "SERVER_ERRORS" = current_minute."SERVER_ERRORS" + EXCLUDED."SERVER_ERRORS",
            "CANCELLED_REQUESTS" = current_minute."CANCELLED_REQUESTS" + EXCLUDED."CANCELLED_REQUESTS",
            "SUCCESS_COUNT" = current_minute."SUCCESS_COUNT" + EXCLUDED."SUCCESS_COUNT",
            "NOT_FOUND_COUNT" = CASE WHEN current_minute."NOT_FOUND_COUNT" IS NULL OR EXCLUDED."NOT_FOUND_COUNT" IS NULL
                THEN NULL ELSE current_minute."NOT_FOUND_COUNT" + EXCLUDED."NOT_FOUND_COUNT" END,
            "TOTAL_ELAPSED_MILLISECONDS" = current_minute."TOTAL_ELAPSED_MILLISECONDS" + EXCLUDED."TOTAL_ELAPSED_MILLISECONDS",
            "MIN_ELAPSED_MILLISECONDS" = LEAST(current_minute."MIN_ELAPSED_MILLISECONDS", EXCLUDED."MIN_ELAPSED_MILLISECONDS"),
            "MAX_ELAPSED_MILLISECONDS" = GREATEST(current_minute."MAX_ELAPSED_MILLISECONDS", EXCLUDED."MAX_ELAPSED_MILLISECONDS"),
            "LATENCY_HISTOGRAM_JSON" = (
                SELECT jsonb_agg((COALESCE(current_minute."LATENCY_HISTOGRAM_JSON" ->> bucket.index, '0')::bigint +
                    COALESCE(EXCLUDED."LATENCY_HISTOGRAM_JSON" ->> bucket.index, '0')::bigint) ORDER BY bucket.index)
                FROM generate_series(0, 12) AS bucket(index)),
            "REVISION" = current_minute."REVISION" + 1,
            "UPDATED_AT_UTC" = EXCLUDED."UPDATED_AT_UTC";
        """;
}
