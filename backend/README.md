# 后端 API 与 PostgreSQL 存储

.NET 10 单 API，源码 `src/IpLocation.Api/`，测试 `tests/IpLocation.Api.Tests/`。先读[根规则](../AGENTS.md)、[查询合同](../docs/api/查询接口.md)和[已确认存储结构](../docs/spec/后台存储结构草案.md)。

## 启动与验证

仓库根目录执行：

```sh
dotnet tool restore
dotnet run --project backend/src/IpLocation.Api/IpLocation.Api.csproj --no-launch-profile -- --Runtime:SettingsFile "/absolute/path/to/runtime.json"
node scripts/verify.mjs --scope backend
```

默认监听 `http://127.0.0.1:5080`，同源页面需先构建前端。运行配置参数应给绝对路径，避免dotnet run切换到工程目录后误解相对路径。不提供该参数也可运行，但来源路径为空时不会猜测个人文件位置。

`/health` 只表示进程响应，报告数据库是否配置，不连接或验证数据库。`/console`、登录、管理API和专属资源在静态文件之前统一核对来源。

`Console:AllowedNetworks` 默认允许 `127.0.0.1`、`::1`、`192.0.2.0/24`、`198.51.100.0/24`。其中 `192.0.2.0/24` 与 `198.51.100.0/24` 属于 RFC 5737 文档示例网段，仅用于演示格式，部署时请替换为实际管理网段。`localhost` 通过 IPv4/IPv6 回环地址访问，不按客户端提供的 Host 或转发头放行；默认监听仍为回环地址，局域网连接还需显式配置监听和入口。更高优先级配置源声明名单时整份替换默认值，显式 `[]` 拒绝全部来源；仅通过 IP 检查不代表管理员已初始化或已登录。

当前代码对IP白名单拒绝返回空404及`Cache-Control: no-store`，适用于`/console`、`/api/console`、`/console-assets`及其子路径和`/assets/console-*`专属资源，大小写及HTTP方法共用门禁。拒绝正文为空，不返回统一业务JSON、后台提示、登录跳转或身份Cookie；这是`ApiResult<T>`之外的传输层例外。已有登录会话同样受每次请求的实时名单检查。允许来源的身份401、权限403、防伪400及存储错误语义保持；全局歧义路径仍先于门禁，对普通路径和后台路径统一返回400 `AMBIGUOUS_PATH`。

相关行为由后端集成测试覆盖；具体测试数量以当前工作区实际运行结果为准。

## Tunnel 与可信代理

`Program.cs` 已在访问日志、后台白名单及认证之前调用 `UseForwardedHeaders`，但仅当 `TrustedProxies:KnownProxies` 或 `KnownIPNetworks` 非空时启用。空名单有意忽略全部转发头，保留 socket 连接地址。不要通过 `ASPNETCORE_FORWARDEDHEADERS_ENABLED` 绕过这份显式信任配置，也不直接信任任意 `CF-Connecting-IP`。

Cloudflare Tunnel 经宿主转入 Docker 时，在部署配置中填写容器实际看到的直接代理精确地址，保持 `TrustedProxies:ForwardLimit=1`；已有 `X-Forwarded-For` 和 `X-Forwarded-Proto` 由框架统一解析。纯 IP、首页自身查询、访问日志和后台白名单使用相同的识别结果；不需要再加 nginx。代理网关变化时重新核实，不把整个 Docker 或私有网段加入可信名单。Compose 参数及源站入口说明见[部署文档](../deploy/docker/README.md)。

## 真实来源配置

纯真来源的对外显示名为“纯真 社区版”，由适配器描述统一提供给查询、来源目录及后台；公开编码仍为 CZ88，底层按下文 QQWry DAT 能力读取。展示名称不改变来源 ID、文件格式、地址族或人工授权边界。

`GeoLite2` 使用 `Enabled`、`DatabasePath`。当前默认库为 `dbip`，GeoLite2 默认停用。所有来源的旧 `LicenseConfirmed`、`LicenseReviewMode` 及 `GeoLite2.MaxAgeDays` 字段仅作兼容，不参与启用、激活、默认库或查询判断。实际路径放在部署方配置中，仓库没有个人路径默认值。GeoLite2 只接受 City，定制 Country 不启用；IP2Location 使用独立 DB11 MMDB 适配器。

