import { displayProviderVersion } from '../../utils/presentation';
import { useCallback, useState } from 'react';
import { Alert, Segmented, Tag } from 'antd';
import { getDatabases, getOverview } from '../../services/console/api';
import { useConsoleResource } from '../../models/ConsoleContext';
import { Icon } from '../../components/Icon';
import { ProviderLogo } from '../../components/ProviderLogo';
import type { ConsoleOverview } from '../../types/console';
import { ConsolePanel, NoRecords, QueueHealth, RefreshButton, ResourceState, SourceTag, numberText, timeText } from './ConsoleShared';
import { TrendChart } from './components/TrendChart';

export function RequestTrend({ points, fromUtc, toUtc }: { points: ConsoleOverview['trend']; fromUtc: string; toUtc: string }) {
  return <TrendChart points={points} fromUtc={fromUtc} toUtc={toUtc} title="公共请求趋势" requestLabel="公共请求" queryLabel="逻辑查询" emptyText="当前时间范围暂无访问趋势" />;
}
export function OverviewPage() {
  const [hours, setHours] = useState(24);
  const loader = useCallback((signal: AbortSignal) => {
    const to = new Date(); const from = new Date(to.getTime() - hours * 3600000);
    return Promise.all([getOverview({ fromUtc: from.toISOString(), toUtc: to.toISOString() }, signal), getDatabases(signal)]).then(([overview, databases]) => ({ overview, databases }));
  }, [hours]);
  const resource = useConsoleResource(loader);
  const overview = resource.data?.overview;
  const databases = resource.data?.databases;
  const errorRate = overview?.totals.requests ? (overview.totals.clientErrors + overview.totals.serverErrors) / overview.totals.requests * 100 : null;
  const mcpPanel = overview ? <ConsolePanel title="MCP 调用趋势" subtitle={`${timeText(overview.fromUtc)} — ${timeText(overview.toUtc)}`}>
    {overview.mcpTotals && overview.mcpTrend ? <>
      <div className="console-mcp-totals"><span>MCP 请求 <strong>{numberText(overview.mcpTotals.requests)}</strong></span><span>MCP 定位查询 <strong>{numberText(overview.mcpTotals.queryRequests)}</strong></span><span>HTTP 错误 <strong>{numberText(overview.mcpTotals.httpErrors)}</strong></span></div>
      <TrendChart points={overview.mcpTrend} fromUtc={overview.fromUtc} toUtc={overview.toUtc} title="MCP 请求趋势" requestLabel="MCP 请求" queryLabel="MCP 定位查询" emptyText="当前时间范围暂无 MCP 趋势" />
      <p className="console-chart-caption">MCP 请求包含初始化和目录读取；定位查询仅统计 query_ip / query_my_ip。HTTP 错误不等于全部工具业务失败。</p>
    </> : <NoRecords text="服务尚未返回 MCP 统计" />}
  </ConsolePanel> : null;
  return <><div className="console-toolbar"><div className="console-range-label"><span className="status-dot is-ready" />服务运行情况 <span>真实采集数据</span></div><div className="console-toolbar-actions"><Segmented aria-label="统计时间范围" value={hours} onChange={value => setHours(Number(value))} options={[{ label: '近 24 小时', value: 24 }, { label: '近 7 天', value: 168 }]} /><RefreshButton onClick={resource.reload} loading={resource.loading} /></div></div><ResourceState {...resource} />{overview && databases ? <><div className="console-metric-grid">{[
    { label: '公共请求', value: numberText(overview.totals.requests), detail: '首页 / 纯 IP / 查询与来源元数据', icon: 'globe' },
    { label: '首页 HTML', value: numberText(overview.totals.homeHtmlRequests), detail: '浏览器页面请求次数', icon: 'external' },
    { label: '纯 IP 调用', value: numberText(overview.totals.plainIpRequests), detail: 'curl / wget 等根路径请求', icon: 'terminal' },
    { label: '逻辑查询', value: numberText(overview.totals.queryRequests), detail: '一次聚合请求计一次', icon: 'search' },
  ].map(item => <div className="console-metric-card" key={item.label}><div className="console-metric-top"><span>{item.label}</span><Icon name={item.icon} size={19} /></div><strong>{item.value}</strong><p>{item.detail}</p></div>)}</div><div className="console-dashboard-main"><ConsolePanel title="公共访问趋势" subtitle={`${timeText(overview.fromUtc)} — ${timeText(overview.toUtc)}`} extra={<Tag variant="filled" color="#013187">{overview.storageKind === 'postgresql' ? 'PostgreSQL' : overview.storageKind}</Tag>}><RequestTrend points={overview.trend} fromUtc={overview.fromUtc} toUtc={overview.toUtc} /></ConsolePanel><ConsolePanel title="查询质量" subtitle="基于已采集请求的统计窗口"><div className="console-quality"><div><span>HTTP 错误率</span><strong>{errorRate === null ? '—' : `${errorRate.toFixed(2)}%`}</strong><small>4xx + 5xx / 公共请求</small></div><div><span>P95 响应耗时</span><strong>{overview.totals.p95Milliseconds === null ? '—' : `${overview.totals.p95Milliseconds.toFixed(1)}`}<small>ms</small></strong><small>直方图桶上界估算</small></div><div><span>平均响应耗时</span><b>{overview.totals.averageMilliseconds === null ? '无样本' : `${overview.totals.averageMilliseconds.toFixed(2)} ms`}</b></div><div><span>取消请求</span><b>{numberText(overview.totals.cancelledRequests)}</b></div></div></ConsolePanel></div>{mcpPanel}<ConsolePanel title="数据源运行状态" subtitle="启用状态与文件就绪独立显示；缺失字段不补造。" extra={<a href="/console/databases">管理数据库 →</a>}><div className="console-source-grid">{databases.providers.map(provider => <div className="console-source-tile" key={provider.id}><div><ProviderLogo provider={provider} compact /><strong>{provider.name}</strong>{databases.defaultDatabase === provider.id ? <Tag color="blue">默认</Tag> : null}</div><SourceTag status={provider.status} /><p>{provider.version ? displayProviderVersion(provider.id, provider.version) : '暂无数据版本'}</p><small>{[provider.supportsIpv4 ? 'IPv4' : '', provider.supportsIpv6 ? 'IPv6' : ''].filter(Boolean).join(' / ') || '地址族待确认'}</small></div>)}</div></ConsolePanel><div className="console-dashboard-bottom"><ConsolePanel title="访问采集健康" subtitle="普通访问日志尽力记录，队列或存储故障可能产生缺口。"><QueueHealth queue={overview.queue} /></ConsolePanel><ConsolePanel title="最近管理操作" subtitle="可靠审计 · 与普通访问队列分离" extra={<a href="/console/audit">全部审计 →</a>}>{overview.recentAudits.length ? <div className="console-audit-timeline">{overview.recentAudits.slice(0, 5).map(item => <div key={item.id}><i /><div><strong>{item.action}</strong><p>{item.actor || '未识别身份'} · {item.result}</p></div><time>{timeText(item.occurredAtUtc)}</time></div>)}</div> : <NoRecords text="暂无管理审计记录" />}</ConsolePanel></div>{overview.notes.map((note, index) => <Alert key={index} type="info" showIcon title={note} className="console-note" />)}<p className="console-data-caption">更新于 {timeText(overview.capturedAtUtc)} · 当前进程启动于 {timeText(overview.startedAtUtc)} · 统计可读范围 {overview.retentionDays} 天 · 请求次数不代表真实用户人数。</p></> : null}</>;
}
