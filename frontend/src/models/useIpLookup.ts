import { useCallback, useEffect, useRef, useState } from 'react';
import { getProviders, queryIp, queryVisitorIp } from '../services/ip-location/api';
import type { ApiResponse, ProviderDescriptor, QueryResult } from '../types/ip-location';

export function useIpLookup() {
  const [providers, setProviders] = useState<ProviderDescriptor[]>([]);
  const [providersLoading, setProvidersLoading] = useState(true);
  const [providersError, setProvidersError] = useState<string | null>(null);
  const [response, setResponse] = useState<ApiResponse<QueryResult> | null>(null);
  const [visitorIp, setVisitorIp] = useState<string | null>(null);
  const [visitorState, setVisitorState] = useState<'loading' | 'identified' | 'failed'>('loading');
  const [loading, setLoading] = useState(false);
  const [networkError, setNetworkError] = useState<string | null>(null);
  const [queriedAt, setQueriedAt] = useState<Date | null>(null);
  const currentRequest = useRef(0);
  const activeController = useRef<AbortController | null>(null);

  const lookup = useCallback(async (ip?: string) => {
    const refreshProviders = currentRequest.current > 0;
    const requestId = ++currentRequest.current;
    activeController.current?.abort();
    const controller = new AbortController();
    activeController.current = controller;
    setLoading(true);
    setNetworkError(null);
    setResponse(null);
    setQueriedAt(null);
    if (ip === undefined) setVisitorState('loading');
    else setVisitorState(current => current === 'loading' ? 'failed' : current);
    try {
      const next = ip === undefined ? await queryVisitorIp(controller.signal) : await queryIp(ip, controller.signal);
      // 即使网络栈忽略取消，较早的默认查询也不能覆盖之后的手动查询。
      if (controller.signal.aborted || requestId !== currentRequest.current) return;
      setResponse(next);
      if (refreshProviders) {
        // 管理员启停或激活后，新查询同步来源状态，避免长期开页继续显示旧版本与署名。
        void getProviders(controller.signal).then(({ result }) => {
          if (controller.signal.aborted || requestId !== currentRequest.current) return;
          if (result.success && Array.isArray(result.data)) { setProviders(result.data); setProvidersError(null); }
          else setProvidersError(result.message || '无法刷新数据源状态。');
        }).catch(error => { if (!controller.signal.aborted && requestId === currentRequest.current) setProvidersError(error instanceof Error ? error.message : '无法刷新数据源状态。'); });
      }
      setQueriedAt(new Date());
      if (ip === undefined) {
        setVisitorIp(next.result.data?.ip || null);
        setVisitorState(next.result.data?.ip ? 'identified' : 'failed');
      }
    } catch (error) {
      if (controller.signal.aborted || requestId !== currentRequest.current) return;
      setNetworkError(error instanceof Error ? error.message : '查询失败，请重试。');
      if (ip === undefined) setVisitorState('failed');
    } finally {
      if (requestId === currentRequest.current) setLoading(false);
    }
  }, []);

  useEffect(() => {
    const controller = new AbortController();
    void getProviders(controller.signal).then(({ result }) => {
      if (controller.signal.aborted) return;
      if (result.success && Array.isArray(result.data)) setProviders(result.data);
      else setProvidersError(result.message || '无法获取数据源状态。');
    }).catch((error: unknown) => {
      if (!controller.signal.aborted) setProvidersError(error instanceof Error ? error.message : '无法获取数据源状态。');
    }).finally(() => {
      if (!controller.signal.aborted) setProvidersLoading(false);
    });
    void lookup();
    return () => {
      controller.abort();
      activeController.current?.abort();
    };
  }, [lookup]);

  return { providers, providersLoading, providersError, response, visitorIp, visitorState, loading, networkError, queriedAt, lookup };
}
