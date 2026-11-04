import { CopyButton } from './CopyButton';
import { useEffect } from 'react';

const endpoint = 'https://ip.tongyaninfo.com/mcp';
const clientConfiguration = JSON.stringify({ mcpServers: { 'tongyan-ip': { type: 'http', url: endpoint } } }, null, 2);
const queryExample = JSON.stringify({ jsonrpc: '2.0', id: 2, method: 'tools/call', params: { name: 'query_ip', arguments: { ip: '1.1.1.1', db: 'all' } } }, null, 2);
// 完整说明与页面复用同一地址和配置，避免智能体复制入口与手动接入信息漂移。
const connectionInstructions = `请为我的智能体添加以下远程 MCP 服务：
服务名称：tongyan-ip
传输方式：Streamable HTTP
服务地址：${endpoint}
认证方式：无需登录、API Key 或 Token。

请按当前客户端支持的 MCP 配置方式添加。如果不能自动配置，请告诉我如何手动添加。
配置 JSON：
${clientConfiguration}

可用工具：
- query_ip：传入单个 ip（IPv4 / IPv6），可选 db，默认 all。
- query_my_ip：查询 MCP 客户端本次连接的出口 IP，可选 db，默认 all；云端客户端返回的可能是云端服务出口。
- list_providers：查看来源启用、就绪状态、地址族、版本与署名，无需参数。
IP 归属地以各来源实际数据为准，不代表精确位置。`;

/** 只提供公开接入说明，浏览器首页不自动建立 MCP 会话或发送工具调用。 */
export function McpConnection({ onReady }: { onReady?: () => void } = {}) {
  useEffect(() => { onReady?.(); }, [onReady]);
  return <div className="mcp-panels">
    <section className="api-panel mcp-panel" aria-label="MCP 连接配置">
      <div className="mcp-panel-heading"><h3>添加远程 MCP 服务</h3><CopyButton value={connectionInstructions} label="复制接入说明" tooltip="复制这段内容到您的智能体中添加 MCP 服务" /></div>
      <p>传输方式选择 <strong>Streamable HTTP</strong>，服务地址填写：</p>
      <div className="code-block"><code>{endpoint}</code><CopyButton value={endpoint} label="复制地址" /></div>
      <p>无需登录、API Key 或 Token，公开接入，不限制调用来源 IP 或 Origin。查询默认使用 <code>all</code>，按各来源实际启用和就绪状态返回结果。</p>
      <h4>客户端配置示例</h4>
      <div className="code-block mcp-code"><pre><code>{clientConfiguration}</code></pre><CopyButton value={clientConfiguration} label="复制配置" /></div>
      <p className="mcp-caption">点击右上角“复制接入说明”，粘贴给支持 MCP 配置的智能体添加服务；无法自动配置时，可手动复制 JSON 到客户端设置。配置格式、字段名称和远程 HTTP 支持以客户端要求为准。</p>
    </section>
    <section className="api-panel mcp-panel" aria-label="MCP 工具与调用示例">
      <h3>可用工具</h3>
      <dl className="mcp-tools">
        <div><dt><code>query_ip</code></dt><dd>传入单个 <code>ip</code>（IPv4 / IPv6）；可选 <code>db</code> 指定来源，默认 <code>all</code>。</dd></div>
        <div><dt><code>query_my_ip</code></dt><dd>识别 MCP 客户端本次连接的出口 IP 并查询；可选 <code>db</code>，默认 <code>all</code>。</dd></div>
        <div><dt><code>list_providers</code></dt><dd>查看各数据源的启用、就绪状态、地址族、版本与署名，无需参数。</dd></div>
      </dl>
      <p>“我的 IP”来自 MCP 客户端连接；客户端运行在云端时，识别到的可能是云端服务出口地址。</p>
      <h4>工具调用示例</h4>
      <div className="code-block mcp-code"><pre><code>{queryExample}</code></pre><CopyButton value={queryExample} label="复制调用示例" /></div>
      <p className="mcp-caption">连接初始化由 MCP 客户端完成。工具结果提供结构化 JSON，业务内容沿用 <code>success / code / message / data</code>，保留各来源状态与实际字段。IP 归属地不代表精确位置。</p>
    </section>
  </div>;
}
