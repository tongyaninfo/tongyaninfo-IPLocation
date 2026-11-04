import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import HomePage from '../src/pages/Home/HomePage';
import type { ProviderDescriptor, QueryResult } from '../src/types/ip-location';

const providers: ProviderDescriptor[] = [{ id: 'dbip', name: 'DB-IP Lite', status: 'notReady', supportsIpv4: true, supportsIpv6: true, enabled: true, licenseConfirmed: true, ready: false, version: null, message: '数据文件尚未配置。', licenseUrl: 'https://db-ip.com/db/lite.php', attribution: 'IP Geolocation by DB-IP' }, { id: 'ip2location', name: 'IP2Location LITE', status: 'licenseBlocked', supportsIpv4: true, supportsIpv6: true, enabled: false, licenseConfirmed: false, ready: false, version: null, message: '暂停对外启用。', licenseUrl: 'https://www.ip2location.com/lite-terms', attribution: '通研信息 uses the IP2Location LITE database' }];
function json<T>(data: T, status = 200, success = true): Response { return new Response(JSON.stringify({ success, code: success ? 'OK' : 'DB_NOT_READY', message: success ? '查询完成' : '没有已就绪来源。', data }), { status }); }
function query(ip: string): QueryResult { return { ip, addressType: 'public', status: 'unavailable', sources: providers.map(provider => ({ id: provider.id, name: provider.name, status: provider.status, version: null, fields: {}, elapsedMs: 0, message: provider.message, licenseUrl: provider.licenseUrl, attribution: provider.attribution })) }; }

