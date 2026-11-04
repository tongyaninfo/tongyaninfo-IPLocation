import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AccessLogsPage } from '../src/pages/Console/AccessLogsPage';
import type { AccessRecord, RequestHeadersSnapshot } from '../src/types/console';
import { apiResponse, overview, queue } from './consoleFixtures';

function record(id: string, sourceAddress: string, requestHeaders?: RequestHeadersSnapshot | null): AccessRecord {
  return { id, sourceAddress, occurredAtUtc: overview.toUtc, routeCategory: 'query', method: 'GET', format: 'json', database: 'dbip', databaseVersion: 'controlled-version', statusCode: 200, elapsedMilliseconds: 1, isCancelled: false, clientCategory: 'browser', traceId: `trace-${id}`, requestHeaders };
}
function page(items: AccessRecord[], currentPage = 1, total = items.length) {
  return { items, total, page: currentPage, pageSize: 20, fromUtc: overview.fromUtc, toUtc: overview.toUtc, retentionDays: 30, queue };
}
async function expandRecord(address: string) {
  const source = await screen.findByText(address, { selector: 'code' });
  fireEvent.click(within(source.closest('tr')!).getByRole('button', { name: /Expand row|展开行/ }));
  return screen.getByRole('region', { name: '请求头详情' });
}

describe('访问记录请求头详情', () => {
  it.each([undefined, null])('历史记录requestHeaders=%s时显示未采集，不假报为空请求头', async requestHeaders => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse(page([record('old', '203.0.113.1', requestHeaders)])))));
    render(<AccessLogsPage />);
    const details = await expandRecord('203.0.113.1');
    expect(within(details).getByText('未采集请求头')).not.toBeNull();
    expect(within(details).queryByText('未提供')).toBeNull();
    expect(within(details).queryByRole('note')).toBeNull();
  });

  it('User-Agent及Referer明确展示，多值保留换行，HTML和长行保持纯文本', async () => {
    const html = '<img src=x onerror="window.headerExecuted=true">';
    const longValue = 'x'.repeat(1500);
    const referer = 'https://example.test/landing';
    const snapshot: RequestHeadersSnapshot = { headers: { 'User-Agent': ['Controlled Browser/1.0', 'Secondary Client/2.0'], referer: [referer], 'x-note': [html, '<b>plain text</b>'], 'x-long': [longValue] }, truncated: true };
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse(page([record('headers', '203.0.113.2', snapshot)])))));
    render(<AccessLogsPage />);
    const details = await expandRecord('203.0.113.2');
    expect(within(details).getByText('User-Agent')).not.toBeNull();
    expect(within(details).getByText('Referer')).not.toBeNull();
    expect(within(details).getByText('Controlled Browser/1.0\nSecondary Client/2.0', { normalizer: value => value })).not.toBeNull();
    expect(within(details).getByText(`${html}\n<b>plain text</b>`, { normalizer: value => value })).not.toBeNull();
    expect(within(details).getByText(longValue).textContent).toBe(longValue);
    expect(within(details).getByText(referer).closest('a')).toBeNull();
    expect(details.querySelector('a, img, script, b')).toBeNull();
    expect(within(details).getByRole('note').textContent).toBe('请求头已截断，当前详情不完整。');
  });

  it('分页及重新筛选后展示对应记录的请求头，筛选从第一页开始', async () => {
    const fetchMock = vi.fn((path: string) => {
      const params = new URL(path, 'http://localhost').searchParams;
      const currentPage = Number(params.get('page') || 1);
      const address = params.get('sourceAddress') || (currentPage === 2 ? '203.0.113.2' : '203.0.113.1');
      const agent = params.has('sourceAddress') ? 'Filtered Browser' : `Page ${currentPage} Browser`;
      return Promise.resolve(apiResponse(page([record(address, address, { headers: { 'user-agent': [agent] }, truncated: false })], currentPage, 21)));
    });
    vi.stubGlobal('fetch', fetchMock); render(<AccessLogsPage />);
    let details = await expandRecord('203.0.113.1');
    expect(within(details).getByText('Page 1 Browser')).not.toBeNull();
    fireEvent.click(screen.getByTitle('2'));
    details = await expandRecord('203.0.113.2');
    expect(within(details).getByText('Page 2 Browser')).not.toBeNull();
    expect(screen.queryByText('Page 1 Browser')).toBeNull();
    fireEvent.change(screen.getByLabelText('来源 IP'), { target: { value: '203.0.113.9' } });
    fireEvent.submit(screen.getByRole('form', { name: '访问记录筛选' }));
    details = await expandRecord('203.0.113.9');
    expect(within(details).getByText('Filtered Browser')).not.toBeNull();
    expect(within(details).getByText('未采集到其他请求头')).not.toBeNull();
    expect(within(details).queryByRole('note')).toBeNull();
    const last = new URL(fetchMock.mock.calls.at(-1)![0], 'http://localhost').searchParams;
    expect(last.get('page')).toBe('1'); expect(last.get('sourceAddress')).toBe('203.0.113.9');
    expect(screen.queryByText('Page 2 Browser')).toBeNull();
  });
});
