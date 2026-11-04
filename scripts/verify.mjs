import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

// 验证只运行现有检查，不安装依赖、创建数据库、删除文件或操作 Git。
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const args = process.argv.slice(2);
const scope = args.length === 0 ? 'all' : args.length === 2 && args[0] === '--scope' ? args[1] : '';
if (!['docs', 'backend', 'frontend', 'all'].includes(scope)) {
  console.error('Usage: node scripts/verify.mjs [--scope docs|backend|frontend|all]');
  process.exit(2);
}

function checkDocs() {
  const ignored = new Set(['.git', 'node_modules', 'bin', 'obj', 'dist', 'TestResults']);
  const files = [];
  const visit = directory => {
    for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
      if (ignored.has(entry.name) || entry.name.startsWith('.')) continue;
      const target = path.join(directory, entry.name);
      if (entry.isDirectory()) visit(target);
      else if (entry.isFile() && entry.name.endsWith('.md')) files.push(target);
    }
  };
  visit(root);
  const problems = [];
  let links = 0;
  for (const file of files) {
    const content = fs.readFileSync(file, 'utf8');
    const label = path.relative(root, file);
    if (!content.startsWith('# ')) problems.push(`${label}: missing title`);
    if (!content.endsWith('\n')) problems.push(`${label}: missing final newline`);
    if (content.includes('\uFFFD')) problems.push(`${label}: invalid UTF-8 replacement`);
    if ((content.match(/^```/gm) ?? []).length % 2) problems.push(`${label}: unbalanced code fences`);
    if (content.split('\n').some(line => /[\t ]+$/.test(line))) problems.push(`${label}: trailing whitespace`);
    for (const match of content.matchAll(/\[[^\]]+\]\(([^)]+)\)/g)) {
      const href = match[1];
      if (/^(https?:|mailto:|#)/.test(href)) continue;
      const target = path.resolve(path.dirname(file), decodeURIComponent(href.split('#')[0]));
      if (!fs.existsSync(target)) problems.push(`${label}: missing link ${href}`);
      links++;
    }
  }
  for (const problem of problems) console.error(problem);
  if (problems.length) process.exit(1);
  console.log(`Documentation passed: ${files.length} files, ${links} local links.`);
}

function run(command, commandArgs, directory = root) {
  console.log(`Running: ${command} ${commandArgs.join(' ')}`);
  const result = spawnSync(command, commandArgs, { cwd: directory, stdio: 'inherit', shell: false });
  if (result.error) console.error(result.error.message);
  if (result.error || result.status !== 0) process.exit(result.status ?? 1);
}

if (scope === 'docs' || scope === 'all') checkDocs();
if (scope === 'backend' || scope === 'all') {
  run('dotnet', ['build', 'backend/IpLocation.slnx', '--nologo', '-p:UseSharedCompilation=false', '-m:1', '-nr:false']);
  run('dotnet', ['test', 'backend/tests/IpLocation.Api.Tests/IpLocation.Api.Tests.csproj', '--nologo', '-p:UseSharedCompilation=false', '-m:1', '-nr:false']);
  run('node', ['scripts/verify-migrations.mjs']);
}
if (scope === 'frontend' || scope === 'all') {
  for (const command of ['typecheck', 'test', 'build']) run('npm', ['run', command], path.join(root, 'frontend'));
}
