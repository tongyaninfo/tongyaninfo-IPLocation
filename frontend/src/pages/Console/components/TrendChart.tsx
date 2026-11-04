import { memo, useEffect, useMemo, useRef, useState } from 'react';
import type { KeyboardEvent, PointerEvent } from 'react';
import { NoRecords, numberText, timeText } from '../ConsoleShared';

export interface TrendPoint { timeUtc: string; requests: number; queryRequests: number; errors: number }
interface TrendChartProps {
  points: TrendPoint[]; fromUtc: string; toUtc: string;
  title: string; requestLabel: string; queryLabel: string; emptyText: string;
}
interface Cursor {
  x: number; y: number; index: number; localX: number; localY: number;
  plotLeft: number; plotBottom: number; width: number; height: number; keyboard: boolean;
}
const width = 780, height = 235, left = 38, right = 18, top = 20, bottom = 31;
const plotWidth = width - left - right, plotHeight = height - top - bottom;
const clamp = (value: number, minimum: number, maximum: number) => Math.max(minimum, Math.min(maximum, value));

function geometry(points: TrendPoint[], fromUtc: string, toUtc: string) {
  const start = Date.parse(fromUtc), end = Date.parse(toUtc);
  const maximum = points.reduce((value, point) => Math.max(value, point.requests, point.queryRequests), 1);
  const x = (timeUtc: string) => left + clamp((Date.parse(timeUtc) - start) / Math.max(1, end - start), 0, 1) * plotWidth;
  const y = (value: number) => height - bottom - value / maximum * plotHeight;
  return { points, start, end, maximum, x, y, positions: points.map(point => x(point.timeUtc)) };
}
type Geometry = ReturnType<typeof geometry>;

/** 曲线与网格只在数据变化时重绘；长窗口省略密集小圆点，悬浮仍读取每个真实统计桶。 */
const StaticTrend = memo(function StaticTrend({ graph, requestLabel, queryLabel }: { graph: Geometry; requestLabel: string; queryLabel: string }) {
  const line = (key: 'requests' | 'queryRequests') => graph.points.map((point, index) => `${graph.positions[index]},${graph.y(point[key])}`).join(' ');
  return <>
    {[0, .25, .5, .75, 1].map(ratio => <g key={ratio}>
      <line x1={left} x2={width - right} y1={graph.y(graph.maximum * ratio)} y2={graph.y(graph.maximum * ratio)} stroke="#e8eef5" strokeDasharray="4 5" />
      <text x={left - 9} y={graph.y(graph.maximum * ratio) + 4} textAnchor="end">{Math.round(graph.maximum * ratio)}</text>
    </g>)}
    <polyline points={line('requests')} fill="none" stroke="#013187" strokeWidth="2.7" strokeLinejoin="round" />
    <polyline points={line('queryRequests')} fill="none" stroke="#EF8200" strokeWidth="2" strokeLinejoin="round" />
    {graph.points.length <= 240 ? graph.points.map((point, index) => <g key={point.timeUtc}>
      <circle data-series="requests" cx={graph.positions[index]} cy={graph.y(point.requests)} r="2.6" fill="#013187"><title>{timeText(point.timeUtc)} · {requestLabel} {point.requests}</title></circle>
      <circle cx={graph.positions[index]} cy={graph.y(point.queryRequests)} r="2.2" fill="#EF8200"><title>{timeText(point.timeUtc)} · {queryLabel} {point.queryRequests}</title></circle>
    </g>) : null}
    {[0, .5, 1].map(ratio => <text key={ratio} x={left + ratio * plotWidth} y={height - 7} textAnchor={ratio === 0 ? 'start' : ratio === 1 ? 'end' : 'middle'}>{axisTime(graph.start + ratio * (graph.end - graph.start), graph.end - graph.start)}</text>)}
  </>;
});

function axisTime(timestamp: number, duration: number) {
  return new Date(timestamp).toLocaleString('zh-CN', {
    timeZone: 'Asia/Shanghai', hour12: false,
    ...(duration >= 86400000 ? { month: '2-digit' as const, day: '2-digit' as const } : {}),
    hour: '2-digit', minute: '2-digit',
  });
}
function nearestIndex(positions: number[], x: number) {
  let low = 0, high = positions.length - 1;
  while (low < high) { const middle = Math.floor((low + high) / 2); if (positions[middle] < x) low = middle + 1; else high = middle; }
  return low > 0 && x - positions[low - 1] <= positions[low] - x ? low - 1 : low;
}

/** SVG 留白和缩放均由矩阵处理；无矩阵的测试环境才使用相同比例的几何回退。 */
function coordinates(svg: SVGSVGElement, x: number, y: number, inverse = false) {
  const matrix = svg.getScreenCTM?.();
  if (matrix && typeof svg.createSVGPoint === 'function') {
    const point = svg.createSVGPoint(); point.x = x; point.y = y;
    return point.matrixTransform(inverse ? matrix.inverse() : matrix);
  }
  const rect = svg.getBoundingClientRect();
  const scale = Math.min(rect.width / width, rect.height / height);
  if (!scale) return null;
  const offsetX = rect.left + (rect.width - width * scale) / 2;
  const offsetY = rect.top + (rect.height - height * scale) / 2;
  return inverse ? { x: (x - offsetX) / scale, y: (y - offsetY) / scale } : { x: offsetX + x * scale, y: offsetY + y * scale };
}

