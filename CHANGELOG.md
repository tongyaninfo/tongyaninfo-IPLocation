# 变更记录

本项目的显著变更记录在此文件中。格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循[语义化版本](https://semver.org/lang/zh-CN/)。

## [Unreleased]

### 新增

- 产品内反馈入口：文字 + 最终截图（标注/遮挡）+ 原始 WAV 录音，经同源 API 转发到官方服务，支持回执查询处理状态。
- `backend/vendor/tongyan-support/` 内嵌 `Tongyan.Support.Client` 源码；服务器端接收实现不在本仓库。
- 后端反馈转发端点；反馈请求按来源限流。

### 变更

- 产品反馈只在用户主动提交时建立安装身份并签名请求；产品运行期间不自动注册、不上报安装心跳或统计信息。
- Docker Compose 提供 `/support` 状态目录与环境变量 `Support__StateDirectory`，与部署一起备份。

### 移除

- 自动安装心跳、后台“产品遥测”设置与 `Support:TelemetryEnabled` 配置；开源首版不包含自动上报能力。


## [0.1.0] - 2026-11-04

首个开源版本。

### 新增

- .NET 10 后端：公开查询协议（根路径纯 IP、`/?ip=&db=&format=`、`/search/{ip}` 兼容）、公开只读 MCP（`query_ip`、`query_my_ip`、`list_providers`）、后台管理 API 与访问统计。
- 六类只读 IP 数据源适配器：DB-IP City Lite、IP2Location LITE DB11、ip2region、IPinfo Lite、纯真社区版 QQWry、MaxMind GeoLite2 City（默认停用）。
- 可信代理头解析、后台来源 IP 白名单、Cookie 管理员登录与写请求防伪、管理员初始化/重置 CLI。
- 异步有界访问日志队列、请求头/请求路径白名单采集、MCP 调用上下文审计、PostgreSQL 分钟聚合统计。
- 九类后台表 EF 模型、迁移与离线幂等 SQL；后台来源启停/排序、上传校验与版本激活、访问记录、审计与系统设置。
- React 19 + TypeScript + Vite 前端：响应式公共首页、MCP 接入说明、后台五个管理页面、自动/浅色/深色主题。
- Docker 多阶段构建、预构建产物打包与 Compose 编排；Cloudflare Tunnel / 反向代理可信地址配置说明。
- 开源工程文件：Apache-2.0 许可、NOTICE 数据源署名、贡献与安全指南、CI、Issue/PR 模板。

### 变更

- 开源默认隐私配置：`AccessLogging:RetainFullSourceIp` 默认 `false`（按网段脱敏），部署方需显式开启才保存完整来源 IP。
- 后台白名单默认值仅保留回环地址与 RFC 5737 文档示例网段，体现开源示例配置。
- 文档与示例中的部署环境、内网地址、私有镜像仓库与个人路径已替换为通用占位值。

### 说明

- IP 数据库文件不随仓库分发；来源许可与署名要求见 [NOTICE](NOTICE) 与[数据源与许可](docs/spec/数据源与许可.md)。
