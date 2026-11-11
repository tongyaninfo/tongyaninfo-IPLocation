import { useCallback, useEffect, useRef, useState } from 'react';
import { ImageEditor } from './ImageEditor';
import { createReceiptToken, createRequestId, SupportRequestError } from './types';
import type { FeedbackCategory, FeedbackReceipt, FeedbackSubmission, FeedbackTransport } from './types';
import { microphoneUnavailableReason, useWavRecorder } from './useWavRecorder';
import './styles.css';

const categories = { bug: '功能异常', suggestion: '优化建议', question: '使用咨询', other: '其他' };
const statuses: Record<string, string> = { pending: '待处理', processing: '处理中', resolved: '已解决', rejected: '不处理' };
type Media = { id: string; file: File; preview: string; uploaded?: string; durationMs?: number };
type ReceiptRecord = { product: string; clientRequestId: string; receiptToken: string; feedbackId?: string };

function OriginalAudio({ src }: { src: string }) {
  return (
    // biome-ignore lint/a11y/useMediaCaption: 原始用户录音没有转写，不伪造字幕或调用 AI。
    <audio controls src={src} preload="metadata" aria-label="反馈原始录音" />
  );
}

function readReceipt(key: string): ReceiptRecord | null {
  try {
    const value: unknown = JSON.parse(sessionStorage.getItem(key) || 'null');
    if (!value || typeof value !== 'object') return null;
    const record = value as Record<string, unknown>;
    if (typeof record.product !== 'string' || typeof record.clientRequestId !== 'string' ||
      typeof record.receiptToken !== 'string' || !/^[a-f0-9]{64}$/.test(record.receiptToken) ||
      (record.feedbackId !== undefined && typeof record.feedbackId !== 'string')) return null;
    return { product: record.product, clientRequestId: record.clientRequestId, receiptToken: record.receiptToken, feedbackId: record.feedbackId };
  } catch { return null; }
}

/** 仅回执留在当前标签会话，正文与媒体始终只驻留内存。 */
function rememberReceipt(key: string, value: ReceiptRecord) {
  try { sessionStorage.setItem(key, JSON.stringify(value)); } catch { /* 存储受限仍允许当前页面提交和复制回执。 */ }
}

