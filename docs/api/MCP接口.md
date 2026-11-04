# 公开 MCP 接口

本项目在 `/mcp` 提供远程只读 MCP，覆盖首页已有的 IP 查询与来源目录能力。公开接入不需要登录、API Key、Token 或后台会话，不设置来源 IP、Origin 白名单，允许浏览器跨域调用。后台管理接口和调用日志仍受原有白名单、管理员身份保护。

传输采用官方 C# SDK 的无状态 Streamable HTTP；协议外层遵循 JSON-RPC / MCP，工具业务内容保留项目 `ApiResult<T>` 的 `success / code / message / data` 语义。MCP 不提供上传、激活、设置、管理审计或数据文件下载工具。实现与验证边界以仓库测试和实际运行结果为准，不以地址约定推导部署或验收结果。

## 接入

- 远程地址：`https://ip.tongyaninfo.com/mcp`。
- 本机默认地址：`http://127.0.0.1:5080/mcp`，与现有 API 共用宿主和端口。
- 选择支持 **Streamable HTTP** 的远程 MCP 客户端，填写地址，认证选择“无”。客户端配置字段以客户端自身约定为准。
- 请求及访客相关响应禁止共享缓存；可信来源识别复用项目明确配置的代理链，不信任调用方任意提供的转发头。
- 单次MCP请求体上限16KiB，超限返回413；无效JSON、重复属性和协议/参数错误明确返回错误，不把无界原始输入写入上下文。

常见客户端的 JSON 配置示例：

```json
{
  "mcpServers": {
    "tongyan-ip": {
      "url": "https://ip.tongyaninfo.com/mcp"
    }
  }
}
```

该片段说明服务名称和地址；某些客户端还需要填写传输类型，不能把它当作适用于所有客户端的固定配置格式。无需在配置中添加认证请求头。

首页“添加远程MCP服务”区右上角提供复制完整接入说明的按钮，悬浮提示为“复制这段内容到您的智能体中添加 MCP 服务”。可将说明交给支持MCP配置的智能体协助添加；客户端不能自动配置时，按其要求手工粘贴JSON并选择Streamable HTTP、无认证。不承诺任意智能体能自动修改客户端配置。

## 工具

| 工具 | 参数 | 行为 |
| --- | --- | --- |
| `query_ip` | `ip` 必填，`db` 可选、默认 `all` | 查询一个 IPv4/IPv6；`db` 支持公开来源 ID 或 `all` |
| `query_my_ip` | `db` 可选、默认 `all` | 从本次 MCP HTTP 连接及可信代理链识别地址，再执行同一查询 |
| `list_providers` | 无 | 返回来源目录、后台排序、启用及就绪状态、地址族、真实版本和适用署名 |

示例工具参数：

```json
{
  "name": "query_ip",
  "arguments": {
    "ip": "1.1.1.1",
    "db": "all"
  }
}
```

`query_my_ip` 返回 **MCP 客户端连接出口 IP**。若客户端运行在云端，结果可能是云服务器或代理出口，不能当作终端用户浏览器的 IP，也不能据此发现终端所有 IPv4/IPv6。

查询工具复用 `IIpQueryService`：只接收单个合法 IP，不隐式解析域名、URL、端口或 CIDR；IPv4 映射 IPv6 统一规范化，特殊用途地址仅分类，不调用公网定位。未知来源、停用来源、未就绪、超时及单源故障沿用[查询合同](查询接口.md)的明确业务状态，不静默替换来源。`all` 保留各来源差异与缺失值，不拼造地理事实。IP 地理信息由数据文件推断，不是访问者精确位置。

HTTP 的 JSON/XML/TXT 是现有查询接口的输出形式；MCP 使用协议规定的工具结果，不为三种格式重复建立工具。HTTP 成功不等于工具业务成功，客户端应同时检查 MCP 错误标识及业务 `success / code / data.status`。

## 服务来源引用

三个工具执行后的业务响应，其 `message` 末尾统一追加换行和以下来源行，成功与业务失败均包含：

```text
来源：https://ip.tongyaninfo.com
```

MCP的 `structuredContent` 和唯一text回退仍表达同一份 `ApiResult` JSON，只保留 `success / code / message / data` 四字段，不另加纯文本块或新的来源字段。来源只追加到MCP适配层创建的响应副本，不修改查询服务的原结果，也不改变普通HTTP JSON/XML/TXT响应。SDK在工具执行前处理的缺参、未知工具或协议错误，不由业务格式器强加该来源行。

服务说明及三个工具的description同时提示智能体在引用结果的答复末尾附来源行。它标识聚合查询服务，不替代各底层数据库已有的署名、许可链接或来源差异；这些来源信息继续保留。客户端和模型是否最终按提示引用取决于其行为，服务端不保证每次回答都遵循。

## MCP 调用上下文与后台审计

后台页面为 `/console/mcp-logs`，读取接口为 `GET /api/console/mcp-logs`，完整分页和权限合同见[后台管理接口](后台管理接口.md)。公开调用方不能读取日志。