describe('首页查询交互', () => {
  it('公开首页提供查询、API和MCP导航，不展示或探测管理入口', async () => {
    const fetchMock = vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(providers) : json(query('203.0.113.5'), 503, false)));
    vi.stubGlobal('fetch', fetchMock);
    render(<HomePage />);
    await waitFor(() => expect(screen.getByTestId('visitor-ip').textContent).toBe('203.0.113.5'));
    const navigation = screen.getByRole('navigation', { name: '主导航' });
    expect(within(navigation).getAllByRole('link').map(link => link.getAttribute('href'))).toEqual(['#query', '#api', '#mcp']);
    expect(screen.queryByText(/管理控制台|管理后台/)).toBeNull();
    expect(document.querySelector('a[href^="/console"]')).toBeNull();
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual(['/api/providers', '/?db=all&format=json']);
  });

  it.each(['203.0.113.5', '2001:db8::1'])('识别访问地址后默认填入查询框：%s', async ip => {
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(providers) : json(query(ip), 503, false))));
    render(<HomePage />);
    const input = screen.getByRole('textbox', { name: '查询一个 IP 地址' }) as HTMLInputElement;
    await waitFor(() => expect(input.value).toBe(ip));
    expect(screen.getByTestId('visitor-ip').textContent).toBe(ip);
  });

  it('迟到的访问地址不覆盖手动输入，查询我的IP时重新填回访问地址', async () => {
    let finishDefault: ((value: Response) => void) | undefined;
    const initial = new Promise<Response>(resolve => { finishDefault = resolve; });
    let selfQueries = 0;
    vi.stubGlobal('fetch', vi.fn((path: string) => path === '/api/providers' ? Promise.resolve(json(providers))
      : selfQueries++ === 0 ? initial : Promise.resolve(json(query('203.0.113.5'), 503, false))));
    render(<HomePage />);
    const input = screen.getByRole('textbox', { name: '查询一个 IP 地址' }) as HTMLInputElement;
    fireEvent.change(input, { target: { value: '8.8.8.8' } });
    await act(async () => { finishDefault?.(json(query('203.0.113.5'), 503, false)); await initial; });
    await waitFor(() => expect(screen.getByTestId('visitor-ip').textContent).toBe('203.0.113.5'));
    expect(input.value).toBe('8.8.8.8');
    fireEvent.change(input, { target: { value: '' } });
    expect(input.value).toBe('');
    fireEvent.click(screen.getByRole('button', { name: '查询我的 IP' }));
    await waitFor(() => expect(input.value).toBe('203.0.113.5'));
  });

  it('IPinfo展示源内国家、大洲与AS字段，并提供官方署名回链', async () => {
    const active: ProviderDescriptor[] = [{ ...providers[0], id: 'ipinfo', name: 'IPinfo Lite', status: 'ready', ready: true, version: 'controlled-version', attribution: 'IP address data is powered by IPinfo', licenseUrl: 'https://creativecommons.org/licenses/by-sa/4.0/' }];
    const result: QueryResult = { ip: '203.0.113.5', addressType: 'public', status: 'success', sources: [{ ...active[0], status: 'success', fields: { country: 'China', countryCode: 'CN', continent: 'Asia', continentCode: 'AS', asn: 'AS64500', asName: '受控网络组织', asDomain: 'example.test' }, elapsedMs: 1 }] };
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(active) : json(result))));
    render(<HomePage />);
    expect(await screen.findByText('AS64500')).not.toBeNull();
    expect(screen.getByText('受控网络组织')).not.toBeNull();
    expect(screen.getByText('example.test')).not.toBeNull();
    expect(screen.getByText('Asia · AS')).not.toBeNull();
    expect(screen.queryByText('近似坐标')).toBeNull();
    expect(screen.getByRole('link', { name: 'IP address data is powered by IPinfo' }).getAttribute('href')).toBe('https://ipinfo.io');
  });
  it('启用IP2Location后展示用户指定的英文原句与官方链接', async () => {
    const active: ProviderDescriptor[] = [{ ...providers[1], status: 'ready', enabled: true, licenseConfirmed: true, ready: true, message: '数据文件已就绪。' }];
    const result: QueryResult = { ip: '127.0.0.1', addressType: 'loopback', status: 'unsupported', sources: [] };
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(active) : json(result))));
    render(<HomePage />);
    const link = await screen.findByRole('link', { name: 'IP geolocation' });
    expect(link.getAttribute('href')).toBe('https://lite.ip2location.com');
    expect(link.closest('[aria-label="本次结果数据来源"]')?.textContent).toContain('通研信息 uses the IP2Location LITE database for IP geolocation.');
  });
  it('已就绪的 GeoLite2 即使本次不定位也保留来源署名', async () => {
    const active: ProviderDescriptor[] = [{ ...providers[0], id: 'geolite2', name: 'MaxMind GeoLite2 City', status: 'ready', ready: true, version: 'controlled-version', attribution: 'This product includes GeoLite Data created by MaxMind, available from https://www.maxmind.com.', licenseUrl: 'https://www.maxmind.com/en/geolite/eula' }];
    const result: QueryResult = { ip: '127.0.0.1', addressType: 'loopback', status: 'unsupported', sources: [] };
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(active) : json(result))));
    render(<HomePage />);
    expect(await screen.findByRole('link', { name: /This product includes GeoLite Data created by MaxMind/ })).not.toBeNull();
    expect(screen.queryByText(/不得用于识别或定位特定家庭/)).toBeNull();
    expect(screen.queryByRole('link', { name: 'CC BY-SA 4.0' })).toBeNull();
  });
  it('默认查询 503 时仍展示识别地址与来源原因，禁用来源不出现在实际署名', async () => {
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(providers) : json(query('203.0.113.5'), 503, false))));
    render(<HomePage />);
    await waitFor(() => expect(screen.getByTestId('visitor-ip').textContent).toBe('203.0.113.5'));
    expect(screen.getByTestId('queried-ip').textContent).toBe('203.0.113.5');
    expect(screen.getByText('没有已就绪来源。')).not.toBeNull();
    expect(screen.queryByText('暂停对外启用。')).toBeNull();
    expect(screen.queryByRole('heading', { name: 'IP2Location LITE' })).toBeNull();
    expect(screen.queryByText('通研信息 uses the IP2Location LITE database')).toBeNull();
    expect(screen.getByText('已就绪 0 / 1')).not.toBeNull();
  });

  it('较晚完成的默认请求不能覆盖新的手动查询结果', async () => {
    let finishDefault: ((value: Response) => void) | undefined;
    const initial = new Promise<Response>(resolve => { finishDefault = resolve; });
    const fetchMock = vi.fn((path: string) => path === '/api/providers' ? Promise.resolve(json(providers)) : path.startsWith('/?db=') ? initial : Promise.resolve(json(query('8.8.8.8'), 503, false)));
    vi.stubGlobal('fetch', fetchMock);
    render(<HomePage />);
    fireEvent.change(screen.getByLabelText('查询一个 IP 地址'), { target: { value: '8.8.8.8' } });
    fireEvent.submit(screen.getByRole('form', { name: 'IP 查询表单' }));
    await waitFor(() => expect(screen.getByTestId('queried-ip').textContent).toBe('8.8.8.8'));
    expect(screen.getByText('未能识别')).not.toBeNull();
    expect(screen.queryByText('识别中')).toBeNull();
    await act(async () => { finishDefault?.(json(query('203.0.113.5'), 503, false)); await initial; });
    expect(screen.getByTestId('queried-ip').textContent).toBe('8.8.8.8');
    expect(fetchMock.mock.calls.some(([path]) => path === '/?ip=8.8.8.8&db=all&format=json')).toBe(true);
  });

  it('空白输入不发起手动请求并给出可读提示', async () => {
    const fetchMock = vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(providers) : json(query('203.0.113.5'), 503, false)));
    vi.stubGlobal('fetch', fetchMock);
    render(<HomePage />);
    await waitFor(() => expect(screen.getByTestId('queried-ip').textContent).toBe('203.0.113.5'));
    fireEvent.change(screen.getByLabelText('查询一个 IP 地址'), { target: { value: '   ' } });
    fireEvent.submit(screen.getByRole('form', { name: 'IP 查询表单' }));
    expect(screen.getByText('请先输入需要查询的单个 IP 地址。')).not.toBeNull();
    expect(fetchMock.mock.calls.filter(([path]) => path.startsWith('/?ip='))).toHaveLength(0);
  });

  it('手动查询网络失败时展示失败信息，不谎称查询成功', async () => {
    vi.stubGlobal('fetch', vi.fn((path: string) => path.startsWith('/?ip=') ? Promise.reject(new Error('failed')) : Promise.resolve(path === '/api/providers' ? json(providers) : json(query('203.0.113.5'), 503, false))));
    render(<HomePage />);
    await waitFor(() => expect(screen.getByTestId('queried-ip').textContent).toBe('203.0.113.5'));
    fireEvent.change(screen.getByLabelText('查询一个 IP 地址'), { target: { value: '8.8.8.8' } });
    fireEvent.submit(screen.getByRole('form', { name: 'IP 查询表单' }));
    await waitFor(() => expect(screen.getByText('连接服务失败')).not.toBeNull());
    expect(screen.getByText('暂时无法连接查询服务，请稍后重试。')).not.toBeNull();
  });

  it('部分来源成功时保留实际字段及零坐标，并给出部分可用提示', async () => {
    const data = query('203.0.113.5');
    data.status = 'partial';
    data.sources[0] = { ...data.sources[0], status: 'success', version: 'controlled-test', fields: { country: '受控样例国家', latitude: 0, longitude: 0 } };
    data.sources[1] = { ...data.sources[1], status: 'timeout', message: '此来源查询超时。' };
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(providers.map(provider => ({ ...provider, enabled: true }))) : json(data))));
    render(<HomePage />);
    await waitFor(() => expect(screen.getByText('受控样例国家')).not.toBeNull());
    expect(screen.getByText('0, 0')).not.toBeNull();
    expect(screen.getByText('已返回可用来源')).not.toBeNull();
    expect(screen.getByText('此来源查询超时。')).not.toBeNull();
    expect(screen.getByRole('link', { name: 'IP Geolocation by DB-IP' })).not.toBeNull();
  });
});

