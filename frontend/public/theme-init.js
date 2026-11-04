// 在应用和样式绘制前恢复主题，避免已有深色偏好时先闪现浅色页面。
(() => {
  let mode = 'auto';
  try {
    const stored = window.localStorage.getItem('tongyan-theme-mode');
    if (stored === 'light' || stored === 'dark') mode = stored;
  } catch { /* 浏览器拒绝存储时，默认跟随系统。 */ }
  const systemDark = typeof window.matchMedia === 'function' && window.matchMedia('(prefers-color-scheme: dark)').matches;
  const resolved = mode === 'dark' || (mode === 'auto' && systemDark) ? 'dark' : 'light';
  const root = document.documentElement;
  root.dataset.theme = resolved;
  root.dataset.themeMode = mode;
  root.style.colorScheme = resolved;
  const themeColor = document.querySelector('meta[name="theme-color"]');
  if (themeColor) themeColor.setAttribute('content', resolved === 'dark' ? '#0d1626' : '#ffffff');
})();
