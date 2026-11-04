import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { AccessLogsPage } from '../src/pages/Console/AccessLogsPage';
import type { AccessRecord } from '../src/types/console';
import { apiResponse, overview, queue } from './consoleFixtures';

function record(page = 1, overrides: Partial<AccessRecord> = {}): AccessRecord {
  return { id: `record-${page}`, sourceAddress: `203.0.113.${page}`, occurredAtUtc: overview.toUtc, routeCategory: 'query', method: 'GET', format: 'json', database: 'dbip', databaseVersion: null, statusCode: 200, elapsedMilliseconds: 1, isCancelled: false, clientCategory: 'browser', traceId: `trace-${page}`, ...overrides };
}
function response(page = 1, total = 41, items = [record(page)]) {
  return apiResponse({ items, total, page, pageSize: 20, fromUtc: overview.fromUtc, toUtc: overview.toUtc, retentionDays: 30, queue });
}
function installPages(total = 41) {
  const fetchMock = vi.fn((path: string) => Promise.resolve(response(Number(new URL(path, 'http://localhost').searchParams.get('page') || 1), total)));
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}
function arrow(key: string, options: KeyboardEventInit = {}, target: Document | Element = document) {
  const event = new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true, ...options });
  fireEvent(target, event);
  return event;
}
async function ready(page = 1) {
  return screen.findByText(`203.0.113.${page}`, { selector: 'code' });
}
async function details() {
  const source = await ready();
  fireEvent.click(within(source.closest('tr')!).getByRole('button', { name: /Expand row|展开行/ }));
  return screen.getByRole('region', { name: '请求路径详情' });
}

describe('访问记录完整请求路径', () => {
  it('原样展示路径、重复及空查询参数和百分号编码，HTML仅作文本', async () => {
    const requestPath = '/search/2001%3Adb8%3A%3A1?ip=2001%3Adb8%3A%3A1&db=all&db=&note=a+b%20c&html=<img src=x onerror="alert(1)">';
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(1, 1, [record(1, { requestPath })]))));
    render(<AccessLogsPage />);
    const detail = await details();
    expect(within(detail).getByText(requestPath, { exact: true }).textContent).toBe(requestPath);
    expect(detail.querySelector('a, img, script')).toBeNull();
    expect(within(detail).queryByRole('note')).toBeNull();
    expect(screen.getByRole('columnheader', { name: '请求路径' })).not.toBeNull();
  });

  it.each([undefined, null])('历史路径%s显示未采集，不推断或补造请求地址', async requestPath => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(1, 1, [record(1, { requestPath })]))));
    render(<AccessLogsPage />);
    const detail = await details();
    expect(within(detail).getByText('未采集请求路径')).not.toBeNull();
    expect(detail.querySelector('pre')).toBeNull();
    expect(within(detail).queryByRole('note')).toBeNull();
  });

  it('长路径详情保留后端实际内容并明确展示截断标记', async () => {
    const requestPath = `/?note=${'long%20value'.repeat(2200)}`;
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(response(1, 1, [record(1, { requestPath, requestPathTruncated: true })]))));
    render(<AccessLogsPage />);
    const detail = await details();
    expect(detail.querySelector('pre code')?.textContent).toBe(requestPath);
    expect(within(detail).getByRole('note').textContent).toBe('请求路径已截断，当前详情不完整。');
  });
});