it('后台停用后新的公开查询同步来源状态及署名', async () => {
  let enabled = true;
  const active = { ...providers[1], enabled: true, ready: true, licenseConfirmed: true, status: 'ready' };
  const fetchMock = vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json([{ ...active, enabled, ready: enabled, status: enabled ? 'ready' : 'disabled' }]) : json({ ip: '127.0.0.1', addressType: 'loopback', status: 'special', sources: [] })));
  vi.stubGlobal('fetch', fetchMock);
  render(<HomePage />);
  expect(await screen.findByRole('link', { name: 'IP geolocation' })).not.toBeNull();
  enabled = false;
  fireEvent.click(screen.getByRole('button', { name: '查询我的 IP' }));
  await waitFor(() => expect(screen.getByText('已就绪 0 / 0')).not.toBeNull());
  expect(screen.queryByRole('heading', { name: 'IP2Location LITE' })).toBeNull();
  expect(screen.getByRole('heading', { name: '暂无启用的数据源' })).not.toBeNull();
  expect(screen.queryByRole('link', { name: 'IP geolocation' })).toBeNull();
});

it('停用GeoLite2与纯真不会显示卡片、计数或旧成功结果的署名', async () => {
  const enabled = { ...providers[0], ready: true, status: 'ready', version: 'controlled-active' };
  const disabled = [
    { ...enabled, id: 'geolite2', name: 'GeoLite2 City', enabled: false, status: 'disabled', attribution: '已停用的MaxMind署名' },
    { ...enabled, id: 'CZ88', name: '纯真', enabled: false, status: 'disabled', attribution: '已停用的纯真署名' },
  ];
  const staleQuery: QueryResult = { ip: '203.0.113.5', addressType: 'public', status: 'success', sources: disabled.map(provider => ({ id: provider.id, name: provider.name, status: 'success', version: provider.version, fields: { country: '旧查询结果' }, elapsedMs: 1, message: null, attribution: provider.attribution, licenseUrl: null })) };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json([enabled, ...disabled]) : json(staleQuery))));
  render(<HomePage />);
  expect(await screen.findByText('已就绪 1 / 1')).not.toBeNull();
  expect(screen.getByRole('heading', { name: 'DB-IP Lite' })).not.toBeNull();
  expect(screen.queryByRole('heading', { name: 'GeoLite2 City' })).toBeNull();
  expect(screen.queryByRole('heading', { name: '纯真' })).toBeNull();
  expect(screen.queryByText('旧查询结果')).toBeNull();
  expect(screen.queryByText('已停用的MaxMind署名')).toBeNull();
  expect(screen.queryByText('已停用的纯真署名')).toBeNull();
  expect(screen.queryByText(/GeoLite 数据使用限制/)).toBeNull();
  expect(screen.getByRole('link', { name: 'IP Geolocation by DB-IP' })).not.toBeNull();
  expect(screen.getByRole('heading', { name: /数据源结果/ }).querySelector('.count-badge')?.textContent).toBe('1');
});

