import brandavif168 from '../assets/brand/tyinfo-logo-lh-168.avif';
import brandavif264 from '../assets/brand/tyinfo-logo-lh-264.avif';
import brandavif336 from '../assets/brand/tyinfo-logo-lh-336.avif';
import brandavif396 from '../assets/brand/tyinfo-logo-lh-396.avif';
import brandavif528 from '../assets/brand/tyinfo-logo-lh-528.avif';
import brandwebp168 from '../assets/brand/tyinfo-logo-lh-168.webp';
import brandwebp264 from '../assets/brand/tyinfo-logo-lh-264.webp';
import brandwebp336 from '../assets/brand/tyinfo-logo-lh-336.webp';
import brandwebp396 from '../assets/brand/tyinfo-logo-lh-396.webp';
import brandwebp528 from '../assets/brand/tyinfo-logo-lh-528.webp';
const avifSources = `${brandavif168} 168w, ${brandavif264} 264w, ${brandavif336} 336w, ${brandavif396} 396w, ${brandavif528} 528w`;
const webpSources = `${brandwebp168} 168w, ${brandwebp264} 264w, ${brandwebp336} 336w, ${brandwebp396} 396w, ${brandwebp528} 528w`;

export function Brand({ compact = false }: { compact?: boolean }) {
  const sizes = compact ? '(max-width: 600px) 126px, (max-width: 820px) 144px, 174px' : '(max-width: 600px) 132px, (max-width: 820px) 144px, 168px';
  return <a className={`brand${compact ? ' brand-compact' : ''}`} href="https://www.tongyaninfo.com/" aria-label="通研技术官网">
    {/* AVIF 优先，WebP 回退；按实际显示宽度和像素密度仅下载一个合适版本。 */}
    <picture className="brand-picture"><source type="image/avif" srcSet={avifSources} sizes={sizes} /><img className="brand-logo" src={brandwebp528} srcSet={webpSources} sizes={sizes} width={528} height={176} alt="通研技术" loading="eager" fetchPriority="high" decoding="async" /></picture>
    <span className="brand-caption"><strong>IP 聚合查询</strong><small>IP INTELLIGENCE</small></span>
  </a>;
}
