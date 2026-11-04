import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ReactNode } from 'react';
import { ConsoleContext } from '../src/models/ConsoleContext';
import ConsolePage from '../src/pages/Console/console';
import { OverviewPage, RequestTrend } from '../src/pages/Console/OverviewPage';
import { DatabasesPage } from '../src/pages/Console/DatabasesPage';
import { AccessLogsPage } from '../src/pages/Console/AccessLogsPage';
import { AuditPage } from '../src/pages/Console/AuditPage';
import { SettingsPage } from '../src/pages/Console/SettingsPage';
import { apiResponse, audit, businessResponse, databases, overview, settings } from './consoleFixtures';

function renderBusiness(content: ReactNode) {
  const recheck = vi.fn(async () => {});
  render(<ConsoleContext.Provider value={{ csrfToken: 'controlled-session-token', recheck }}>{content}</ConsoleContext.Provider>);
  return recheck;
}
function installReadMocks() {
  const fetchMock = vi.fn((path: string) => Promise.resolve(businessResponse(path) || apiResponse(null, 404, 'NOT_FOUND')));
  vi.stubGlobal('fetch', fetchMock); return fetchMock;
}

describe('后台真实业务页面', () => {
  it('总览提供独立MCP趋势和准确图例，不将HTTP错误表述为所有业务失败', async () => {
    installReadMocks(); renderBusiness(<OverviewPage />);
    expect(await screen.findByRole('heading', { name: 'MCP 调用趋势' })).not.toBeNull();
    expect(screen.getByRole('img', { name: /MCP 请求趋势/ })).not.toBeNull();
    expect(screen.getByRole('img', { name: /公共请求趋势/ })).not.toBeNull();
    expect(screen.getByText(/定位查询仅统计 query_ip \/ query_my_ip/)).not.toBeNull();
    expect(screen.getByText(/HTTP 错误不等于全部工具业务失败/)).not.toBeNull();
  });
  it('总览显示真实统计、采集缺口和来源状态，切换时间范围重新请求', async () => {
    const fetchMock = installReadMocks();
    renderBusiness(<OverviewPage />);
    expect(await screen.findByRole('heading', { name: '公共访问趋势' })).not.toBeNull();
    expect(screen.getByRole('img', { name: /公共请求趋势/ })).not.toBeNull();
    expect(screen.getAllByText('120').length).toBeGreaterThan(0);
    expect(screen.getByText('GeoLite2 City')).not.toBeNull();
    expect(screen.getByText('受控统计窗口，非真实生产数据。')).not.toBeNull();
    fireEvent.click(screen.getByText('近 7 天'));
    await waitFor(() => expect(fetchMock.mock.calls.filter(([path]) => path.startsWith('/api/console/overview'))).toHaveLength(2));
    const lastPath = fetchMock.mock.calls.filter(([path]) => path.startsWith('/api/console/overview'))[1][0];
    const params = new URL(lastPath, 'http://localhost').searchParams;
    expect((Date.parse(params.get('toUtc')!) - Date.parse(params.get('fromUtc')!)) / 3600000).toBe(168);
  });

  it('总览无样本时显示真实空状态与未知延迟，不补演示曲线', async () => {
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path.startsWith('/api/console/overview') ? apiResponse({ ...overview, trend: [], totals: { ...overview.totals, requests: 0, averageMilliseconds: null, p95Milliseconds: null }, queue: { ...overview.queue, dropped: 3, writeFailures: 1 } }) : apiResponse(databases))));
    renderBusiness(<OverviewPage />);
    expect(await screen.findByText('当前时间范围暂无访问趋势')).not.toBeNull();
    expect(screen.getByText('采集存在缺口或故障')).not.toBeNull();
    expect(screen.getByText('无样本')).not.toBeNull();
    expect(screen.queryByRole('img', { name: /公共请求趋势/ })).toBeNull();
  });

  it('业务读取失败可重试，后续成功显示真实记录', async () => {
    let failed = true;
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(failed ? apiResponse(null, 503, 'STORAGE_UNAVAILABLE', '审计存储暂不可用') : apiResponse({ items: [audit], total: 1, page: 1, pageSize: 20 }))));
    renderBusiness(<AuditPage />);
    expect(await screen.findByText('审计存储暂不可用')).not.toBeNull();
    failed = false; fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }));
    expect(await screen.findByText('settings.updated')).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: /Expand row|展开行/ }));
    expect(await screen.findByText('controlled-audit-trace')).not.toBeNull();
    expect(screen.getByText(/defaultDatabase/)).not.toBeNull();
  });

  it('访问记录筛选发送正确参数，无数据仍显示健康状态', async () => {
    const fetchMock = installReadMocks(); renderBusiness(<AccessLogsPage />);
    expect(await screen.findByText('当前筛选范围内暂无记录')).not.toBeNull();
    fireEvent.change(screen.getByLabelText('来源 IP'), { target: { value: '2001:db8::1' } });
    fireEvent.change(screen.getByLabelText('HTTP 状态'), { target: { value: '503' } });
    fireEvent.submit(screen.getByRole('form', { name: '访问记录筛选' }));
    await waitFor(() => expect(fetchMock.mock.calls).toHaveLength(2));
    const params = new URL(fetchMock.mock.calls[1][0], 'http://localhost').searchParams;
    expect(params.get('sourceAddress')).toBe('2001:db8::1'); expect(params.get('statusCode')).toBe('503'); expect(params.get('page')).toBe('1');
    expect(await screen.findByText('访问队列运行中')).not.toBeNull();
  });

  it('数据库启停沿用修订号与CSRF，重复点击不重复写入，成功后刷新状态', async () => {
    let finish: ((response: Response) => void) | undefined;
    let enabled = true;
    const fetchMock = vi.fn((path: string, options?: RequestInit) => {
      if (options?.method === 'POST') return new Promise<Response>(resolve => { finish = resolve; });
      return Promise.resolve(apiResponse({ ...databases, defaultDatabase: enabled ? 'geolite2' : 'dbip', providers: [{ ...databases.providers[0], enabled, status: enabled ? 'ready' : 'disabled' }, { ...databases.providers[0], id: 'dbip', name: 'DB-IP Lite', licenseConfirmed: false, licenseReviewMode: 'manual', versions: [] }] }));
    });
    vi.stubGlobal('fetch', fetchMock); renderBusiness(<DatabasesPage />);
    const button = (await screen.findAllByRole('button', { name: /停\s*用/ }))[0];
    fireEvent.click(button);
    fireEvent.submit(screen.getByRole('form', { name: '停用默认数据库表单' }));
    fireEvent.submit(screen.getByRole('form', { name: '停用默认数据库表单' }));
    expect(fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST')).toHaveLength(1);
    const [path, options] = fetchMock.mock.calls.find(([, options]) => options?.method === 'POST')!;
    expect(path).toBe('/api/console/databases/geolite2/state');
    expect(options?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'controlled-session-token' });
    expect(JSON.parse(String(options?.body))).toEqual({ enabled: false, revision: 0, defaultDatabase: 'dbip' });
    enabled = false; finish!(apiResponse({}));
    expect(await screen.findByText('默认数据库已切换，原数据库已停用')).not.toBeNull();
    expect(await screen.findByRole('button', { name: /启\s*用/ })).not.toBeNull();
  });

  it('激活校验失败保留当前版本并展示服务端结果', async () => {
    vi.stubGlobal('fetch', vi.fn((path: string, options?: RequestInit) => Promise.resolve(options?.method === 'POST' ? apiResponse(null, 409, 'VERSION_NOT_READY', '版本尚未就绪，活动版本未改变。') : apiResponse(databases))));
    renderBusiness(<DatabasesPage />);
    fireEvent.click(await screen.findByRole('button', { name: '激活此版本' }));
    expect(await screen.findByText('版本尚未就绪，活动版本未改变。')).not.toBeNull();
    expect(screen.getByText('2026-11-01')).not.toBeNull();
    expect(screen.queryByText('数据库版本已激活，后续查询使用新版本')).toBeNull();
  });

  it('上传无需授权表单，仅传文件和地址族，传输完成后仍等待技术校验并显式激活', async () => {
    installReadMocks();
    let xhr: ControlledXhr;
    class ControlledXhr {
      upload = { onprogress: null as null | ((event: { lengthComputable: boolean; loaded: number; total: number }) => void) };
      onload: (() => void) | null = null; onerror = null; onabort: (() => void) | null = null;
      status = 200; responseText = JSON.stringify({ success: true, code: 'OK', message: 'OK', data: {} }); withCredentials = false;
      open = vi.fn(); setRequestHeader = vi.fn(); send = vi.fn(); abort = vi.fn();
      constructor() { xhr = this; }
    }
    vi.stubGlobal('XMLHttpRequest', ControlledXhr);
    renderBusiness(<DatabasesPage />);
    fireEvent.click(await screen.findByRole('button', { name: '上传新版' }));
    const form = screen.getByRole('form', { name: '上传数据库表单' });
    const file = new File(['controlled-mmdb-fixture'], 'new.mmdb');
    fireEvent.change(within(form).getByLabelText('数据库文件（原始 MMDB / XDB / DAT）'), { target: { files: [file] } });
    expect(within(form).queryByRole('checkbox')).toBeNull();
    expect(within(form).queryByRole('textbox')).toBeNull();
    expect((within(form).getByRole('button', { name: '上传并校验' }) as HTMLButtonElement).disabled).toBe(false);
    fireEvent.submit(form);
    expect(xhr!.open).toHaveBeenCalledWith('POST', '/api/console/databases/geolite2/uploads');
    expect(xhr!.setRequestHeader).toHaveBeenCalledWith('X-CSRF-TOKEN', 'controlled-session-token');
    expect(xhr!.setRequestHeader.mock.calls.some(([key]) => key === 'Content-Type')).toBe(false);
    const sent = xhr!.send.mock.calls[0][0] as FormData;
    expect(Array.from(sent.keys()).sort()).toEqual(['addressFamily', 'file']);
    expect(sent.get('licenseEvidence')).toBeNull(); expect(sent.get('licenseConfirmed')).toBeNull();
    expect(sent.get('file')).toBe(file);
    act(() => xhr!.upload.onprogress!({ lengthComputable: true, loaded: 100, total: 100 }));
    expect(await screen.findByText('传输完成，服务端正在校验；请等待最终结果。')).not.toBeNull();
    await act(async () => xhr!.onload!());
    expect(await screen.findByText('数据库已上传并校验，请在版本列表显式激活')).not.toBeNull();
  });

  it('设置保留可信当前IP并提交普通配置，冲突不假报成功', async () => {
    const fetchMock = vi.fn((path: string, options?: RequestInit) => Promise.resolve(options?.method === 'POST' ? apiResponse(null, 409, 'SETTINGS_CONFLICT', '配置已被其他管理员修改，请刷新。') : apiResponse(settings)));
    vi.stubGlobal('fetch', fetchMock); renderBusiness(<SettingsPage />);
    const form = await screen.findByRole('form', { name: '系统设置表单' });
    expect(screen.getByText('127.0.0.1', { selector: 'code' })).not.toBeNull();
    fireEvent.change(screen.getByLabelText('整体查询超时（ms）'), { target: { value: '1500' } });
    fireEvent.submit(form);
    expect(await screen.findByText('配置已被其他管理员修改，请刷新。')).not.toBeNull();
    const options = fetchMock.mock.calls.find(([, options]) => options?.method === 'POST')![1];
    expect(JSON.parse(String(options?.body))).toEqual({ revision: 2, query: { ...settings.query, totalTimeoutMs: 1500 }, allowedNetworks: ['127.0.0.1', '::1'], basic: settings.basic });
    expect(screen.queryByText('系统设置已保存，生效范围请查看各项说明')).toBeNull();
  });

  it('业务请求发现会话失效后卸载管理页面并返回独立登录', async () => {
    window.history.replaceState(null, '', '/console/audit');
    let valid = true;
    vi.stubGlobal('fetch', vi.fn((path: string) => {
      if (path.endsWith('/csrf')) return Promise.resolve(apiResponse({ token: 'controlled-token' }));
      if (path.startsWith('/api/console/audits')) { valid = false; return Promise.resolve(apiResponse(null, 401, 'CONSOLE_UNAUTHENTICATED', '会话已失效')); }
      return Promise.resolve(apiResponse({ allowed: true, initialized: true, authenticated: valid, user: valid ? { id: '1', account: 'admin', displayName: '测试管理员' } : null }, valid ? 200 : 401, valid ? 'OK' : 'CONSOLE_UNAUTHENTICATED'));
    }));
    render(<ConsolePage />);
    expect(await screen.findByRole('form', { name: '管理员登录表单' })).not.toBeNull();
    expect(screen.queryByRole('heading', { name: '管理员操作记录' })).toBeNull();
    expect(screen.queryByRole('navigation', { name: '管理导航' })).toBeNull();
  });
});