it('已启用但未就绪的来源仍显示自己的状态', async () => {
  const active = providers.map(provider => ({ ...provider, enabled: true }));
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(active) : json({ ip: '127.0.0.1', addressType: 'loopback', status: 'special', sources: [] }))));
  render(<HomePage />);
  expect(await screen.findByText('已就绪 0 / 2')).not.toBeNull();
  expect(screen.getByRole('heading', { name: 'DB-IP Lite' })).not.toBeNull();
  expect(screen.getByRole('heading', { name: 'IP2Location LITE' })).not.toBeNull();
  expect(screen.getByText('数据文件尚未配置。')).not.toBeNull();
  expect(screen.getByText('暂停对外启用。')).not.toBeNull();
});

it('历史字段不影响已启用就绪来源的卡片或署名', async () => {
  const manual: ProviderDescriptor = { ...providers[0], id: 'ipinfo', name: 'IPinfo Lite', status: 'ready', ready: true, licenseConfirmed: false, licenseReviewMode: 'manual', attribution: 'IP address data is powered by IPinfo', licenseUrl: null };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json([manual]) : json({ ip: '127.0.0.1', addressType: 'loopback', status: 'special', sources: [] }))));
  render(<HomePage />);
  expect(await screen.findByText('已就绪 1 / 1')).not.toBeNull();
  expect(screen.getByRole('heading', { name: 'IPinfo Lite' })).not.toBeNull();
  expect(screen.getByRole('link', { name: 'IP address data is powered by IPinfo' })).not.toBeNull();
  expect(screen.queryByText('许可待确认')).toBeNull();
});

