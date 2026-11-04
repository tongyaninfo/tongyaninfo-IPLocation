import { Button, Tooltip } from 'antd';
import { useEffect, useRef, useState } from 'react';
import { Icon } from './Icon';

export function CopyButton({ value, label = '复制', iconOnly = false, className, disabled = false, tooltip }: {
  value: string; label?: string; iconOnly?: boolean; className?: string; disabled?: boolean; tooltip?: string;
}) {
  const [state, setState] = useState<'idle' | 'copied' | 'failed'>('idle');
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);
  async function copy() {
    if (disabled) return;
    try {
      await navigator.clipboard.writeText(value);
      setState('copied');
    } catch { setState('failed'); }
    clearTimeout(timer.current);
    timer.current = setTimeout(() => setState('idle'), 2200);
  }
  const text = state === 'copied' ? '已复制' : state === 'failed' ? '请手动复制' : label;
  return <Tooltip title={state === 'idle' ? tooltip || text : text}><Button className={['copy-button', className].filter(Boolean).join(' ')} disabled={disabled}
    type="text" size="small" onClick={() => void copy()} aria-label={text} icon={<Icon name={state === 'copied' ? 'check' : 'copy'} size={16} />}>{iconOnly ? undefined : text}</Button></Tooltip>;
}
