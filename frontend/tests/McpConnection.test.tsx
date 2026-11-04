import { act, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { McpConnection } from '../src/components/McpConnection';
import { DeferredApiExamples } from '../src/components/DeferredApiExamples';

beforeEach(() => window.history.replaceState(null, '', '/'));

describe('公开 MCP 接入说明', () => {
  it('右上角复制完整智能体接入说明，悬浮提示与手动配置引导明确', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', new Proxy(navigator, { get(target, property) { return property === 'clipboard' ? { writeText } : Reflect.get(target, property, target); } }));
    render(<McpConnection />);
    const button = screen.getByRole('button', { name: '复制接入说明' });
    fireEvent.mouseOver(button);
    expect(await screen.findByRole('tooltip', { name: '复制这段内容到您的智能体中添加 MCP 服务' })).not.toBeNull();
    fireEvent.click(button);
    await waitFor(() => expect(writeText).toHaveBeenCalledOnce());
    const text = writeText.mock.calls[0][0];
    for (const fragment of ['请为我的智能体添加', '服务名称：tongyan-ip', 'Streamable HTTP', 'https://ip.tongyaninfo.com/mcp', '无需登录、API Key 或 Token', '"mcpServers"', '"type": "http"', 'query_ip', 'query_my_ip', 'list_providers']) expect(text).toContain(fragment);
    expect(screen.getByText(/无法自动配置时，可手动复制 JSON 到客户端设置/)).not.toBeNull();
    expect(screen.getByRole('button', { name: '复制地址' })).not.toBeNull();
    expect(screen.getByRole('button', { name: '复制配置' })).not.toBeNull();
    expect(screen.getByRole('button', { name: '复制调用示例' })).not.toBeNull();
  });
  it('配置和工具调用可以复制，公开策略、默认来源及出口IP语义明确，不主动请求MCP', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    const fetchMock = vi.fn();
    vi.stubGlobal('fetch', fetchMock);
    vi.stubGlobal('navigator', new Proxy(navigator, { get(target, property) { return property === 'clipboard' ? { writeText } : Reflect.get(target, property, target); } }));
    render(<McpConnection />);
    expect(screen.getByText('Streamable HTTP')).not.toBeNull();
    expect(screen.getByText(/无需登录、API Key 或 Token/)).not.toBeNull();
    expect(screen.getByText(/客户端运行在云端时/)).not.toBeNull();
    for (const tool of ['query_ip', 'query_my_ip', 'list_providers']) expect(screen.getByText(tool)).not.toBeNull();
    fireEvent.click(screen.getByRole('button', { name: '复制地址' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('https://ip.tongyaninfo.com/mcp'));
    fireEvent.click(screen.getByRole('button', { name: '复制配置' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(2));
    expect(JSON.parse(writeText.mock.calls[1][0]).mcpServers['tongyan-ip']).toEqual({ type: 'http', url: 'https://ip.tongyaninfo.com/mcp' });
    fireEvent.click(screen.getByRole('button', { name: '复制调用示例' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledTimes(3));
    expect(JSON.parse(writeText.mock.calls[2][0])).toMatchObject({ jsonrpc: '2.0', method: 'tools/call', params: { name: 'query_ip', arguments: { ip: '1.1.1.1', db: 'all' } } });
    expect(fetchMock).not.toHaveBeenCalled();
  });

  it('懒加载前MCP锚点保持存在，直接点击锚点即可加载接入内容', async () => {
    class InactiveObserver { observe() {} disconnect() {} }
    vi.stubGlobal('IntersectionObserver', InactiveObserver);
    render(<DeferredApiExamples providers={[]} />);
    expect(document.getElementById('mcp')).not.toBeNull();
    expect(screen.queryByRole('region', { name: 'MCP 连接配置' })).toBeNull();
    act(() => { window.location.hash = '#mcp'; window.dispatchEvent(new HashChangeEvent('hashchange')); });
    expect(await screen.findByRole('region', { name: 'MCP 连接配置' })).not.toBeNull();
    expect(document.querySelectorAll('#mcp')).toHaveLength(1);
  });

  it('直接打开锚点等查询稳定后定位一次，后续手动查询不抢回滚动位置', async () => {
    window.history.replaceState(null, '', '/#mcp');
    const scroll = vi.fn();
    const originalScroll = HTMLElement.prototype.scrollIntoView;
    HTMLElement.prototype.scrollIntoView = scroll;
    try {
      const { rerender } = render(<DeferredApiExamples providers={[]} queryReady={false} />);
      await screen.findByRole('region', { name: 'MCP 连接配置' });
      expect(scroll).not.toHaveBeenCalled();
      rerender(<DeferredApiExamples providers={[]} queryReady />);
      await waitFor(() => expect(scroll).toHaveBeenCalledOnce());
      rerender(<DeferredApiExamples providers={[]} queryReady={false} />);
      rerender(<DeferredApiExamples providers={[]} queryReady />);
      await act(async () => { await new Promise(resolve => setTimeout(resolve, 30)); });
      expect(scroll).toHaveBeenCalledOnce();
    } finally { HTMLElement.prototype.scrollIntoView = originalScroll; }
  });
});
