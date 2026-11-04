import { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Modal, Progress, Table, Tag } from 'antd';
import type { ColumnsType } from 'antd/es/table';
import { activateDatabase, getDatabases, saveDatabaseOrder, setDatabaseState, uploadDatabase } from '../../services/console/api';
import { useConsoleAction, useConsoleResource } from '../../models/ConsoleContext';
import type { DatabaseVersion, ManagedProvider } from '../../types/console';
import { ProviderLogo } from '../../components/ProviderLogo';
import { displayProviderVersion } from '../../utils/presentation';
import { ActionMessage, ConsolePanel, NoRecords, RefreshButton, ResourceState, SourceTag, bytesText, timeText } from './ConsoleShared';

export function DatabasesPage() {
  const loader = useCallback((signal: AbortSignal) => getDatabases(signal), []);
  const resource = useConsoleResource(loader);
  const action = useConsoleAction();
  const [selected, setSelected] = useState<string | null>(null);
  const [displayOrder, setDisplayOrder] = useState<string[]>([]);
  const savedOrderSnapshot = useRef('');
  const [pendingDisable, setPendingDisable] = useState<ManagedProvider | null>(null);
  const [replacement, setReplacement] = useState('');
  const [uploadProvider, setUploadProvider] = useState<ManagedProvider | null>(null);
  const [file, setFile] = useState<File | null>(null);
  const [family, setFamily] = useState('dual');
  const [progress, setProgress] = useState(0);
  const [fileError, setFileError] = useState('');
  const uploadController = useRef<AbortController | null>(null);
  useEffect(() => () => uploadController.current?.abort(), []);
  useEffect(() => {
    if (!resource.data) return;
    const snapshot = JSON.stringify([resource.data.displayOrderRevision, resource.data.providers.map(item => item.id)]);
    // 普通启停刷新不丢弃未保存的排序；仅在服务端顺序/修订变化或显式刷新时恢复服务端快照。
    if (savedOrderSnapshot.current !== snapshot) {
      savedOrderSnapshot.current = snapshot;
      setDisplayOrder(resource.data.providers.map(item => item.id));
    }
  }, [resource.data]);
  const positions = new Map(displayOrder.map((id, index) => [id, index]));
  const orderedProviders = [...(resource.data?.providers || [])].sort((first, second) =>
    (positions.get(first.id) ?? Infinity) - (positions.get(second.id) ?? Infinity));
  const orderChanged = resource.data !== null && orderedProviders.some((item, index) => item.id !== resource.data!.providers[index].id);
  const provider = orderedProviders.find(item => item.id === selected) || orderedProviders[0];
  function refreshDatabases() { savedOrderSnapshot.current = ''; resource.reload(); }
  function moveProvider(id: string, direction: -1 | 1) {
    if (action.busy) return;
    const next = orderedProviders.map(item => item.id);
    const index = next.indexOf(id), target = index + direction;
    if (index < 0 || target < 0 || target >= next.length) return;
    [next[index], next[target]] = [next[target], next[index]];
    setDisplayOrder(next);
  }
  function saveOrder() {
    if (!resource.data || !orderChanged || action.busy) return;
    const body = { providerIds: orderedProviders.map(item => item.id), revision: resource.data.displayOrderRevision };
    void action.run(token => saveDatabaseOrder(body, token), '数据库排序已保存，首页卡片和署名将按此顺序展示', resource.reload);
  }
  const columns: ColumnsType<DatabaseVersion> = [
    { title: '数据版本', dataIndex: 'version', width: 190, render: (value: string) => provider ? displayProviderVersion(provider.id, value) : value },
    { title: '格式 / 地址族', width: 150, render: (_, item) => `${item.format} / ${item.addressFamily}` },
    { title: '大小', dataIndex: 'sizeBytes', width: 110, render: bytesText },
    { title: '状态', width: 140, render: (_, item) => item.active ? <Tag color="green">当前生效</Tag> : <Tag>{item.status}</Tag> },
    { title: '接收时间（上海）', dataIndex: 'createdAtUtc', width: 185, render: timeText },
    { title: '操作', width: 120, render: (_, item) => <Button size="small" disabled={action.busy || item.active || !['ready', 'validated', 'active', 'inactive', 'retired'].includes(item.status.toLowerCase())} onClick={() => { if (provider) void action.run(token => activateDatabase(provider.id, { versionId: item.id, revision: provider.revision }, token), '数据库版本已激活，后续查询使用新版本', resource.reload); }}>激活此版本</Button> },
  ];
  function openUpload(item: ManagedProvider) { setUploadProvider(item); setFile(null); setProgress(0); setFileError(''); setFamily(item.id === 'CZ88' ? 'ipv4' : item.supportsIpv6 ? item.supportsIpv4 ? 'dual' : 'ipv6' : 'ipv4'); }
  function toggleProvider(item: ManagedProvider) {
    if (item.enabled && resource.data?.defaultDatabase === item.id) {
      setPendingDisable(item);
      setReplacement(resource.data.providers.find(candidate => candidate.id !== item.id && candidate.enabled && candidate.ready)?.id || '');
      return;
    }
    void action.run(token => setDatabaseState(item.id, { enabled: !item.enabled, revision: item.revision }, token), item.name + (item.enabled ? '已停用' : '已启用'), resource.reload);
  }
  function submitUpload() {
    if (!uploadProvider || !file) return;
    const form = new FormData(); form.append('file', file); form.append('addressFamily', family);
    const controller = new AbortController(); uploadController.current = controller;
    void action.run(token => uploadDatabase(uploadProvider.id, form, token, setProgress, controller.signal), '数据库已上传并校验，请在版本列表显式激活', () => { setUploadProvider(null); resource.reload(); });
  }
  return <><div className="console-toolbar"><span className="console-muted">启用状态直接对应公共查询；上传、校验与激活分别管理。</span><RefreshButton onClick={refreshDatabases} loading={resource.loading || action.busy} /></div><ActionMessage {...action} /><ResourceState {...resource} />{resource.data ? <><ConsolePanel title="数据库显示顺序" subtitle="调整后保存，同步首页卡片与页脚署名；停用来源也保留在排序中。"><ol className="console-provider-order" aria-label="数据库排序">{orderedProviders.map((item, index) => <li key={item.id}><span className="console-order-position">{index + 1}</span><div className="console-order-name"><strong>{item.name}</strong><code>{item.id}</code></div><div className="console-order-controls"><Button size="small" aria-label={`上移${item.name}`} disabled={action.busy || index === 0} onClick={() => moveProvider(item.id, -1)}>上移</Button><Button size="small" aria-label={`下移${item.name}`} disabled={action.busy || index === orderedProviders.length - 1} onClick={() => moveProvider(item.id, 1)}>下移</Button></div></li>)}</ol><div className="console-order-save"><span aria-live="polite">{orderChanged ? '有未保存的顺序调整；刷新将载入服务端顺序。' : '当前为已保存的显示顺序。'}</span><div><Button disabled={!orderChanged || action.busy} onClick={() => setDisplayOrder(resource.data!.providers.map(item => item.id))}>取消调整</Button><Button type="primary" disabled={!orderChanged || action.busy} loading={action.busy && orderChanged} onClick={saveOrder}>保存排序</Button></div></div></ConsolePanel><div className="console-database-grid">{orderedProviders.map(item => <section className={`console-database-card${provider?.id === item.id ? ' selected' : ''}`} key={item.id}><div className="console-database-heading"><ProviderLogo provider={item} compact /><div><h2>{item.name}</h2><code>{item.id}</code></div>{resource.data!.defaultDatabase === item.id ? <Tag color="blue">默认查询</Tag> : null}</div><div className="console-database-status"><SourceTag status={item.status} /><span>{item.enabled ? '已启用' : '已停用'}</span></div><p className="console-database-version">{item.version ? displayProviderVersion(item.id, item.version) : '暂无活动数据版本'}</p><p className="console-muted">{item.message || (item.ready ? '当前文件可供查询' : '需要上传可用数据文件')}</p><div className="console-database-capability"><span>{[item.supportsIpv4 ? 'IPv4' : '', item.supportsIpv6 ? 'IPv6' : ''].filter(Boolean).join(' / ') || '未适配'}</span><span>{item.versions.length} 个托管版本</span></div><div className="console-database-actions"><Button disabled={action.busy} onClick={() => toggleProvider(item)}>{item.enabled ? '停用' : '启用'}</Button><Button type="primary" disabled={action.busy} onClick={() => openUpload(item)}>上传新版</Button><Button type="link" onClick={() => setSelected(item.id)}>版本记录</Button></div></section>)}</div>{provider ? <ConsolePanel title={`${provider.name} · 版本与上传`} subtitle="校验通过后显式激活；旧版本不自动删除，现有本机来源文件保持只读。" extra={<select className="console-provider-picker" aria-label="选择版本来源" value={provider.id} onChange={event => setSelected(event.target.value)}>{orderedProviders.map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select>}><Table<DatabaseVersion> rowKey="id" columns={columns} dataSource={provider.versions} size="middle" pagination={false} scroll={{ x: 930 }} locale={{ emptyText: <NoRecords text="暂无后台上传版本，现有配置文件仍按来源状态只读使用" /> }} expandable={{ expandedRowRender: item => <div className="console-record-detail"><p>SHA-256：<code>{item.sha256}</code></p><p>版本 ID：<code>{item.id}</code></p></div> }} />{provider.uploads.length ? <div className="console-upload-history"><h3>最近上传任务</h3>{provider.uploads.slice(0, 5).map(upload => <div key={upload.id}><Tag color={upload.errorCode ? 'red' : 'blue'}>{upload.state}</Tag><span>{upload.phase} · {bytesText(upload.receivedBytes)}</span><time>{timeText(upload.startedAtUtc)}</time>{upload.errorCode ? <code>{upload.errorCode}</code> : null}</div>)}</div> : null}</ConsolePanel> : null}<Alert type="info" showIcon title="数据库文件要求" description={`当前支持原始 .mmdb / .xdb / .dat，单文件上限 ${bytesText(resource.data.uploadMaxBytes)}。不接受压缩包、任意下载 URL，不提供整库公开下载。校验仅检查文件格式、大小及读取能力。`} /></> : null}<Modal title="停用默认数据库" open={Boolean(pendingDisable)} onCancel={() => { if (!action.busy) setPendingDisable(null); }} footer={null} closable={!action.busy} mask={{ closable: !action.busy }} destroyOnHidden><form className="console-upload-form" aria-label="停用默认数据库表单" onSubmit={event => { event.preventDefault(); if (pendingDisable && replacement) void action.run(token => setDatabaseState(pendingDisable.id, { enabled: false, revision: pendingDisable.revision, defaultDatabase: replacement }, token), '默认数据库已切换，原数据库已停用', () => { setPendingDisable(null); resource.reload(); }); }}><Alert type="info" showIcon title="同时选择新的默认来源" description="未指定 db 的公开查询必须保留一个已启用且文件就绪的默认数据库。" /><label className="console-field"><span>替代默认数据库</span><select required value={replacement} onChange={event => setReplacement(event.target.value)} disabled={action.busy}><option value="">请选择已就绪来源</option>{resource.data?.providers.filter(item => item.id !== pendingDisable?.id && item.enabled && item.ready).map(item => <option key={item.id} value={item.id}>{item.name}</option>)}</select></label>{action.error ? <Alert type="error" title={action.error} /> : null}<Button type="primary" htmlType="submit" disabled={!replacement} loading={action.busy}>切换默认并停用</Button></form></Modal><Modal title={`${uploadProvider?.name || ''} · 上传新版数据库`} open={Boolean(uploadProvider)} onCancel={() => { if (!action.busy) setUploadProvider(null); }} footer={null} closable={!action.busy} mask={{ closable: !action.busy }} destroyOnHidden><form className="console-upload-form" aria-label="上传数据库表单" onSubmit={event => { event.preventDefault(); submitUpload(); }}><label className="console-field"><span>数据库文件（原始 MMDB / XDB / DAT）</span><input aria-label="数据库文件（原始 MMDB / XDB / DAT）" type="file" accept={uploadProvider?.id === 'CZ88' ? '.dat' : uploadProvider?.id === 'ip2region' ? '.xdb' : '.mmdb'} disabled={action.busy} required onChange={event => { const next = event.target.files?.[0] || null; const valid = !next || !resource.data || next.size <= resource.data.uploadMaxBytes; setFile(valid ? next : null); setFileError(valid ? '' : '文件超过允许的上传大小。'); }} /><small>{file ? `${file.name} · ${bytesText(file.size)}` : '文件由服务端命名并保存在受控目录。'}</small></label><label className="console-field"><span>地址族</span><select value={family} disabled={action.busy || uploadProvider?.id === 'CZ88'} onChange={event => setFamily(event.target.value)}><option value="ipv4">IPv4</option>{uploadProvider?.id !== 'CZ88' ? <option value="ipv6">IPv6</option> : null}{uploadProvider?.id !== 'ip2region' && uploadProvider?.id !== 'CZ88' ? <option value="dual">IPv4 + IPv6</option> : null}</select></label>{fileError ? <Alert type="error" title={fileError} /> : null}{action.error ? <Alert type="error" title="上传未完成" description={action.error} /> : null}{action.busy ? <><Progress percent={progress} status="active" /><p className="console-muted">{progress >= 100 ? '传输完成，服务端正在校验；请等待最终结果。' : '正在传输文件，请保持页面打开。'}</p></> : null}<Alert type="info" showIcon title="上传完成后仍需显式激活" description="校验失败不会替换当前可用版本。此操作不会自动删除任何旧文件。" /><Button block type="primary" htmlType="submit" disabled={!file} loading={action.busy}>上传并校验</Button></form></Modal></>;
}
