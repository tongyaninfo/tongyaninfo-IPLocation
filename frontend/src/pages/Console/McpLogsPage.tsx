import { useCallback, useState } from 'react';
import { Button, Table, Tag } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { useConsoleResource } from '../../models/ConsoleContext';
import { getMcpLogs } from '../../services/console/api';
import type { AccessRecord, McpCallContext } from '../../types/console';
import { RequestHeadersDetail, SourceLocationDetail } from './AccessLogsPage';
import { ConsolePanel, NoRecords, QueueHealth, RefreshButton, ResourceState, timeText, toUtc } from './ConsoleShared';
import { McpPayload } from './components/McpPayload';

function McpContextDetail({ context }: { context?: McpCallContext | null }) {
  if (!context) return <section aria-label="MCP 调用上下文"><h3>MCP 调用上下文</h3><p className="console-muted">未采集 MCP 调用上下文</p></section>;
  const fields: [string, string | null][] = [
    ['协议版本', context.protocolVersion], ['协议方法', context.method], ['请求 ID', context.requestId], ['工具名称', context.toolName],
    ['客户端名称（自报）', context.clientName], ['客户端版本（自报）', context.clientVersion], ['查询 IP', context.ip], ['查询数据库', context.database],
    ['业务结果码', context.resultCode], ['业务状态', context.resultStatus], ['错误标记', context.isError ? '是' : '否'],
  ];
  return <section className="console-request-headers console-mcp-context" aria-label="MCP 调用上下文">
    <h3>MCP 调用上下文</h3>
    <p className="console-muted">匿名客户端信息由调用方提供，不代表已核验身份；展示已记录的协议上下文、受控入参和出参。</p>
    {context.truncated ? <p className="console-headers-notice" role="note">调用上下文已清洗或截断，以下展示实际保存的内容。</p> : null}
    <dl>{fields.map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value ?? '未采集'}</dd></div>)}</dl>
    <h4>来源结果摘要</h4>
    {context.sources.length ? <ul>{context.sources.map((source, index) => <li key={`${source.id}-${index}`}><code>{source.id}</code> · 状态：{source.status} · 版本：{source.version ?? '未提供'}</li>)}</ul> : <p className="console-muted">未采集来源结果摘要</p>}
    <McpPayload label="入参" value={context.input} truncated={context.inputTruncated} captured={context.inputCaptured} />
    <McpPayload label="出参" value={context.output} truncated={context.outputTruncated} captured={context.outputCaptured} />
  </section>;
}