使用官方 MaxMind.GeoIP2 6.0.0，中文优先、缺失值保留，RegisteredCountry 不冒充定位国家，City 库不补造 ISP/ASN。版本来自 MMDB 构建时间。已移除按构建时间自动停用的逻辑；版本更新与许可由部署方人工处理，不自动删除原库。

`IP2Location` 使用 `Enabled`、`DatabasePath` 配置 DB11 MMDB，复用 MaxMind.GeoIP2 的内存映射读取器，不建立 CSV 索引。版本取 MMDB metadata 构建时间。旧 `DataVersion`、`UseContentHashAsVersion` 及 CSV 资源限制仅供保留的旧 CSV 代码使用，不影响当前 MMDB 来源。当前下载包可能仅支持 IPv4，即使 metadata 为 `ip_version=6`，也不会宣称支持 IPv6。名称按库内中文、英文、其他原文顺序返回，城市不补译；邮编、坐标和时区保留实际字段，无 ISP/ASN。系统不作授权判断。

`DbIp` 使用 `Enabled`、`DatabasePath` 配置 DB-IP City Lite MMDB，复用 MaxMind SDK。`Ip2Region` 使用 `Enabled`、`Ipv4DatabasePath`、`Ipv6DatabasePath` 配置 v3 双栈 XDB，`MaxDatabaseBytes` 限制单文件大小。两者默认关闭，由部署方在运行配置中显式开启并提供只读路径；来源版本来自文件内部元数据，当前默认查询库为 `dbip`。

`Ipinfo` 使用 `Enabled`、`DatabasePath` 配置 IPinfo Lite MMDB，默认关闭。使用 MaxMind.Db 底层读取器读取扁平的国家、大洲与 AS 字段，不访问在线 API 或 Token。统一 DTO 包含 `continentCode`、`continent`、`asDomain` 三个可空字段，三种格式及前端同步支持。

公开查询入口为`/?ip=1.1.1.1&db=CZ88&format=json`，省略format默认JSON，省略ip由可信连接识别（如`/?db=all`）。根路径仅在传入ip/db/format时进入API，无参保持首页/纯IP协商；旧`/search/{ip}[/{format}]`及`/api/ip/me`兼容。纯真公开编码为`CZ88`，旧`chunzhen`参数继续兼容。`ProviderCodes`集中映射公开编码与原存储编码，既有版本、指针、上传任务及启停key不搬迁；读取旧排序/默认值后输出新编码，访问记录筛选兼容新旧代码。

`Chunzhen` 使用 `Enabled`、`DatabasePath`、`MaxDatabaseBytes`（默认 256 MiB）、`MaxIndexEntries`（默认一千万）。支持传统 QQWry `.dat` IPv4，GBK 只读内存映射和二分索引，无需密钥或新增依赖；不支持 CZDB 或 IPv6。当前布局要求数据区位于索引前。地区原文保存在 `region`，网络描述保存在 `isp`，不猜测国家、省市或坐标；日期版本保留 `yyyy-MM-dd`，无日期时使用 SHA-256。可从后台上传激活，默认路径为空。

## 持久化与发号

`Database:Enabled=false` 默认使用有界内存存储。只有显式开启并提供 `ConnectionStrings:IpLocation` 的运行配置才选择 PostgreSQL；缺连接配置拒绝启用，写入失败不回退成内存成功。连接凭据应放在仓库外的私有配置或环境变量中，不输出或提交。

`IIdService / YiteIdService` 通过 Yitter.IdGenerator 1.0.14 本地发号。WorkerId配置在 `IdGeneration`，默认位宽范围0..63，每个同域进程独立分配；同一进程多个宿主共用相同Worker的生成器，不反复重置静态助手。业务ID不使用数据库自增，JSON以字符串传输。

公共请求仅非阻塞入队；请求头按固定白名单和容量边界生成快照，保留User-Agent/安全Referer等排障信息，过滤凭据头。来源地址定位在后台日志消费者使用CZ88处理，特殊地址不做公网定位，纯真不可用或不支持时保留明确状态；前台不等待定位。消费者生成业务ID后批量写日志，稳定EventKey去重，仅成功新增事件参与同事务分钟统计。历史读取使用 `IPersistentAccessLogReader.GetSnapshotAsync`，限制行数、时间、超时和保留范围。同步 GetSnapshot 仅是已提交事件的进程窗口，不是PG历史。