MCP 访问记录复用现有有界非阻塞队列、批量写入及普通访问保留策略，以可空 `ACCESS_LOG.MCP_CONTEXT_JSON jsonb` 保存经过白名单清洗的协议上下文。该列由 `20261103140118_AddMcpCallContext` 增量迁移新增；应用启动不自动迁移，由部署方显式应用。

记录范围包括：

- 既有时间、可信来源 IP、安全请求头、来源地址归属地、HTTP 状态、耗时、取消状态和 Trace ID。
- MCP 协议版本、方法、请求 ID、工具名，以及实际收到的客户端名称/版本。客户端信息属于匿名调用方自报，缺失时保持空值，不能当作已认证身份。
- 受控查询参数快照，包括规范化查询目标 IP、来源 ID，以及业务结果、错误分类和实际来源/版本/状态摘要。
- 以 `input / output` 保存有界、脱敏的调用参数和输出JSON快照，分别提供 `inputTruncated / outputTruncated` 和 `inputCaptured / outputCaptured`。Captured为false表示未采集；为true且对应值为null表示实际记录了JSON null，不能显示成未采集。旧日志值为null、Captured为false，不回填；这是同一 `MCP_CONTEXT_JSON` 的内容扩展，不增加列或迁移。

`tools/call` 的 `input` 只记录实际收到的 `arguments`，未提供arguments时为 `{}`；其他协议方法记录清洗后的 `params`，不包含 `_meta`。标量 `mcpContext.ip` 仍只接受合法、规范化IP；新 `input` 可以保留有界的无效参数文本，不能把标量IP规范化规则理解为整个输入快照不含无效参数。无效或超限JSON无法解析时不保存原文，input显示未采集。

`output` 为实际JSON-RPC `result` 或 `error` 的对象内容，省略重复的 `jsonrpc / id` 外壳，请求ID已单独记录。工具结果保留错误标记等协议字段及业务结构化内容，不以原始HTTP响应全文替代。

不保存任意 `_meta`、完整原始HTTP请求/响应、Cookie、Authorization、Token 或聊天历史。服务端只知道客户端实际发送的协议信息，不能读取它未发送的完整对话。输入/输出仅保留清洗快照，敏感key的值替换为 `[REDACTED]`，协议错误的data/details/stack等诊断内容不透传；正常 `ApiResult.data` 仍按业务输出保存。所有快照在读出时再次清洗。

快照中的普通字符串若是绝对HTTP/HTTPS URL，按安全Referer相同语义移除账户信息、查询串及片段，变化时标记清洗；干净URL、中文和非URL无效IP文本保持原意。该规则不对任意自由文本进行秘密识别。

`input` 上限8KiB、`output` 上限32KiB、总上下文上限48KiB，均按UTF-8序列化大小计算；每个输入/输出快照递归深度最多12、每个容器最多64项、最多1024节点、单字符串最多4096字符。超限保留合法JSON并标记对应 `inputTruncated / outputTruncated`；敏感key替换、URL清洗、_meta/诊断字段排除也标记清洗，顶层 `truncated` 合并这些状态。true表示上下文已清洗或截断，SDK_meta被排除时业务structuredContent仍可能完整，不能只据该标记认定业务数据丢失。最多保留6项来源摘要。输出优先使用MCP `structuredContent`，等价的text fallback不重复存储，等价去重本身不算截断；其他JSON文本先解析再清洗。请求ID以有界文本表示，缺失版本、客户端和结果字段保持null，不从其他请求推测补齐。

日志是尽力记录：队列满、数据库不可用、进程故障或关闭排空超时可能产生缺口。后台显示积压、丢弃、写入失败及最后成功时间，不能将其当作绝不丢失的管理员可靠审计。普通日志 `accessLogRetentionDays=0` 同时停止 MCP 新记录与相应统计，历史暂留；恢复正数或 `null` 后恢复采集。读取限制不等于物理删除，自动清理未开启。

统计分开计算 MCP HTTP 请求和定位工具请求：识别到 `tools/call` 且工具为 `query_ip / query_my_ip` 即计定位请求，包含参数或协议错误的请求，例如HeaderMismatch；不表示实际调用了某个数据源。协议发现、初始化及工具目录请求不计作定位请求，来源实际调用次数不由该数推导。管理审计继续沿用原有可靠事务流程，不与 MCP 公共调用日志混用。

运行总览提供独立MCP请求与定位查询趋势，HTTP错误与工具业务错误分开；字段与交互合同见[后台管理接口](后台管理接口.md)。实际行为除合同外还需以仓库测试与实际运行结果为准，不以合同更新推导已部署。

## 官方协议资料

- [Streamable HTTP](https://modelcontextprotocol.io/specification/2026-07-28/basic/transports/streamable-http)
- [C# SDK 传输](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/transports/transports.html)
- [C# SDK 无状态模式](https://csharp.sdk.modelcontextprotocol.io/v2/concepts/stateless/stateless.html)