export function McpLogsPage() {
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const loader = useCallback((signal: AbortSignal) => getMcpLogs({ ...filters, page, pageSize }, signal), [filters, page, pageSize]);
  const resource = useConsoleResource(loader);
  const fields = [
    { key: 'sourceAddress', label: '来源 IP', placeholder: '完整来源地址' },
    { key: 'toolName', label: '工具名称', placeholder: '如 query_ip' },
    { key: 'resultCode', label: '业务结果码', placeholder: '如 OK / INVALID_IP' },
    { key: 'traceId', label: 'Trace ID', placeholder: '请求追踪标识' },
  ];
  const columns: ColumnsType<AccessRecord> = [
    { title: '时间（上海）', dataIndex: 'occurredAtUtc', width: 185, render: timeText },
    { title: '来源 IP', dataIndex: 'sourceAddress', width: 180, render: value => <code>{value}</code> },
    { title: '客户端（自报）', width: 180, render: (_, row) => row.mcpContext?.clientName ? <span>{row.mcpContext.clientName}<small className="console-table-secondary">{row.mcpContext.clientVersion || '版本未采集'}</small></span> : '未采集' },
    { title: '协议方法 / 工具', width: 190, render: (_, row) => <span>{row.mcpContext?.method || '未采集'}<small className="console-table-secondary">{row.mcpContext?.toolName || '—'}</small></span> },
    { title: '业务结果', width: 160, render: (_, row) => row.mcpContext?.resultCode ? <Tag color={row.mcpContext.isError ? 'orange' : 'green'}>{row.mcpContext.resultCode}</Tag> : '未采集' },
    { title: 'HTTP 状态', dataIndex: 'statusCode', width: 100, render: value => <Tag color={value >= 500 ? 'red' : value >= 400 ? 'orange' : 'green'}>{value}</Tag> },
    { title: '耗时', dataIndex: 'elapsedMilliseconds', width: 110, render: value => `${Number(value).toFixed(2)} ms` },
  ];
  return <>
    <ConsolePanel title="MCP 调用审计查询" subtitle="公开 MCP 请求的协议、工具和查询上下文；客户端信息为匿名自报。" extra={<RefreshButton onClick={resource.reload} loading={resource.loading} />}>
      <form className="console-filter-form" aria-label="MCP 调用审计筛选" onSubmit={event => {
        event.preventDefault(); setPage(1); setFilters({ ...draft, fromUtc: toUtc(draft.fromUtc || ''), toUtc: toUtc(draft.toUtc || '') });
      }}>
        {fields.map(field => <label className="console-field" key={field.key}><span>{field.label}</span><input value={draft[field.key] || ''} placeholder={field.placeholder} onChange={event => setDraft(value => ({ ...value, [field.key]: event.target.value }))} /></label>)}
        {[{ key: 'fromUtc', label: '开始时间' }, { key: 'toUtc', label: '结束时间' }].map(field => <label className="console-field" key={field.key}><span>{field.label}（本地时区）</span><input type="datetime-local" value={draft[field.key] || ''} onChange={event => setDraft(value => ({ ...value, [field.key]: event.target.value }))} /></label>)}
        <div className="console-filter-buttons"><Button type="primary" htmlType="submit">查询调用</Button><Button onClick={() => { setDraft({}); setFilters({}); setPage(1); }}>重置</Button></div>
      </form>
    </ConsolePanel>
    <ResourceState {...resource} />
    {resource.data ? <>
      <div className="console-log-health"><QueueHealth queue={resource.data.queue} compact /></div>
      <ConsolePanel title="MCP 请求明细" subtitle={`${timeText(resource.data.fromUtc)} — ${timeText(resource.data.toUtc)} · ${resource.data.retentionDays === null ? '不设年龄读取截止（单次查询仍有界）' : resource.data.retentionDays === 0 ? '已停止新增，历史暂留' : `历史可读范围 ${resource.data.retentionDays} 天`}`}>
        <Table<AccessRecord> columns={columns} rowKey="id" dataSource={resource.data.items} scroll={{ x: 1200 }} size="middle" locale={{ emptyText: <NoRecords text="当前筛选范围暂无 MCP 调用" /> }}
          pagination={{ current: page, pageSize, total: resource.data.total, showSizeChanger: true, showTotal: total => `共 ${total} 条调用`, onChange: (next, size) => { setPage(size !== pageSize ? 1 : next); setPageSize(size); } }}
          expandable={{ expandedRowRender: row => <div className="console-record-detail"><p>Trace ID：<code>{row.traceId}</code></p><p>记录 ID：<code>{row.id}</code> · 请求取消：{row.isCancelled ? '是' : '否'}</p><McpContextDetail context={row.mcpContext} /><SourceLocationDetail snapshot={row.sourceLocation} /><RequestHeadersDetail snapshot={row.requestHeaders} /></div> }} />
      </ConsolePanel>
    </> : null}
    <p className="console-data-caption">MCP 日志沿用普通访问日志的采集开关和保留策略；保存天数设为 0 时停止新增。请求采用有界队列异步记录，存储故障、队列满和异常退出可能造成缺口；入参和出参以有界受控快照保存，不采集完整聊天或凭据，超限时明确标记截断。</p>
  </>;
}
