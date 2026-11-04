import { useMemo } from 'react';
import { CopyButton } from '../../../components/CopyButton';

/** 仅在记录展开后格式化已保存的JSON；不解析为HTML，也不将截断快照称作完整原始数据。 */
export function McpPayload({ label, value, truncated = false, captured = false }: { label: '入参' | '出参'; value?: unknown | null; truncated?: boolean; captured?: boolean }) {
  // 采集标记保留合法JSON null，历史缺少标记的空值仍显示未采集。
  const text = useMemo(() => captured || value != null ? JSON.stringify(value ?? null, null, 2) : null, [value, captured]);
  return <section className="console-mcp-payload" aria-label={`MCP ${label}详情`}>
    <div className="console-mcp-payload-heading"><h4>{label} JSON</h4>{text != null ? <CopyButton value={text} label={`复制已记录${label}`} /> : null}</div>
    {text == null ? <p className="console-muted">未采集{label}，历史记录不会回填。</p> : <>
      <p className="console-muted">内容为服务端保存的受控快照；敏感字段以替换标记显示，复制内容与当前记录一致。</p>
      {truncated ? <p className="console-headers-notice" role="note">{label}已截断，当前 JSON 不是完整原始{label}。</p> : null}
      <pre tabIndex={0}>{text}</pre>
    </>}
  </section>;
}