describe('访问记录左右键分页', () => {
  it('左右键请求相邻页，首末页不越界且不吞掉边界按键', async () => {
    const fetchMock = installPages();
    render(<AccessLogsPage />); await ready();
    expect(arrow('ArrowLeft').defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(arrow('ArrowRight').defaultPrevented).toBe(true); await ready(2);
    expect(arrow('ArrowRight').defaultPrevented).toBe(true); await ready(3);
    expect(arrow('ArrowRight').defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(arrow('ArrowLeft').defaultPrevented).toBe(true); await ready(2);
    expect(fetchMock.mock.calls.map(([path]) => new URL(path, 'http://localhost').searchParams.get('page'))).toEqual(['1', '2', '3', '2']);
  });

  it('加载期间不重复翻页，保留筛选并在重新筛选后回到第一页', async () => {
    let finish: ((value: Response) => void) | undefined;
    const fetchMock = vi.fn((path: string) => {
      const parameters = new URL(path, 'http://localhost').searchParams;
      if (parameters.get('page') === '2') return new Promise<Response>(resolve => { finish = resolve; });
      return Promise.resolve(response());
    });
    vi.stubGlobal('fetch', fetchMock); render(<AccessLogsPage />); await ready();
    fireEvent.change(screen.getByLabelText('数据库'), { target: { value: 'dbip' } });
    fireEvent.submit(screen.getByRole('form', { name: '访问记录筛选' }));
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(2)); await ready();
    arrow('ArrowRight');
    await waitFor(() => expect(fetchMock).toHaveBeenCalledTimes(3));
    expect(arrow('ArrowRight').defaultPrevented).toBe(false);
    expect(arrow('ArrowLeft').defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    expect(new URL(fetchMock.mock.calls[2][0], 'http://localhost').searchParams.get('database')).toBe('dbip');
    await act(async () => finish!(response(2)));
    await ready(2);
    fireEvent.submit(screen.getByRole('form', { name: '访问记录筛选' }));
    await ready();
    expect(new URL(fetchMock.mock.calls.at(-1)![0], 'http://localhost').searchParams.get('page')).toBe('1');
  });

  it.each([0, 1])('总数%s时没有可翻页记录', async total => {
    const fetchMock = vi.fn(() => Promise.resolve(response(1, total, total ? [record()] : [])));
    vi.stubGlobal('fetch', fetchMock); render(<AccessLogsPage />);
    if (total) await ready(); else await screen.findByText('当前筛选范围内暂无记录');
    expect(arrow('ArrowLeft').defaultPrevented).toBe(false);
    expect(arrow('ArrowRight').defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('初始加载及加载失败时不翻页', async () => {
    let finish: ((value: Response) => void) | undefined;
    const fetchMock = vi.fn(() => new Promise<Response>(resolve => { finish = resolve; }));
    vi.stubGlobal('fetch', fetchMock); render(<AccessLogsPage />);
    expect(arrow('ArrowRight').defaultPrevented).toBe(false);
    await act(async () => finish!(apiResponse(null, 503, 'STORAGE_UNAVAILABLE', '受控存储失败')));
    await screen.findByText('受控存储失败');
    expect(arrow('ArrowRight').defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('输入焦点及交互控件保留方向键，普通页面区域仍可翻页', async () => {
    const fetchMock = installPages();
    const { container } = render(<><AccessLogsPage /><textarea aria-label="备注" /><select aria-label="选项"><option>one</option></select><div contentEditable suppressContentEditableWarning tabIndex={0}>editable</div>{['combobox', 'spinbutton', 'slider', 'listbox', 'menu', 'menubar', 'tablist', 'tree', 'grid', 'radiogroup', 'dialog', 'alertdialog'].map(role => <div role={role} tabIndex={0} key={role}>{role}</div>)}</>);
    await ready();
    const controls = container.querySelectorAll<HTMLElement>('input, textarea, select, button, [contenteditable], [role][tabindex="0"]');
    for (const control of controls) {
      control.focus();
      expect(arrow('ArrowRight', {}, control).defaultPrevented).toBe(false);
      // document 派发时也以当前焦点为准，不能只检查 event.target。
      if (document.activeElement === control) expect(arrow('ArrowRight').defaultPrevented).toBe(false);
      control.blur();
    }
    expect(fetchMock).toHaveBeenCalledTimes(1);
    arrow('ArrowRight'); await ready(2);
    expect(fetchMock).toHaveBeenCalledTimes(2);
  });

  it.each([{ altKey: true }, { ctrlKey: true }, { metaKey: true }, { shiftKey: true }, { repeat: true }, { isComposing: true }])('忽略修饰键、重复和输入法事件 %o', async options => {
    const fetchMock = installPages(); render(<AccessLogsPage />); await ready();
    expect(arrow('ArrowRight', options).defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('不抢占已处理事件，卸载后清理按键监听且不再发起分页', async () => {
    const add = vi.spyOn(document, 'addEventListener');
    const remove = vi.spyOn(document, 'removeEventListener');
    const fetchMock = installPages(); const { unmount } = render(<AccessLogsPage />); await ready();
    const handled = new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true });
    handled.preventDefault(); fireEvent(document, handled);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    const listeners = add.mock.calls.filter(([name]) => name === 'keydown').map(([, listener]) => listener);
    unmount();
    expect(listeners.length).toBeGreaterThan(0);
    for (const listener of listeners) expect(remove.mock.calls.some(([name, removed]) => name === 'keydown' && removed === listener)).toBe(true);
    expect(arrow('ArrowRight').defaultPrevented).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    add.mockRestore(); remove.mockRestore();
  });
});
