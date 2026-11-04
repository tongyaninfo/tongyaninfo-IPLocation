import { createRequire } from 'node:module';
import { describe, expect, it } from 'vitest';

// Node 构建工具独立于应用运行时，使用项目要求的 Node 24 直接加载。
const { inlineThemeScript, assertSafeThemeSource } = createRequire(import.meta.url)('../scripts/inline-theme.mjs') as {
  inlineThemeScript: (html: string, source: string) => string;
  assertSafeThemeSource: (source: string) => void;
};

describe('生产主题引导内联边界', () => {
  const html = '<head><script src="/theme-init.js"></script><script type="module" src="/assets/app.js"></script></head>';

  it('原样内联可信代码并保持早于应用模块执行', () => {
    const source = 'document.documentElement.dataset.theme = "dark";\n';
    const result = inlineThemeScript(html, source);
    expect(result).toContain(`<script>${source}</script>`);
    expect(result).not.toContain('src="/theme-init.js"');
    expect(result.indexOf(source)).toBeLessThan(result.indexOf('type="module"'));
  });

  it.each(['</script>', '</SCRIPT >', 'const text = "</script><script>alert(1)</script>";'])('拒绝结束标签：%s', source => {
    expect(() => assertSafeThemeSource(source)).toThrow(/script end tag/);
    expect(() => inlineThemeScript(html, source)).toThrow(/script end tag/);
  });

  it.each(['<head></head>', html + '<script src="/theme-init.js"></script>'])('入口缺失或重复时停止构建', sourceHtml => {
    expect(() => inlineThemeScript(sourceHtml, 'void 0;')).toThrow(/exactly one/);
  });
});
