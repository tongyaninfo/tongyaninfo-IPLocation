import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { RequestTrend } from '../src/pages/Console/OverviewPage';
import type { TrendPoint } from '../src/pages/Console/components/TrendChart';

const fromUtc = '2026-11-03T00:00:00Z', toUtc = '2026-11-03T12:00:00Z';
const points: TrendPoint[] = [[0, 2, 1], [10, 10, 4], [11, 6, 3]].map(([hour, requests, queryRequests]) => ({ timeUtc: new Date(Date.UTC(2026, 10, 3, hour)).toISOString(), requests, queryRequests, errors: 0 }));
function installMatrix(svg: SVGSVGElement) {
  const inverse = { isInverse: true };
  const matrix = { isInverse: false, inverse: vi.fn(() => inverse) };
  Object.defineProperty(svg, 'getBoundingClientRect', { configurable: true, value: () => ({ x: 100, y: 50, left: 100, top: 50, right: 1660, bottom: 520, width: 1560, height: 470 }) });
  Object.defineProperty(svg, 'getScreenCTM', { configurable: true, value: () => matrix });
  Object.defineProperty(svg, 'createSVGPoint', { configurable: true, value: () => ({ x: 0, y: 0, matrixTransform(transform: { isInverse: boolean }) { return transform.isInverse ? { x: (this.x - 100) / 2, y: (this.y - 50) / 2 } : { x: this.x * 2 + 100, y: this.y * 2 + 50 }; } }) });
  return matrix;
}
function pointer(svg: SVGSVGElement, type: string, x: number, y: number, pointerType = 'mouse') {
  // React的合成pointerleave由原生pointerout推导，测试需模拟实际浏览器事件。
  const event = new MouseEvent(type === 'pointerleave' ? 'pointerout' : type, { bubbles: true, clientX: 100 + x * 2, clientY: 50 + y * 2, relatedTarget: type === 'pointerleave' ? document.body : null });
  Object.defineProperty(event, 'pointerType', { value: pointerType });
  fireEvent(svg, event);
}

describe('共用SVG趋势十字线', () => {
  it('使用SVG逆矩阵跟随连续坐标，XY轴值与最近真实桶分开，不在稀疏时间里插值统计', async () => {
    const { container } = render(<RequestTrend points={points} fromUtc={fromUtc} toUtc={toUtc} />);
    const svg = screen.getByRole('img', { name: /公共请求趋势/ }) as unknown as SVGSVGElement;
    const matrix = installMatrix(svg);
    const originalLine = container.querySelector('polyline');
    pointer(svg, 'pointermove', 400, 112);
    const tooltip = await screen.findByRole('tooltip');
    expect(matrix.inverse).toHaveBeenCalled();
    expect(screen.getByTestId('trend-x-value').textContent).toContain('14:00');
    expect(screen.getByTestId('trend-y-value').textContent).toBe('Y：5');
    expect(within(tooltip).getByText(/18:00:00/)).not.toBeNull();
    expect(within(tooltip).getByText('10')).not.toBeNull();
    expect(within(tooltip).getByText('4')).not.toBeNull();
    const lines = container.querySelectorAll('.console-chart-crosshair line');
    expect(lines).toHaveLength(2);
    expect(lines[0].getAttribute('x1')).toBe('400');
    expect(lines[1].getAttribute('y1')).toBe('112');
    expect(container.querySelector('polyline')).toBe(originalLine);
    expect(container.querySelectorAll('circle[data-series="requests"]')).toHaveLength(3);
    pointer(svg, 'pointerleave', 400, 112);
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
  });

  it('边界外指针坐标受绘图区约束，触屏离开后保留读数，取消时清除', async () => {
    const { container } = render(<RequestTrend points={points} fromUtc={fromUtc} toUtc={toUtc} />);
    const svg = screen.getByRole('img') as unknown as SVGSVGElement; installMatrix(svg);
    pointer(svg, 'pointerdown', -100, 999, 'touch');
    await screen.findByRole('tooltip');
    expect(container.querySelector('.console-chart-crosshair line')?.getAttribute('x1')).toBe('38');
    expect(screen.getByTestId('trend-y-value').textContent).toBe('Y：0');
    pointer(svg, 'pointerleave', 0, 0, 'touch');
    expect(screen.getByRole('tooltip')).not.toBeNull();
    pointer(svg, 'pointercancel', 0, 0, 'touch');
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
  });

  it('方向键和Home/End选择实际统计点，Escape和失焦清除，不依赖鼠标', async () => {
    render(<RequestTrend points={points} fromUtc={fromUtc} toUtc={toUtc} />);
    const svg = screen.getByRole('img') as unknown as SVGSVGElement; installMatrix(svg);
    fireEvent.keyDown(svg, { key: 'Home' });
    expect(within(screen.getByRole('tooltip')).getByText('2')).not.toBeNull();
    fireEvent.keyDown(svg, { key: 'ArrowRight' });
    expect(within(screen.getByRole('tooltip')).getByText('10')).not.toBeNull();
    fireEvent.keyDown(svg, { key: 'End' });
    expect(within(screen.getByRole('tooltip')).getByText('6')).not.toBeNull();
    fireEvent.keyDown(svg, { key: 'Escape' }); expect(screen.queryByRole('tooltip')).toBeNull();
    fireEvent.keyDown(svg, { key: 'ArrowLeft' });
    expect(within(screen.getByRole('tooltip')).getByText('6')).not.toBeNull();
    fireEvent.blur(svg); expect(screen.queryByRole('tooltip')).toBeNull();
  });

  it('空数据不生成虚构趋势，单个零值统计点仍可查询并明确为零', () => {
    const { rerender } = render(<RequestTrend points={[]} fromUtc={fromUtc} toUtc={toUtc} />);
    expect(screen.queryByRole('img', { name: /公共请求趋势/ })).toBeNull();
    expect(screen.getByText('当前时间范围暂无访问趋势')).not.toBeNull();
    rerender(<RequestTrend points={[{ ...points[0], requests: 0, queryRequests: 0 }]} fromUtc={fromUtc} toUtc={toUtc} />);
    const svg = screen.getByRole('img', { name: /公共请求趋势/ }) as unknown as SVGSVGElement; installMatrix(svg);
    fireEvent.keyDown(svg, { key: 'Home' });
    expect(within(screen.getByRole('tooltip')).getAllByText('0')).toHaveLength(2);
  });

  it('长窗口保留全部曲线点但不创建大量圆点，数据切换清除旧的悬浮信息', async () => {
    const many = Array.from({ length: 1000 }, (_, index) => ({ timeUtc: new Date(Date.parse(fromUtc) + index * 60000).toISOString(), requests: index, queryRequests: 1, errors: 0 }));
    const { container, rerender } = render(<RequestTrend points={many} fromUtc={fromUtc} toUtc="2026-11-04T00:00:00Z" />);
    expect(container.querySelectorAll('circle[data-series="requests"]')).toHaveLength(0);
    expect(container.querySelector('polyline')?.getAttribute('points')?.split(' ')).toHaveLength(1000);
    const svg = screen.getByRole('img') as unknown as SVGSVGElement; installMatrix(svg);
    fireEvent.keyDown(svg, { key: 'End' }); expect(within(screen.getByRole('tooltip')).getByText('999')).not.toBeNull();
    rerender(<RequestTrend points={points} fromUtc={fromUtc} toUtc={toUtc} />);
    await waitFor(() => expect(screen.queryByRole('tooltip')).toBeNull());
  });
});
