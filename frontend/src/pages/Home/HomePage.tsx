import { Alert, Button, Input, Skeleton, Tag } from 'antd';
import { Fragment, useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { DeferredApiExamples } from '../../components/DeferredApiExamples';
import { Brand } from '../../components/Brand';
import { CopyButton } from '../../components/CopyButton';
import { Icon } from '../../components/Icon';
import { ThemeSwitcher } from '../../components/ThemeSwitcher';
import { ProviderCard } from '../../components/ProviderCard';
import { useIpLookup } from '../../models/useIpLookup';
import { addressTypeText, sourceVisuals } from '../../utils/presentation';

export default function HomePage() {
  const model = useIpLookup();
  const [input, setInput] = useState('');
  const [inputError, setInputError] = useState(false);
  const inputEdited = useRef(false);
  useEffect(() => {
    // 仅用后端识别的访问地址预填；迟到的自动请求不能覆盖用户已输入或主动清空的内容。
    if (model.visitorIp && !inputEdited.current) {
      setInput(model.visitorIp);
      setInputError(false);
    }
  }, [model.visitorIp]);
  const query = model.response?.result.data;
  const businessError = model.response && !model.response.result.success ? model.response.result.message : null;
  const visibleProviders = model.providers.filter(provider => provider.enabled === true);
  const readyCount = visibleProviders.filter(provider => provider.ready).length;
  const queriedSources = new Map(query?.sources.filter(source => ['success', 'notFound'].includes(source.status)).map(source => [source.id, source]));
  // 以当前启用列表的顺序展示卡片和署名，查询返回顺序或旧结果不能复活停用来源。
  // 纯真持续按启用状态署名，不依赖文件就绪或本次查询结果。
  const usedSources = visibleProviders.filter(provider => provider.ready || provider.id === 'CZ88' || queriedSources.has(provider.id))
    .map(provider => queriedSources.get(provider.id) || provider);
  const queriedTime = model.queriedAt?.toLocaleTimeString('zh-CN', { timeZone: 'Asia/Shanghai', hour12: false });

  function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const ip = input.trim();
    setInputError(!ip);
    if (ip) void model.lookup(ip);
  }

  function queryMyIp() {
    inputEdited.current = false;
    setInput(model.visitorIp || '');
    setInputError(false);
    void model.lookup();
  }

  return <div className="public-app">
    <header className="public-header"><div className="container header-inner"><Brand /><div className="header-actions"><nav aria-label="主导航"><a className="active" href="#query">IP 查询</a><a href="#api">API 文档</a><a href="#mcp">MCP 接入</a></nav><ThemeSwitcher /></div></div></header>
    <main>
      <section className="hero" id="query"><div className="hero-grid" aria-hidden="true" /><div className="container hero-content">
        <div className="hero-copy"><div className="hero-pill"><span />AI 友好的 IP 工具站</div><h1><span>连接每个 IP</span>，<br />看见网络的更多信息。</h1><p>自动识别当前访问地址，比较不同数据库的归属地与网络信息。<br className="desktop-break" />从浏览器、命令行到 AI 智能体，让查询简单一点。<br className="desktop-break" />公开 MCP，无需登录，让智能体直接调用 IP 查询工具。</p>
          <div className="hero-features"><span><Icon name="globe" size={16} />IPv4 / IPv6</span><span><Icon name="database" size={16} />多来源对照</span><span><Icon name="terminal" size={16} />公开 MCP</span></div>
        </div>
        <aside className="visitor-card" aria-label="本次访问 IP"><div className="visitor-top"><span className="visitor-globe"><Icon name="globe" size={22} /></span><span>本次访问 IP</span><Tag color={model.visitorState === 'identified' ? '#013187' : 'default'} variant="filled">{model.visitorState === 'loading' ? '识别中' : model.visitorState === 'identified' ? '已识别' : '未能识别'}</Tag></div>
          {/* IPv6 优先在冒号后换行；wbr 不改变复制内容或地址文本。 */}
          <div className="visitor-ip" data-testid="visitor-ip">{model.visitorState === 'loading' ? <Skeleton.Input active size="small" /> : model.visitorIp ? model.visitorIp.split(':').map((segment, index, segments) => <Fragment key={index}>{segment}{index < segments.length - 1 ? <>:<wbr /></> : null}</Fragment>) : '—'}</div>
          <div className="visitor-bottom"><span>来自本次请求的连接地址</span>{model.visitorIp ? <CopyButton value={model.visitorIp} iconOnly /> : null}</div><div className="visitor-note"><Icon name="shield" size={14} />地址由服务端识别，查询结果不代表精确位置</div>
        </aside>
        <div className="search-panel"><form onSubmit={submit} aria-label="IP 查询表单"><label htmlFor="ip-input">查询一个 IP 地址</label><div className="search-row"><Input id="ip-input" size="large" placeholder="输入 IPv4 或 IPv6 地址" prefix={<Icon name="search" size={21} />} value={input} onChange={event => { inputEdited.current = true; setInput(event.target.value); setInputError(false); }} status={inputError ? 'error' : undefined} autoComplete="off" spellCheck={false} aria-invalid={inputError} aria-describedby="query-input-hint" allowClear /><Button type="primary" size="large" htmlType="submit" icon={<Icon name="arrow" size={19} />}>查询 IP</Button></div><div className={`search-hint${inputError ? ' input-error' : ''}`} id="query-input-hint">{inputError ? '请先输入需要查询的单个 IP 地址。' : '支持单个 IPv4 / IPv6。无需登录，直接查看各来源结果。'}<button type="button" onClick={queryMyIp}><Icon name="refresh" size={13} />查询我的 IP</button></div></form></div>
      </div></section>
      <div className="container results-container"><section id="data-sources" aria-labelledby="sources-heading" aria-busy={model.loading}>
        <div className="section-heading sources-heading"><div><div className="eyebrow">MULTI-SOURCE LOOKUP</div><h2 id="sources-heading">数据源结果 <span className="count-badge">{model.providersLoading ? '…' : visibleProviders.length}</span></h2><p>{query ? <><code className="query-ip" data-testid="queried-ip">{query.ip}</code><span className="query-divider">·</span>{addressTypeText(query.addressType)}{queriedTime ? <span className="queried-time">查询于 {queriedTime}</span> : null}</> : '同一地址，不同来源。保留每个数据库自己的答案。'}</p></div><div className="source-summary"><span className={`status-dot${readyCount ? ' is-ready' : ''}`} />{model.providersLoading ? '正在获取来源状态' : `已就绪 ${readyCount} / ${visibleProviders.length}`}<span className="summary-caption">按来源实际状态展示</span></div></div>
        <div className="result-notices" aria-live="polite">{model.loading ? <Alert type="info" showIcon title="正在查询" description="单个来源的状态会独立保留，请稍候。" /> : null}{model.networkError ? <Alert type="error" showIcon title="连接服务失败" description={model.networkError} action={<Button size="small" onClick={() => void model.lookup(input.trim() || undefined)}>重试</Button>} /> : null}{!model.loading && businessError ? <Alert type="warning" showIcon title="查询暂不可用" description={businessError} /> : null}{query?.status === 'partial' ? <Alert type="info" showIcon title="已返回可用来源" description={model.response?.result.message} /> : null}{model.providersError ? <Alert type="error" showIcon title="无法获取来源状态" description={model.providersError} /> : null}</div>
        <div className="provider-grid">{model.providersLoading ? Array.from({ length: 6 }, (_, index) => <div className="provider-card provider-skeleton" key={index}><Skeleton active paragraph={{ rows: 3 }} /></div>) : visibleProviders.map(provider => <ProviderCard key={provider.id} provider={provider} result={query?.sources.find(source => source.id === provider.id)} loading={model.loading} />)}</div>
        {!model.providersLoading && visibleProviders.length === 0 && !model.providersError ? <div className="no-sources"><Icon name="database" size={30} /><h3>暂无启用的数据源</h3><p>来源启用后将在此展示查询结果。</p></div> : null}
        <p className="result-explainer"><Icon name="info" size={15} />IP 归属地由各数据库推断，可能存在差异；私有、回环等特殊地址不进行公网定位。</p>
      </section>
      <DeferredApiExamples providers={model.providers} queryReady={!model.loading && !model.providersLoading} />
      <section className="principles"><div><span className="principle-icon"><Icon name="database" /></span><div><h3>来源清楚</h3><p>保留各来源状态、版本与实际字段</p></div></div><div><span className="principle-icon"><Icon name="shield" /></span><div><h3>信息有边界</h3><p>缺失信息如实显示，不拼成精确位置</p></div></div><div><span className="principle-icon"><Icon name="terminal" /></span><div><h3>适合你的工作流</h3><p>浏览器、脚本和服务使用统一接口</p></div></div></section>
      </div>
    </main>
    <footer className="public-footer"><div className="container"><div className="footer-main"><span>通研信息 <span className="footer-dot">·</span> IP 聚合查询</span><span>让网络信息更清晰。</span><a href="#api">API 使用说明 <Icon name="arrow" size={14} /></a></div>{usedSources.length ? <div className="attribution" aria-label="本次结果数据来源">{usedSources.map(source => source.id === 'CZ88' ? <p key={source.id}>IP地址位置数据由<a href="https://www.cz88.net">纯真CZ88</a>提供支持</p> : <span key={source.id}>{source.id === 'ip2location' ? <>通研信息 uses the IP2Location LITE database for <a href="https://lite.ip2location.com" target="_blank" rel="noreferrer">IP geolocation</a>.</> : source.id === 'dbip' ? <a href="https://db-ip.com" target="_blank" rel="noreferrer">IP Geolocation by DB-IP</a> : <a href={sourceVisuals[source.id]?.homepage} target="_blank" rel="noreferrer">{source.attribution || source.name}</a>}</span>)}</div> : <p className="footer-status">当前尚无实际使用的查询来源，来源就绪并参与查询后显示相应署名。</p>}</div></footer>
  </div>;
}
