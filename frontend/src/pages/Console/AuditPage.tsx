import { useCallback, useState } from 'react';
import { Button, Table, Tag } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { getAudits } from '../../services/console/api';
import { useConsoleResource } from '../../models/ConsoleContext';
import type { AuditRecord } from '../../types/console';
import { ConsolePanel, NoRecords, RefreshButton, ResourceState, timeText, toUtc } from './ConsoleShared';

export function AuditPage() {
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const loader = useCallback((signal: AbortSignal) => getAudits({ ...filters, page, pageSize }, signal), [filters, page, pageSize]);
  const resource = useConsoleResource(loader);
  const columns: ColumnsType<AuditRecord> = [
    { title: '时间（上海）', dataIndex: 'occurredAtUtc', width: 185, render: timeText },
    { title: '操作者', dataIndex: 'actor', width: 145, render: value => value || '未识别身份' },
    { title: '来源 IP', dataIndex: 'sourceAddress', width: 170, render: value => <code>{value}</code> },
    { title: '操作', dataIndex: 'action', width: 180 },
    { title: '目标', width: 210, render: (_, row) => <span>{row.targetType}<small className="console-table-secondary">{row.targetId || '—'}</small></span> },
    { title: '结果', dataIndex: 'result', width: 110, render: value => <Tag color={/success|succeed|activated|completed/i.test(value) ? 'green' : /fail|denied|reject/i.test(value) ? 'red' : 'blue'}>{value}</Tag> },
  ];
  return <><ConsolePanel title="管理审计查询" subtitle="登录、配置、上传与激活使用独立可靠审计，关键操作失败也会保留结果。" extra={<RefreshButton onClick={resource.reload} loading={resource.loading} />}><form className="console-filter-form" aria-label="管理审计筛选" onSubmit={event => { event.preventDefault(); setPage(1); setFilters({ ...draft, fromUtc: toUtc(draft.fromUtc || ''), toUtc: toUtc(draft.toUtc || '') }); }}>{[{ key: 'actor', label: '操作者' }, { key: 'action', label: '操作' }, { key: 'result', label: '结果' }, { key: 'traceId', label: 'Trace ID' }].map(field => <label className="console-field" key={field.key}><span>{field.label}</span><input value={draft[field.key] || ''} placeholder={`筛选${field.label}`} onChange={event => setDraft(value => ({ ...value, [field.key]: event.target.value }))} /></label>)}{[{ key: 'fromUtc', label: '开始时间' }, { key: 'toUtc', label: '结束时间' }].map(field => <label className="console-field" key={field.key}><span>{field.label}（本地时区）</span><input type="datetime-local" value={draft[field.key] || ''} onChange={event => setDraft(value => ({ ...value, [field.key]: event.target.value }))} /></label>)}<div className="console-filter-buttons"><Button type="primary" htmlType="submit">查询审计</Button><Button onClick={() => { setDraft({}); setFilters({}); setPage(1); }}>重置</Button></div></form></ConsolePanel><ResourceState {...resource} />{resource.data ? <ConsolePanel title="管理员操作记录" subtitle="展开查看请求追踪标识与脱敏差异。"><Table<AuditRecord> columns={columns} rowKey="id" dataSource={resource.data.items} scroll={{ x: 1010 }} size="middle" locale={{ emptyText: <NoRecords text="当前筛选范围暂无管理审计" /> }} pagination={{ current: page, pageSize, total: resource.data.total, showSizeChanger: true, showTotal: total => `共 ${total} 条审计`, onChange: (next, size) => { setPage(size !== pageSize ? 1 : next); setPageSize(size); } }} expandable={{ expandedRowRender: row => <div className="console-record-detail"><p>Trace ID：<code>{row.traceId}</code></p><p>审计 ID：<code>{row.id}</code></p><pre>{row.difference == null ? '没有附加差异' : JSON.stringify(row.difference, null, 2)}</pre></div> }} /></ConsolePanel> : null}</>;
}
