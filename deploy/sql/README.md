# PostgreSQL 首次结构交付

本目录提供 `iplocation` 数据库/Schema 的初始化 SQL，与后端 EF 迁移一一对应：

| SQL | 对应 EF 迁移 | 内容 |
|---|---|---|
| [InitialBackendStorage.sql](InitialBackendStorage.sql) | `20261102153235_InitialBackendStorage` | 9 张业务表与标准 EF 迁移历史表 |
| [AddAccessRequestMetadata.sql](AddAccessRequestMetadata.sql) | `20261103052149_AddAccessRequestMetadata` | 新增 `ACCESS_LOG.REQUEST_HEADERS_JSON`、`SOURCE_LOCATION_JSON` 两个可空 jsonb 列 |
| [AddMcpCallContext.sql](AddMcpCallContext.sql) | `20261103140118_AddMcpCallContext` | 新增 `ACCESS_LOG.MCP_CONTEXT_JSON` 可空 jsonb 列 |

脚本均为幂等 SQL，只修改对应结构；普通 ID 为显式 `bigint`，由本地 `YiteIdService` 生成，没有数据库 Identity/自增，也没有默认管理员或密码种子。Web 启动不会自动建库或迁移，也不会 seed 账号。

## 离线生成与检查

从仓库根执行，均不连接数据库：

```sh
dotnet tool restore
dotnet ef migrations has-pending-model-changes --project backend/src/IpLocation.Api --context IpLocationDbContext --no-build
dotnet ef migrations list --no-connect --project backend/src/IpLocation.Api --context IpLocationDbContext --no-build
node scripts/verify-migrations.mjs
```

`dotnet tool restore` 只还原仓库本地清单中的 EF 工具，不安装全局依赖。`scripts/verify-migrations.mjs` 在内存中重新生成三段幂等 SQL，并与本目录交付文件比较，不覆盖文件。

设计时工厂不读取运行配置、`.env` 或秘密，使用不含凭据的离线设计连接生成 Npgsql 模型；不调用 `Open` / `CanConnect` / `Migrate` / `EnsureCreated`。`--no-connect` 不报告迁移是否已应用，这是刻意保留的验证边界。

## 实际应用前

1. 确认目标实例、备份、数据库及角色/密码配置。
2. 选择一种方式应用结构：`docker compose` 首次启动时由 `docker-entrypoint-initdb.d` 执行本目录 SQL，或手动执行 EF 迁移。
3. 幂等脚本仍会修改真实结构，不能因为“幂等”就自动执行。
4. 应用后初始化管理员；管理员密码通过受控文件或环境提供，不写入源码与命令行历史。

当前 SQL 与 EF 模型保持离线一致，但不替代真实环境的执行验证。
