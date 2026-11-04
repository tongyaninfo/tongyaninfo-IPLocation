import ip2regionAvif from '../assets/logos/ip2region-lh.avif';
import ip2regionWebp from '../assets/logos/ip2region-lh.webp';
import maxmindavif96 from '../assets/logos/maxmind-lh-96.avif';
import maxmindavif192 from '../assets/logos/maxmind-lh-192.avif';
import maxmindavif288 from '../assets/logos/maxmind-lh-288.avif';
import maxmindwebp96 from '../assets/logos/maxmind-lh-96.webp';
import maxmindwebp192 from '../assets/logos/maxmind-lh-192.webp';
import maxmindwebp288 from '../assets/logos/maxmind-lh-288.webp';
export interface SourceVisual {
  mark: string; color: string; homepage: string;
  logo?: { src: string; background: 'light' | 'brand'; maxWidth?: number; srcSet?: string; avifSrcSet?: string; sizes?: string; width?: number; height?: number };
}
export const sourceVisuals: Record<string, SourceVisual> = {
  dbip: { mark: 'DB', color: 'blue', homepage: 'https://db-ip.com', logo: { src: dbipLogo, background: 'light', maxWidth: 48 } },
  ipinfo: { mark: 'IP', color: 'cyan', homepage: 'https://ipinfo.io', logo: { src: ipinfoLogo, background: 'light' } },
  ip2region: { mark: 'IR', color: 'purple', homepage: 'https://github.com/lionsoul2014/ip2region', logo: { src: ip2regionWebp, avifSrcSet: ip2regionAvif, width: 64, height: 64, background: 'light' } },
  CZ88: { mark: '纯', color: 'amber', homepage: 'https://www.cz88.net', logo: { src: cz88Logo, background: 'brand' } },
  geolite2: { mark: 'MM', color: 'green', homepage: 'https://www.maxmind.com', logo: { src: maxmindwebp192, srcSet: `${maxmindwebp96} 96w, ${maxmindwebp192} 192w, ${maxmindwebp288} 288w`, avifSrcSet: `${maxmindavif96} 96w, ${maxmindavif192} 192w, ${maxmindavif288} 288w`, sizes: '96px', width: 400, height: 69, background: 'light' } },
  ip2location: { mark: 'IL', color: 'slate', homepage: 'https://www.ip2location.com', logo: { src: ip2locationLogo, background: 'light' } },
};

export function statusPresentation(status: string): { text: string; color: string } {
  const values: Record<string, { text: string; color: string }> = {
    ready: { text: '已就绪', color: 'green' },
    success: { text: '查询成功', color: 'green' },
    partial: { text: '部分可用', color: 'gold' },
    notFound: { text: '无匹配', color: 'default' },
    notReady: { text: '未就绪', color: 'default' },
    notConfigured: { text: '未配置', color: 'default' },
    disabled: { text: '已停用', color: 'default' },
    // 兼容旧接口状态，前端只展示普通未就绪状态。
    licenseBlocked: { text: '未就绪', color: 'default' },
    unavailable: { text: '暂不可用', color: 'red' },
    unsupported: { text: '不支持此地址', color: 'default' },
    timeout: { text: '查询超时', color: 'red' },
    canceled: { text: '查询取消', color: 'default' },
    special: { text: '特殊用途地址', color: 'cyan' },
  };
  return values[status] || { text: status || '状态未知', color: 'default' };
}

export function addressTypeText(value?: string): string {
  const labels: Record<string, string> = {
    public: '公网地址', private: '私有地址', loopback: '回环地址',
    linkLocal: '链路本地地址', multicast: '多播地址', unspecified: '未指定地址',
    reserved: '保留地址', special: '特殊用途地址', documentation: '文档示例地址',
  };
  return value ? labels[value] || value : '等待识别';
}

export function safeExternalHref(value: string | null | undefined): string | undefined {
  if (!value) return undefined;
  try {
    const url = new URL(value);
    return url.protocol === 'https:' ? url.href : undefined;
  } catch { return undefined; }
}

export const displayValue = (value: string | number | null | undefined): string =>
  value === null || value === undefined || value === '' ? '未提供' : String(value);

/** 纯真地区原文仅在显示时统一层级分隔符，网络描述和其他来源内容不作替换。 */
export function displayProviderRegion(id: string, region: string | null | undefined): string | null | undefined {
  return id === 'CZ88' ? region?.replace(/\s*–\s*/g, ' · ') : region;
}

/** 仅解释读取器实际给出的版本精度；文件构建时间不等同于数据发布日期或后台上传时间。 */
export function providerVersionLabel(id: string, version: string): string {
  if (version.startsWith('sha256:')) return '内容标识';
  if (id === 'CZ88' && /^\d{4}-\d{2}-\d{2}$/.test(version)) return '数据日期';
  if (['geolite2', 'dbip', 'ipinfo', 'ip2location', 'ip2region'].includes(id)
    && /\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z/.test(version)) return '文件构建时间';
  return '版本';
}

const buildTimeFormatter = new Intl.DateTimeFormat('en-CA', {
  timeZone: 'Asia/Shanghai', year: 'numeric', month: '2-digit', day: '2-digit',
  hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
});

/** 仅格式化库内构建时间；日期版本不补时间，复合版本中的IPv4/IPv6时间分别转换。 */
export function displayProviderVersion(id: string, version: string): string {
  if (providerVersionLabel(id, version) !== '文件构建时间') return version;
  return version.replace(/\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z/g, value => {
    const time = new Date(value);
    if (Number.isNaN(time.getTime())) return value;
    const parts = Object.fromEntries(buildTimeFormatter.formatToParts(time).map(part => [part.type, part.value]));
    return `${parts.year}-${parts.month}-${parts.day} ${parts.hour}:${parts.minute}:${parts.second}`;
  });
}
import dbipLogo from '../assets/logos/dbip.webp';
// SVG根宽高是旧Chrome在picture/flex中定尺寸的依据，优化资源时不能移除。
import ipinfoLogo from '../assets/logos/ipinfo.svg';
import cz88Logo from '../assets/logos/cz88.webp';
import ip2locationLogo from '../assets/logos/ip2location.webp';

