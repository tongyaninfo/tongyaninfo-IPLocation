import { Alert, Button, Empty, Skeleton, Tag } from 'antd';
import type { ReactNode } from 'react';
import type { AccessQueueMetrics } from '../../types/console';
import { statusPresentation } from '../../utils/presentation';
import { Icon } from '../../components/Icon';

export const numberText = (value: number) => value.toLocaleString('zh-CN');
export const timeText = (value?: string | null) => value ? new Date(value).toLocaleString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false }) : '暂无记录';
export const shortTime = (value: string) => new Date(value).toLocaleTimeString('zh-CN', { timeZone: 'Asia/Shanghai', hour: '2-digit', minute: '2-digit', hour12: false });
export const bytesText = (value: number) => value >= 1024 ** 3 ? `${(value / 1024 ** 3).toFixed(2)} GiB` : value >= 1024 ** 2 ? `${(value / 1024 ** 2).toFixed(1)} MiB` : `${numberText(value)} B`;
export const toUtc = (value: string) => value ? new Date(value).toISOString() : '';
export function SourceTag({ status }: { status: string }) { const view = statusPresentation(status); return <Tag color={view.color}>{view.text}</Tag>; }
export function ConsolePanel({ title, subtitle, extra, children, className = '' }: { title: string; subtitle?: string; extra?: ReactNode; children: ReactNode; className?: string }) {
  return <section className={`console-panel ${className}`}><div className="console-panel-heading"><div><h2>{title}</h2>{subtitle ? <p>{subtitle}</p> : null}</div>{extra}</div>{children}</section>;
}
export function ResourceState({ loading, error, reload }: { loading: boolean; error: string; reload: () => void }) {
  if (loading) return <div className="console-panel console-loading" role="status" aria-label="正在载入管理数据"><Skeleton active paragraph={{ rows: 6 }} /></div>;
  if (error) return <Alert type="error" showIcon title="管理数据加载失败" description={error} action={<Button onClick={reload}>重试</Button>} role="alert" />;
  return null;
}
export function ActionMessage({ error, success }: { error: string; success: string }) {
  return <div className="console-action-messages" aria-live="polite">{error ? <Alert type="error" showIcon title="操作未完成" description={error} role="alert" /> : null}{success ? <Alert type="success" showIcon title={success} /> : null}</div>;
}
export function NoRecords({ text = '当前筛选范围内暂无记录' }: { text?: string }) { return <Empty image={Empty.PRESENTED_IMAGE_SIMPLE} description={text} />; }
export function RefreshButton({ onClick, loading = false }: { onClick: () => void; loading?: boolean }) { return <Button icon={<Icon name="refresh" size={15} />} onClick={onClick} loading={loading}>刷新数据</Button>; }
export function QueueHealth({ queue, compact = false }: { queue: AccessQueueMetrics; compact?: boolean }) {
  const unhealthy = queue.dropped > 0 || queue.writeFailures > 0 || !queue.isAccepting;
  return <div className={`queue-health${compact ? ' queue-health-compact' : ''}`}><div className="queue-health-status"><span className={`status-dot${unhealthy ? '' : ' is-ready'}`} /><strong>{unhealthy ? '采集存在缺口或故障' : '访问队列运行中'}</strong><span>当前进程窗口</span></div><div className="queue-health-numbers"><div><span>队列积压</span><strong>{numberText(queue.queued)}</strong></div><div><span>正在写入</span><strong>{numberText(queue.inFlight)}</strong></div><div><span>已写入</span><strong>{numberText(queue.written)}</strong></div>{queue.suppressed ? <div><span>按策略跳过</span><strong>{numberText(queue.suppressed)}</strong></div> : null}<div className={queue.dropped ? 'metric-warning' : ''}><span>已丢弃</span><strong>{numberText(queue.dropped)}</strong></div><div className={queue.writeFailures ? 'metric-warning' : ''}><span>写入失败</span><strong>{numberText(queue.writeFailures)}</strong></div></div><p className="console-muted">最近成功写入：{timeText(queue.lastSuccessUtc)}{queue.lastFailureUtc ? ` · 最近失败：${timeText(queue.lastFailureUtc)}` : ''}</p></div>;
}