it.each(['notConfigured', 'notFound', 'special'])('纯真启用时在%s场景仍展示用户指定的完整署名', async scenario => {
  const pure: ProviderDescriptor = { ...providers[0], id: 'CZ88', name: '纯真', enabled: true, ready: scenario === 'notFound', status: scenario === 'notFound' ? 'ready' : 'notReady', licenseConfirmed: false, licenseUrl: null, attribution: null, message: '数据文件尚未配置。' };
  const data: QueryResult = { ip: scenario === 'special' ? '127.0.0.1' : '203.0.113.5', addressType: scenario === 'special' ? 'loopback' : 'public', status: scenario === 'notFound' ? 'notFound' : scenario === 'special' ? 'special' : 'unavailable', sources: scenario === 'notFound' ? [{ id: pure.id, name: pure.name, status: 'notFound', version: null, fields: {}, elapsedMs: 1, message: null, licenseUrl: null, attribution: null }] : [] };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json([pure]) : json(data))));
  render(<HomePage />);
  const link = await screen.findByRole('link', { name: '纯真CZ88' });
  expect(link.getAttribute('href')).toBe('https://www.cz88.net');
  expect(link.closest('p')?.outerHTML).toBe('<p>IP地址位置数据由<a href="https://www.cz88.net">纯真CZ88</a>提供支持</p>');
  expect(screen.queryByText(/立即获得免费/)).toBeNull();
});

it('纯真停用后移除准确署名及卡片，旧查询结果不能将其带回', async () => {
  let enabled = true;
  const pure: ProviderDescriptor = { ...providers[0], id: 'CZ88', name: '纯真', enabled: true, ready: false, status: 'notReady', licenseConfirmed: false, licenseUrl: null };
  const stale: QueryResult = { ip: '203.0.113.5', addressType: 'public', status: 'success', sources: [{ id: 'CZ88', name: '纯真', status: 'success', version: null, fields: {}, elapsedMs: 1, message: null, licenseUrl: null, attribution: '旧署名' }] };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json([{ ...pure, enabled, status: enabled ? 'notReady' : 'disabled' }]) : json(stale))));
  render(<HomePage />);
  expect(await screen.findByRole('link', { name: '纯真CZ88' })).not.toBeNull();
  enabled = false; fireEvent.click(screen.getByRole('button', { name: '查询我的 IP' }));
  await waitFor(() => expect(screen.getByText('已就绪 0 / 0')).not.toBeNull());
  expect(screen.queryByRole('link', { name: '纯真CZ88' })).toBeNull();
  expect(screen.queryByRole('heading', { name: '纯真' })).toBeNull();
  expect(screen.queryByText('旧署名')).toBeNull();
});

it('纯真area显示为网络描述，其他来源仍显示运营商', async () => {
  const active: ProviderDescriptor[] = [
    { ...providers[0], id: 'CZ88', name: '纯真 QQWry', status: 'ready', ready: true, supportsIpv6: false },
    { ...providers[0], status: 'ready', ready: true },
  ];
  const data: QueryResult = { ip: '203.0.113.5', addressType: 'public', status: 'success', sources: active.map(provider => ({ id: provider.id, name: provider.name, status: 'success', version: 'controlled', fields: { region: '文件中的地区原文', isp: provider.id === 'CZ88' ? '示例组织网络描述' : '示例运营商' }, elapsedMs: 1, message: null, attribution: null, licenseUrl: null })) };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(active) : json(data))));
  render(<HomePage />);
  const pure = await screen.findByRole('article', { name: '纯真 QQWry 数据源' });
  expect(await within(pure).findByText('网络描述')).not.toBeNull();
  expect(within(pure).getByText('示例组织网络描述')).not.toBeNull();
  expect(within(pure).queryByText('运营商')).toBeNull();
  expect(within(screen.getByRole('article', { name: 'DB-IP Lite 数据源' })).getByText('运营商')).not.toBeNull();
});