it('旧访问记录请求晚到不会覆盖新的筛选结果', async () => {
  let finishInitial: ((response: Response) => void) | undefined;
  const pending = new Promise<Response>(resolve => { finishInitial = resolve; });
  const makePage = (sourceAddress: string) => ({ items: [{ id: sourceAddress, occurredAtUtc: overview.toUtc, sourceAddress, routeCategory: 'query', method: 'GET', format: 'json', database: 'geolite2', databaseVersion: 'controlled-version', statusCode: 200, elapsedMilliseconds: 1, isCancelled: false, clientCategory: 'browser', traceId: 'controlled-trace' }], total: 1, page: 1, pageSize: 20, fromUtc: overview.fromUtc, toUtc: overview.toUtc, retentionDays: 30, queue: overview.queue });
  vi.stubGlobal('fetch', vi.fn((path: string) => path.includes('sourceAddress=') ? Promise.resolve(apiResponse(makePage('203.0.113.2'))) : pending));
  renderBusiness(<AccessLogsPage />);
  fireEvent.change(screen.getByLabelText('来源 IP'), { target: { value: '203.0.113.2' } });
  fireEvent.submit(screen.getByRole('form', { name: '访问记录筛选' }));
  expect(await screen.findByText('203.0.113.2', { selector: 'code' })).not.toBeNull();
  await act(async () => { finishInitial!(apiResponse(makePage('203.0.113.1'))); await pending; });
  expect(screen.queryByText('203.0.113.1', { selector: 'code' })).toBeNull();
  expect(screen.getByText('203.0.113.2', { selector: 'code' })).not.toBeNull();
});

