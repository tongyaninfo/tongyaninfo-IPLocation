# 参与贡献

感谢参与 IP 聚合查询（IPLocation）。本文说明开发环境、代码约定与提交要求。

## 开发环境

- **.NET SDK** 10.0.x（`global.json` 固定为 10.0.300，可用 `rollForward` 使用更新的补丁版本）
- **Node.js** 24 或更高版本（前端 `package.json` 要求 `>=24.0.0`）
- **PostgreSQL** 17（可选；只有启用后台持久化时才需要，默认可使用内存存储）

## 本地验证

提交前请在仓库根目录运行与改动范围对应的检查：

```sh
# 文档：标题、UTF-8、代码围栏、本地链接
node scripts/verify.mjs --scope docs

# 后端：构建、测试、离线迁移 SQL 一致性
node scripts/verify.mjs --scope backend

# 前端：类型检查、测试、构建
node scripts/verify.mjs --scope frontend

# 全量
node scripts/verify.mjs --scope all
```

也可以手动执行：

```sh
cd backend && dotnet build && dotnet test
cd frontend && npm ci && npm run build && npm test
```

后端测试会启动完整宿主；测试项目已通过模块初始化强制关闭产品心跳（`Support__TelemetryEnabled=false`），不会向官方服务注册测试安装。手动启动实例联调时如需关闭心跳，可设置 `Support__TelemetryEnabled=false`；产品运行默认开启，用于统计安装量。

请在 Pull Request 中如实填写运行过的命令与结果；没有运行的项目不要声称通过。

## 提交 Pull Request

1. 先开 Issue 或确认现有 Issue，说明要解决的问题与范围；行为变化需同步更新文档和测试。
2. 从 `main` 创建分支，分支名建议 `feat/`、`fix/`、`docs/`、`chore/` + 英文短主题。
3. 提交信息采用 `type(scope): 描述`，type 限 `feat fix docs style refactor perf test chore ci revert merge`，冒号后一个空格，描述简明。
4. 一个 PR 聚焦一个主题；避免顺手重构无关代码或升级无关依赖。
5. PR 会运行 [CI](.github/workflows/ci.yml)；请在合并前修复失败项。

## 代码约定

- 使用中文沟通、文档与注释；标识符、命令、配置键按语言惯例。
- 遵循 [AGENTS.md](AGENTS.md) 中的架构与安全约束：后端 DTO、网络信任边界、隐私默认值、后台访问控制等。
- 前端、后端、文档、脚本的修改范围应保持一致；接口变更须同步 `docs/api/` 与前端类型。

## 安全与数据红线

以下内容**不得**出现在代码、配置、测试夹具或提交历史中：

- 真实凭据：密码、Token、连接串、产品密钥、证书私钥。示例只使用占位符（如 `CHANGE_ME`）或环境变量。
- 真实内网地址、生产主机名、私有镜像仓库地址、个人绝对路径。
- IP 数据库文件（MMDB/XDB/DAT）、数据库导出、日志、备份或运行数据。
- 构建产物：`bin/`、`obj/`、`dist/`、`node_modules/`。

测试与文档中的示例 IP 统一使用 RFC 5737 文档网段：`192.0.2.0/24`、`198.51.100.0/24`、`203.0.113.0/24`；需要私网示例时使用 RFC 1918 网段（如 `10.20.30.0/24`）并注明是示例。

## 新增或修改数据源适配器

- 数据源适配器放在 `backend/src/IpLocation.Api/Service/impl/Providers/`，实现 `IIpDataProvider`。
- 保持来源间的字段差异与缺失值，不补造、不跨来源填充。
- 更新 [数据源与许可](docs/spec/数据源与许可.md) 与 [NOTICE](NOTICE)，补充官方链接与署名要求；数据文件不得提交到仓库。
- 为解析器补充单元测试；涉及真实数据文件的验证请在本地进行，不把数据文件加入测试夹具。

## 报告安全问题

安全问题请按 [SECURITY.md](SECURITY.md) 的流程私下报告，不要提交公开 Issue 或在 PR 中披露细节。
