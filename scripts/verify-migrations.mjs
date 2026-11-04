import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

// 只使用独立设计时工厂生成 SQL 到内存比较，不连接数据库或改写交付文件。
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const project = 'backend/src/IpLocation.Api/IpLocation.Api.csproj';
const canonical = text => text.replace(/^\uFEFF/, '').replace(/\r\n/g, '\n').trim();
// 初始结构和后续增量分别交付，旧环境只需审阅并应用对应增量。
for (const [from, to] of [['0', 'InitialBackendStorage'], ['InitialBackendStorage', 'AddAccessRequestMetadata'],
  ['AddAccessRequestMetadata', 'AddMcpCallContext']]) {
  const result = spawnSync('dotnet', ['ef', 'migrations', 'script', from, to,
    '--idempotent', '--project', project, '--startup-project', project,
    '--context', 'IpLocationDbContext', '--no-build'], { cwd: root, encoding: 'utf8', maxBuffer: 8 * 1024 * 1024 });
  if (result.error || result.status !== 0) {
    console.error(result.error?.message ?? result.stderr);
    console.error('Restore the repository-local tool first: dotnet tool restore');
    process.exit(1);
  }
  const delivered = fs.readFileSync(path.join(root, `deploy/sql/${to}.sql`), 'utf8');
  if (canonical(result.stdout) !== canonical(delivered)) {
    console.error(`Delivered ${to} SQL differs from the EF migration. Regenerate and review the SQL artifact.`);
    process.exit(1);
  }
  console.log(`${to} migration SQL matches the delivered idempotent script (offline).`);
}
