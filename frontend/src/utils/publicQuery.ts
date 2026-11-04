import { canonicalProviderId } from './providerIdentity.ts';

export interface PublicQueryOptions {
  ip?: string;
  db?: string;
  format?: 'json' | 'xml' | 'txt';
}

/** 页面请求与命令示例共用根路径协议；省略 IP 时由后端识别连接地址。 */
export function buildPublicQueryPath({ ip, db, format }: PublicQueryOptions = {}): string {
  const parameters = new URLSearchParams();
  if (ip !== undefined) parameters.set('ip', ip);
  if (db !== undefined) parameters.set('db', canonicalProviderId(db));
  if (format !== undefined) parameters.set('format', format);
  const query = parameters.toString();
  return query ? `/?${query}` : '/';
}

/** 开发代理只截取显式查询入口；键名大小写与后端 Query 匹配保持一致。 */
export function isRootQueryRequest(requestUrl: string): boolean {
  const url = new URL(requestUrl, 'http://localhost');
  return url.pathname === '/' && [...url.searchParams.keys()].some(key => ['ip', 'db', 'format'].includes(key.toLowerCase()));
}
