import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import ConsolePage from '../src/pages/Console/console';
import type { ConsoleAccess } from '../src/types/ip-location';
import { businessResponse } from './consoleFixtures';

beforeEach(() => window.history.replaceState(null, '', '/console'));

const user = { id: '800000000000000001', account: 'test-admin', displayName: '受控管理员' };
const signedOut: ConsoleAccess = { allowed: true, initialized: true, authenticated: false, user: null };
const signedIn: ConsoleAccess = { ...signedOut, authenticated: true, user };
function response(data: unknown, httpStatus = 200, code = 'OK', message = '受控状态说明') {
  return new Response(JSON.stringify({ success: httpStatus >= 200 && httpStatus < 300, code, message, data }), { status: httpStatus });
}
function fillCredentials() {
  fireEvent.change(screen.getByLabelText('管理员账号'), { target: { value: ' test-admin ' } });
  fireEvent.change(screen.getByLabelText('密码'), { target: { value: 'controlled-test-password' } });
}

describe('管理后台服务端访问门禁', () => {
  it.each([
    [403, 'CONSOLE_IP_DENIED', null, '当前访问来源不被允许'],
    [503, 'CONSOLE_NOT_INITIALIZED', { ...signedOut, initialized: false }, '管理后台尚未初始化'],
    [503, 'CONSOLE_STORAGE_UNAVAILABLE', null, '暂时无法核验访问权限'],
  ])('%s / %s 时不展示登录和后台数据，也不请求防伪令牌', async (httpStatus, code, data, title) => {
    const fetchMock = vi.fn().mockResolvedValue(response(data, httpStatus, code));
    vi.stubGlobal('fetch', fetchMock);
    render(<ConsolePage />);
    await waitFor(() => expect(screen.getByText(title)).not.toBeNull());
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/console/access');
    expect(screen.queryByRole('form', { name: '管理员登录表单' })).toBeNull();
    expect(screen.queryByText('暂无已接入的管理数据')).toBeNull();
    expect(screen.queryByRole('navigation', { name: '管理导航' })).toBeNull();
    expect(screen.queryByRole('heading', { name: '运行总览' })).toBeNull();
  });

  it('401 且已有管理员时取得匿名防伪令牌后显示真实登录表单', async () => {
    const fetchMock = vi.fn((path: string) => Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf')
      ? response({ token: 'controlled-anonymous-token' })
      : response(signedOut, 401, 'CONSOLE_UNAUTHENTICATED'))));
    vi.stubGlobal('fetch', fetchMock);
    render(<ConsolePage />);
    expect(await screen.findByRole('form', { name: '管理员登录表单' })).not.toBeNull();
    expect(window.location.pathname).toBe('/console/login');
    expect(screen.getByRole('heading', { level: 1, name: '管理员登录' })).not.toBeNull();
    expect(screen.queryByRole('navigation', { name: '管理导航' })).toBeNull();
    expect(screen.queryByRole('heading', { name: '运行总览' })).toBeNull();
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual(['/api/console/access', '/api/console/auth/csrf']);
    expect(screen.queryByText('暂无已接入的管理数据')).toBeNull();
    fireEvent.submit(screen.getByRole('form', { name: '管理员登录表单' }));
    expect(screen.getByText('请输入管理员账号和密码。')).not.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it('真实身份通过后显示管理员名称并加载真实业务接口', async () => {
    const fetchMock = vi.fn((path: string) => Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf') ? response({ token: 'controlled-session-token' }) : response(signedIn))));
    vi.stubGlobal('fetch', fetchMock);
    render(<ConsolePage />);
    expect(await screen.findByText('受控管理员')).not.toBeNull();
    expect(screen.getByRole('button', { name: '退出登录' })).not.toBeNull();
    expect(await screen.findByRole('heading', { name: '公共访问趋势' })).not.toBeNull();
    expect(screen.queryByText('PostgreSQL · 结构待确认')).toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(4);
  });

  it('防伪令牌不可用时隐藏已有身份和后台数据', async () => {
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf')
      ? response(null, 503, 'CONSOLE_STORAGE_UNAVAILABLE')
      : response(signedIn)))));
    render(<ConsolePage />);
    expect(await screen.findByText('暂时无法核验访问权限')).not.toBeNull();
    expect(screen.queryByText('受控管理员')).toBeNull();
    expect(screen.queryByText('暂无已接入的管理数据')).toBeNull();
  });
});

