const managementPaths = new Set([
  '/console/overview', '/console/databases', '/console/access-logs', '/console/mcp-logs', '/console/audit', '/console/settings',
]);

/** 登录回址只接受已实现的管理页面，不接受站外地址、协议相对地址或任意查询参数。 */
export function consoleReturnPath(candidate: string | null): string {
  return candidate && managementPaths.has(candidate) ? candidate : '/console/overview';
}

export function consoleLoginPath(returnTo: string): string {
  const target = consoleReturnPath(returnTo);
  return target === '/console/overview' ? '/console/login' : `/console/login?returnTo=${encodeURIComponent(target)}`;
}