// 00:00→10:00 的十小时空档必须比10:00→11:00的一小时宽十倍。
it('稀疏趋势点按真实时间间隔和窗口映射横坐标，不将缺失时间补为零', () => {
  const points = [0, 10, 11].map(hour => ({ timeUtc: new Date(Date.UTC(2026, 10, 3, hour)).toISOString(), requests: 5, queryRequests: 2, errors: 0 }));
  const { container } = render(<RequestTrend points={points} fromUtc="2026-11-03T00:00:00Z" toUtc="2026-11-03T12:00:00Z" />);
  const positions = Array.from(container.querySelectorAll('circle[data-series="requests"]')).map(point => Number(point.getAttribute('cx')));
  expect((positions[1] - positions[0]) / (positions[2] - positions[1])).toBeCloseTo(10);
  expect(positions[2]).toBeLessThan(762);
  expect(container.querySelectorAll('circle[data-series="requests"]')).toHaveLength(3);
  expect(screen.getByText('仅连接已记录统计点，未记录区间不补零。')).not.toBeNull();
});

it('新版本可按技术状态激活，不显示额外的判断性说明', async () => {
  const manual = { ...databases.providers[0], licenseConfirmed: false, licenseReviewMode: 'manual', versions: databases.providers[0].versions.map(version => ({ ...version, licenseConfirmed: false, licenseReviewMode: 'manual' })) };
  const fetchMock = vi.fn((path: string, options?: RequestInit) => Promise.resolve(options?.method === 'POST' ? apiResponse({}) : apiResponse({ ...databases, providers: [manual] })));
  vi.stubGlobal('fetch', fetchMock); renderBusiness(<DatabasesPage />);
  const activate = await screen.findByRole('button', { name: '激活此版本' });
  expect((activate as HTMLButtonElement).disabled).toBe(false);
  expect(screen.getByText('已启用')).not.toBeNull();
  expect(screen.queryByText('授权人工处理')).toBeNull();
  expect(screen.queryByText('许可待确认')).toBeNull();
  expect(screen.queryByText('许可已确认')).toBeNull();
  fireEvent.click(activate);
  expect(await screen.findByText('数据库版本已激活，后续查询使用新版本')).not.toBeNull();
  expect(fetchMock.mock.calls.find(([, options]) => options?.method === 'POST')?.[0]).toBe('/api/console/databases/geolite2/activate');
});