describe('管理员登录和退出', () => {
  it('检查中采用独立页面，不闪现登录表单和管理导航', () => {
    vi.stubGlobal('fetch', vi.fn(() => new Promise<Response>(() => {})));
    render(<ConsolePage />);
    expect(screen.getByRole('status')).not.toBeNull();
    expect(screen.queryByRole('form', { name: '管理员登录表单' })).toBeNull();
    expect(screen.queryByRole('navigation', { name: '管理导航' })).toBeNull();
    expect(screen.queryByRole('heading', { name: '运行总览' })).toBeNull();
  });

  it('从管理子页转到独立登录，登录后返回原管理页面', async () => {
    window.history.replaceState(null, '', '/console/databases');
    let authenticated = false;
    vi.stubGlobal('fetch', vi.fn((path: string) => {
      const business = businessResponse(path); if (business) return Promise.resolve(business);
      if (path.endsWith('/csrf')) return Promise.resolve(response({ token: 'controlled-token' }));
      if (path.endsWith('/login')) { authenticated = true; return Promise.resolve(response(signedIn)); }
      return Promise.resolve(authenticated ? response(signedIn) : response(signedOut, 401, 'CONSOLE_UNAUTHENTICATED'));
    }));
    render(<ConsolePage />);
    await screen.findByRole('form', { name: '管理员登录表单' });
    expect(window.location.pathname).toBe('/console/login');
    expect(new URLSearchParams(window.location.search).get('returnTo')).toBe('/console/databases');
    fillCredentials();
    fireEvent.submit(screen.getByRole('form', { name: '管理员登录表单' }));
    await screen.findByRole('heading', { name: 'IP 数据库' });
    expect(window.location.pathname).toBe('/console/databases');
    expect(screen.getByRole('navigation', { name: '管理导航' })).not.toBeNull();
    expect(screen.queryByRole('form', { name: '管理员登录表单' })).toBeNull();
  });

  it.each([
    ['/console/settings', '/console/settings'],
    ['https://example.net/console/settings', '/console/overview'],
    ['//example.net/console/settings', '/console/overview'],
    ['/console/login', '/console/overview'],
    ['/console/overview?next=https://example.net', '/console/overview'],
    ['/console/../settings', '/console/overview'],
  ])('已登录访问登录页只接受受控回址 %s', async (returnTo, expected) => {
    window.history.replaceState(null, '', `/console/login?returnTo=${encodeURIComponent(returnTo)}`);
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf') ? response({ token: 'controlled-token' }) : response(signedIn)))));
    render(<ConsolePage />);
    await screen.findByRole('navigation', { name: '管理导航' });
    expect(window.location.pathname).toBe(expected);
    expect(window.location.search).toBe('');
    expect(screen.queryByRole('form', { name: '管理员登录表单' })).toBeNull();
  });

  it('登录和退出使用防伪令牌与同源 Cookie，身份切换后重新核验状态并更新令牌', async () => {
    let authenticated = false;
    let csrfNumber = 0;
    const fetchMock = vi.fn((path: string, options?: RequestInit) => {
      const business = businessResponse(path); if (business) return Promise.resolve(business);
      if (path.endsWith('/csrf')) return Promise.resolve(response({ token: `controlled-token-${++csrfNumber}` }));
      if (path.endsWith('/login')) {
        authenticated = true;
        return Promise.resolve(response(signedIn));
      }
      if (path.endsWith('/logout')) {
        authenticated = false;
        return Promise.resolve(response({}));
      }
      if (path === '/api/console/access') return Promise.resolve(authenticated ? response(signedIn) : response(signedOut, 401, 'CONSOLE_UNAUTHENTICATED'));
      throw new Error(`Unexpected controlled path: ${path}; method=${options?.method}`);
    });
    vi.stubGlobal('fetch', fetchMock);
    render(<ConsolePage />);
    await screen.findByRole('form', { name: '管理员登录表单' });
    fillCredentials();
    fireEvent.submit(screen.getByRole('form', { name: '管理员登录表单' }));
    await screen.findByText('受控管理员');
    const loginOptions = fetchMock.mock.calls.find(([path]) => path.endsWith('/login'))?.[1];
    expect(loginOptions?.method).toBe('POST');
    expect(loginOptions?.credentials).toBe('same-origin');
    expect(loginOptions?.cache).toBe('no-store');
    expect(loginOptions?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'controlled-token-1', 'Content-Type': 'application/json' });
    expect(JSON.parse(String(loginOptions?.body))).toEqual({ account: 'test-admin', password: 'controlled-test-password' });
    fireEvent.click(screen.getByRole('button', { name: '退出登录' }));
    await screen.findByRole('form', { name: '管理员登录表单' });
    const logoutOptions = fetchMock.mock.calls.find(([path]) => path.endsWith('/logout'))?.[1];
    expect(logoutOptions?.method).toBe('POST');
    expect(logoutOptions?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'controlled-token-2' });
    expect(fetchMock.mock.calls.filter(([path]) => path === '/api/console/access')).toHaveLength(3);
    expect(csrfNumber).toBe(3);
    expect(screen.queryByText('受控管理员')).toBeNull();
    expect((screen.getByLabelText('密码') as HTMLInputElement).value).toBe('');
    expect(window.location.pathname).toBe('/console/login');
    expect(screen.queryByRole('navigation', { name: '管理导航' })).toBeNull();
  });

  it('登录失败显示服务端错误并清空密码，未收到真实身份前不开放后台', async () => {
    const fetchMock = vi.fn((path: string) => Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf')
      ? response({ token: 'controlled-token' })
      : path.endsWith('/login')
        ? response(null, 401, 'CONSOLE_LOGIN_FAILED', '账号或密码不正确。')
        : response(signedOut, 401, 'CONSOLE_UNAUTHENTICATED'))));
    vi.stubGlobal('fetch', fetchMock);
    render(<ConsolePage />);
    await screen.findByRole('form', { name: '管理员登录表单' });
    fillCredentials();
    fireEvent.submit(screen.getByRole('form', { name: '管理员登录表单' }));
    expect(await screen.findByText('账号或密码不正确。')).not.toBeNull();
    expect((screen.getByLabelText('密码') as HTMLInputElement).value).toBe('');
    expect(screen.queryByText('暂无已接入的管理数据')).toBeNull();
  });

  it('HTTP 400 防伪令牌过期后获取新令牌，下一次提交使用更新后的令牌', async () => {
    let tokenNumber = 0;
    const fetchMock = vi.fn((path: string, _options?: RequestInit) => Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf')
      ? response({ token: `controlled-token-${++tokenNumber}` })
      : path.endsWith('/login')
        ? response(null, 400, 'CONSOLE_CSRF_INVALID', '操作验证已失效，请重试。')
        : response(signedOut, 401, 'CONSOLE_UNAUTHENTICATED'))));
    vi.stubGlobal('fetch', fetchMock);
    render(<ConsolePage />);
    await screen.findByRole('form', { name: '管理员登录表单' });
    fillCredentials();
    fireEvent.submit(screen.getByRole('form', { name: '管理员登录表单' }));
    await waitFor(() => expect(tokenNumber).toBe(2));
    await waitFor(() => expect((screen.getByLabelText('管理员账号') as HTMLInputElement).disabled).toBe(false));
    fillCredentials();
    fireEvent.submit(screen.getByRole('form', { name: '管理员登录表单' }));
    await waitFor(() => expect(fetchMock.mock.calls.filter(([path]) => path.endsWith('/login'))).toHaveLength(2));
    expect(fetchMock.mock.calls.filter(([path]) => path.endsWith('/login'))[1][1]?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'controlled-token-2' });
  });

  it('退出网络失败时保留实际页面并提示未完成，不假报已经退出', async () => {
    vi.stubGlobal('fetch', vi.fn((path: string) => path.endsWith('/logout')
      ? Promise.reject(new Error('private transport details'))
      : Promise.resolve(businessResponse(path) ?? (path.endsWith('/csrf') ? response({ token: 'controlled-token' }) : response(signedIn)))));
    render(<ConsolePage />);
    await screen.findByText('受控管理员');
    fireEvent.click(screen.getByRole('button', { name: '退出登录' }));
    expect(await screen.findByText('退出登录未完成')).not.toBeNull();
    expect(screen.getByText('受控管理员')).not.toBeNull();
    expect(screen.queryByRole('form', { name: '管理员登录表单' })).toBeNull();
    expect(screen.queryByText('private transport details')).toBeNull();
  });
});
