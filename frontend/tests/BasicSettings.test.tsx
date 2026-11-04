import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { SettingsPage } from '../src/pages/Console/SettingsPage';
import { ConsoleContext } from '../src/models/ConsoleContext';
import { apiResponse, settings } from './consoleFixtures';

/** 检查真实提交的 JSON，空字符串不能被 Number('') 误转成停用值0。 */
describe('基础运行设置', () => {
  it.each([['', null], ['0', 0], ['14', 14]] as const)('保存天数 %s 保留正确语义', async (input, expected) => {
    let stored = structuredClone(settings);
    let submitted: Record<string, unknown> | null = null;
    vi.stubGlobal('fetch', vi.fn((_path: string, options?: RequestInit) => {
      if (options?.method === 'POST') {
        submitted = JSON.parse(String(options.body));
        stored = { ...stored, basic: (submitted as unknown as typeof settings).basic,
          runtime: { ...stored.runtime, queueCapacityRequiresRestart: true } };
      }
      return Promise.resolve(apiResponse(stored));
    }));
    render(<ConsoleContext.Provider value={{ csrfToken: 'controlled-token', recheck: vi.fn() }}><SettingsPage /></ConsoleContext.Provider>);
    const retention = await screen.findByLabelText('访问日志保存天数');
    fireEvent.change(retention, { target: { value: input } });
    fireEvent.change(screen.getByLabelText('完整来源 IP'), { target: { value: 'false' } });
    fireEvent.change(screen.getByLabelText('管理员会话时长（分钟）'), { target: { value: '60' } });
    fireEvent.change(screen.getByLabelText('日志队列容量'), { target: { value: '20000' } });
    fireEvent.submit(screen.getByRole('form', { name: '系统设置表单' }));
    await waitFor(() => expect(submitted).not.toBeNull());
    expect(submitted!.basic).toEqual({ accessLogRetentionDays: expected, retainFullSourceIp: false, sessionMinutes: 60, queueCapacity: 20000 });
    expect(await screen.findByText('日志队列容量已保存，等待重启服务生效')).not.toBeNull();
    expect(screen.getByText('重启服务后生效；当前运行容量 10,000 条。')).not.toBeNull();
  });

  it('显式无限期值显示为空，输入约束不允许负数或小数', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.resolve(apiResponse({ ...settings,
      basic: { ...settings.basic!, accessLogRetentionDays: null } }))));
    render(<ConsoleContext.Provider value={{ csrfToken: 'controlled-token', recheck: vi.fn() }}><SettingsPage /></ConsoleContext.Provider>);
    const input = await screen.findByLabelText('访问日志保存天数') as HTMLInputElement;
    expect(input.value).toBe('');
    expect(input.min).toBe('0');
    expect(input.step).toBe('1');
    expect(input.required).toBe(false);
    expect(screen.getByText(/已有历史暂留/)).not.toBeNull();
  });
});
