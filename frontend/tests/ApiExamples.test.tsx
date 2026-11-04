import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiExamples } from '../src/components/ApiExamples';
import type { ProviderDescriptor } from '../src/types/ip-location';

function provider(id: string, overrides: Partial<ProviderDescriptor> = {}): ProviderDescriptor {
  return { id, name: id, enabled: true, ready: true, status: 'ready', supportsIpv4: true, supportsIpv6: false, licenseConfirmed: false, version: null, message: null, attribution: null, licenseUrl: null, ...overrides };
}
const group = (specified: boolean) => screen.getByRole('region', { name: specified ? '查询指定 IP 命令示例' : '我的 IP 命令示例' });
const tabNames = (region: HTMLElement) => within(region).getAllByRole('tab').map(tab => tab.textContent);
const selectTab = (region: HTMLElement, name: string) => fireEvent.click(within(region).getByRole('tab', { name }));
const command = (specified: boolean, db: string, format = 'json') =>
  `curl 'https://ip.tongyaninfo.com/?${specified ? 'ip=1.1.1.1&' : ''}db=${db}&format=${format}'`;
const activePanel = (region: HTMLElement) => within(region).getByRole('tabpanel');
async function expectCommand(region: HTMLElement, expected: string) {
  // AntD 会保留已访问的旧标签内容，只检查当前可见面板，避免相同命令文本造成歧义。
  await waitFor(() => expect(activePanel(region).querySelector('code')?.textContent).toBe(expected));
}

const allSources = [provider('dbip'), provider('CZ88'), provider('ip2location'), provider('ipinfo'), provider('ip2region'), provider('geolite2')];
const sourceLabels = [['dbip', 'DB-IP'], ['CZ88', '纯真CZ88'], ['ip2location', 'IP2Location'], ['ipinfo', 'IPinfo'], ['ip2region', 'ip2region'], ['geolite2', 'GeoLite2']];

