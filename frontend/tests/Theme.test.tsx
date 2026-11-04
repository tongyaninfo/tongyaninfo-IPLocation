import { readFileSync } from 'node:fs';
import { act, fireEvent, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ThemeSwitcher } from '../src/components/ThemeSwitcher';
import { ThemeProvider, useTheme } from '../src/models/useTheme';

const storageKey = 'tongyan-theme-mode';
const systemQuery = '(prefers-color-scheme: dark)';

/** 使用真正的 change 回调模拟系统设置，不通过改写组件状态证明主题行为。 */
function systemTheme(initiallyDark = false) {
  const listeners = new Set<EventListenerOrEventListenerObject>();
  const media = {
    matches: initiallyDark,
    media: systemQuery,
    onchange: null,
    addEventListener: vi.fn((name: string, listener: EventListenerOrEventListenerObject) => {
      if (name === 'change') listeners.add(listener);
    }),
    removeEventListener: vi.fn((name: string, listener: EventListenerOrEventListenerObject) => {
      if (name === 'change') listeners.delete(listener);
    }),
    addListener: vi.fn(),
    removeListener: vi.fn(),
    dispatchEvent: vi.fn(),
  };
  vi.spyOn(window, 'matchMedia').mockImplementation(() => media as unknown as MediaQueryList);
  return {
    listeners,
    change(dark: boolean) {
      media.matches = dark;
      act(() => {
        const event = new Event('change');
        Object.defineProperties(event, { matches: { value: dark }, media: { value: systemQuery } });
        for (const listener of listeners) {
          if (typeof listener === 'function') listener(event);
          else listener.handleEvent(event);
        }
      });
    },
  };
}

function ThemeProbe() {
  const theme = useTheme()!;
  return <><ThemeSwitcher /><output aria-label="生效主题">{theme.mode}/{theme.resolvedTheme}</output></>;
}

function renderTheme() {
  return render(<ThemeProvider><ThemeProbe /></ThemeProvider>);
}

function expectTheme(mode: string, resolved: string) {
  expect(screen.getByLabelText('生效主题').textContent).toBe(`${mode}/${resolved}`);
  expect(document.documentElement.dataset.themeMode).toBe(mode);
  expect(document.documentElement.dataset.theme).toBe(resolved);
}

beforeEach(() => {
  window.localStorage.clear();
  window.sessionStorage.clear();
  delete document.documentElement.dataset.theme;
  delete document.documentElement.dataset.themeMode;
  let meta = document.querySelector('meta[name="theme-color"]');
  if (!meta) {
    meta = document.createElement('meta');
    meta.setAttribute('name', 'theme-color');
    document.head.appendChild(meta);
  }
  meta.setAttribute('content', '#ffffff');
});

afterEach(() => vi.restoreAllMocks());