it('技术就绪的来源可以在设置中选择为默认库', async () => {
  const manual = { ...databases.providers[0], id: 'dbip', name: 'DB-IP City Lite', licenseConfirmed: false, licenseReviewMode: 'manual' };
  const currentSettings = { ...settings, providers: [...settings.providers, manual] };
  const fetchMock = vi.fn((path: string, options?: RequestInit) => Promise.resolve(apiResponse(options?.method === 'POST' ? { ...currentSettings, query: { ...settings.query, defaultDatabase: 'dbip' } } : currentSettings)));
  vi.stubGlobal('fetch', fetchMock); renderBusiness(<SettingsPage />);
  const select = await screen.findByRole('combobox', { name: /默认查询数据库/ });
  expect(within(select).getByRole('option', { name: 'DB-IP City Lite' })).not.toBeNull();
  fireEvent.change(select, { target: { value: 'dbip' } });
  fireEvent.submit(screen.getByRole('form', { name: '系统设置表单' }));
  expect(await screen.findByText('系统设置已保存，生效范围请查看各项说明')).not.toBeNull();
  const options = fetchMock.mock.calls.find(([, request]) => request?.method === 'POST')![1];
  expect(JSON.parse(String(options?.body)).query.defaultDatabase).toBe('dbip');
});

