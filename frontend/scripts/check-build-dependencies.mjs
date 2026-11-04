import fs from 'node:fs';
import path from 'node:path';
import zlib from 'node:zlib';
import { fileURLToPath } from 'node:url';

const frontendRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const forbidden = new Set(['@stagewise/toolbar', 'cfonts']);

/** 同时检查真实包身份与 npm 别名，避免只改依赖键名绕过构建边界。 */
export function checkLockfile(lock) {
  if (!lock.packages || ![2, 3].includes(lock.lockfileVersion)) throw new Error('需要 npm v2/v3 锁文件。');
  const problems = [];
  for (const [location, metadata] of Object.entries(lock.packages)) {
    const locationName = location.match(/(?:^|\/)node_modules\/((?:@[^/]+\/)?[^/]+)$/)?.[1];
    if (forbidden.has(metadata.name) || forbidden.has(locationName)) problems.push(location || '(root)');
    for (const field of ['dependencies', 'devDependencies', 'optionalDependencies', 'peerDependencies']) {
      for (const [name, version] of Object.entries(metadata[field] || {})) {
        const alias = String(version).match(/^npm:((?:@[^/]+\/)?[^@]+)(?:@|$)/)?.[1];
        if (forbidden.has(name) || forbidden.has(alias)) problems.push(`${location || '(root)'}.${field}.${name}`);
      }
    }
  }
  if (problems.length) throw new Error(`构建依赖包含禁用包：${problems.join(', ')}`);
  return Object.keys(lock.packages).length;
}

/** 遍历嵌套安装目录及链接目标，锁文件干净不能掩盖本机遗留的禁用依赖。 */
export function checkInstalledDependencies(nodeModules) {
  const seen = new Set();
  let count = 0;
  function visit(directory) {
    if (!fs.existsSync(directory)) return;
    const realDirectory = fs.realpathSync(directory);
    if (seen.has(realDirectory)) return;
    seen.add(realDirectory);
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (entry.name.startsWith('.') || (!entry.isDirectory() && !entry.isSymbolicLink())) continue;
      const packagePath = path.join(directory, entry.name);
      if (entry.name.startsWith('@')) { visit(packagePath); continue; }
      const metadataPath = path.join(packagePath, 'package.json');
      if (fs.existsSync(metadataPath)) {
        const metadata = JSON.parse(fs.readFileSync(metadataPath, 'utf8'));
        if (forbidden.has(metadata.name)) throw new Error(`本机安装目录包含禁用包：${metadata.name} (${packagePath})`);
        count++;
      }
      visit(path.join(packagePath, 'node_modules'));
    }
  }
  if (!fs.existsSync(nodeModules)) throw new Error('缺少 node_modules；安装后重新验证，或仅运行 --lock-only。');
  visit(nodeModules);
  return count;
}

/** 发布前检查正文和压缩侧车，不让工具代码混入本次可分发目录。 */
export function checkBuildArtifacts(directory) {
  let count = 0;
  function visit(current) {
    for (const entry of fs.readdirSync(current, { withFileTypes: true })) {
      const file = path.join(current, entry.name);
      if (entry.isDirectory()) { visit(file); continue; }
      if (!/\.(?:js|css|html|json|map|txt)(?:\.br|\.gz)?$/.test(file)) continue;
      let content = fs.readFileSync(file);
      if (file.endsWith('.br')) content = zlib.brotliDecompressSync(content);
      if (file.endsWith('.gz')) content = zlib.gunzipSync(content);
      if (/stagewise|cfonts/i.test(content.toString('utf8'))) throw new Error(`发布产物包含禁用工具标识：${file}`);
      count++;
    }
  }
  visit(directory);
  return count;
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.length > 1 || (args.length === 1 && args[0] !== '--lock-only')) {
    console.error('Usage: node scripts/check-build-dependencies.mjs [--lock-only]');
    process.exitCode = 2;
  } else {
    const locked = checkLockfile(JSON.parse(fs.readFileSync(path.join(frontendRoot, 'package-lock.json'), 'utf8')));
    const installed = args[0] === '--lock-only' ? null : checkInstalledDependencies(path.join(frontendRoot, 'node_modules'));
    console.log(`构建依赖边界检查通过：锁文件 ${locked} 条目${installed === null ? '' : `，本机安装 ${installed} 包`}。`);
  }
}
