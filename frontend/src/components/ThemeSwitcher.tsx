import { useTheme } from '../models/useTheme';
import { Icon } from './Icon';

/** 沿用官网双图标交互：再次点击当前手动模式，恢复跟随系统。 */
export function ThemeSwitcher() {
  const theme = useTheme();
  if (!theme) return null;
  const { mode, selectMode } = theme;
  // Chrome 79 的 fieldset 不按 flex 排列子元素；普通分组保留可访问名称和原交互。
  return <div role="group" className="theme-switcher" aria-label={mode === 'auto' ? '页面主题，自动跟随系统' : '页面主题'}>
    <button type="button" className="theme-button" aria-pressed={mode === 'light'}
      aria-label={mode === 'light' ? '浅色模式已开启，点击恢复自动' : '切换为浅色模式'}
      title={mode === 'light' ? '浅色模式 · 再次点击跟随系统' : '浅色模式'}
      onClick={() => selectMode(mode === 'light' ? 'auto' : 'light')}><Icon name="sun" size={18} /></button>
    <button type="button" className="theme-button" aria-pressed={mode === 'dark'}
      aria-label={mode === 'dark' ? '深色模式已开启，点击恢复自动' : '切换为深色模式'}
      title={mode === 'dark' ? '深色模式 · 再次点击跟随系统' : '深色模式'}
      onClick={() => selectMode(mode === 'dark' ? 'auto' : 'dark')}><Icon name="moon" size={18} /></button>
  </div>;
}