describe('公开API示例的两个独立分组', () => {
  it('分组标题、默认选择及全部标签顺序清楚，ALL始终位于来源之后', async () => {
    render(<ApiExamples providers={allSources} />);
    const mine = group(false), specified = group(true);
    expect(within(mine).getByRole('heading', { name: '我的 IP' })).not.toBeNull();
    expect(within(specified).getByRole('heading', { name: '查询指定 IP' })).not.toBeNull();
    expect(tabNames(mine)).toEqual(['我的 IP', 'JSON', 'XML', 'TXT', ...sourceLabels.map(([, label]) => label), 'ALL']);
    expect(tabNames(specified)).toEqual(['JSON', 'XML', 'TXT', ...sourceLabels.map(([, label]) => label), 'ALL']);
    expect(within(specified).queryByRole('tab', { name: '我的 IP' })).toBeNull();
    expect(within(mine).getByRole('tab', { name: '我的 IP' }).getAttribute('aria-selected')).toBe('true');
    expect(within(specified).getByRole('tab', { name: 'JSON' }).getAttribute('aria-selected')).toBe('true');
    await expectCommand(mine, 'curl https://ip.tongyaninfo.com');
    await expectCommand(specified, command(true, 'dbip'));
  });

  it('两组所有结构化命令和六来源使用正确根参数，我的IP不携带ip', async () => {
    render(<ApiExamples providers={allSources} />);
    for (const specified of [false, true]) {
      const region = group(specified);
      for (const [label, db, format] of [['JSON', 'dbip', 'json'], ['XML', 'dbip', 'xml'], ['TXT', 'all', 'txt'],
        ...sourceLabels.map(([id, label]) => [label, id, 'json']), ['ALL', 'all', 'json']]) {
        selectTab(region, label);
        await expectCommand(region, command(specified, db, format));
        const text = activePanel(region).querySelector('code')!.textContent!;
        const url = new URL(text.slice("curl '".length, -1));
        expect(url.pathname).toBe('/');
        expect(url.searchParams.get('ip')).toBe(specified ? '1.1.1.1' : null);
        expect(url.searchParams.get('db')).toBe(db);
        expect(url.searchParams.get('format')).toBe(format);
        expect(text.startsWith("curl '") && text.endsWith("'")).toBe(true);
      }
    }
  });

  it('两组共同遵循后台来源顺序，仅过滤enabled，未就绪仍保留命令及技术原因', async () => {
    const sources = [provider('CZ88', { name: '纯真 QQWry', ready: false, status: 'notReady' }), provider('ipinfo'),
      provider('geolite2', { enabled: false }), provider('dbip'), provider('ip2location'), provider('ip2region')];
    render(<ApiExamples providers={sources} />);
    for (const specified of [false, true]) {
      const region = group(specified);
      expect(tabNames(region)).toEqual([...(specified ? [] : ['我的 IP']), 'JSON', 'XML', 'TXT', '纯真CZ88', 'IPinfo', 'DB-IP', 'IP2Location', 'ip2region', 'ALL']);
      expect(within(region).queryByRole('tab', { name: 'GeoLite2' })).toBeNull();
      selectTab(region, '纯真CZ88');
      await expectCommand(region, command(specified, 'CZ88'));
      expect(within(activePanel(region)).getByText('指定 纯真 QQWry 查询；当前未就绪，来源文件就绪后才能返回定位结果。')).not.toBeNull();
      expect(within(region).queryByText(/许可|授权/)).toBeNull();
    }
  });

  it('选择互不影响，重排保留选择，分别停用时回退各自默认且重新启用不抢回选择', async () => {
    const pure = provider('CZ88'), ipinfo = provider('ipinfo'), dbip = provider('dbip');
    const { rerender } = render(<ApiExamples providers={[pure, ipinfo, dbip]} />);
    const mine = group(false), specified = group(true);
    selectTab(mine, '纯真CZ88');
    expect(within(specified).getByRole('tab', { name: 'JSON' }).getAttribute('aria-selected')).toBe('true');
    selectTab(specified, 'IPinfo');
    expect(within(mine).getByRole('tab', { name: '纯真CZ88' }).getAttribute('aria-selected')).toBe('true');
    rerender(<ApiExamples providers={[dbip, ipinfo, pure]} />);
    expect(tabNames(mine)).toEqual(['我的 IP', 'JSON', 'XML', 'TXT', 'DB-IP', 'IPinfo', '纯真CZ88', 'ALL']);
    expect(tabNames(specified)).toEqual(['JSON', 'XML', 'TXT', 'DB-IP', 'IPinfo', '纯真CZ88', 'ALL']);
    expect(within(mine).getByRole('tab', { name: '纯真CZ88' }).getAttribute('aria-selected')).toBe('true');
    expect(within(specified).getByRole('tab', { name: 'IPinfo' }).getAttribute('aria-selected')).toBe('true');
    rerender(<ApiExamples providers={[dbip, ipinfo, { ...pure, enabled: false }]} />);
    await waitFor(() => expect(within(mine).getByRole('tab', { name: '我的 IP' }).getAttribute('aria-selected')).toBe('true'));
    expect(within(specified).getByRole('tab', { name: 'IPinfo' }).getAttribute('aria-selected')).toBe('true');
    rerender(<ApiExamples providers={[dbip, { ...ipinfo, enabled: false }, { ...pure, enabled: false }]} />);
    await waitFor(() => expect(within(specified).getByRole('tab', { name: 'JSON' }).getAttribute('aria-selected')).toBe('true'));
    for (const region of [mine, specified]) {
      expect(within(region).queryByRole('tab', { name: '纯真CZ88' })).toBeNull();
      expect(within(region).queryByRole('tab', { name: 'IPinfo' })).toBeNull();
    }
    rerender(<ApiExamples providers={[pure, dbip, ipinfo]} />);
    expect(within(mine).getByRole('tab', { name: '我的 IP' }).getAttribute('aria-selected')).toBe('true');
    expect(within(specified).getByRole('tab', { name: 'JSON' }).getAttribute('aria-selected')).toBe('true');
    expect(within(mine).getByRole('tab', { name: '纯真CZ88' }).getAttribute('aria-selected')).toBe('false');
    expect(within(specified).getByRole('tab', { name: 'IPinfo' }).getAttribute('aria-selected')).toBe('false');
    await expectCommand(mine, 'curl https://ip.tongyaninfo.com');
    await expectCommand(specified, command(true, 'dbip'));
  });

  it('复制使用各组当前命令，ALL与TXT不混用格式，也不写入浏览器识别地址', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', new Proxy(navigator, { get(target, property) {
      return property === 'clipboard' ? { writeText } : Reflect.get(target, property, target);
    } }));
    render(<ApiExamples providers={[provider('CZ88')]} />);
    const mine = group(false), specified = group(true);
    const selections: [HTMLElement, string, string][] = [
      [mine, 'ALL', command(false, 'all')], [specified, 'TXT', command(true, 'all', 'txt')],
      [mine, '纯真CZ88', command(false, 'CZ88')], [specified, 'ALL', command(true, 'all')],
      [mine, '我的 IP', 'curl https://ip.tongyaninfo.com'],
    ];
    for (const [index, [region, label, expected]] of selections.entries()) {
      selectTab(region, label);
      await expectCommand(region, expected);
      fireEvent.click(within(activePanel(region)).getByRole('button', { name: /^(复制|已复制)$/ }));
      await waitFor(() => expect(writeText).toHaveBeenNthCalledWith(index + 1, expected));
    }
    expect(writeText).toHaveBeenCalledTimes(selections.length);
  });

  it('固定标签与同名来源ID使用独立key，两组命令各自保留正确db', async () => {
    render(<ApiExamples providers={[provider('json', { name: '自定义JSON库' })]} />);
    for (const specified of [false, true]) {
      const region = group(specified);
      selectTab(region, 'JSON');
      await expectCommand(region, command(specified, 'dbip'));
      selectTab(region, '自定义JSON库');
      await expectCommand(region, command(specified, 'json'));
    }
  });

  it('没有启用来源时仍提供两组基本格式与ALL，不虚构数据库来源', () => {
    render(<ApiExamples providers={[provider('dbip', { enabled: false })]} />);
    expect(tabNames(group(false))).toEqual(['我的 IP', 'JSON', 'XML', 'TXT', 'ALL']);
    expect(tabNames(group(true))).toEqual(['JSON', 'XML', 'TXT', 'ALL']);
  });
});
