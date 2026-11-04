import { useCallback, useEffect, useState } from 'react';
import { Button, Table, Tag } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { getAccessLogs } from '../../services/console/api';
import { useConsoleResource } from '../../models/ConsoleContext';
import { displayProviderRegion, displayProviderVersion, providerVersionLabel } from '../../utils/presentation';
import type { AccessRecord, RequestHeadersSnapshot, SourceLocationSnapshot } from '../../types/console';
import { ConsolePanel, NoRecords, QueueHealth, RefreshButton, ResourceState, timeText, toUtc } from './ConsoleShared';

const sourceLocationStates: Record<string, { text: string; color: string }> = {
  success: { text: '已解析', color: 'green' }, notFound: { text: '无匹配', color: 'default' },
  special: { text: '特殊用途地址', color: 'cyan' }, unsupported: { text: '不支持此地址', color: 'default' },
  unavailable: { text: '解析失败', color: 'red' }, timeout: { text: '解析超时', color: 'orange' },
  disabled: { text: '来源已停用', color: 'default' }, notReady: { text: '来源未就绪', color: 'default' },
  invalid: { text: '地址无效', color: 'red' }, cancelled: { text: '解析取消', color: 'default' },
};
function LocationState({ status }: { status: string }) {
  const view = sourceLocationStates[status] || { text: status, color: 'default' };
  return <Tag color={view.color}>{view.text}</Tag>;
}
function SourceLocationSummary({ snapshot }: { snapshot?: SourceLocationSnapshot | null }) {
  if (!snapshot) return <span className="console-table-secondary">未采集</span>;
  const location = [snapshot.country, snapshot.province, snapshot.city].filter(Boolean).join(' · ')
    || displayProviderRegion(snapshot.provider, snapshot.rawLocation) || '地区信息未提供';
  return <div className="console-source-location-summary">
    {snapshot.status === 'success' ? <span>{location}</span> : <LocationState status={snapshot.status} />}
    {snapshot.isp ? <small>{snapshot.provider === 'CZ88' ? '网络描述' : '运营商'}：{snapshot.isp}</small> : null}
  </div>;
}
export function SourceLocationDetail({ snapshot }: { snapshot?: SourceLocationSnapshot | null }) {
  if (!snapshot) return <section className="console-source-location" aria-label="来源地址归属地详情"><h3>来源地址归属地</h3><p className="console-muted">未采集来源地址归属地</p></section>;
  return <section className="console-source-location" aria-label="来源地址归属地详情">
    <h3>来源地址归属地</h3>
    <div className="console-source-location-meta"><LocationState status={snapshot.status} /><span>来源：{snapshot.provider}</span>{snapshot.version ? <span>{providerVersionLabel(snapshot.provider, snapshot.version)}：{displayProviderVersion(snapshot.provider, snapshot.version)}</span> : <span>版本未提供</span>}</div>
    <dl>
      <div><dt>国家</dt><dd>{snapshot.country || '未提供'}</dd></div>
      <div><dt>省 / 州</dt><dd>{snapshot.province || '未提供'}</dd></div>
      <div><dt>城市</dt><dd>{snapshot.city || '未提供'}</dd></div>
      <div><dt>{snapshot.provider === 'CZ88' ? '网络描述' : '运营商'}</dt><dd>{snapshot.isp || '未提供'}</dd></div>
      <div><dt>来源地区文本</dt><dd>{displayProviderRegion(snapshot.provider, snapshot.rawLocation) || '未提供'}</dd></div>
    </dl>
  </section>;
}

