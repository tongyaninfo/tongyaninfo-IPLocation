import { canonicalProvider } from '../../utils/providerIdentity';
import { buildPublicQueryPath } from '../../utils/publicQuery';
import type { ApiResponse, ApiResult, ProviderDescriptor, QueryResult } from '../../types/ip-location';

export class ApiConnectionError extends Error {
  constructor(message = '暂时无法连接查询服务，请稍后重试。') {
    super(message);
    this.name = 'ApiConnectionError';
  }
}

interface ApiRequestOptions {
  method?: 'GET' | 'POST' | 'PUT';
  body?: unknown;
  csrfToken?: string;
}

/** 共用同源 Cookie 与统一响应解析；写请求显式传入防伪令牌，不在浏览器持久化。 */
export async function requestApi<T>(path: string, signal?: AbortSignal, options: ApiRequestOptions = {}): Promise<ApiResponse<T>> {
  let response: Response;
  const headers: Record<string, string> = { Accept: 'application/json' };
  if (options.body !== undefined && !(options.body instanceof FormData)) headers['Content-Type'] = 'application/json';
  if (options.csrfToken) headers['X-CSRF-TOKEN'] = options.csrfToken;
  try {
    response = await fetch(path, {
      headers,
      method: options.method ?? 'GET',
      body: options.body instanceof FormData ? options.body : options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: 'same-origin',
      cache: 'no-store',
      signal,
    });
  } catch (error) {
    if (signal?.aborted) throw error;
    throw new ApiConnectionError();
  }
  // 非 2xx 的业务响应也保留 data，来源未就绪与后台门禁不能误报成网络失败。
  let result: ApiResult<T>;
  try {
    result = await response.json();
  } catch {
    throw new ApiConnectionError('查询服务返回了无法识别的响应，请稍后重试。');
  }
  if (!result || typeof result.success !== 'boolean' || typeof result.code !== 'string' || typeof result.message !== 'string') {
    throw new ApiConnectionError('查询服务返回格式不正确，请稍后重试。');
  }
  return { httpStatus: response.status, result };
}

function canonicalQuerySources(response: ApiResponse<QueryResult>): ApiResponse<QueryResult> {
  const data = response.result.data;
  if (!data || !Array.isArray(data.sources)) return response;
  return { ...response, result: { ...response.result, data: { ...data, sources: data.sources.map(canonicalProvider) } } };
}

export const getProviders = (signal?: AbortSignal) => requestApi<ProviderDescriptor[]>('/api/providers', signal).then(response =>
  Array.isArray(response.result.data) ? { ...response, result: { ...response.result, data: response.result.data.map(canonicalProvider) } } : response);
export const queryVisitorIp = (signal?: AbortSignal) =>
  requestApi<QueryResult>(buildPublicQueryPath({ db: 'all', format: 'json' }), signal).then(canonicalQuerySources);
export const queryIp = (ip: string, signal?: AbortSignal) =>
  requestApi<QueryResult>(buildPublicQueryPath({ ip, db: 'all', format: 'json' }), signal).then(canonicalQuerySources);
