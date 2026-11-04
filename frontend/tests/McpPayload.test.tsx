import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { McpPayload } from '../src/pages/Console/components/McpPayload';

describe('MCP入参和出参快照', () => {
  it('已采集的JSON null明确显示且可复制，旧记录空值仍为未采集', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', new Proxy(navigator, { get(target, property) { return property === 'clipboard' ? { writeText } : Reflect.get(target, property, target); } }));
    render(<><McpPayload label="入参" value={null} captured /><McpPayload label="出参" value={null} /></>);
    const input = screen.getByRole('region', { name: 'MCP 入参详情' }), output = screen.getByRole('region', { name: 'MCP 出参详情' });
    expect(input.querySelector('pre')?.textContent).toBe('null');
    expect(within(output).getByText('未采集出参，历史记录不会回填。')).not.toBeNull();
    expect(within(output).queryByRole('button')).toBeNull();
    fireEvent.click(within(input).getByRole('button', { name: '复制已记录入参' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith('null'));
  });

  it('格式化并复制实际保存的合法JSON，危险文本不解析成HTML，截断与敏感替换说明保留', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', new Proxy(navigator, { get(target, property) { return property === 'clipboard' ? { writeText } : Reflect.get(target, property, target); } }));
    const value = { success: true, token: '[已脱敏]', data: { note: '<img src=x onerror="window.unsafe=true">', longText: 'x'.repeat(16000) } };
    render(<McpPayload label="出参" value={value} truncated />);
    const details = screen.getByRole('region', { name: 'MCP 出参详情' });
    const expected = JSON.stringify(value, null, 2);
    expect(details.querySelector('pre')?.textContent).toBe(expected);
    expect(details.querySelector('img,script,a')).toBeNull();
    expect(within(details).getByRole('note').textContent).toContain('不是完整原始出参');
    expect(within(details).getByText(/敏感字段以替换标记显示/)).not.toBeNull();
    fireEvent.click(within(details).getByRole('button', { name: '复制已记录出参' }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith(expected));
  });

  it.each([0, false, '', []])('合法的%s不误判为未采集', value => {
    render(<McpPayload label="入参" value={value} />);
    expect(screen.getByRole('region', { name: 'MCP 入参详情' }).querySelector('pre')?.textContent).toBe(JSON.stringify(value, null, 2));
    expect(screen.queryByText(/未采集入参/)).toBeNull();
  });
});
