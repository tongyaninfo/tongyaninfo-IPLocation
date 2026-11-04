import { useCallback, useEffect, useState } from 'react';
import { Alert, Button } from 'antd';
import { getSettings, saveSettings } from '../../services/console/api';
import { useConsoleAction, useConsoleResource } from '../../models/ConsoleContext';
import type { ConsoleBasicSettings, QuerySettings } from '../../types/console';
import { ActionMessage, ConsolePanel, RefreshButton, ResourceState } from './ConsoleShared';

export function SettingsPage() {
  const loader = useCallback((signal: AbortSignal) => getSettings(signal), []);
  const resource = useConsoleResource(loader);
  const action = useConsoleAction();
  const [query, setQuery] = useState<QuerySettings | null>(null);
  const [basic, setBasic] = useState<ConsoleBasicSettings | null>(null);
  const [networks, setNetworks] = useState('');
  useEffect(() => {
    if (!resource.data) return;
    const { runtime } = resource.data;
    setQuery({ ...resource.data.query });
    // 兼容尚未返回 basic 的旧服务；保留显式 null，不能把“无限期”换回默认天数。
    setBasic({ ...(resource.data.basic ?? { accessLogRetentionDays: runtime.recentEventRetentionDays,
      retainFullSourceIp: runtime.retainFullSourceIp, sessionMinutes: runtime.sessionMinutes, queueCapacity: runtime.queueCapacity }) });
    setNetworks(resource.data.allowedNetworks.join('\n'));
  }, [resource.data]);
  const runtime = resource.data?.runtime;
  return <>
    <div className="console-toolbar"><span className="console-muted">查询、白名单和日志策略即时生效；队列容量重启后生效。</span><RefreshButton onClick={resource.reload} loading={resource.loading} /></div>
    <ActionMessage {...action} /><ResourceState {...resource} />
    {resource.data && query && basic && runtime ? <form aria-label="系统设置表单" onSubmit={event => {
      event.preventDefault();
      const current = resource.data!;
      void action.run(token => saveSettings({ revision: current.revision, query, basic,
        allowedNetworks: networks.split(/\r?\n/).map(value => value.trim()).filter(Boolean) }, token),
      '系统设置已保存，生效范围请查看各项说明', resource.reload);
    }}>
      <div className="console-settings-grid">
        <ConsolePanel title="公共查询设置" subtitle="默认源影响未指定 db 的查询；单源与整体截止时间共同限制等待。">
          <div className="console-settings-fields">
            <label className="console-field"><span>默认查询数据库</span><select value={query.defaultDatabase} onChange={event => setQuery({ ...query, defaultDatabase: event.target.value })}>{resource.data.providers.map(provider => <option key={provider.id} value={provider.id}>{provider.name}{provider.enabled && provider.ready ? '' : '（未就绪或未启用）'}</option>)}</select><small>只能保存已启用且文件就绪的来源。</small></label>
            {[{ key: 'providerTimeoutMs' as const, label: '单来源超时（ms）', min: 1 }, { key: 'totalTimeoutMs' as const, label: '整体查询超时（ms）', min: 1 }, { key: 'maxConcurrentSources' as const, label: '来源最大并发', min: 1 }].map(field => <label className="console-field" key={field.key}><span>{field.label}</span><input type="number" min={field.min} step="1" required value={query[field.key]} onChange={event => setQuery({ ...query, [field.key]: Number(event.target.value) })} /></label>)}
          </div>
        </ConsolePanel>
        <ConsolePanel title="管理入口 IP 白名单" subtitle="支持单个 IPv4 / IPv6 与 CIDR，每行一项。">
          <p className="console-current-source">当前可信连接 IP：<code>{resource.data.currentSourceAddress || '未识别'}</code></p>
          <label className="console-field"><span>允许访问的地址或网段</span><textarea rows={9} required value={networks} onChange={event => setNetworks(event.target.value)} placeholder={'127.0.0.1\n::1\n192.0.2.0/24'} /></label>
          <Alert type="info" showIcon title="保存前校验当前访问来源" description="服务端会拒绝移除当前访问来源的配置，防止误锁。保存后立即检查所有后续请求及已有会话。" />
        </ConsolePanel>
      </div>
      <ConsolePanel title="基础运行配置" subtitle="非敏感基础项可编辑；存储连接与会话密钥仍由服务器管理。">
        <div className="console-settings-fields">
          <label className="console-field"><span>访问日志保存天数</span><input aria-label="访问日志保存天数" type="number" min={0} max={365} step="1" value={basic.accessLogRetentionDays ?? ''} placeholder="留空：无限期保留" onChange={event => setBasic({ ...basic, accessLogRetentionDays: event.target.value === '' ? null : Number(event.target.value) })} /><small>留空表示无限期；0 表示保存后停止新增普通访问明细与统计。已有历史暂留，管理员操作审计独立保留。正数限制历史可读天数，不自动删除历史数据。</small></label>
          <label className="console-field"><span>完整来源 IP</span><select aria-label="完整来源 IP" value={String(basic.retainFullSourceIp)} onChange={event => setBasic({ ...basic, retainFullSourceIp: event.target.value === 'true' })}><option value="true">受控保留完整 IP</option><option value="false">按网段脱敏</option></select><small>保存后作用于新采集记录；不会改写已有历史。</small></label>
          <label className="console-field"><span>管理员会话时长（分钟）</span><input aria-label="管理员会话时长（分钟）" type="number" min={5} max={1440} step="1" required value={basic.sessionMinutes} onChange={event => setBasic({ ...basic, sessionMinutes: Number(event.target.value) })} /><small>影响之后的新登录，现有会话到期时间不变。</small></label>
          <label className="console-field"><span>日志队列容量</span><input aria-label="日志队列容量" type="number" min={1} max={1000000} step="1" required value={basic.queueCapacity} onChange={event => setBasic({ ...basic, queueCapacity: Number(event.target.value) })} /><small>重启服务后生效；当前运行容量 {runtime.queueCapacity.toLocaleString('zh-CN')} 条。</small></label>
        </div>
        {runtime.queueCapacityRequiresRestart ? <Alert type="warning" showIcon title="日志队列容量已保存，等待重启服务生效" /> : null}
        <dl className="console-runtime-grid">{[
          ['后台存储', runtime.databaseEnabled ? 'PostgreSQL 已启用' : '内存模式'], ['可信代理条目', String(runtime.trustedProxyCount)],
          ['转发链上限', `${runtime.forwardLimit} 跳`], ['配置修订号', String(resource.data.revision)],
        ].map(([label, value]) => <div key={label}><dt>{label}</dt><dd>{value}</dd></div>)}</dl>
      </ConsolePanel>
      <div className="console-save-bar"><p>保存将记录操作者、可信来源及脱敏差异。配置冲突时请刷新后重新编辑。</p><Button type="primary" htmlType="submit" loading={action.busy} disabled={resource.loading}>保存设置</Button></div>
    </form> : null}
  </>;
}
