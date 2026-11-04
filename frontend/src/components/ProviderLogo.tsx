import { sourceVisuals } from '../utils/presentation';

/** 来源标识由原图等比优化；视口以下按需加载，纯真白字保留固定品牌背板。 */
export function ProviderLogo({ provider, compact = false }: { provider: { id: string; name: string }; compact?: boolean }) {
  const visual = sourceVisuals[provider.id];
  if (!visual?.logo) return <span className="source-mark" title={provider.name}>{visual?.mark || provider.name.slice(0, 2)}</span>;
  const logo = visual.logo;
  const content = <picture className="provider-logo-picture">{logo.avifSrcSet ? <source type="image/avif" srcSet={logo.avifSrcSet} sizes={compact ? '(max-width: 600px) 76px, 80px' : logo.sizes} /> : null}<img src={logo.src} srcSet={logo.srcSet} sizes={compact ? '(max-width: 600px) 76px, 80px' : logo.sizes} width={logo.width} height={logo.height} alt={`${provider.name} Logo`} loading="lazy" decoding="async" style={logo.maxWidth ? { maxWidth: `min(100%, ${logo.maxWidth}px)` } : undefined} /></picture>;
  const className = `provider-logo provider-logo-${logo.background}${compact ? ' provider-logo-compact' : ''}`;
  // 纯真官网沿用已登记地址，Logo与原图样式共用，不把整个查询卡片变成链接。
  if (provider.id === 'CZ88') return <a className={className} href={visual.homepage} target="_blank" rel="noopener noreferrer"
    aria-label={`${provider.name} Logo，打开官网（新标签页）`}>{content}</a>;
  return <span className={className}>
    {content}
  </span>;
}
