# Docker Compose 部署

本目录提供 IP 聚合查询的完整 Docker 编排：API（.NET 10，内置前端静态文件）与 PostgreSQL 17。

## 文件

| 文件 | 用途 |
|---|---|
| [Dockerfile](Dockerfile) | 多阶段构建（Node 24 构建前端 → .NET 10 SDK 发布 → ASP.NET 运行时），供可联网构建机 / CI 使用 |
| [Dockerfile.prebuilt](Dockerfile.prebuilt) | 产物打包（构建上下文含 `app/`：publish 产物 + `frontend/dist`），供目标机无法访问 npm/NuGet 时使用 |
| [compose.yaml](compose.yaml) | 完整编排：api + postgres，含数据卷、端口与环境变量映射 |

## 部署目录布局

以下为示例布局，路径可自行调整（相对路径以 `compose.yaml` 所在目录为基准）：

```text
/opt/iplocation/
├── compose.yaml            # 从本目录复制
├── .env                    # IPLOCATION_DB_PASSWORD、IPLOCATION_API_TAG（建议 600 权限）
├── Dockerfile.prebuilt
├── app/                    # prebuilt 构建上下文：publish 产物 + frontend/dist
├── data/                   # 数据源只读挂载：dbip/、ip2location/、ip2region/、ipinfo/、cz88/
├── sql/                    # 首次初始化 SQL（按文件名排序执行）
├── keys/                   # 会话密钥与管理员密码文件（600）
├── uploads/                # 后台来源上传目录
└── pgdata/                 # PostgreSQL 数据（首次启动创建）
```

## 构建镜像

方式一（构建机可访问 npm/NuGet，构建上下文为仓库根）：

```sh
docker build -f deploy/docker/Dockerfile -t registry.example.com/tongyaninfo/iplocation-api:<tag> .
docker push registry.example.com/tongyaninfo/iplocation-api:<tag>
```

方式二（目标机网络受限，使用 prebuilt 产物打包）：

```sh
# 在能联网的机器：npm run build、dotnet publish -c Release -o app；
# 组织构建目录：Dockerfile.prebuilt + app/（含 frontend/dist）
docker build -f Dockerfile.prebuilt -t registry.example.com/tongyaninfo/iplocation-api:<tag> .
docker push registry.example.com/tongyaninfo/iplocation-api:<tag>
```

也可直接构建本地镜像而不推送；此时将 `compose.yaml` 中的 `image` 改为本地标签，或使用 `build:` 段。

## 启动与初始化

```sh
cd /opt/iplocation
docker compose -f compose.yaml up -d postgres   # 首次启动会执行 sql/ 下初始化脚本
docker compose -f compose.yaml up -d api        # postgres healthy 后启动

# 首次部署初始化管理员（密码文件使用绝对路径，WorkerId 与 Web 进程区分）
docker compose -f compose.yaml run --rm api \
  --Admin:Initialize=true --Admin:Account=<账号> --Admin:PasswordFile=/keys/admin-password --IdGeneration:WorkerId=63
```

## 运行注意

- 默认查询库为 `dbip`；`/?ip=1.1.1.1&db=all` 查询全部启用就绪来源，省略 `ip` 则识别当前连接地址；GeoLite2 默认停用，实际启用状态以后台保存值为准。
- 来源启停、排序、上传激活由后台「IP数据库」页管理（写 SYSTEM_SETTING 与版本表）；环境变量只提供初始基线与文件路径。
- 数据源文件以只读卷挂载到 `/data`；目录 `data/`、`keys/`、`uploads/` 需要事先准备，其中数据源文件请自行按各来源许可下载。
- 后台入口受来源 IP 白名单限制。`appsettings.json` 中的 `192.0.2.0/24`、`198.51.100.0/24` 是 RFC 5737 文档示例网段，请在后台或配置中替换为实际管理网段。从目标机本机 `127.0.0.1` 访问时，后端看到的可能是 Docker 网关地址，会被白名单拒绝，需从白名单网段访问。
- 凭据不写入仓库与 compose 文件：数据库密码在部署目录 `.env`，管理员密码文件在 `keys/`。
- `pgdata/` 卷包含数据库数据，不要用 `docker compose down -v` 清理；数据库结构与数据变更前请先备份。

## Cloudflare Tunnel 与可信代理

若在 Docker 前置了 Cloudflare Tunnel（或 nginx 等反向代理），应用需要配置可信代理才能识别真实访客 IP。应用使用 `ForwardedHeaders`，只需相信直接代理的精确地址，并限制只读取最右侧一跳 `X-Forwarded-For`；`X-Forwarded-Proto` 同时恢复外层协议。代理网关变化时通过 `IPLOCATION_TRUSTED_PROXY_ADDRESS` 覆盖，不要信任整个容器或私有网段。

`IPLOCATION_API_BIND_ADDRESS` 控制宿主监听地址，默认 `0.0.0.0`。若 cloudflared 运行在宿主上且只允许 Tunnel 访问，建议设为 `127.0.0.1`；保留其他监听方式时需用防火墙限制源站入口，避免绕过隧道到达可信转发路径：

```sh
IPLOCATION_TRUSTED_PROXY_ADDRESS="YOUR_DIRECT_PROXY_IP" \
IPLOCATION_API_BIND_ADDRESS=127.0.0.1 \
docker compose -f compose.yaml up -d --no-deps api
```

更新镜像和配置后，从 Tunnel 与受控直连接口分别验证：根路径纯 IP、`/?db=all`、显式 IP 的三种格式、IPv4/IPv6、伪造头拒收及后台白名单。若无法访问后台，先确认来源地址在白名单内；后台入口需手工访问 `/console`。

## 构建产物注意

- 构建前端时使用本次全新产出的 `dist/` 目录；Brotli/Gzip 副本需与对应原文一并打包，由服务端按请求协商，不要只上传压缩副本或改写页面为 `.br/.gz` 地址。
- 生产 JavaScript/CSS 目标为 Chrome 79，避免可选链、空值合并和逻辑赋值出现在入口或懒加载脚本中；构建脚本会检查兼容性并在不通过时停止。