it('运行总览只显示来源技术状态，不显示额外判断性说明', async () => {
  const manual = { ...databases.providers[0], licenseConfirmed: false, licenseReviewMode: 'manual' };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path.startsWith('/api/console/overview') ? apiResponse(overview) : apiResponse({ ...databases, providers: [manual] }))));
  renderBusiness(<OverviewPage />);
  expect(await screen.findByText('IPv4 / IPv6')).not.toBeNull();
  expect(screen.queryByText('授权人工处理')).toBeNull();
  expect(screen.getByText('已就绪')).not.toBeNull();
  expect(screen.queryByText('许可待确认')).toBeNull();
  expect(screen.queryByText('许可已确认')).toBeNull();
});

it('未配置文件的纯真可正常启用，界面不展示许可授权门槛', async () => {
  let enabled = false;
  const pure = { ...databases.providers[0], id: 'CZ88', name: '纯真', enabled: false, ready: false, status: 'disabled', licenseConfirmed: false, licenseReviewMode: null, versions: [], message: '数据文件尚未配置。' };
  const fetchMock = vi.fn((path: string, options?: RequestInit) => {
    if (options?.method === 'POST') { enabled = true; return Promise.resolve(apiResponse({})); }
    return Promise.resolve(apiResponse({ ...databases, defaultDatabase: 'dbip', providers: [{ ...databases.providers[0], id: 'dbip', name: 'DB-IP City Lite' }, { ...pure, enabled, status: enabled ? 'notReady' : 'disabled' }] }));
  });
  vi.stubGlobal('fetch', fetchMock); const { container } = render(<ConsoleContext.Provider value={{ csrfToken: 'controlled-session-token', recheck: async () => {} }}><DatabasesPage /></ConsoleContext.Provider>);
  fireEvent.click(await screen.findByRole('button', { name: /启\s*用/ }));
  expect(await screen.findByText('纯真已启用')).not.toBeNull();
  // 成功提示与刷新后的卡片分属两次异步更新，等待真实刷新完成再核对状态。
  const card = (await screen.findByRole('heading', { name: '纯真' })).closest('section')!;
  expect(within(card).getByRole('button', { name: /停\s*用/ })).not.toBeNull();
  expect(within(card).getByText('未就绪')).not.toBeNull();
  expect(container.textContent).not.toMatch(/许可|授权/);
  const [path, options] = fetchMock.mock.calls.find(([, request]) => request?.method === 'POST')!;
  expect(path).toBe('/api/console/databases/CZ88/state');
  expect(JSON.parse(String(options?.body))).toEqual({ enabled: true, revision: 0 });
});