export function RequestHeadersDetail({ snapshot }: { snapshot?: RequestHeadersSnapshot | null }) {
  if (!snapshot) return <section className="console-request-headers" aria-label="请求头详情"><h3>请求头</h3><p className="console-muted">未采集请求头</p></section>;
  const entries = Object.entries(snapshot.headers);
  // 头名按HTTP语义忽略大小写；只作文本展示，Referer和其他值均不自动生成链接或HTML。
  const valuesFor = (name: string) => entries.filter(([key]) => key.toLowerCase() === name).flatMap(([, values]) => values);
  const others = entries.filter(([name]) => !['user-agent', 'referer'].includes(name.toLowerCase()));
  return <section className="console-request-headers" aria-label="请求头详情">
    <h3>请求头</h3>
    {snapshot.truncated ? <p className="console-headers-notice" role="note">请求头已截断，当前详情不完整。</p> : null}
    <dl>
      <div><dt>User-Agent</dt><dd>{valuesFor('user-agent').join('\n') || '未提供'}</dd></div>
      <div><dt>Referer</dt><dd>{valuesFor('referer').join('\n') || '未提供'}</dd></div>
      {others.map(([name, values]) => <div key={name}><dt>{name}</dt><dd>{values.join('\n') || '未提供'}</dd></div>)}
    </dl>
    {!others.length ? <p className="console-muted">未采集到其他请求头</p> : null}
  </section>;
}

function RequestPathDetail({ record }: { record: AccessRecord }) {
  return <section className="console-request-path" aria-label="请求路径详情">
    <h3>请求路径（含参数）</h3>
    {record.requestPath == null ? <p className="console-muted">未采集请求路径</p> : <>
      {record.requestPathTruncated ? <p className="console-headers-notice" role="note">请求路径已截断，当前详情不完整。</p> : null}
      <pre><code>{record.requestPath}</code></pre>
    </>}
  </section>;
}

