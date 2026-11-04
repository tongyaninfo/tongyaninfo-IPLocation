/** 主题来自受版本管理的本地脚本；禁止结束标签跨出当前脚本元素。 */
export function assertSafeThemeSource(source) {
  if (/<\/script/i.test(source)) throw new Error('Theme initialization must not contain an HTML script end tag.');
}

export function inlineThemeScript(html, source) {
  assertSafeThemeSource(source);
  const marker = '<script src="/theme-init.js"></script>';
  if (html.split(marker).length !== 2) throw new Error('Expected exactly one theme initialization entry.');
  return html.replace(marker, () => `<script>${source}</script>`);
}