it('纯真可上传原始DAT，地址族固定IPv4且只发送技术文件参数', async () => {
  const pure = { ...databases.providers[0], id: 'CZ88', name: '纯真', ready: false, enabled: false, supportsIpv4: true, supportsIpv6: false, status: 'disabled', versions: [] };
  vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse({ ...databases, providers: [databases.providers[0], pure] }))));
  let sent: FormData | undefined;
  let uploadPath = '';
  class DatUploadRequest {
    upload = {}; onload: (() => void) | null = null; onerror = null; onabort = null;
    status = 200; responseText = JSON.stringify({ success: true, code: 'OK', message: 'OK', data: {} }); withCredentials = false;
    open = vi.fn((_method: string, path: string) => { uploadPath = path; }); setRequestHeader = vi.fn(); abort = vi.fn();
    send(data: FormData) { sent = data; this.onload?.(); }
  }
  vi.stubGlobal('XMLHttpRequest', DatUploadRequest);
  renderBusiness(<DatabasesPage />);
  const heading = await screen.findByRole('heading', { name: '纯真' });
  fireEvent.click(within(heading.closest('section')!).getByRole('button', { name: '上传新版' }));
  const form = screen.getByRole('form', { name: '上传数据库表单' });
  const input = within(form).getByLabelText('数据库文件（原始 MMDB / XDB / DAT）');
  expect(input.getAttribute('accept')).toBe('.dat');
  const family = within(form).getByRole('combobox', { name: '地址族' }) as HTMLSelectElement;
  expect(family.value).toBe('ipv4'); expect(family.disabled).toBe(true);
  expect(family.options).toHaveLength(1);
  fireEvent.change(input, { target: { files: [new File(['controlled-dat'], 'qqwry.dat')] } });
  fireEvent.submit(form);
  expect(await screen.findByText('数据库已上传并校验，请在版本列表显式激活')).not.toBeNull();
  expect(uploadPath).toBe('/api/console/databases/CZ88/uploads');
  expect(sent?.get('addressFamily')).toBe('ipv4');
  expect(Array.from(sent!.keys()).sort()).toEqual(['addressFamily', 'file']);
});

