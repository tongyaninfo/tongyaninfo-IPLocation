import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ProviderCard } from '../src/components/ProviderCard';
import type { ProviderDescriptor, ProviderResult } from '../src/types/ip-location';

const provider: ProviderDescriptor = {
  id: 'dbip', name: 'DB-IP City Lite', status: 'ready', enabled: true, ready: true,
  supportsIpv4: true, supportsIpv6: true, licenseConfirmed: false,
  version: '2026-11-03', message: null, attribution: null, licenseUrl: null,
};
const result: ProviderResult = {
  id: provider.id, name: provider.name, status: 'success', version: provider.version,
  elapsedMs: 1, message: null, attribution: null, licenseUrl: null,
  fields: { countryCode: 'CN', country: '中国', region: '四川', city: '成都',
    isp: '示例\n  网络', asn: 0, asName: '示例组织', asDomain: 'example.test',
    continent: '亚洲', continentCode: 'AS', latitude: 0, longitude: 104,
    postalCode: '610000', timeZone: 'Asia/Shanghai' },
};

function clipboard() {
  const writeText = vi.fn().mockResolvedValue(undefined);
  // 不操作宿主剪贴板；其余 Navigator 属性保留原接收者，避免影响UI库能力检测。
  vi.stubGlobal('navigator', new Proxy(navigator, { get(target, key) {
    return key === 'clipboard' ? { writeText } : Reflect.get(target, key, target);
  } }));
  return writeText;
}

