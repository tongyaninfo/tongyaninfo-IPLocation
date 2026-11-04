import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { build } from 'vite';
import { assertSafeThemeSource, inlineThemeScript } from './inline-theme.mjs';
import { verifyBrowserTarget } from './verify-browser-target.mjs';
import { checkBuildArtifacts } from './check-build-dependencies.mjs';

const frontendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repositoryRoot = path.resolve(frontendRoot, '..');
const buildId = `${new Date().toISOString().replace(/\D/g, '')}-${crypto.randomBytes(3).toString('hex')}`;
const buildRoot = path.join(repositoryRoot, '.local', 'frontend-builds', buildId);
const distPath = path.join(buildRoot, 'dist');
const compatibilityDistPath = path.join(frontendRoot, 'dist');
const gzip = promisify(zlib.gzip);
const brotli = promisify(zlib.brotliCompress);
const themeContent = await fs.readFile(path.join(frontendRoot, 'public', 'theme-init.js'), 'utf8');
assertSafeThemeSource(themeContent);

// 每次使用全新目录，不清理既有 dist；发布者只使用 latest 文件指向的本次产物。
await fs.mkdir(distPath, { recursive: true });
await build({
  root: frontendRoot,
  configFile: path.join(frontendRoot, 'vite.config.ts'),
  build: { outDir: distPath, emptyOutDir: false, copyPublicDir: false, assetsInlineLimit: 0 },
});

// 构建内联同一份可信主题代码，保留早执行，省去一次阻塞请求；开发环境入口保留。
const indexFile = path.join(distPath, 'index.html');
const indexHtml = await fs.readFile(indexFile, 'utf8');
await fs.writeFile(indexFile, inlineThemeScript(indexHtml, themeContent));
// 检查真正发布的入口及懒加载脚本，防止新语法再次导致旧浏览器解析白屏。
const browserTarget = verifyBrowserTarget(distPath);

const files = [];
async function visit(directory) {
  for (const entry of await fs.readdir(directory, { withFileTypes: true })) {
    const absolutePath = path.join(directory, entry.name);
    if (entry.isDirectory()) await visit(absolutePath);
    else files.push(absolutePath);
  }
}
await visit(distPath);

const assets = [];
for (const absolutePath of files) {
  const content = await fs.readFile(absolutePath);
  const asset = { path: path.relative(distPath, absolutePath).split(path.sep).join('/'), bytes: content.length };
  // 图片已有自身编码；只预压缩文本资源，至少节约 128 字节及 5% 才发布侧车。
  if (/\.(?:js|css|svg)$/.test(absolutePath)) {
    const compressed = await Promise.all([
      brotli(content, { params: { [zlib.constants.BROTLI_PARAM_QUALITY]: 11 } }),
      gzip(content, { level: 9 }),
    ]);
    for (const [index, suffix] of ['br', 'gz'].entries()) {
      if (compressed[index].length <= content.length * 0.95 && content.length - compressed[index].length >= 128) {
        await fs.writeFile(`${absolutePath}.${suffix}`, compressed[index]);
        asset[suffix] = compressed[index].length;
      }
    }
  }
  assets.push(asset);
}

// 先验证正文及压缩侧车；失败时不更新兼容目录与可发布目录指针。
const dependencyBoundary = { checkedTextFiles: checkBuildArtifacts(distPath), forbiddenPackages: ['@stagewise/toolbar', 'cfonts'] };

// 本机后端继续读 frontend/dist，旧哈希文件保留；该兼容目录不能作为发布打包来源。
await fs.cp(distPath, compatibilityDistPath, { recursive: true, force: true });
const report = {
  buildId,
  builtAtUtc: new Date().toISOString(),
  distPath,
  compatibilityDistPath,
  themeInline: true,
  browserTarget,
  dependencyBoundary,
  assets,
  rawBytes: assets.reduce((total, asset) => total + asset.bytes, 0),
  brotliBytes: assets.reduce((total, asset) => total + (asset.br ?? asset.bytes), 0),
  gzipBytes: assets.reduce((total, asset) => total + (asset.gz ?? asset.bytes), 0),
};
await fs.writeFile(path.join(buildRoot, 'build-report.json'), `${JSON.stringify(report, null, 2)}\n`);
await fs.writeFile(path.join(repositoryRoot, '.local', 'frontend-build-latest.json'), `${JSON.stringify(report, null, 2)}\n`);
console.log(`Fresh frontend output: ${distPath}`);
console.log(`Current assets: ${assets.length}; raw ${report.rawBytes} bytes; Brotli ${report.brotliBytes}; gzip ${report.gzipBytes}.`);
