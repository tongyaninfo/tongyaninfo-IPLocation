import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react';
import { Skeleton } from 'antd';
import type { ProviderDescriptor } from '../types/ip-location';

const ApiExamples = lazy(() => import('./ApiExamples').then(module => ({ default: module.ApiExamples })));
const McpConnection = lazy(() => import('./McpConnection').then(module => ({ default: module.McpConnection })));

/** 首屏以下的接口示例接近视口时再加载，公共查询不等待标签页组件。 */
export function DeferredApiExamples({ providers, queryReady = true }: { providers: ProviderDescriptor[]; queryReady?: boolean }) {
  const element = useRef<HTMLDivElement>(null);
  const [visible, setVisible] = useState(() => ['#api', '#mcp'].includes(window.location.hash));
  const [apiReady, setApiReady] = useState(false);
  const [mcpReady, setMcpReady] = useState(false);
  const [anchorRevision, setAnchorRevision] = useState(0);
  const positionedAnchor = useRef(-1);
  const markApiReady = useCallback(() => setApiReady(true), []);
  const markMcpReady = useCallback(() => setMcpReady(true), []);
  useEffect(() => {
    // 锚点在懒加载前也存在；直接打开或点击接入链接时立即加载说明。
    const openAnchor = () => { if (['#api', '#mcp'].includes(window.location.hash)) { setVisible(true); setAnchorRevision(value => value + 1); } };
    window.addEventListener('hashchange', openAnchor);
    return () => window.removeEventListener('hashchange', openAnchor);
  }, []);
  useEffect(() => {
    if (!apiReady || !mcpReady || !queryReady || positionedAnchor.current === anchorRevision || !['#api', '#mcp'].includes(window.location.hash)) return;
    // React 初次渲染时浏览器可能已经错过原生锚点。等待查询和两块说明稳定后再定位，避免骨架替换使目标偏移。
    const frame = requestAnimationFrame(() => {
      const target = document.getElementById(window.location.hash.slice(1));
      if (target && typeof target.scrollIntoView === 'function') {
        target.scrollIntoView({ block: 'start', behavior: 'instant' });
        positionedAnchor.current = anchorRevision;
      }
    });
    return () => cancelAnimationFrame(frame);
  }, [apiReady, mcpReady, queryReady, anchorRevision]);
  useEffect(() => {
    if (!('IntersectionObserver' in window)) { setVisible(true); return; }
    const observer = new IntersectionObserver(entries => {
      if (entries.some(entry => entry.isIntersecting)) {
        setVisible(true);
        observer.disconnect();
      }
    }, { rootMargin: '300px' });
    if (element.current) observer.observe(element.current);
    return () => observer.disconnect();
  }, []);
  const placeholder = <section id="api" className="api-section" aria-label="接口示例加载区域"><div className="section-heading"><div><div className="eyebrow">BUILT FOR DEVELOPERS</div><h2>查询，也可以只用一行命令。</h2></div></div><div className="api-panel api-placeholder"><Skeleton active paragraph={{ rows: 3 }} /></div></section>;
  return <div ref={element}>{visible ? <Suspense fallback={placeholder}><ApiExamples providers={providers} onReady={markApiReady} /></Suspense> : placeholder}
    <section className="api-section mcp-section" id="mcp" aria-labelledby="mcp-heading">
      <div className="section-heading"><div><div className="eyebrow">MODEL CONTEXT PROTOCOL</div><h2 id="mcp-heading">让 AI 工具接入 IP 查询。</h2><p>公开 MCP 接口，复用首页的多来源查询能力。</p></div></div>
      {visible ? <Suspense fallback={<div className="api-panel api-placeholder"><Skeleton active paragraph={{ rows: 4 }} /></div>}><McpConnection onReady={markMcpReady} /></Suspense> : <div className="api-panel api-placeholder"><Skeleton active paragraph={{ rows: 4 }} /></div>}
    </section>
  </div>;
}
