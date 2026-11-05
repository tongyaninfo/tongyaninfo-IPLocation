# Tongyan.Support 客户端库（vendor）

本目录以源码形式内嵌产品反馈与安装心跳的客户端实现，随本项目一起按 [Apache License 2.0](../../../LICENSE) 发布：

| 项目 | 用途 |
| --- | --- |
| `support-client/`（`Tongyan.Support.Client`） | 持久安装身份、HMAC 签名请求、安装注册、心跳、反馈提交与回执查询 |
| `telemetry-sdk-dotnet/`（`Tongyan.Telemetry`） | 五分钟心跳循环（`HeartbeatRunner`），启动后立即发送一次 |

服务器端接收入库、审核与公开回复不在本仓库，由官方服务提供。构建产物（`bin/`、`obj/`）不提交，由 `dotnet build` 生成。

> 测试与本地验证需设置 `Support__TelemetryEnabled=false`，避免向官方服务注册测试安装；详细配置、上报字段与关闭方式见 [后端说明](../../README.md)。
