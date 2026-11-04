import { Tag } from 'antd';
import type { ProviderDescriptor, ProviderResult } from '../types/ip-location';
import { displayProviderRegion, displayProviderVersion, displayValue, providerVersionLabel, sourceVisuals, statusPresentation } from '../utils/presentation';
import { CopyButton } from './CopyButton';
import { Icon } from './Icon';
import { ProviderLogo } from './ProviderLogo';

// 卡片按普通文本展示，复制时同样折叠空白，避免来源值中的换行被误读为额外字段。
const normalizeCopyValue = (value: string): string => value.replace(/\s+/g, ' ').trim();

export function ProviderCard({ provider, result, loading }: { provider: ProviderDescriptor; result?: ProviderResult; loading: boolean }) {
  const color = sourceVisuals[provider.id]?.color || 'slate';
  // 特殊地址的跳过状态不掩盖来源本身的停用或缺库状态。
  const status = statusPresentation(result?.status === 'unsupported' && !provider.ready ? provider.status : result?.status || provider.status);
  const fields = result?.fields;
  const hasFields = fields && Object.values(fields).some(value => value !== null && value !== undefined && value !== '');
  // 纯真将地区层级存为一段文本，显示时统一分隔符，保留接口原文及名称中的普通连字符。
  const region = displayProviderRegion(provider.id, fields?.region);
  const location = fields ? [fields.country, region, fields.city].filter(Boolean).join(' · ') : '';
  const locationText = location || '地理位置未提供';
  const statusText = loading ? '查询中' : status.text;
  const addressFamilies = [provider.supportsIpv4 ? 'IPv4' : '', provider.supportsIpv6 ? 'IPv6' : ''].filter(Boolean).join(' / ');
  const version = result?.version || provider.version;
  const versionLabel = version ? providerVersionLabel(provider.id, version) : '版本';
  const versionValue = version ? displayProviderVersion(provider.id, version) : '未提供';
  const versionText = version ? `${versionLabel} ${versionValue}` : '版本未提供';
  // 显示与复制复用同一组可见字段，缺失值沿用“未提供”，不把隐藏的接口字段或原始 JSON 拼入全文。
  const rows = hasFields ? [
    { label: provider.id === 'CZ88' ? '网络描述' : '运营商', value: displayValue(fields.isp) },
    { label: 'ASN', value: displayValue(fields.asn) },
    { label: 'AS 组织', value: displayValue(fields.asName) },
    ...(fields.asDomain ? [{ label: 'AS 域名', value: fields.asDomain }] : []),
    ...(fields.continent || fields.continentCode ? [{ label: '大洲', value: [fields.continent, fields.continentCode].filter(Boolean).join(' · ') }] : []),
    ...((fields.latitude !== undefined && fields.latitude !== null) || (fields.longitude !== undefined && fields.longitude !== null)
      ? [{ label: '近似坐标', value: `${displayValue(fields.latitude)}, ${displayValue(fields.longitude)}` }] : []),
    ...(fields.postalCode ? [{ label: '邮编', value: fields.postalCode }] : []),
    ...(fields.timeZone ? [{ label: '时区 / 时差', value: fields.timeZone }] : []),
  ] : [];
  const emptyMessage = loading ? '正在请求此来源的查询状态…' : result?.message || provider.message || '此来源尚未提供可显示的查询结果。';
  const providerReason = !loading && result?.status === 'unsupported' && !provider.ready && provider.message !== result.message ? provider.message : null;
  const allFields = [
    `数据源：${normalizeCopyValue(provider.name)}`,
    `状态：${normalizeCopyValue(statusText)}`,
    `地址族：${displayValue(addressFamilies)}`,
    ...(hasFields ? [`地理位置：${normalizeCopyValue(locationText)}`, ...rows.map(row => `${row.label}：${normalizeCopyValue(row.value)}`)]
      : [`状态说明：${normalizeCopyValue(emptyMessage)}`, ...(providerReason ? [`来源说明：${normalizeCopyValue(providerReason)}`] : [])]),
    `${versionLabel}：${normalizeCopyValue(versionValue)}`,
    '来源：https://ip.tongyaninfo.com',
  ].join('\n');
  return <article className={`provider-card source-${color}`} aria-label={`${provider.name} 数据源`}>
    <div className="provider-heading">
      <ProviderLogo provider={provider} />
      <div className="provider-card-actions"><Tag color={status.color} variant="filled">{statusText}</Tag><CopyButton value={allFields} label="复制此数据源全部字段" iconOnly disabled={loading} className="provider-copy-all" /></div>
      <div className="provider-title"><h3>{provider.id === 'CZ88'
        ? <a href={sourceVisuals.CZ88.homepage} target="_blank" rel="noopener noreferrer" title="新标签页打开纯真官网">{provider.name}</a>
        : provider.name}</h3><span>{addressFamilies}</span></div>
    </div>
    {hasFields ? <div className="provider-data">
      <div className="location-line"><Icon name="globe" size={18} /><div className="provider-location-value"><strong>{locationText}</strong><CopyButton value={normalizeCopyValue(locationText)} label="复制地理位置" iconOnly disabled={loading} className="provider-field-copy" /></div></div>
      <dl className="field-list">
        {rows.map(row => <div key={row.label}><dt>{row.label}</dt><dd><span className="provider-field-value">{row.value}</span><CopyButton value={normalizeCopyValue(row.value)} label={`复制${row.label}`} iconOnly disabled={loading} className="provider-field-copy" /></dd></div>)}
      </dl>
    </div> : <div className="provider-empty">
      <span className="provider-empty-icon"><Icon name={provider.id === 'ip2location' ? 'shield' : 'database'} size={23} /></span>
      <p className="provider-empty-message"><span className="provider-field-value">{emptyMessage}</span><CopyButton value={normalizeCopyValue(emptyMessage)} label="复制状态说明" iconOnly disabled={loading} className="provider-field-copy" /></p>
      {providerReason ? <p className="provider-reason"><span className="provider-field-value">{providerReason}</span><CopyButton value={normalizeCopyValue(providerReason)} label="复制来源说明" iconOnly disabled={loading} className="provider-field-copy" /></p> : null}
    </div>}
    <div className="provider-meta"><span className="provider-version-value" title={version ? `${versionText}${versionLabel === '文件构建时间' ? '（北京时间）' : ''}` : undefined}>{versionText}</span></div>
  </article>;
}
