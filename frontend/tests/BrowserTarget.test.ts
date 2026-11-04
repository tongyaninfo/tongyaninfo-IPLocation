import { createRequire } from 'node:module';
import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

// 构建检查直接使用已安装的 Rolldown/Oxc，不增加浏览器运行时依赖或全局依赖。
interface SourceReport {
  target: string;
  counts: Record<string, number>;
  jsFileCount?: number;
  inlineScriptCount?: number;
  files: { file: string; counts: Record<string, number>; parseErrors: string[]; transformErrors: string[]; transformWarnings: string[]; transformedCounts: Record<string, number> }[];
}
const { verifyBrowserSource, verifyBrowserTarget } = createRequire(import.meta.url)('../scripts/verify-browser-target.mjs') as {
  verifyBrowserSource: (source: string, file?: string, kind?: string) => SourceReport;
  verifyBrowserTarget: (distPath: string) => SourceReport;
};

describe('Chrome79发布语法守卫', () => {
  it('允许旧浏览器可解析语法，字符串和注释中的新语法不误报', () => {
    const report = verifyBrowserSource('const text = "x?.y ?? z ||= 1"; /* static {} */ async function query() { await fetch("/"); }');
    expect(report.target).toBe('chrome79');
    expect(Object.values(report.counts).every(value => value === 0)).toBe(true);
    expect(report.files[0].parseErrors).toEqual([]);
    expect(report.files[0].transformErrors).toEqual([]);
    expect(report.files[0].transformWarnings).toEqual([]);
  });

  it.each([
    ['optionalChaining', 'const value = source?.value;'],
    ['nullishCoalescing', 'const value = source ?? "fallback";'],
    ['logicalAssignment', 'let value; value ||= 1;'],
    ['classStaticBlock', 'class Value { static { this.ready = true; } }'],
    ['privateMethod', 'class Value { #read() {} }'],
    ['privateBrandCheck', 'class Value { #read; has(other) { return #read in other; } }'],
    ['topLevelAwait', 'await fetch("/");'],
    ['regexpMatchIndices', 'const value = /value/d;'],
    ['regexpUnicodeSets', 'const value = /value/v;'],
    ['importAttributes', 'import value from "./value.json" with { type: "json" };'],
  ])('拒绝产物内的%s，而不是静默转换后误报通过', (feature, source) => {
    expect(() => verifyBrowserSource(source, 'assets/fixture.js')).toThrow(new RegExp(`fixture\\.js: ${feature}=`));
    try { verifyBrowserSource(source); }
    catch (error) {
      const report = (error as Error & { report: SourceReport }).report;
      expect(report.counts[feature]).toBeGreaterThan(0);
    }
  });

  it('检查真正的懒加载文件和HTML内联主题，数据脚本不作为JavaScript解析', () => {
    // 每次唯一受控目录并保留，遵守测试不能自动删除文件的项目约定。
    const root = path.resolve(import.meta.dirname, '../../.local/verification/browser-target', `fixture-${Date.now()}-${Math.random().toString(16).slice(2)}`);
    fs.mkdirSync(path.join(root, 'assets'), { recursive: true });
    fs.writeFileSync(path.join(root, 'assets/app.js'), 'export const ready = true;');
    fs.writeFileSync(path.join(root, 'assets/lazy.js'), 'export function run() { return 1; }');
    fs.writeFileSync(path.join(root, 'index.html'), '<script type="application/json">{"text":"?."}</script><script>document.documentElement.dataset.theme="light";</script><script type="module" src="/assets/app.js"></script>');
    const report = verifyBrowserTarget(root);
    expect(report.jsFileCount).toBe(2);
    expect(report.inlineScriptCount).toBe(1);
    expect(report.files.map(file => file.file)).toEqual(['assets/app.js', 'assets/lazy.js', 'index.html#inline-1.js']);
    fs.writeFileSync(path.join(root, 'assets/lazy.js'), 'export const value = source?.value;');
    expect(() => verifyBrowserTarget(root)).toThrow(/assets\/lazy\.js: optionalChaining=/);
    fs.writeFileSync(path.join(root, 'assets/lazy.js'), 'export const value = 1;');
    fs.writeFileSync(path.join(root, 'index.html'), '<script>document.querySelector("meta")?.setAttribute("content", "dark");</script>');
    expect(() => verifyBrowserTarget(root)).toThrow(/index\.html#inline-1\.js: optionalChaining=/);
  });
});