export function AccessLogsPage() {
  const [filters, setFilters] = useState<Record<string, string>>({});
  const [draft, setDraft] = useState<Record<string, string>>({});
  const [page, setPage] = useState(1);
  const [pageSize, setPageSize] = useState(20);
  const loader = useCallback((signal: AbortSignal) => getAccessLogs({ ...filters, page, pageSize }, signal), [filters, page, pageSize]);
  const resource = useConsoleResource(loader);
  useEffect(() => {
    function changePage(event: KeyboardEvent) {
      if (!['ArrowLeft', 'ArrowRight'].includes(event.key) || event.defaultPrevented || event.repeat
        || event.altKey || event.ctrlKey || event.metaKey || event.shiftKey || event.isComposing
        || resource.loading || !resource.data || !resource.data.items.length) return;
      // 输入、可编辑区域及有自身方向键语义的控件保留原有交互，不抢占其按键。
      const interactive = 'input, textarea, select, button, a, [contenteditable]:not([contenteditable="false"]), [role="combobox"], [role="spinbutton"], [role="slider"], [role="listbox"], [role="menu"], [role="menubar"], [role="tablist"], [role="tree"], [role="grid"], [role="radiogroup"], [role="dialog"], [role="alertdialog"]';
      const target = event.target instanceof Element ? event.target : null;
      if (target?.closest(interactive) || document.activeElement?.closest(interactive)) return;
      const lastPage = Math.max(1, Math.ceil(resource.data.total / pageSize));
      const nextPage = Math.min(lastPage, Math.max(1, page + (event.key === 'ArrowRight' ? 1 : -1)));
      if (nextPage === page) return;
      event.preventDefault();
      setPage(nextPage);
    }
    document.addEventListener('keydown', changePage);
    return () => document.removeEventListener('keydown', changePage);
  }, [page, pageSize, resource.data, resource.loading]);
  const fields = [{ key: 'sourceAddress', label: '来源 IP', placeholder: '完整来源地址' }, { key: 'database', label: '数据库', placeholder: '如 geolite2' }, { key: 'routeCategory', label: '路由类别', placeholder: '如 plainIp / query' }, { key: 'statusCode', label: 'HTTP 状态', placeholder: '如 200 / 503' }, { key: 'format', label: '输出格式', placeholder: 'json / xml / txt' }];
  const columns: ColumnsType<AccessRecord> = [
    { title: '时间（上海）', dataIndex: 'occurredAtUtc', width: 185, render: timeText },
    { title: '来源 IP', dataIndex: 'sourceAddress', width: 180, render: value => <code>{value}</code> },
    { title: '来源归属地 / 网络', width: 250, render: (_, row) => <SourceLocationSummary snapshot={row.sourceLocation} /> },
    { title: '请求路径', dataIndex: 'requestPath', width: 300, render: value => value == null ? <span className="console-table-secondary">未采集路径</span> : <code className="console-request-path-summary">{value}</code> },
    { title: '请求类别', dataIndex: 'routeCategory', width: 120, render: value => <Tag>{value}</Tag> },
    { title: '方法 / 格式', width: 120, render: (_, row) => `${row.method} / ${row.format || '—'}` },
    { title: '数据库', dataIndex: 'database', width: 130, render: value => value || '—' },
    { title: '状态', dataIndex: 'statusCode', width: 90, render: value => <Tag color={value >= 500 ? 'red' : value >= 400 ? 'orange' : 'green'}>{value}</Tag> },
    { title: '耗时', dataIndex: 'elapsedMilliseconds', width: 110, render: value => `${Number(value).toFixed(2)} ms` },
    { title: '客户端', dataIndex: 'clientCategory', width: 100 },
  ];
  return <><ConsolePanel title="访问记录查询" subtitle="展开查看完整请求路径、来源地址归属地及请求头；支持键盘左右键翻页。" extra={<RefreshButton onClick={resource.reload} loading={resource.loading} />}><form className="console-filter-form" aria-label="访问记录筛选" onSubmit={event => { event.preventDefault(); setPage(1); setFilters({ ...draft, fromUtc: toUtc(draft.fromUtc || ''), toUtc: toUtc(draft.toUtc || '') }); }}>{fields.map(field => <label className="console-field" key={field.key}><span>{field.label}</span><input value={draft[field.key] || ''} placeholder={field.placeholder} onChange={event => setDraft(value => ({ ...value, [field.key]: event.target.value }))} /></label>)}{[{ key: 'fromUtc', label: '开始时间' }, { key: 'toUtc', label: '结束时间' }].map(field => <label className="console-field" key={field.key}><span>{field.label}（本地时区）</span><input type="datetime-local" value={draft[field.key] || ''} onChange={event => setDraft(value => ({ ...value, [field.key]: event.target.value }))} /></label>)}<div className="console-filter-buttons"><Button type="primary" htmlType="submit">查询记录</Button><Button onClick={() => { setDraft({}); setFilters({}); setPage(1); }}>重置</Button></div></form></ConsolePanel><ResourceState {...resource} />{resource.data ? <><div className="console-log-health"><QueueHealth queue={resource.data.queue} compact /></div><ConsolePanel title="公共请求明细" subtitle={`${timeText(resource.data.fromUtc)} — ${timeText(resource.data.toUtc)} · ${resource.data.retentionDays === null ? '无限期保留（单次查询仍有界）' : resource.data.retentionDays === 0 ? '已停止新增，历史暂留' : `历史可读范围 ${resource.data.retentionDays} 天`}`}><Table<AccessRecord> rowKey="id" size="middle" columns={columns} dataSource={resource.data.items} scroll={{ x: 1700 }} locale={{ emptyText: <NoRecords /> }} pagination={{ current: page, pageSize, total: resource.data.total, showSizeChanger: true, showTotal: total => `共 ${total} 条记录`, onChange: (next, size) => { setPage(size !== pageSize ? 1 : next); setPageSize(size); } }} expandable={{ expandedRowRender: row => <div className="console-record-detail"><p>Trace ID：<code>{row.traceId}</code></p><p>数据库版本：{row.databaseVersion ? displayProviderVersion(row.database || '', row.databaseVersion) : '未记录'} · 请求取消：{row.isCancelled ? '是' : '否'}</p><p>记录 ID：<code>{row.id}</code></p><RequestPathDetail record={row} /><SourceLocationDetail snapshot={row.sourceLocation} /><RequestHeadersDetail snapshot={row.requestHeaders} /></div> }} /></ConsolePanel><p className="console-data-caption">采集队列及故障指标属于当前进程窗口；存储故障、队列满和异常退出均可能产生统计缺口。</p></> : null}</>;
}
