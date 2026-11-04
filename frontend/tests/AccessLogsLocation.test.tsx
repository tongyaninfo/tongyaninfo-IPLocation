import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AccessLogsPage } from '../src/pages/Console/AccessLogsPage';
import type { AccessRecord, SourceLocationSnapshot } from '../src/types/console';
import { apiResponse, overview, queue } from './consoleFixtures';

const location: SourceLocationSnapshot = { provider: 'CZ88', version: '2026-10-28', status: 'success', country: '中国', province: '安徽', city: '蚌埠', isp: '示例组织–网络', rawLocation: '中国–安徽–蚌埠' };
function mockRecord(sourceLocation?: SourceLocationSnapshot | null) {
  const row: AccessRecord = { id: 'location-record', occurredAtUtc: overview.toUtc, sourceAddress: '203.0.113.89', routeCategory: 'query', method: 'GET', format: 'json', database: 'dbip', databaseVersion: 'controlled-target-database', statusCode: 200, elapsedMilliseconds: 1, isCancelled: false, clientCategory: 'browser', traceId: 'controlled-location-trace', sourceLocation };
  const fetchMock = vi.fn(() => Promise.resolve(apiResponse({ items: [row], total: 1, page: 1, pageSize: 20, fromUtc: overview.fromUtc, toUtc: overview.toUtc, retentionDays: 30, queue })));
  vi.stubGlobal('fetch', fetchMock); return fetchMock;
}
async function rowAndDetails() {
  const source = await screen.findByText('203.0.113.89', { selector: 'code' });
  const row = source.closest('tr')!;
  fireEvent.click(within(row).getByRole('button', { name: /Expand row|展开行/ }));
  return { row, details: screen.getByRole('region', { name: '来源地址归属地详情' }) };
}

describe('访问记录来源地址定位', () => {
  it('摘要和详情展示后端来源快照，不在前端发起定位查询且保留网络描述', async () => {
    const fetchMock = mockRecord(location); render(<AccessLogsPage />);
    const { row, details } = await rowAndDetails();
    expect(within(row).getByText('中国 · 安徽 · 蚌埠')).not.toBeNull();
    expect(within(row).getByText('网络描述：示例组织–网络')).not.toBeNull();
    expect(within(details).getByText('已解析')).not.toBeNull();
    expect(within(details).getByText('中国')).not.toBeNull();
    expect(within(details).getByText('安徽')).not.toBeNull();
    expect(within(details).getByText('蚌埠')).not.toBeNull();
    expect(within(details).getByText('网络描述')).not.toBeNull();
    expect(within(details).getByText('示例组织–网络')).not.toBeNull();
    expect(within(details).getByText('中国 · 安徽 · 蚌埠')).not.toBeNull();
    expect(within(details).getByText('数据日期：2026-10-28')).not.toBeNull();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it.each([undefined, null])('历史sourceLocation=%s显示未采集，不当成无匹配或零值', async snapshot => {
    mockRecord(snapshot); render(<AccessLogsPage />);
    const { row, details } = await rowAndDetails();
    expect(within(row).getByText('未采集')).not.toBeNull();
    expect(within(details).getByText('未采集来源地址归属地')).not.toBeNull();
    expect(within(details).queryByText('无匹配')).toBeNull();
    expect(within(details).queryByText('中国')).toBeNull();
  });

  it.each([
    ['special', '特殊用途地址'], ['unsupported', '不支持此地址'], ['unavailable', '解析失败'], ['notFound', '无匹配'],
  ])('来源定位%s独立展示为%s，不从HTTP成功状态推断定位成功', async (status, text) => {
    mockRecord({ ...location, status, country: null, province: null, city: null, isp: null, rawLocation: null });
    render(<AccessLogsPage />);
    const { row, details } = await rowAndDetails();
    expect(within(row).getByText('200')).not.toBeNull();
    expect(within(row).getByText(text)).not.toBeNull();
    expect(within(details).getByText(text)).not.toBeNull();
    expect(within(details).queryByText('中国')).toBeNull();
    expect(within(details).queryByText('已解析')).toBeNull();
  });

  it('仅有来源地区文本时用于摘要展示，不在前端编造省市字段', async () => {
    mockRecord({ ...location, country: null, province: null, city: null, isp: null, rawLocation: '示例地点–组织园区' });
    render(<AccessLogsPage />);
    const { row, details } = await rowAndDetails();
    expect(within(row).getByText('示例地点 · 组织园区')).not.toBeNull();
    expect(within(details).getByText('省 / 州').nextElementSibling?.textContent).toBe('未提供');
    expect(within(details).getByText('城市').nextElementSibling?.textContent).toBe('未提供');
  });
});
