import { createContext, useCallback, useContext, useEffect, useRef, useState } from 'react';
import { ConsoleApiError } from '../services/console/api';

export const ConsoleContext = createContext({ csrfToken: '', recheck: async () => {} });
export function useConsoleError() {
  const { recheck } = useContext(ConsoleContext);
  return useCallback((error: unknown) => {
    // 业务接口发现旧会话或来源权限变化，立即卸载业务页面并重新核验门禁。
    if (error instanceof ConsoleApiError && (error.httpStatus === 401 || error.httpStatus === 403 || error.code === 'CONSOLE_CSRF_INVALID')) void recheck();
    return error instanceof Error ? error.message : '管理服务暂不可用，请稍后重试。';
  }, [recheck]);
}
export function useConsoleResource<T>(loader: (signal: AbortSignal) => Promise<T>) {
  const [data, setData] = useState<T | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState('');
  const [revision, setRevision] = useState(0);
  const describeError = useConsoleError();
  useEffect(() => {
    const controller = new AbortController();
    setLoading(true); setError(''); setData(null);
    void loader(controller.signal).then(next => { if (!controller.signal.aborted) setData(next); })
      .catch(failure => { if (!controller.signal.aborted) setError(describeError(failure)); })
      .finally(() => { if (!controller.signal.aborted) setLoading(false); });
    return () => controller.abort();
  }, [loader, revision, describeError]);
  return { data, loading, error, reload: useCallback(() => setRevision(value => value + 1), []) };
}
export function useConsoleAction() {
  const { csrfToken } = useContext(ConsoleContext);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [success, setSuccess] = useState('');
  const running = useRef(false);
  const active = useRef(true);
  const describeError = useConsoleError();
  useEffect(() => { active.current = true; return () => { active.current = false; }; }, []);
  async function run(action: (token: string) => Promise<unknown>, message: string, onSuccess?: () => void) {
    if (running.current || !csrfToken) return;
    running.current = true; setBusy(true); setError(''); setSuccess('');
    try { await action(csrfToken); if (active.current) { setSuccess(message); onSuccess?.(); } }
    catch (failure) { if (active.current) setError(describeError(failure)); }
    finally { running.current = false; if (active.current) setBusy(false); }
  }
  return { busy, error, success, run };
}