/** 账号由宿主隔离；安装标识只由产品后端补充，不以机器 ID 查询公开反馈列表。 */
export default function FeedbackWidget({ transport, product, scope, theme }: {
  transport: FeedbackTransport;
  product: string;
  scope: string;
  theme?: 'light' | 'dark';
}) {
  const storageKey = `tongyan.feedback.receipt.${product}.${scope}`;
  const [open, setOpen] = useState(false);
  const [tab, setTab] = useState<'new' | 'receipt'>('new');
  const [category, setCategory] = useState<FeedbackCategory>('bug');
  const [description, setDescription] = useState('');
  const [media, setMedia] = useState<Media[]>([]);
  const [editing, setEditing] = useState<Blob | null>(null);
  const [busy, setBusy] = useState('');
  const [error, setError] = useState('');
  const [record, setRecord] = useState<ReceiptRecord | null>(() => readReceipt(storageKey));
  const [receipt, setReceipt] = useState<FeedbackReceipt | null>(null);
  const [lookupId, setLookupId] = useState('');
  const [lookupToken, setLookupToken] = useState('');
  const snapshot = useRef<FeedbackSubmission | null>(null);
  const draft = useRef<{ clientRequestId: string; receiptToken: string } | null>(null);
  const request = useRef<AbortController | null>(null);
  const active = useRef(true);
  const running = useRef(false);
  const screenStream = useRef<MediaStream | null>(null);
  const stopRecordingRef = useRef<() => void>(() => undefined);
  const recorder = useWavRecorder({ maxSeconds: 180, onLimit: () => stopRecordingRef.current() });
  const previews = useRef<string[]>([]);
  previews.current = media.map(item => item.preview);
  const panel = useRef<HTMLDivElement>(null);
  const trigger = useRef<HTMLButtonElement>(null);
  useEffect(() => {
    active.current = true;
    return () => {
      active.current = false; request.current?.abort(); recorder.cancel();
      screenStream.current?.getTracks().forEach(track => { track.stop(); });
      previews.current.forEach(url => { URL.revokeObjectURL(url); });
    };
  }, []);
  useEffect(() => {
    if (!open) return;
    panel.current?.focus();
    const close = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && !event.defaultPrevented && !editing && recorder.state === 'idle') {
        setOpen(false); trigger.current?.focus();
      }
    };
    window.addEventListener('keydown', close);
    return () => window.removeEventListener('keydown', close);
  }, [open, editing, recorder.state]);
  const images = media.filter(item => item.file.type.startsWith('image/'));
  const audios = media.filter(item => item.file.type === 'audio/wav');
  const immutable = !!busy || snapshot.current !== null;
  function addMedia(file: File, durationMs?: number) {
    setMedia(items => [...items, { id: createRequestId(), file, preview: URL.createObjectURL(file), durationMs }]);
  }
  const stopRecording = useCallback(() => {
    const value = recorder.stop();
    if (!value || !active.current) return;
    if (value.blob.size > 8 * 1024 * 1024) { setError('录音超过 8 MB，请缩短录制时长。'); return; }
    addMedia(new File([value.blob], 'feedback.wav', { type: 'audio/wav' }), value.durationMs);
  }, [recorder]);
  stopRecordingRef.current = stopRecording;
  async function capture() {
    setError('');
    if (!navigator.mediaDevices?.getDisplayMedia) { setError('当前浏览器不支持屏幕截图，请使用“选择截图”上传图片。'); return; }
    try {
      const stream = await navigator.mediaDevices.getDisplayMedia({ video: true, audio: false });
      if (!active.current) { stream.getTracks().forEach(track => { track.stop(); }); return; }
      screenStream.current = stream;
      const video = document.createElement('video');
      video.srcObject = stream; video.muted = true;
      await video.play();
      const canvas = document.createElement('canvas');
      canvas.width = video.videoWidth; canvas.height = video.videoHeight;
      canvas.getContext('2d')?.drawImage(video, 0, 0);
      stream.getTracks().forEach(track => { track.stop(); }); screenStream.current = null;
      canvas.toBlob(blob => { if (active.current && blob) setEditing(blob); }, 'image/png');
    } catch (failure) {
      screenStream.current?.getTracks().forEach(track => { track.stop(); }); screenStream.current = null;
      if (active.current) setError(failure instanceof DOMException && failure.name === 'NotAllowedError' ? '已取消屏幕截图，可改用选择截图。' : '截图失败，请选择已有截图。');
    }
  }
  async function submit() {
    if (running.current || recorder.state !== 'idle' || (!description.trim() && !media.length)) return;
    running.current = true; setError(''); setBusy('正在提交…');
    const controller = new AbortController(); request.current = controller;
    try {
      if (!draft.current) draft.current = { clientRequestId: createRequestId(), receiptToken: createReceiptToken() };
      const metadata = { product, ...draft.current };
      rememberReceipt(storageKey, metadata); setRecord(metadata);
      if (!snapshot.current) {
        const attachmentIds: string[] = [];
        for (const [index, item] of media.entries()) {
          if (controller.signal.aborted) return;
          setBusy(`正在上传附件 ${index + 1}/${media.length}…`);
          const uploaded = item.uploaded || (await transport.upload(item.id, item.file, controller.signal)).attachmentId;
          attachmentIds.push(uploaded);
          if (active.current) setMedia(items => items.map(value => value.id === item.id ? { ...value, uploaded } : value));
        }
        snapshot.current = { ...draft.current, category, title: description.trim().split('\n')[0].slice(0, 120) || categories[category],
          description: description.trim(), attachmentIds };
      }
      setBusy('正在提交反馈…');
      const result = await transport.submit(snapshot.current, controller.signal);
      if (controller.signal.aborted || !active.current) return;
      const saved = { ...metadata, feedbackId: result.id };
      rememberReceipt(storageKey, saved); setRecord(saved); setReceipt(result); setTab('receipt');
      previews.current.forEach(url => { URL.revokeObjectURL(url); }); setMedia([]); setDescription('');
      draft.current = null; snapshot.current = null;
    } catch (failure) {
      if (!controller.signal.aborted && active.current) {
        if (failure instanceof SupportRequestError && [400, 403, 413, 422].includes(failure.status)) snapshot.current = null;
        setError(`${failure instanceof Error ? failure.message : '提交失败。'}${snapshot.current ? ' 结果尚未确认，重试会沿用本次请求和附件。' : ' 草稿已保留。'}`);
      }
    } finally { running.current = false; if (!controller.signal.aborted && active.current) setBusy(''); }
  }
  async function lookup(id = record?.feedbackId, token = record?.receiptToken, byRequest = false) {
    if (!id || !token || running.current) return;
    const controller = new AbortController(); request.current = controller;
    running.current = true; setBusy('正在查询回执…'); setError('');
    try {
      const result = await (byRequest ? transport.lookupByRequest(id, token, controller.signal) : transport.lookup(id, token, controller.signal));
      if (!controller.signal.aborted && active.current) {
        setReceipt(result);
        if (byRequest && record) {
          const saved = { ...record, feedbackId: result.id };
          rememberReceipt(storageKey, saved); setRecord(saved);
          draft.current = null; snapshot.current = null;
          previews.current.forEach(url => { URL.revokeObjectURL(url); });
          setMedia([]); setDescription('');
        }
      }
    } catch (failure) { if (!controller.signal.aborted && active.current) setError(failure instanceof Error ? failure.message : '回执查询失败。'); }
    finally { running.current = false; if (!controller.signal.aborted && active.current) setBusy(''); }
  }
  function close() {
    if (recorder.recording) stopRecording(); else recorder.cancel();
    setOpen(false); trigger.current?.focus();
  }
  return <div className="ty-feedback" data-feedback-theme={theme}>
    {!open && <button ref={trigger} type="button" className="ty-feedback-trigger ty-feedback-primary" onClick={() => setOpen(true)}>意见反馈</button>}
    {open && <div ref={panel} className="ty-feedback-panel" role="dialog" aria-modal="false" aria-label="产品意见反馈" tabIndex={-1}>
      <div className="ty-feedback-header"><h2>意见反馈</h2><button type="button" onClick={close} aria-label="关闭反馈面板">关闭</button></div>
      <nav className="ty-feedback-row" aria-label="反馈功能"><button type="button" aria-pressed={tab === 'new'} onClick={() => setTab('new')}>提交反馈</button><button type="button" aria-pressed={tab === 'receipt'} onClick={() => setTab('receipt')}>反馈回执</button></nav>
      {error && <p className="ty-feedback-error" role="alert">{error}</p>}
      {busy && <p role="status">{busy}</p>}
      {tab === 'new' && (editing ? <ImageEditor source={editing} onCancel={() => setEditing(null)} onSave={blob => {
        if (blob.size > 10 * 1024 * 1024) { setError('处理后的图片超过 10 MB，请使用更小截图。'); return; }
        addMedia(new File([blob], 'feedback.png', { type: 'image/png' })); setEditing(null);
      }} /> : <>
        <p>问题和建议将提交到通研官网，由产品维护人员处理。</p>
        {record && !record.feedbackId && !draft.current && <p className="ty-feedback-note">当前标签保存了一条待确认回执。请先到“反馈回执”查询原请求结果，避免重复提交相同问题。</p>}
        <label><span>问题类型</span><select value={category} disabled={immutable} onChange={event => setCategory(event.target.value as FeedbackCategory)}>{Object.entries(categories).map(([key, label]) => <option key={key} value={key}>{label}</option>)}</select></label>
        <label><span>问题描述（{description.length}/2000）</span><textarea maxLength={2000} value={description} disabled={immutable} onChange={event => setDescription(event.target.value)} placeholder="描述发生的问题、复现步骤或改进建议" /></label>
        <small>文字、截图、录音至少一项。不要提交密码、密钥、证书或其他敏感内容。</small>
        <div className="ty-feedback-row"><button type="button" disabled={immutable || images.length >= 3 || recorder.state !== 'idle'} onClick={() => void capture()}>屏幕截图</button><label style={{ margin: 0 }}>选择截图<input type="file" aria-label="选择截图" accept="image/png,image/jpeg" disabled={immutable || images.length >= 3 || recorder.state !== 'idle'} onChange={event => {
          const file = event.target.files?.[0]; event.target.value = '';
          if (!file) return;
          if (!['image/png', 'image/jpeg'].includes(file.type) || file.size > 10 * 1024 * 1024) { setError('仅支持不超过 10 MB 的 PNG/JPEG。'); return; }
          setError(''); setEditing(file);
        }} /></label></div>
        <small>最多 3 张图片；请在预览中遮挡敏感区域，只提交确认后的最终图片。</small>
        <ul className="ty-feedback-media">{images.map(item => <li key={item.id}><img src={item.preview} alt="已确认提交的最终截图" /><button type="button" disabled={immutable} onClick={() => { URL.revokeObjectURL(item.preview); setMedia(items => items.filter(value => value.id !== item.id)); }}>移除图片</button></li>)}</ul>
        <div className="ty-feedback-row">{recorder.state === 'idle' ? <button type="button" disabled={immutable || audios.length >= 3 || !!microphoneUnavailableReason()} onClick={() => void recorder.start()}>开始录音</button> : <><span role="status">{recorder.recording ? `正在录音 ${Math.floor(recorder.elapsedMs / 1000)} / 180 秒` : '正在等待麦克风授权…'}</span><button type="button" onClick={stopRecording}>结束录音</button><button type="button" onClick={recorder.cancel}>放弃录音</button></>}</div>
        <small>{microphoneUnavailableReason() || '最多 3 段录音，每段最长 3 分钟、8 MB；仅保存原始录音。'}</small>
        {recorder.error && <p className="ty-feedback-error" role="alert">{recorder.error}</p>}
        <ul className="ty-feedback-media">{audios.map(item => <li key={item.id}><span>录音 · {Math.ceil((item.durationMs || 0) / 1000)} 秒</span><OriginalAudio src={item.preview} /><button type="button" disabled={immutable} onClick={() => { URL.revokeObjectURL(item.preview); setMedia(items => items.filter(value => value.id !== item.id)); }}>移除录音</button></li>)}</ul>
        <button type="button" className="ty-feedback-primary" disabled={!!busy || recorder.state !== 'idle' || (!description.trim() && !media.length)} onClick={() => void submit()}>{snapshot.current ? '重试本次提交' : '提交反馈'}</button>
        <small>关闭面板保留本页草稿；刷新或退出账号会清空正文和媒体。回执只保存在当前标签会话，可自行复制保存。</small>
      </>)}
      {tab === 'receipt' && <>
        {record?.feedbackId ? <><p>反馈编号：<code>{record.feedbackId}</code></p><button type="button" disabled={!!busy} onClick={() => void lookup()}>查询本次反馈</button><button type="button" onClick={() => {
          void navigator.clipboard?.writeText(JSON.stringify({ product, feedbackId: record.feedbackId, receiptToken: record.receiptToken })).catch(() => setError('复制失败，可展开回执后手工保存。'));
        }}>复制回执</button><details><summary>查看私密回执</summary><p>持有回执可查询此条反馈，请妥善保存。</p><code>{JSON.stringify({ product, feedbackId: record.feedbackId, receiptToken: record.receiptToken })}</code></details></> : record ? <div className="ty-feedback-note"><p>上次提交结果待确认。请求编号：{record.clientRequestId}。请保留回执，避免重新创建相同反馈。</p><button type="button" disabled={!!busy} onClick={() => void lookup(record.clientRequestId, record.receiptToken, true)}>查询原提交结果</button><details><summary>查看待确认私密回执</summary><code>{JSON.stringify(record)}</code></details></div> : <p>当前标签会话还没有反馈回执。</p>}
        {receipt && <div className="ty-feedback-note"><strong>处理状态：{statuses[receipt.status] || receipt.status}</strong><p>{receipt.publicReply || receipt.reply || '暂未收到公开回复。'}</p></div>}
        <details><summary>使用已保存的单条回执查询</summary><label><span>反馈编号</span><input type="text" value={lookupId} onChange={event => setLookupId(event.target.value)} /></label><label><span>回执查询码</span><input type="text" value={lookupToken} autoComplete="off" onChange={event => setLookupToken(event.target.value)} /></label><button type="button" disabled={!!busy || !lookupId.trim() || !/^[a-f0-9]{64}$/.test(lookupToken)} onClick={() => void lookup(lookupId.trim(), lookupToken)}>查询此条反馈</button></details>
      </>}
    </div>}
  </div>;
}