describe('页面主题选择', () => {
  it.each([false, true])('首次访问默认自动，系统深色=%s', dark => {
    systemTheme(dark);
    renderTheme();
    expectTheme('auto', dark ? 'dark' : 'light');
    expect(screen.getByRole('group', { name: '页面主题，自动跟随系统' })).not.toBeNull();
    expect(screen.getByRole('button', { name: '切换为浅色模式' }).getAttribute('aria-pressed')).toBe('false');
    expect(screen.getByRole('button', { name: '切换为深色模式' }).getAttribute('aria-pressed')).toBe('false');
  });

  it('自动模式实时响应系统变化，并同步浏览器主题色', () => {
    const system = systemTheme();
    renderTheme();
    system.change(true);
    expectTheme('auto', 'dark');
    expect(document.querySelector('meta[name="theme-color"]')?.getAttribute('content')).toBe('#0d1626');
    system.change(false);
    expectTheme('auto', 'light');
    expect(document.querySelector('meta[name="theme-color"]')?.getAttribute('content')).toBe('#ffffff');
  });

  it('手动选择优先于系统设置，重新挂载后保留选择', () => {
    const system = systemTheme(true);
    const first = renderTheme();
    fireEvent.click(screen.getByRole('button', { name: '切换为浅色模式' }));
    expectTheme('light', 'light');
    expect(window.localStorage.getItem(storageKey)).toBe('light');
    system.change(false);
    system.change(true);
    expectTheme('light', 'light');
    expect(screen.getByRole('button', { name: '浅色模式已开启，点击恢复自动' }).getAttribute('aria-pressed')).toBe('true');
    first.unmount();
    renderTheme();
    expectTheme('light', 'light');
  });

  it('再次点击当前手动模式恢复自动，并继续响应系统变化', () => {
    const system = systemTheme();
    renderTheme();
    fireEvent.click(screen.getByRole('button', { name: '切换为深色模式' }));
    expectTheme('dark', 'dark');
    fireEvent.click(screen.getByRole('button', { name: '深色模式已开启，点击恢复自动' }));
    expectTheme('auto', 'light');
    expect(window.localStorage.getItem(storageKey)).toBe('auto');
    system.change(true);
    expectTheme('auto', 'dark');
  });

  it('同源其他标签页的选择与清空会同步，其他存储键不改变主题', () => {
    systemTheme();
    renderTheme();
    window.localStorage.setItem(storageKey, 'dark');
    act(() => window.dispatchEvent(new StorageEvent('storage', { key: storageKey, newValue: 'dark', storageArea: window.localStorage })));
    expectTheme('dark', 'dark');
    window.localStorage.setItem(storageKey, 'light');
    act(() => window.dispatchEvent(new StorageEvent('storage', { key: 'unrelated-preference', newValue: 'anything', storageArea: window.localStorage })));
    expectTheme('dark', 'dark');
    window.localStorage.clear();
    act(() => window.dispatchEvent(new StorageEvent('storage', { key: null, newValue: null, storageArea: window.localStorage })));
    expectTheme('auto', 'light');
  });

  it('非法持久化值回到自动，不影响当前系统主题', () => {
    systemTheme(true);
    window.localStorage.setItem(storageKey, 'unexpected-mode');
    renderTheme();
    expectTheme('auto', 'dark');
  });

  it('同名sessionStorage事件不覆盖当前localStorage主题', () => {
    systemTheme();
    renderTheme();
    window.sessionStorage.setItem(storageKey, 'dark');
    act(() => window.dispatchEvent(new StorageEvent('storage', { key: storageKey, newValue: 'dark', storageArea: window.sessionStorage })));
    expectTheme('auto', 'light');
  });

  it('浏览器拒绝读写存储时仍可切换当前页面，重载后恢复自动', () => {
    systemTheme(true);
    const read = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Storage disabled', 'SecurityError'); });
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new DOMException('Storage disabled', 'SecurityError'); });
    const first = renderTheme();
    expectTheme('auto', 'dark');
    expect(() => fireEvent.click(screen.getByRole('button', { name: '切换为浅色模式' }))).not.toThrow();
    expectTheme('light', 'light');
    first.unmount();
    renderTheme();
    expectTheme('auto', 'dark');
    read.mockRestore();
    write.mockRestore();
  });

  it('matchMedia不可用时安全降级为浅色，手动模式仍可使用', () => {
    vi.stubGlobal('matchMedia', undefined);
    renderTheme();
    expectTheme('auto', 'light');
    fireEvent.click(screen.getByRole('button', { name: '切换为深色模式' }));
    expectTheme('dark', 'dark');
  });

  it('卸载时释放系统与跨标签监听，后续系统事件不再更新页面', () => {
    const system = systemTheme();
    const added = vi.spyOn(window, 'addEventListener');
    const removed = vi.spyOn(window, 'removeEventListener');
    const mounted = renderTheme();
    expect(system.listeners.size).toBeGreaterThan(0);
    const storageListeners = added.mock.calls.filter(([name]) => name === 'storage').map(([, listener]) => listener);
    expect(storageListeners.length).toBeGreaterThan(0);
    mounted.unmount();
    expect(system.listeners.size).toBe(0);
    expect(storageListeners.every(listener => removed.mock.calls.some(([name, removedListener]) => name === 'storage' && removedListener === listener))).toBe(true);
    system.change(true);
    expect(document.documentElement.dataset.theme).toBe('light');
  });
});

describe('首屏主题恢复', () => {
  it.each([
    { stored: null, systemDark: false, expectedMode: 'auto', expectedTheme: 'light' },
    { stored: null, systemDark: true, expectedMode: 'auto', expectedTheme: 'dark' },
    { stored: 'light', systemDark: true, expectedMode: 'light', expectedTheme: 'light' },
    { stored: 'dark', systemDark: false, expectedMode: 'dark', expectedTheme: 'dark' },
    { stored: 'invalid', systemDark: true, expectedMode: 'auto', expectedTheme: 'dark' },
  ])('首屏与React接管保持一致：$stored/$systemDark', ({ stored, systemDark, expectedMode, expectedTheme }) => {
    systemTheme(systemDark);
    if (stored) window.localStorage.setItem(storageKey, stored);
    // 执行实际静态引导脚本，验证加载应用前后的可见主题一致。
    const source = readFileSync('public/theme-init.js', 'utf8');
    new Function(source)();
    expect(document.documentElement.dataset.themeMode).toBe(expectedMode);
    expect(document.documentElement.dataset.theme).toBe(expectedTheme);
    renderTheme();
    expectTheme(expectedMode, expectedTheme);
  });

  it('首屏存储被拒绝时仍应用系统主题，无系统API时安全降级', () => {
    systemTheme(true);
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new DOMException('Storage disabled', 'SecurityError'); });
    const source = readFileSync('public/theme-init.js', 'utf8');
    expect(() => new Function(source)()).not.toThrow();
    expect(document.documentElement.dataset.themeMode).toBe('auto');
    expect(document.documentElement.dataset.theme).toBe('dark');
    vi.stubGlobal('matchMedia', undefined);
    expect(() => new Function(source)()).not.toThrow();
    expect(document.documentElement.dataset.theme).toBe('light');
  });

  it('首屏脚本在样式和React模块前同步加载', () => {
    const html = readFileSync('index.html', 'utf8');
    const bootstrap = html.indexOf('src="/theme-init.js"');
    expect(bootstrap).toBeGreaterThan(-1);
    expect(bootstrap).toBeLessThan(html.indexOf('</head>'));
    expect(bootstrap).toBeLessThan(html.indexOf('src="/src/main.tsx"'));
  });
});
