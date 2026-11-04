import { useEffect, useState } from 'react';
import { Tabs, Tag } from 'antd';
import type { ProviderDescriptor } from '../types/ip-location';
import { statusPresentation } from '../utils/presentation';
import { buildPublicQueryPath } from '../utils/publicQuery';
import type { PublicQueryOptions } from '../utils/publicQuery';
import { CopyButton } from './CopyButton';
import { Icon } from './Icon';

// 单引号保留 shell 中的 &，所有结构化示例与页面请求复用同一个 URL 构造函数。
const queryCommand = (options: PublicQueryOptions) => `curl 'https://ip.tongyaninfo.com${buildPublicQueryPath(options)}'`;
const sourceLabels: Record<string, string> = {
  CZ88: '纯真CZ88', ip2location: 'IP2Location', dbip: 'DB-IP',
  ipinfo: 'IPinfo', ip2region: 'ip2region', geolite2: 'GeoLite2',
};

function ApiExampleGroup({ providers, specifiedIp }: { providers: ProviderDescriptor[]; specifiedIp: boolean }) {
  const title = specifiedIp ? '查询指定 IP' : '我的 IP';
  const initialKey = specifiedIp ? 'fixed:json' : 'fixed:ip';
  // 我的 IP 始终省略参数，不能把浏览器识别结果写进命令而变成指定地址查询。
  const ip = specifiedIp ? '1.1.1.1' : undefined;
  const [selectedKey, setSelectedKey] = useState(initialKey);
  const fixedExamples = [
    ...(!specifiedIp ? [{ key: 'fixed:ip', label: '我的 IP', command: 'curl https://ip.tongyaninfo.com', hint: '直接输出本次请求识别到的 IP，兼容 curl / wget。' }] : []),
    { key: 'fixed:json', label: 'JSON', command: queryCommand({ ip, db: 'dbip', format: 'json' }), hint: 'JSON 返回统一查询结果；省略 format 默认 JSON，默认库为 DB-IP City Lite。' },
    { key: 'fixed:xml', label: 'XML', command: queryCommand({ ip, db: 'dbip', format: 'xml' }), hint: 'XML 与 JSON 使用相同业务状态、来源和结果字段。' },
    { key: 'fixed:txt', label: 'TXT', command: queryCommand({ ip, db: 'all', format: 'txt' }), hint: 'db=all 逐源返回文本结果；未就绪来源会明确标示状态。' },
  ];
  // 复用首页取得的来源及顺序，仅按启用状态追加标签；文件未就绪仍提供明确的接口用法。
  const examples = [...fixedExamples, ...providers.filter(provider => provider.enabled === true).map(provider => {
    const families = [provider.supportsIpv4 ? 'IPv4' : '', provider.supportsIpv6 ? 'IPv6' : ''].filter(Boolean).join(' / ');
    return {
      key: `provider:${provider.id}`,
      label: sourceLabels[provider.id] || provider.name,
      command: queryCommand({ ip, db: provider.id, format: 'json' }),
      hint: provider.ready
        ? `指定 ${provider.name} 查询；${families ? `当前文件支持 ${families}。` : '地址族以来源返回为准。'}`
        : `指定 ${provider.name} 查询；当前${statusPresentation(provider.status).text}，来源文件就绪后才能返回定位结果。`,
    };
  }), { key: 'fixed:all', label: 'ALL', command: queryCommand({ ip, db: 'all', format: 'json' }), hint: '返回全部登记来源的状态，按实际启用及就绪情况查询并保留各源结果。' }];
  const activeKey = examples.some(example => example.key === selectedKey) ? selectedKey : initialKey;
  useEffect(() => {
    // 两组各自回到默认标签；重排保持选中，重新启用也不自动切回旧来源。
    if (activeKey !== selectedKey) setSelectedKey(activeKey);
  }, [activeKey, selectedKey]);

  return <section className="api-panel api-example-group" aria-label={`${title} 命令示例`}>
      <div className="api-group-heading"><h3>{title}</h3><p>{specifiedIp ? '以 1.1.1.1 为例，替换 ip 参数即可查询其他地址。' : '省略 ip 参数，由服务端识别本次连接地址。'}</p></div>
      <Tabs activeKey={activeKey} onChange={setSelectedKey} items={examples.map(example => ({ key: example.key, label: example.label, children: <div className="api-example"><div className="code-block"><span className="terminal-prompt" aria-hidden="true">$</span><code>{example.command}</code><CopyButton value={example.command} /></div><p>{example.hint}</p></div> }))} />
      <div className="api-footnote"><Tag color="cyan" variant="filled">默认 dbip</Tag><span>示例为目标域名用法，是否可查询取决于部署状态及来源就绪情况。</span></div>
    </section>;
}

export function ApiExamples({ providers, onReady }: { providers: ProviderDescriptor[]; onReady?: () => void }) {
  useEffect(() => { onReady?.(); }, [onReady]);
  return <section className="api-section" id="api" aria-labelledby="api-heading">
    <div className="section-heading"><div><div className="eyebrow">BUILT FOR DEVELOPERS</div><h2 id="api-heading">查询，也可以只用一行命令。</h2><p>统一接口，三种输出格式。让 IP 信息进入你的工作流。</p></div><span className="api-icon"><Icon name="terminal" size={32} /></span></div>
    <div className="api-example-groups">
      <ApiExampleGroup providers={providers} specifiedIp={false} />
      <ApiExampleGroup providers={providers} specifiedIp />
    </div>
  </section>;
}