it('后台上下调整全部数据库并保存排序，携带排序修订号和CSRF且待处理时禁止重复提交', async () => {
  const all = [databases.providers[0], { ...databases.providers[0], id: 'CZ88', name: '纯真 QQWry', enabled: false, ready: false, status: 'disabled', versions: [] }, { ...databases.providers[0], id: 'dbip', name: 'DB-IP City Lite', versions: [] }];
  let storedIds = all.map(item => item.id), revision = 7;
  let finish: ((response: Response) => void) | undefined;
  const fetchMock = vi.fn((path: string, options?: RequestInit) => {
    if (options?.method === 'POST') return new Promise<Response>(resolve => { finish = resolve; });
    return Promise.resolve(apiResponse({ ...databases, displayOrderRevision: revision, providers: storedIds.map(id => all.find(item => item.id === id)!) }));
  });
  vi.stubGlobal('fetch', fetchMock); renderBusiness(<DatabasesPage />);
  const orderIds = () => within(screen.getByRole('list', { name: '数据库排序' })).getAllByRole('listitem').map(item => item.querySelector('code')?.textContent);
  fireEvent.click(await screen.findByRole('button', { name: '上移纯真 QQWry' }));
  fireEvent.click(screen.getByRole('button', { name: '下移GeoLite2 City' }));
  expect(orderIds()).toEqual(['CZ88', 'dbip', 'geolite2']);
  expect((screen.getByRole('button', { name: '上移纯真 QQWry' }) as HTMLButtonElement).disabled).toBe(true);
  const save = screen.getByRole('button', { name: '保存排序' });
  fireEvent.click(save); fireEvent.click(save);
  const writes = fetchMock.mock.calls.filter(([, options]) => options?.method === 'POST');
  expect(writes).toHaveLength(1);
  expect(writes[0][0]).toBe('/api/console/databases/order');
  expect(writes[0][1]?.headers).toMatchObject({ 'X-CSRF-TOKEN': 'controlled-session-token' });
  expect(JSON.parse(String(writes[0][1]?.body))).toEqual({ providerIds: ['CZ88', 'dbip', 'geolite2'], revision: 7 });
  expect(screen.getAllByRole('button', { name: /^[上下]移/ }).every(button => (button as HTMLButtonElement).disabled)).toBe(true);
  storedIds = ['CZ88', 'dbip', 'geolite2']; revision = 8;
  await act(async () => finish!(apiResponse({})));
  expect(await screen.findByText('数据库排序已保存，首页卡片和署名将按此顺序展示')).not.toBeNull();
  await waitFor(() => expect(orderIds()).toEqual(storedIds));
  expect((screen.getByRole('button', { name: '保存排序' }) as HTMLButtonElement).disabled).toBe(true);
});

it('后台排序冲突保留未保存顺序，显式刷新后读取服务端排序', async () => {
  const all = [databases.providers[0], { ...databases.providers[0], id: 'dbip', name: 'DB-IP City Lite', versions: [] }];
  let revision = 2;
  const fetchMock = vi.fn((path: string, options?: RequestInit) => Promise.resolve(options?.method === 'POST'
    ? apiResponse(null, 409, 'DATABASE_ORDER_CONFLICT', '排序已被其他管理员修改，请刷新后重试。')
    : apiResponse({ ...databases, displayOrderRevision: revision, providers: all })));
  vi.stubGlobal('fetch', fetchMock); renderBusiness(<DatabasesPage />);
  const orderIds = () => within(screen.getByRole('list', { name: '数据库排序' })).getAllByRole('listitem').map(item => item.querySelector('code')?.textContent);
  fireEvent.click(await screen.findByRole('button', { name: '上移DB-IP City Lite' }));
  fireEvent.click(screen.getByRole('button', { name: '保存排序' }));
  expect(await screen.findByText('排序已被其他管理员修改，请刷新后重试。')).not.toBeNull();
  expect(orderIds()).toEqual(['dbip', 'geolite2']);
  expect(fetchMock).toHaveBeenCalledTimes(2);
  expect(screen.queryByText('数据库排序已保存，首页卡片和署名将按此顺序展示')).toBeNull();
  revision = 3; fireEvent.click(screen.getByRole('button', { name: '刷新数据' }));
  await waitFor(() => expect(orderIds()).toEqual(['geolite2', 'dbip']));
  expect((screen.getByRole('button', { name: '保存排序' }) as HTMLButtonElement).disabled).toBe(true);
});
