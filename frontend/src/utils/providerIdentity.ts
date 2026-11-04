/** 兼容旧响应的纯真编码，进入前端状态和发出请求时统一使用对外编码CZ88。 */
export function canonicalProviderId(id: string): string {
  return ['chunzhen', 'cz88'].includes(id.toLowerCase()) ? 'CZ88' : id;
}

/** 只规范来源标识，不改动来源字段、地区原文或启用状态。 */
export function canonicalProvider<T extends { id: string }>(provider: T): T {
  return { ...provider, id: canonicalProviderId(provider.id) };
}