describe('数据库卡片复制', () => {
  it('整卡按可见行和中文标签换行输出，保留零值、不混入隐藏接口字段', async () => {
    const writeText = clipboard();
    render(<ProviderCard provider={provider} result={result} loading={false} />);
    fireEvent.click(screen.getByRole('button', { name: '复制此数据源全部字段' }));
    const expected = [
      '数据源：DB-IP City Lite', '状态：查询成功', '地址族：IPv4 / IPv6',
      '地理位置：中国 · 四川 · 成都', '运营商：示例 网络', 'ASN：0', 'AS 组织：示例组织',
      'AS 域名：example.test', '大洲：亚洲 · AS', '近似坐标：0, 104',
      '邮编：610000', '时区 / 时差：Asia/Shanghai', '版本：2026-11-03',
      '来源：https://ip.tongyaninfo.com',
    ].join('\n');
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expected));
    expect(expected.split('\n')).toHaveLength(14);
    expect(expected).not.toMatch(/countryCode|elapsedMs|\{"/);
  });

  it.each([
    ['2026-11-03', '版本 2026-11-03'],
    ['2026-11-03T00:00:00Z', '文件构建时间 2026-11-03 08:00:00'],
    ['sha256:example-version', '内容标识 sha256:example-version'],
  ])('位置和查询值保留独立复制，版本 %s 仅展示', async (version, versionText) => {
    const writeText = clipboard();
    render(<ProviderCard provider={provider} result={{ ...result, version }} loading={false} />);
    expect(screen.getByText(versionText, { exact: true })).not.toBeNull();
    expect(screen.queryByRole('button', { name: '复制版本' })).toBeNull();
    const selections = [
      ['复制地理位置', '中国 · 四川 · 成都'], ['复制运营商', '示例 网络'], ['复制ASN', '0'],
      ['复制AS 组织', '示例组织'], ['复制AS 域名', 'example.test'], ['复制大洲', '亚洲 · AS'],
      ['复制近似坐标', '0, 104'], ['复制邮编', '610000'],
      ['复制时区 / 时差', 'Asia/Shanghai'],
    ];
    for (const [index, [label, value]] of selections.entries()) {
      fireEvent.click(screen.getByRole('button', { name: label }));
      await waitFor(() => expect(writeText).toHaveBeenNthCalledWith(index + 1, value));
    }
    expect(writeText).toHaveBeenCalledTimes(selections.length);
  });

  it('纯真使用网络描述和页面地区分隔符，缺失值仍明确保留', async () => {
    const writeText = clipboard();
    const pure = { ...provider, id: 'CZ88', name: '纯真 社区版', supportsIpv6: false };
    render(<ProviderCard provider={pure} loading={false} result={{ ...result, id: pure.id, name: pure.name,
      fields: { region: '中国–四川–成都', isp: null } }} />);
    expect(screen.queryByText('运营商', { exact: true })).toBeNull();
    expect(screen.getByText('数据日期 2026-11-03', { exact: true })).not.toBeNull();
    expect(screen.queryByRole('button', { name: '复制版本' })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '复制网络描述' }));
    await waitFor(() => expect(writeText).toHaveBeenLastCalledWith('未提供'));
    fireEvent.click(screen.getByRole('button', { name: '复制此数据源全部字段' }));
    await waitFor(() => expect(writeText).toHaveBeenLastCalledWith([
      '数据源：纯真 社区版', '状态：查询成功', '地址族：IPv4',
      '地理位置：中国 · 四川 · 成都', '网络描述：未提供', 'ASN：未提供',
      'AS 组织：未提供', '数据日期：2026-11-03',
      '来源：https://ip.tongyaninfo.com',
    ].join('\n')));
  });

  it('无结果复制状态与来源说明，不补造查询字段', async () => {
    const writeText = clipboard();
    const missing = { ...provider, ready: false, status: 'notConfigured', version: null, message: '数据文件尚未配置。' };
    const { rerender } = render(<ProviderCard provider={missing} result={{ ...result, fields: {}, status: 'unsupported',
      version: null, message: '此来源不支持当前地址族。' }} loading={false} />);
    const copyAll = screen.getByRole('button', { name: '复制此数据源全部字段' });
    fireEvent.click(copyAll);
    await waitFor(() => expect(writeText).toHaveBeenCalledWith([
      '数据源：DB-IP City Lite', '状态：未配置', '地址族：IPv4 / IPv6',
      '状态说明：此来源不支持当前地址族。', '来源说明：数据文件尚未配置。', '版本：未提供',
      '来源：https://ip.tongyaninfo.com',
    ].join('\n')));
    fireEvent.click(screen.getByRole('button', { name: '复制状态说明' }));
    await waitFor(() => expect(writeText).toHaveBeenLastCalledWith('此来源不支持当前地址族。'));
    // 无匹配和特殊地址沿用相同整卡文本组装，不因没有定位字段而漏掉底部来源。
    for (const [status, message] of [['notFound', '未找到匹配记录。'], ['unsupported', '特殊用途地址不执行公网定位。'], ['notReady', '数据源尚未就绪。']]) {
      rerender(<ProviderCard provider={provider} result={{ ...result, fields: {}, status, message }} loading={false} />);
      fireEvent.click(copyAll);
      await waitFor(() => expect(writeText).toHaveBeenLastCalledWith(expect.stringContaining(`状态说明：${message}\n版本：2026-11-03\n来源：https://ip.tongyaninfo.com`)));
    }
  });

  it('查询中禁用所有复制入口，完成后复制当前结果而非旧值', async () => {
    const writeText = clipboard();
    const { rerender } = render(<ProviderCard provider={provider} result={result} loading />);
    const card = screen.getByRole('article');
    for (const button of within(card).getAllByRole('button')) expect((button as HTMLButtonElement).disabled).toBe(true);
    fireEvent.click(within(card).getByRole('button', { name: '复制此数据源全部字段' }));
    expect(writeText).not.toHaveBeenCalled();
    rerender(<ProviderCard provider={provider} result={{ ...result, fields: { isp: '新的网络结果' } }} loading={false} />);
    fireEvent.click(within(card).getByRole('button', { name: '复制运营商' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('新的网络结果'));
  });

  it('剪贴板拒绝时显示失败反馈，仍保留可手动选择的字段文本', async () => {
    const writeText = clipboard();
    writeText.mockRejectedValue(new Error('Clipboard denied'));
    render(<ProviderCard provider={provider} result={result} loading={false} />);
    fireEvent.click(screen.getByRole('button', { name: '复制AS 组织' }));
    expect(await screen.findByRole('button', { name: '请手动复制' })).not.toBeNull();
    expect(screen.getByText('示例组织', { exact: true })).not.toBeNull();
  });
});