it.each([
  [['CZ88', 'ipinfo', 'geolite2', 'dbip']],
  [['ipinfo', 'CZ88', 'geolite2', 'dbip']],
])('首页卡片和含未就绪纯真的署名遵循来源顺序%s，查询顺序不覆盖配置', async order => {
  const byId: Record<string, ProviderDescriptor> = {
    CZ88: { ...providers[0], id: 'CZ88', name: '纯真 QQWry', ready: false, status: 'notReady', attribution: null },
    ipinfo: { ...providers[0], id: 'ipinfo', name: 'IPinfo Lite', ready: false, status: 'notReady', attribution: 'IP address data is powered by IPinfo' },
    geolite2: { ...providers[0], id: 'geolite2', name: 'GeoLite2 City', ready: true, status: 'ready', enabled: false, attribution: '已停用来源署名' },
    dbip: { ...providers[0], ready: true, status: 'ready' },
  };
  const data: QueryResult = { ip: '203.0.113.5', addressType: 'public', status: 'success', sources: ['dbip', 'geolite2', 'ipinfo'].map(id => ({ id, name: byId[id].name, status: 'success', version: null, fields: {}, elapsedMs: 1, message: null, attribution: byId[id].attribution, licenseUrl: null })) };
  vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(order.map(id => byId[id])) : json(data))));
  render(<HomePage />);
  await screen.findByRole('link', { name: 'IP address data is powered by IPinfo' });
  const enabledIds = order.filter(id => byId[id].enabled);
  expect(screen.getAllByRole('article').map(card => within(card).getByRole('heading').textContent)).toEqual(enabledIds.map(id => byId[id].name));
  const attributions = screen.getByLabelText('本次结果数据来源');
  const expectedLinks: Record<string, string> = { CZ88: '纯真CZ88', ipinfo: 'IP address data is powered by IPinfo', dbip: 'IP Geolocation by DB-IP' };
  expect(within(attributions).getAllByRole('link').map(link => link.textContent)).toEqual(enabledIds.map(id => expectedLinks[id]));
  const pureLink = within(attributions).getByRole('link', { name: '纯真CZ88' });
  expect(pureLink.closest('p')?.outerHTML).toBe('<p>IP地址位置数据由<a href="https://www.cz88.net">纯真CZ88</a>提供支持</p>');
  expect(screen.queryByText('已停用来源署名')).toBeNull();
});

it('首页沿用已取得的有序来源生成API标签，不为示例重复请求来源', async () => {
  const active: ProviderDescriptor[] = [
    { ...providers[0], id: 'CZ88', name: '纯真 QQWry', ready: false, status: 'notReady' },
    providers[0],
    { ...providers[0], id: 'ipinfo', name: 'IPinfo Lite' },
    { ...providers[0], id: 'ip2region', name: 'ip2region' },
    { ...providers[0], id: 'geolite2', name: 'GeoLite2 City', enabled: false },
    { ...providers[1], enabled: true, status: 'notReady' },
  ];
  const fetchMock = vi.fn((path: string) => Promise.resolve(path === '/api/providers' ? json(active) : json({ ip: '127.0.0.1', addressType: 'loopback', status: 'special', sources: [] })));
  vi.stubGlobal('fetch', fetchMock);
  render(<HomePage />);
  const mine = await screen.findByRole('region', { name: '我的 IP 命令示例' });
  const specified = screen.getByRole('region', { name: '查询指定 IP 命令示例' });
  expect(within(mine).getAllByRole('tab').map(tab => tab.textContent)).toEqual(['我的 IP', 'JSON', 'XML', 'TXT', '纯真CZ88', 'DB-IP', 'IPinfo', 'ip2region', 'IP2Location', 'ALL']);
  expect(within(specified).getAllByRole('tab').map(tab => tab.textContent)).toEqual(['JSON', 'XML', 'TXT', '纯真CZ88', 'DB-IP', 'IPinfo', 'ip2region', 'IP2Location', 'ALL']);
  expect(fetchMock.mock.calls.map(([path]) => path)).toEqual(['/api/providers', '/?db=all&format=json']);
  fireEvent.click(within(mine).getByRole('tab', { name: '纯真CZ88' }));
  expect(await within(mine).findByText("curl 'https://ip.tongyaninfo.com/?db=CZ88&format=json'")).not.toBeNull();
  fireEvent.click(within(specified).getByRole('tab', { name: '纯真CZ88' }));
  expect(await within(specified).findByText("curl 'https://ip.tongyaninfo.com/?ip=1.1.1.1&db=CZ88&format=json'")).not.toBeNull();
});
