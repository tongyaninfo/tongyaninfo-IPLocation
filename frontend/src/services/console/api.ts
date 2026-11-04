import { canonicalProvider, canonicalProviderId } from '../../utils/providerIdentity';
import { requestApi } from '../ip-location/api';
import type { ApiResponse, ConsoleAccess, ConsoleCsrf, ConsoleLoginRequest } from '../../types/ip-location';
import type { AccessRecords, AuditRecord, ConsoleDatabases, ConsoleOverview, ConsoleSettings, ConsoleBasicSettings, PagedResult, QuerySettings } from '../../types/console';

/** 后台只在服务端通过白名单及身份检查后才允许展示。 */
export const getConsoleAccess = (signal?: AbortSignal) => requestApi<ConsoleAccess>('/api/console/access', signal);
export const getConsoleCsrf = (signal?: AbortSignal) => requestApi<ConsoleCsrf>('/api/console/auth/csrf', signal);
export const loginConsole = (credentials: ConsoleLoginRequest, csrfToken: string, signal?: AbortSignal) =>
  requestApi<ConsoleAccess>('/api/console/auth/login', signal, { method: 'POST', body: credentials, csrfToken });
export const logoutConsole = (csrfToken: string, signal?: AbortSignal) =>
  requestApi<object>('/api/console/auth/logout', signal, { method: 'POST', csrfToken });

export class ConsoleApiError extends Error {
  constructor(message: string, public readonly httpStatus: number, public readonly code: string) { super(message); this.name = 'ConsoleApiError'; }
}
export async function consoleData<T>(request: Promise<ApiResponse<T>>): Promise<T> {
  const { result, httpStatus } = await request;
  if (!result.success || httpStatus < 200 || httpStatus >= 300) throw new ConsoleApiError(result.message, httpStatus, result.code);
  return result.data as T;
}
export function consoleQuery(filters: Record<string, string | number | undefined>): string {
  const params = new URLSearchParams();
  Object.entries(filters).forEach(([key, value]) => { if (value !== undefined && value !== '') params.set(key, String(value)); });
  return params.size ? `?${params}` : '';
}
export const getOverview = (filters: Record<string, string>, signal?: AbortSignal) => consoleData(requestApi<ConsoleOverview>(`/api/console/overview${consoleQuery(filters)}`, signal));
export const getDatabases = (signal?: AbortSignal) => consoleData(requestApi<ConsoleDatabases>('/api/console/databases', signal)).then(data => ({
  ...data, defaultDatabase: canonicalProviderId(data.defaultDatabase), providers: data.providers.map(canonicalProvider),
}));
export const saveDatabaseOrder = (body: { providerIds: string[]; revision: number }, csrfToken: string) =>
  consoleData(requestApi<object>('/api/console/databases/order', undefined, { method: 'POST', body: { ...body, providerIds: body.providerIds.map(canonicalProviderId) }, csrfToken }));
export const setDatabaseState = (id: string, body: { enabled: boolean; revision: number; defaultDatabase?: string }, csrfToken: string) =>
  consoleData(requestApi<object>(`/api/console/databases/${encodeURIComponent(canonicalProviderId(id))}/state`, undefined, { method: 'POST', body: { ...body, defaultDatabase: body.defaultDatabase === undefined ? undefined : canonicalProviderId(body.defaultDatabase) }, csrfToken }));
export const activateDatabase = (id: string, body: { versionId: string; revision: number }, csrfToken: string) =>
  consoleData(requestApi<object>(`/api/console/databases/${encodeURIComponent(canonicalProviderId(id))}/activate`, undefined, { method: 'POST', body, csrfToken }));
export const getAccessLogs = (filters: Record<string, string | number>, signal?: AbortSignal) => consoleData(requestApi<AccessRecords>(`/api/console/access-logs${consoleQuery(filters)}`, signal));
export const getMcpLogs = (filters: Record<string, string | number>, signal?: AbortSignal) => consoleData(requestApi<AccessRecords>(`/api/console/mcp-logs${consoleQuery(filters)}`, signal));
export const getAudits = (filters: Record<string, string | number>, signal?: AbortSignal) => consoleData(requestApi<PagedResult<AuditRecord>>(`/api/console/audits${consoleQuery(filters)}`, signal));
function canonicalSettings(data: ConsoleSettings): ConsoleSettings {
  return { ...data, providers: data.providers.map(canonicalProvider), query: { ...data.query, defaultDatabase: canonicalProviderId(data.query.defaultDatabase) } };
}
export const getSettings = (signal?: AbortSignal) => consoleData(requestApi<ConsoleSettings>('/api/console/settings', signal)).then(canonicalSettings);
export const saveSettings = (body: { revision: number; query: QuerySettings; allowedNetworks: string[]; basic?: ConsoleBasicSettings }, csrfToken: string) =>
  consoleData(requestApi<ConsoleSettings>('/api/console/settings', undefined, { method: 'POST', body: { ...body, query: { ...body.query, defaultDatabase: canonicalProviderId(body.query.defaultDatabase) } }, csrfToken })).then(canonicalSettings);

/** 原始文件走 FormData 流式上传；浏览器生成边界，进度只表示传输，不冒充校验或激活。 */
export function uploadDatabase(id: string, data: FormData, csrfToken: string, onProgress: (percent: number) => void, signal: AbortSignal): Promise<object> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest();
    const abort = () => xhr.abort();
    const finish = () => signal.removeEventListener('abort', abort);
    xhr.open('POST', `/api/console/databases/${encodeURIComponent(canonicalProviderId(id))}/uploads`);
    xhr.withCredentials = true;
    xhr.setRequestHeader('Accept', 'application/json');
    xhr.setRequestHeader('X-CSRF-TOKEN', csrfToken);
    xhr.upload.onprogress = event => { if (event.lengthComputable) onProgress(Math.min(100, Math.round(event.loaded / event.total * 100))); };
    xhr.onload = () => {
      finish();
      try {
        const result = JSON.parse(xhr.responseText);
        if (!result || typeof result.success !== 'boolean' || typeof result.message !== 'string') throw new Error();
        if (xhr.status >= 200 && xhr.status < 300 && result.success) resolve(result.data ?? {});
        else reject(new ConsoleApiError(result.message, xhr.status, result.code || 'UPLOAD_FAILED'));
      } catch (error) { reject(error instanceof ConsoleApiError ? error : new Error('上传服务返回了无法识别的响应。')); }
    };
    xhr.onerror = () => { finish(); reject(new Error('文件上传连接中断，请检查任务记录后重试。')); };
    xhr.onabort = () => { finish(); reject(new DOMException('上传已取消', 'AbortError')); };
    signal.addEventListener('abort', abort, { once: true });
    if (signal.aborted) abort(); else xhr.send(data);
  });
}