export function TrendChart({ points, fromUtc, toUtc, title, requestLabel, queryLabel, emptyText }: TrendChartProps) {
  const graph = useMemo(() => geometry(points, fromUtc, toUtc), [points, fromUtc, toUtc]);
  const [cursor, setCursor] = useState<Cursor | null>(null);
  const svg = useRef<SVGSVGElement>(null);
  const frame = useRef<number | null>(null);
  const pending = useRef<Cursor | null>(null);
  useEffect(() => { if (frame.current !== null) cancelAnimationFrame(frame.current); frame.current = null; pending.current = null; setCursor(null); }, [graph]);
  useEffect(() => () => { if (frame.current !== null) cancelAnimationFrame(frame.current); }, []);
  function clearCursor() {
    if (frame.current !== null) cancelAnimationFrame(frame.current);
    frame.current = null; pending.current = null; setCursor(null);
  }
  function makeCursor(x: number, y: number, keyboard = false): Cursor | null {
    if (!svg.current || !points.length) return null;
    const chart = svg.current, rect = chart.getBoundingClientRect();
    x = clamp(x, left, width - right); y = clamp(y, top, height - bottom);
    const position = coordinates(chart, x, y), lowerLeft = coordinates(chart, left, height - bottom);
    if (!position || !lowerLeft) return null;
    return { x, y, index: nearestIndex(graph.positions, x), localX: position.x - rect.left, localY: position.y - rect.top, plotLeft: lowerLeft.x - rect.left, plotBottom: lowerLeft.y - rect.top, width: rect.width, height: rect.height, keyboard };
  }
  function move(event: PointerEvent<SVGSVGElement>) {
    const position = coordinates(event.currentTarget, event.clientX, event.clientY, true);
    if (!position) return;
    pending.current = makeCursor(position.x, position.y);
    // 指针连续移动最多每帧提交一次，静态曲线组件不随十字线状态重新生成。
    if (frame.current === null) frame.current = requestAnimationFrame(() => { setCursor(pending.current); frame.current = null; });
  }
  function keyDown(event: KeyboardEvent<SVGSVGElement>) {
    if (event.key === 'Escape') { clearCursor(); return; }
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key) || !points.length) return;
    event.preventDefault();
    if (frame.current !== null) cancelAnimationFrame(frame.current); frame.current = null;
    const index = event.key === 'Home' ? 0 : event.key === 'End' ? points.length - 1
      : cursor ? clamp(cursor.index + (event.key === 'ArrowRight' ? 1 : -1), 0, points.length - 1) : event.key === 'ArrowLeft' ? points.length - 1 : 0;
    setCursor(makeCursor(graph.positions[index], graph.y(points[index].requests), true));
  }
  if (!points.length) return <div className="console-chart-empty"><NoRecords text={emptyText} /></div>;
  const selected = cursor ? points[cursor.index] : null;
  const coordinateTime = cursor ? graph.start + (cursor.x - left) / plotWidth * (graph.end - graph.start) : 0;
  const coordinateValue = cursor ? (height - bottom - cursor.y) / plotHeight * graph.maximum : 0;
  const tooltipWidth = cursor ? Math.min(230, Math.max(1, cursor.width - 16)) : 0;
  return <div className="console-trend">
    <div className="console-trend-plot">
      <svg ref={svg} viewBox={`0 0 ${width} ${height}`} role="img" tabIndex={0}
        aria-label={`${title}，${points.length}个已记录统计时间点，最高${graph.maximum}次；按实际时间间隔展示，未记录区间不补零；左右方向键查看统计点`}
        onPointerDown={move} onPointerMove={move} onPointerLeave={event => { if (event.pointerType !== 'touch') clearCursor(); }} onPointerCancel={clearCursor} onKeyDown={keyDown} onBlur={clearCursor}>
        <StaticTrend graph={graph} requestLabel={requestLabel} queryLabel={queryLabel} />
        {cursor && selected ? <g className="console-chart-crosshair" pointerEvents="none" aria-hidden="true">
          <line x1={cursor.x} x2={cursor.x} y1={top} y2={height - bottom} />
          <line x1={left} x2={width - right} y1={cursor.y} y2={cursor.y} />
          <circle cx={graph.positions[cursor.index]} cy={graph.y(selected.requests)} r="4" fill="#013187" />
          <circle cx={graph.positions[cursor.index]} cy={graph.y(selected.queryRequests)} r="4" fill="#EF8200" />
        </g> : null}
      </svg>
      {cursor && selected ? <div className="console-chart-readouts" aria-live={cursor.keyboard ? 'polite' : 'off'}>
        <div className="console-chart-axis-value console-chart-axis-x" style={{ left: clamp(cursor.localX - 80, 0, Math.max(0, cursor.width - 160)), top: cursor.plotBottom + 2 }} data-testid="trend-x-value">X：{axisTime(coordinateTime, graph.end - graph.start)}</div>
        <div className="console-chart-axis-value console-chart-axis-y" style={{ left: Math.max(0, cursor.plotLeft - 56), top: clamp(cursor.localY - 11, 0, Math.max(0, cursor.height - 22)) }} data-testid="trend-y-value">Y：{coordinateValue.toLocaleString('zh-CN', { maximumFractionDigits: 2 })}</div>
        <div className="console-chart-tooltip" role="tooltip" style={{ width: tooltipWidth, left: clamp(cursor.localX + 14, 8, Math.max(8, cursor.width - tooltipWidth - 8)), top: clamp(cursor.localY - 115, 4, Math.max(4, cursor.plotBottom - 115)) }}>
          <strong>最近统计桶 · {timeText(selected.timeUtc)}</strong>
          <span><i />{requestLabel}<b>{numberText(selected.requests)}</b></span>
          <span><i className="orange" />{queryLabel}<b>{numberText(selected.queryRequests)}</b></span>
        </div>
      </div> : null}
    </div>
    <div className="console-chart-legend"><span><i />{requestLabel}</span><span><i className="orange" />{queryLabel}</span><span>Asia/Shanghai · 次数</span></div>
    <p className="console-chart-caption">仅连接已记录统计点，未记录区间不补零。</p>
  </div>;
}