请求 path 及查询参数记录复用 `ACCESS_LOG.REQUEST_HEADERS_JSON` 的元数据结构，增加 `requestPath / requestPathTruncated`，不新增列或迁移。路径保留非敏感参数的顺序、重复项和原始转义，敏感参数值脱敏；路径按 JSON 编码后 32 KiB 限额，超限明确标记，请求头原 8 KiB 预算保持。后台访问记录 DTO 以顶层同名字段返回，历史未采集路径为 null、不回填。Referer 仍去除账户、查询串和片段；请求路径可以携带查询目标，但不采集请求体或原始响应，不扩大既有公共路由采集范围。

明细可读保留期默认30天，分钟历史可读范围默认180天；后台基础设置可调整普通访问日志策略。明细保留天数为`null`时不设置年龄读取截止，正数按天限制读取；每次读取仍受31天时间窗口、分页和超时约束。`0`表示保存后停止新增普通访问事件的采集、入队、来源定位和写入，普通分钟统计随该流程停止；已有历史暂留且仍可查询，不执行删除。恢复为`null`或正数后按新策略读取历史并恢复新事件流程。

读取过滤不等于物理删除，自动清理未启用；内存模式另受记录条数和进程寿命限制，不能将`null`宣称为无限容量或持久化保证。初始化、登录、退出及管理变更继续使用独立可靠审计，不受普通访问日志的`0`策略影响。

## 管理员初始化与登录

启动带持久化的站点（需先准备 PostgreSQL 并应用迁移）：

```sh
dotnet run --project backend/src/IpLocation.Api/IpLocation.Api.csproj --no-launch-profile -- --Runtime:SettingsFile "/absolute/path/to/runtime.json"
```

浏览器手工进入 `/console`，未登录时转到独立的 `/console/login`；公共首页不提供后台入口。登录页面不显示管理导航或运行总览。登录成功返回原管理页面或默认总览，退出后回到登录页。接口为 `GET /api/console/access`、`GET /api/console/auth/csrf`、`POST /api/console/auth/login` 与 `POST /api/console/auth/logout`。写请求携带同源防伪 Cookie 和 `X-CSRF-TOKEN`；登录后重新取令牌。后台不强制 HTTPS，白名单内的非回环来源也可通过 HTTP 使用全部管理接口；白名单、身份、限流及防伪检查仍然生效。

会话使用 HttpOnly、SameSite=Strict Cookie，HTTPS 下标记 Secure；每次管理请求重新检查账户启用状态与安全版本。`ConsoleAuth:SessionMinutes` 默认480分钟，后台基础设置可保存管理员会话时长，仅影响后续新登录，已有会话保留其签发时的到期时间。`ConsoleAuth:KeyRingPath` 通过配置指定持久密钥目录。不配置密钥目录时使用临时密钥，不能宣称重启后保留会话。退出递增安全版本，因此会撤销该管理员所有旧会话。

首管理员只能由明确的本地命令创建，不提供公开注册、不在 Web 启动时创建：

```sh
dotnet run --project backend/src/IpLocation.Api/IpLocation.Api.csproj --no-launch-profile -- --Runtime:SettingsFile "/absolute/path/to/runtime.json" --Admin:Initialize=true --Admin:Account=YOUR_ACCOUNT --Admin:PasswordFile=/absolute/private/password-file
```

密码文件必须是绝对路径、非符号链接、仅属主可访问；命令不接受明文密码参数。初始化在同一事务中创建账号和审计，并通过 PostgreSQL 事务锁防止重复或并发创建；已有管理员时拒绝覆盖。命令不创建数据库或应用迁移。

现有管理员重置仅通过本地 CLI 执行，明确指定原账号、新账号及私有密码文件。与初始化互斥，不提供HTTP重置入口：

```sh
dotnet run --project backend/src/IpLocation.Api/IpLocation.Api.csproj --no-launch-profile -- --Runtime:SettingsFile "/absolute/path/to/runtime.json" --Admin:Reset=true --Admin:ExistingAccount=EXISTING_ACCOUNT --Admin:Account=NEW_ACCOUNT --Admin:PasswordFile=/absolute/private/password-file --IdGeneration:WorkerId=63
```

示例WorkerId仅在未被其他同域进程使用时有效；CLI与运行中的Web须分配不同WorkerId。重置保留原账号ID、创建时间及启用状态，递增安全版本撤销旧会话；账号/密码及可靠审计同事务提交。原账号不存在、重名或身份并发变化时拒绝重置；不会创建新账号、修改结构或应用迁移。实际密码不写入源码、命令行或审计。

