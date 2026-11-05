import { useEffect, useRef, useState } from 'react';
import type { PointerEvent } from 'react';

type Tool = 'mask' | 'pen' | 'rectangle' | 'arrow';

/** 遮挡直接覆盖最终像素；上传只取此画布，原图与撤销历史不会发送。 */
export function ImageEditor({ source, onSave, onCancel }: {
  source: Blob;
  onSave: (image: Blob) => void;
  onCancel: () => void;
}) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const history = useRef<ImageData[]>([]);
  const start = useRef<{ x: number; y: number; image: ImageData } | null>(null);
  const [tool, setTool] = useState<Tool>('mask');
  const [revision, setRevision] = useState(0);
  const [error, setError] = useState('');
  useEffect(() => {
    let active = true;
    const url = URL.createObjectURL(source);
    const image = new Image();
    image.onload = () => {
      if (!active || !canvas.current) return;
      const scale = Math.min(1, 2560 / Math.max(image.width, image.height));
      canvas.current.width = Math.round(image.width * scale);
      canvas.current.height = Math.round(image.height * scale);
      canvas.current.getContext('2d')?.drawImage(image, 0, 0, canvas.current.width, canvas.current.height);
      history.current = [];
      setRevision(value => value + 1);
    };
    image.onerror = () => { if (active) setError('图片无法读取，请使用 PNG 或 JPEG。'); };
    image.src = url;
    return () => { active = false; URL.revokeObjectURL(url); };
  }, [source]);
  function position(event: PointerEvent<HTMLCanvasElement>) {
    const rect = event.currentTarget.getBoundingClientRect();
    return { x: (event.clientX - rect.left) * event.currentTarget.width / rect.width,
      y: (event.clientY - rect.top) * event.currentTarget.height / rect.height };
  }
  function paint(event: PointerEvent<HTMLCanvasElement>) {
    const state = start.current;
    const context = canvas.current?.getContext('2d');
    if (!state || !context || !canvas.current) return;
    const point = position(event);
    const width = Math.max(3, canvas.current.width / 240);
    context.lineWidth = width;
    context.lineCap = 'round';
    context.strokeStyle = '#ed2727';
    if (tool === 'pen') {
      context.lineTo(point.x, point.y); context.stroke();
    } else {
      context.putImageData(state.image, 0, 0);
      if (tool === 'mask') {
        context.fillStyle = '#000000';
        context.fillRect(state.x, state.y, point.x - state.x, point.y - state.y);
      } else if (tool === 'rectangle') context.strokeRect(state.x, state.y, point.x - state.x, point.y - state.y);
      else {
        const angle = Math.atan2(point.y - state.y, point.x - state.x);
        const length = width * 5;
        context.beginPath(); context.moveTo(state.x, state.y); context.lineTo(point.x, point.y);
        context.lineTo(point.x - length * Math.cos(angle - Math.PI / 6), point.y - length * Math.sin(angle - Math.PI / 6));
        context.moveTo(point.x, point.y);
        context.lineTo(point.x - length * Math.cos(angle + Math.PI / 6), point.y - length * Math.sin(angle + Math.PI / 6));
        context.stroke();
      }
    }
  }
  return <section className="ty-feedback-editor" aria-label="截图标注与遮挡">
    <h3>检查截图并遮挡敏感内容</h3>
    <p>拖动画布可覆盖密码、账号、IP 等内容。只上传最终图片，不上传原图。</p>
    <div className="ty-feedback-row">{([['mask', '黑色遮挡'], ['pen', '画笔'], ['rectangle', '矩形'], ['arrow', '箭头']] as const).map(([value, label]) =>
      <button type="button" key={value} aria-pressed={tool === value} onClick={() => setTool(value)}>{label}</button>)}
      <button type="button" disabled={!history.current.length} onClick={() => {
        const image = history.current.pop();
        if (image) canvas.current?.getContext('2d')?.putImageData(image, 0, 0);
        setRevision(revision + 1);
      }}>撤销</button>
    </div>
    <canvas ref={canvas} aria-label="标注画布，可使用鼠标或触屏拖动" onPointerDown={event => {
      const context = event.currentTarget.getContext('2d');
      if (!context) return;
      const point = position(event);
      const image = context.getImageData(0, 0, event.currentTarget.width, event.currentTarget.height);
      start.current = { ...point, image };
      history.current.push(image);
      if (history.current.length > 15) history.current.shift();
      event.currentTarget.setPointerCapture(event.pointerId);
      context.beginPath(); context.moveTo(point.x, point.y);
    }} onPointerMove={paint} onPointerUp={event => {
      paint(event); start.current = null; setRevision(value => value + 1);
    }} onPointerCancel={() => { start.current = null; }} />
    {error && <p role="alert">{error}</p>}
    <div className="ty-feedback-row"><button type="button" onClick={onCancel}>取消图片</button><button type="button" className="ty-feedback-primary" onClick={() => {
      canvas.current?.toBlob(image => image ? onSave(image) : setError('图片处理失败，请重试。'), 'image/png');
    }}>确认最终图片</button></div>
  </section>;
}
