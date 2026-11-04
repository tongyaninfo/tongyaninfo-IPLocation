import { act, fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { McpLogsPage } from '../src/pages/Console/McpLogsPage';
import ConsolePage from '../src/pages/Console/console';
import { ConsoleContext } from '../src/models/ConsoleContext';
import type { AccessRecord, McpCallContext } from '../src/types/console';
import { apiResponse, overview, queue } from './consoleFixtures';
import { consoleReturnPath } from '../src/utils/consoleNavigation';

beforeEach(() => window.history.replaceState(null, '', '/console/mcp-logs'));
const context: McpCallContext = { protocolVersion: '2025-06-18', method: 'tools/call', requestId: '2', toolName: 'query_ip', clientName: 'Controlled Client', clientVersion: '1.0', ip: '1.1.1.1', database: 'all', resultCode: 'OK', resultStatus: 'partial', isError: false, sources: [{ id: 'dbip', version: 'controlled-version', status: 'success' }], truncated: false };
function record(mcpContext: McpCallContext | null = context, id = 'mcp-record'): AccessRecord {
  return { id, occurredAtUtc: overview.toUtc, sourceAddress: '203.0.113.8', routeCategory: 'mcp', method: 'POST', format: 'json', database: 'all', databaseVersion: null, statusCode: 200, elapsedMilliseconds: 1.5, isCancelled: false, clientCategory: 'mcp', traceId: `trace-${id}`, mcpContext };
}
function page(items: AccessRecord[], options = {}) { return { items, total: items.length, page: 1, pageSize: 20, fromUtc: overview.fromUtc, toUtc: overview.toUtc, retentionDays: 30, queue, ...options }; }
async function expand() {
  const source = await screen.findByText('203.0.113.8', { selector: 'code' });
  fireEvent.click(within(source.closest('tr')!).getByRole('button', { name: /Expand row|展开行/ }));
  return screen.getByRole('region', { name: 'MCP 调用上下文' });
}

describe('MCP 调用审计页面', () => {
  it('展示协议和工具上下文，客户端自报与HTTP业务状态独立，危险内容只作文本', async () => {
    const dangerous = '<img src=x onerror="window.contextExecuted=true">';
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse(page([record({ ...context, requestId: dangerous, isError: true, resultCode: 'INVALID_IP', resultStatus: null, truncated: true })])))));
    render(<McpLogsPage />);
    const details = await expand();
    expect(within(details).getByText('2025-06-18')).not.toBeNull();
    expect(within(details).getByText('Controlled Client')).not.toBeNull();
    expect(within(details).getByText('1.1.1.1')).not.toBeNull();
    expect(within(details).getByText(dangerous)).not.toBeNull();
    expect(details.querySelector('img,script,a')).toBeNull();
    expect(within(details).getByText(/匿名客户端信息由调用方提供/)).not.toBeNull();
    expect(within(details).getByRole('note').textContent).toBe('调用上下文已清洗或截断，以下展示实际保存的内容。');
    expect(within(details).getByText(/controlled-version/)).not.toBeNull();
    expect(screen.getByText('200')).not.toBeNull();
    expect(screen.getByText(/MCP 日志沿用普通访问日志/)).not.toBeNull();
  });

  it('旧上下文为空时显示未采集，停止新增仍显示历史和采集缺口', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse(page([record(null)], { retentionDays: 0, queue: { ...queue, dropped: 3 } })))));
    render(<McpLogsPage />);
    expect(within(await expand()).getByText('未采集 MCP 调用上下文')).not.toBeNull();
    expect(screen.getByText(/已停止新增，历史暂留/)).not.toBeNull();
    expect(screen.getByText('采集存在缺口或故障')).not.toBeNull();
  });

  it('调用详情接收后端入参出参及采集标记，展开后按真实JSON显示', async () => {
    const output = { success: true, code: 'OK', message: '查询完成', data: { ip: '1.1.1.1' } };
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse(page([record({ ...context, input: null, inputCaptured: true, output, outputCaptured: true, outputTruncated: true })])))));
    render(<McpLogsPage />); await expand();
    expect(screen.getByRole('region', { name: 'MCP 入参详情' }).querySelector('pre')?.textContent).toBe('null');
    const result = screen.getByRole('region', { name: 'MCP 出参详情' });
    expect(result.querySelector('pre')?.textContent).toBe(JSON.stringify(output, null, 2));
    expect(within(result).getByRole('button', { name: '复制已记录出参' })).not.toBeNull();
    expect(within(result).getByRole('note').textContent).toContain('出参已截断');
  });

  it('筛选参数准确且重置页码，旧的慢响应不能覆盖新的筛选', async () => {
    let finishOld: ((response: Response) => void) | undefined;
    const fetchMock = vi.fn((path: string) => {
      const params = new URL(path, 'http://localhost').searchParams;
      if (params.get('toolName') === 'query_my_ip') return new Promise<Response>(resolve => { finishOld = resolve; });
      return Promise.resolve(apiResponse(page(params.has('toolName') ? [record({ ...context, clientName: 'New Client' })] : [], { total: 21 })));
    });
    vi.stubGlobal('fetch', fetchMock); render(<McpLogsPage />);
    await screen.findByText('当前筛选范围暂无 MCP 调用');
    fireEvent.click(screen.getByTitle('2'));
    await waitFor(() => expect(fetchMock.mock.calls).toHaveLength(2));
    fireEvent.change(screen.getByLabelText('工具名称'), { target: { value: 'query_my_ip' } });
    fireEvent.submit(screen.getByRole('form', { name: 'MCP 调用审计筛选' }));
    await waitFor(() => expect(finishOld).toBeDefined());
    fireEvent.change(screen.getByLabelText('工具名称'), { target: { value: 'query_ip' } });
    fireEvent.change(screen.getByLabelText('来源 IP'), { target: { value: '203.0.113.8' } });
    fireEvent.change(screen.getByLabelText('业务结果码'), { target: { value: 'OK' } });
    fireEvent.change(screen.getByLabelText('Trace ID'), { target: { value: 'trace-id' } });
    fireEvent.submit(screen.getByRole('form', { name: 'MCP 调用审计筛选' }));
    expect(await screen.findByText('New Client')).not.toBeNull();
    const last = new URL(fetchMock.mock.calls.at(-1)![0], 'http://localhost');
    expect(last.pathname).toBe('/api/console/mcp-logs');
    expect(Object.fromEntries(last.searchParams)).toMatchObject({ page: '1', toolName: 'query_ip', sourceAddress: '203.0.113.8', resultCode: 'OK', traceId: 'trace-id' });
    await act(async () => { finishOld!(apiResponse(page([record({ ...context, clientName: 'Old Client' })]))); });
    expect(screen.queryByText('Old Client')).toBeNull();
    expect(screen.getByText('New Client')).not.toBeNull();
  });

  it('存储失败可重试，身份过期触发重新核验', async () => {
    const recheck = vi.fn(async () => {});
    let status = 503;
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse(null, status, status === 503 ? 'STORAGE_UNAVAILABLE' : 'CONSOLE_UNAUTHENTICATED', 'MCP存储读取失败'))));
    render(<ConsoleContext.Provider value={{ csrfToken: '', recheck }}><McpLogsPage /></ConsoleContext.Provider>);
    await screen.findByText('MCP存储读取失败');
    expect(recheck).not.toHaveBeenCalled();
    status = 401; fireEvent.click(screen.getByRole('button', { name: /重\s*试/ }));
    await waitFor(() => expect(recheck).toHaveBeenCalledOnce());
  });

  it('新管理路径受登录门禁保护，未登录不读取MCP审计接口且回址保留', async () => {
    const fetchMock = vi.fn((path: string) => Promise.resolve(path.endsWith('/csrf') ? apiResponse({ token: 'controlled-csrf' }) : apiResponse({ allowed: true, initialized: true, authenticated: false, user: null }, 401, 'CONSOLE_UNAUTHENTICATED')));
    vi.stubGlobal('fetch', fetchMock); render(<ConsolePage />);
    await screen.findByRole('form', { name: '管理员登录表单' });
    expect(window.location.pathname).toBe('/console/login');
    expect(new URLSearchParams(window.location.search).get('returnTo')).toBe('/console/mcp-logs');
    expect(fetchMock.mock.calls.map(([path]) => path)).toEqual(['/api/console/access', '/api/console/auth/csrf']);
    expect(consoleReturnPath('/console/mcp-logs')).toBe('/console/mcp-logs');
  });
});