`access` 明确区分存储不可用503、尚无管理员503、未登录401及有效身份200；存储故障不冒充未初始化。登录按可信来源和规范化账号限流，错误账号及密码使用相同响应，审计提交失败不签发 Cookie。已接入总览、来源管理、访问记录、审计和系统设置接口，未知管理 API 在通过身份检查后返回501。

## 公开 MCP 与调用审计

`/mcp` 使用官方 C# SDK 的无状态 Streamable HTTP，同一宿主提供 `query_ip(ip, db="all")`、`query_my_ip(db="all")` 和 `list_providers`。复用查询服务、可信连接识别与来源快照，不调用本项目自己的HTTP查询接口。公开MCP无需登录或Token、不设来源IP/Origin白名单，允许跨域；自身IP是MCP客户端出口。MCP外层遵循JSON-RPC，工具业务内容保持 `ApiResult`，错误同时区分协议、工具和业务结果，不用HTTP200冒充成功。完整接入见[MCP合同](../docs/api/MCP接口.md)。

三个工具执行后的成功及业务失败结果，在MCP专用ApiResult副本的message末尾追加换行与 `来源：https://ip.tongyaninfo.com`。只序列化一次，structuredContent和唯一text回退复用同一份JSON，保持四字段，不修改查询服务原结果或HTTP JSON/XML/TXT。服务instructions和工具description提示智能体答复末尾引用该来源，但不保证模型遵循；服务来源不替代各数据库原有署名。SDK缺参和协议错误在工具执行前处理时不强加来源行，不新增模型、迁移或额外文本块。

后台 `/api/console/mcp-logs` 固定读取MCP路由记录，继续受来源白名单及管理员身份保护，复用31天单次窗口、分页、超时与普通日志保留设置。受控 `mcpContext` 保存在可空 `ACCESS_LOG.MCP_CONTEXT_JSON jsonb`，读写均清洗；`input / output` JSON 与各自截断标记有界保存，输入 8 KiB、输出 32 KiB、总上下文 48 KiB，敏感 key 脱敏、`_meta` 排除。工具input为实收arguments，其他协议方法为params；output为实际result/error内容，优先structuredContent并去重等价text fallback，不保存完整原始HTTP响应。客户端名称/版本属于自报信息，旧记录新字段为null，不回填；无DDL或迁移。

请求侧保留有界非阻塞入队、后台批写；普通 `accessLogRetentionDays=0` 同时停止MCP新日志及相应统计，关闭时不捕获payload，入队前统一清洗序列化。当前进程队列健康表明缺口，不保证绝不丢失。MCP发现、初始化、目录请求不计作定位查询；识别到query_ip/query_my_ip的工具请求计入逻辑查询，即使参数或HeaderMismatch等协议校验失败，不能将该计数叫作实际来源调用。`/api/console/overview` 包含 `mcpTotals / mcpTrend`，复用同一 PG 分钟聚合的 MCP 类别过滤，HTTP 错误和工具业务错误分开。

MCP 上下文列由 `20261103140118_AddMcpCallContext` 增量迁移新增；启动不自动迁移，迁移与验证见 [SQL 交付](../deploy/sql/README.md)。

## 产品反馈

产品端仅在用户主动提交反馈时使用安装身份与转发链路；服务器端（接收入库、审核与公开回复）不在本开源仓库内，由官方服务提供。本仓库保留完整的客户端与转发实现：

- `vendor/tongyan-support/support-client/`（`Tongyan.Support.Client`）：安装身份（持久随机 GUID + 本地 HMAC 秘密）、签名请求、安装注册、反馈提交与回执查询；安装状态默认保存在 `Support:StateDirectory`（本地应用数据下的 `Tongyan/IpLocation/support`）。
- `Controllers/SupportController.cs`：浏览器同源反馈转发（文字 + 最终截图 + WAV 录音），限制附件类型与大小，并按可信来源限流。

| 配置 | 默认值 | 说明 |
| --- | --- | --- |
| `Support:StateDirectory` | `{LocalApplicationData}/Tongyan/IpLocation/support` | 安装身份目录，必须为绝对路径并随部署一起备份 |
| `Support:ProductVersion` | `unknown` | 可选，上报的产品版本 |
| `Support:Endpoint` | 固定为 `https://api.tongyaninfo.com` | 官方接收端；分叉部署时按需调整源码 |

反馈只在用户主动提交时建立安装身份并发送请求；产品运行期间不自动注册、不上报安装心跳或统计信息，也不包含查询内容、访问地址或任何业务数据。

