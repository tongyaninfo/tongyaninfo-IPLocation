import { useCallback, useEffect, useRef, useState } from 'react';
import { getConsoleAccess, getConsoleCsrf, loginConsole, logoutConsole } from '../services/console/api';
import type { ApiResponse, ConsoleAccess, ConsoleLoginRequest, ConsoleUser } from '../types/ip-location';

type Gate = 'checking' | 'denied' | 'notInitialized' | 'unauthenticated' | 'allowed' | 'unavailable';

/** 存储故障与未初始化分开处理，只有真实会话和防伪令牌均有效时才开放交互。 */
function accessGate({ httpStatus, result }: ApiResponse<ConsoleAccess>): Gate {
  if (httpStatus === 403 || result.data?.allowed === false) return 'denied';
  if (httpStatus === 503 && result.code === 'CONSOLE_NOT_INITIALIZED' && result.data?.initialized === false) return 'notInitialized';
  if (httpStatus === 401 && result.code === 'CONSOLE_UNAUTHENTICATED' && result.data?.allowed && result.data.initialized) return 'unauthenticated';
  if (httpStatus === 200 && result.success && result.data?.allowed && result.data.initialized && result.data.authenticated && result.data.user) return 'allowed';
  return 'unavailable';
}

export function useConsoleSession() {
  const [gate, setGate] = useState<Gate>('checking');
  const [detail, setDetail] = useState('');
  const [user, setUser] = useState<ConsoleUser | null>(null);
  const [csrfToken, setCsrfToken] = useState('');
  const [busy, setBusy] = useState(false);
  const [actionError, setActionError] = useState('');
  const request = useRef<AbortController | null>(null);
  const generation = useRef(0);
  const submitting = useRef(false);

  const refresh = useCallback(async () => {
    const current = ++generation.current;
    request.current?.abort();
    const controller = new AbortController();
    request.current = controller;
    setGate('checking');
    setUser(null);
    setCsrfToken('');
    setActionError('');
    try {
      const response = await getConsoleAccess(controller.signal);
      if (controller.signal.aborted || current !== generation.current) return;
      const nextGate = accessGate(response);
      setDetail(response.result.message || response.result.data?.message || '服务端尚未允许进入管理后台。');
      if (nextGate === 'allowed' || nextGate === 'unauthenticated') {
        const csrf = await getConsoleCsrf(controller.signal);
        if (controller.signal.aborted || current !== generation.current) return;
        if (!csrf.result.success || csrf.httpStatus !== 200 || !csrf.result.data?.token) {
          setGate(csrf.httpStatus === 403 ? 'denied' : 'unavailable');
          setDetail(csrf.result.message || '无法获取管理操作防伪令牌，请重新检查。');
          return;
        }
        setCsrfToken(csrf.result.data.token);
      }
      setUser(nextGate === 'allowed' ? response.result.data!.user : null);
      setGate(nextGate);
    } catch (error) {
      if (!controller.signal.aborted && current === generation.current) {
        setGate('unavailable');
        setDetail(error instanceof Error ? error.message : '无法检查后台访问权限。');
      }
    } finally {
      if (current === generation.current) {
        submitting.current = false;
        setBusy(false);
      }
    }
  }, []);

  useEffect(() => {
    void refresh();
    return () => {
      // 页面离开后丢弃令牌与未完成响应，防止旧登录请求重新打开后台界面。
      ++generation.current;
      request.current?.abort();
    };
  }, [refresh]);

  async function authenticate(credentials?: ConsoleLoginRequest): Promise<void> {
    if (submitting.current || !csrfToken || (credentials ? gate !== 'unauthenticated' : gate !== 'allowed')) return;
    submitting.current = true;
    setBusy(true);
    setActionError('');
    const current = generation.current;
    const controller = new AbortController();
    request.current?.abort();
    request.current = controller;
    try {
      const response = credentials
        ? await loginConsole(credentials, csrfToken, controller.signal)
        : await logoutConsole(csrfToken, controller.signal);
      if (controller.signal.aborted || current !== generation.current) return;
      if (response.httpStatus >= 200 && response.httpStatus < 300 && response.result.success) {
        // 身份变化后重新取得会话与对应令牌，不用登录响应猜测已经获得后台权限。
        await refresh();
        return;
      }
      if (response.httpStatus === 503 || (!credentials && response.httpStatus === 401) || response.result.code === 'CONSOLE_IP_DENIED') {
        await refresh();
        return;
      }
      setActionError(response.result.message || (credentials ? '登录未成功，请重试。' : '退出未成功，请重试。'));
      if (response.httpStatus === 403 || response.result.code === 'CONSOLE_CSRF_INVALID') {
        const csrf = await getConsoleCsrf(controller.signal);
        if (controller.signal.aborted || current !== generation.current) return;
        if (csrf.httpStatus === 200 && csrf.result.success && csrf.result.data?.token) setCsrfToken(csrf.result.data.token);
        else await refresh();
      }
    } catch (error) {
      if (!controller.signal.aborted && current === generation.current) {
        setActionError(error instanceof Error ? error.message : '管理操作暂不可用，请重试。');
      }
    } finally {
      if (current === generation.current) {
        submitting.current = false;
        setBusy(false);
      }
    }
  }

  return { gate, detail, user, busy, actionError, csrfToken, refresh, login: (credentials: ConsoleLoginRequest) => authenticate(credentials), logout: () => authenticate() };
}
