import { createContext, useContext, useEffect, useLayoutEffect, useState } from 'react';
import type { ReactNode } from 'react';

export type ThemeMode = 'auto' | 'light' | 'dark';
export type ResolvedTheme = 'light' | 'dark';
export const THEME_STORAGE_KEY = 'tongyan-theme-mode';
export const SYSTEM_THEME_QUERY = '(prefers-color-scheme: dark)';

interface ThemeState {
  mode: ThemeMode;
  resolvedTheme: ResolvedTheme;
  selectMode: (mode: ThemeMode) => void;
}

const ThemeContext = createContext<ThemeState | null>(null);
const parseMode = (value: string | null): ThemeMode => value === 'light' || value === 'dark' ? value : 'auto';

function storedMode(): ThemeMode {
  try { return parseMode(window.localStorage.getItem(THEME_STORAGE_KEY)); }
  catch { return 'auto'; }
}

function systemIsDark() {
  return typeof window.matchMedia === 'function' && window.matchMedia(SYSTEM_THEME_QUERY).matches;
}

/** 偏好仅保存在当前来源的浏览器中；系统变化和同源标签页同步不触发后台设置写入。 */
export function ThemeProvider({ children }: { children: ReactNode }) {
  const [mode, setMode] = useState<ThemeMode>(storedMode);
  const [systemDark, setSystemDark] = useState(systemIsDark);
  const resolvedTheme: ResolvedTheme = mode === 'dark' || (mode === 'auto' && systemDark) ? 'dark' : 'light';

  useEffect(() => {
    const media = typeof window.matchMedia === 'function' ? window.matchMedia(SYSTEM_THEME_QUERY) : null;
    const followSystem = () => setSystemDark(media?.matches ?? false);
    const followStorage = (event: StorageEvent) => {
      if (event.key !== THEME_STORAGE_KEY && event.key !== null) return;
      // 仅同步localStorage偏好，不接收同名sessionStorage项。
      try { if (event.storageArea && event.storageArea !== window.localStorage) return; }
      catch { return; }
      setMode(parseMode(event.key === null ? null : event.newValue));
    };
    followSystem();
    if (media?.addEventListener) media.addEventListener('change', followSystem);
    else media?.addListener?.(followSystem);
    window.addEventListener('storage', followStorage);
    return () => {
      if (media?.removeEventListener) media.removeEventListener('change', followSystem);
      else media?.removeListener?.(followSystem);
      window.removeEventListener('storage', followStorage);
    };
  }, []);

  useLayoutEffect(() => {
    const root = document.documentElement;
    root.dataset.theme = resolvedTheme;
    root.dataset.themeMode = mode;
    root.style.colorScheme = resolvedTheme;
    document.querySelector('meta[name="theme-color"]')?.setAttribute('content', resolvedTheme === 'dark' ? '#0d1626' : '#ffffff');
  }, [mode, resolvedTheme]);

  function selectMode(next: ThemeMode) {
    setMode(next);
    try { window.localStorage.setItem(THEME_STORAGE_KEY, next); }
    catch { /* 存储被浏览器禁用时，仅影响刷新恢复；当前页面仍可正常切换。 */ }
  }

  return <ThemeContext.Provider value={{ mode, resolvedTheme, selectMode }}>{children}</ThemeContext.Provider>;
}

export const useTheme = () => useContext(ThemeContext);