**测试使用受控本地远端**：运行 `dotnet test` 时反馈测试替换为假远端，不访问官方服务，也不需要关闭任何心跳开关；本机联调提交真实反馈前请自行确认目标服务。

## 后台管理

运行总览按完整窗口在PG聚合请求及延迟，显示当前进程队列健康和统计缺口。访问记录及审计提供受限时间筛选和稳定分页；来源IP和请求头快照仅在后台展示，审计差异再次脱敏。请求头快照通过增量迁移存于 `ACCESS_LOG.REQUEST_HEADERS_JSON` 和 `SOURCE_LOCATION_JSON`（均为可空 jsonb）；旧记录保持null，不回填或删除。

来源启停、排序、上传、校验、激活及系统设置复用已有九表，无新迁移。查询使用内存快照，不在普通请求中查询后台管理配置。来源更换使用新读取器及引用计数，旧请求完成后释放旧句柄；上传文件不原地覆盖、不自动清理。上传授权由人工处理，表单仅提交文件及地址族；新托管版本明确标记manual/未由系统确认授权，系统只作文件技术校验。停用状态同步隐藏前台卡片、数量及页脚署名，后台仍可查看完整来源。数据库顺序保存于SYSTEM_SETTING的provider.displayOrder；后台与公开来源列表、首页卡片及署名遵循同一顺序，重启恢复。

`Uploads:StorageRoot` 默认为当前用户 LocalApplicationData 的 `Tongyan/IpLocation/databases`，必须在仓库及Web根之外；默认单文件1GiB，总占用4GiB。支持原MMDB/XDB及纯真DAT，失败文件也计入容量并保留。配置版本只读引用原文件；新上传版本校验后仍需显式激活。

系统设置允许默认来源、查询截止时间/并发、后台白名单及非敏感基础配置更新，变更与可靠审计同事务，使用Revision检查并发，复用既有`SYSTEM_SETTING`，不新增表或迁移。保存前检查当前连接仍被允许。持久化配置初始化未完成时后台默认关闭，不扩大白名单。

响应新增独立`basic`对象：`accessLogRetentionDays`（整数或`null`）、`retainFullSourceIp`、`sessionMinutes`和`queueCapacity`；保存请求可提交同结构，兼容旧请求省略`basic`时保留已有值。保留天数与完整来源IP开关即时影响保存后的新公共请求，会话分钟影响新登录，队列容量需重启后生效。`runtime.queueCapacity`返回当前实际队列容量，`basic.queueCapacity`为已保存目标值，`runtime.queueCapacityRequiresRestart`明确是否待重启；`runtime.recentEventRetentionDays`可为`null`，`runtime.sessionMinutes`表示当前新登录使用的时长。后台存储模式、可信代理摘要和修订号继续作为只读状态，不提供连接、密钥或代理配置编辑。

普通访问消费者对排队事件再次检查策略，关闭后不将跳过事件计为成功写入。设置保存前已开始提交的批次允许完成；本轮不通过阻塞公共查询等待日志排空，也不删除已有PG历史。基础配置保存本身仍写可靠管理员审计，审计不可用时不会假报保存成功。

接口、错误及准确边界见[后台管理接口](../docs/api/后台管理接口.md)。

## 目录与迁移

`Models/Entities` 保存九实体，`Data/Configurations` 显式映射，`Data/Migrations` 是EF生成物；接口在Service，实现在Service/impl，公共代码在Common。模型使用UTC、inet/cidr/jsonb、同源复合外键、自然复合键和并发Revision。

首个迁移和幂等 SQL 见 [SQL 交付](../deploy/sql/README.md)。独立设计时工厂不读运行秘密、不构建业务宿主或连接数据库；Web 启动不建库、迁移或 seed 账号。

## 静态资源与可读中文JSON

HTTP JSON统一通过Common/ApiJsonEncoding配置语言字符编码，QueryFormatter、MVC及Minimal API/WriteAsJsonAsync都直接输出UTF-8中文，同时保留JSON/HTML敏感字符及控制字符转义；内部持久化和日志JSON不变。

静态文件由 `Common/FrontendStaticAssets` 在后台白名单之后协商构建时生成的 Brotli/Gzip 副本，保留原始 MIME、ETag 与 Vary；公共内容指纹资源长期缓存，后台专属资源保持 no-store。首页、纯 IP 及访客 API 仍禁止共享缓存。
