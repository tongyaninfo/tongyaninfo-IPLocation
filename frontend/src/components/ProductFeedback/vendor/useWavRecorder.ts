import { useCallback, useEffect, useRef, useState } from 'react';

export type RecorderState = 'idle' | 'starting' | 'recording';
export const TARGET_SAMPLE_RATE = 16000;
export const LEVEL_HISTORY = 24;

/** 只在安全环境申请麦克风权限，不以失败请求代替可用性判断。 */
export function microphoneUnavailableReason(): string | null {
  if (!window.isSecureContext) return '录音需要 HTTPS 或本机地址，请使用安全地址打开。';
  return typeof navigator.mediaDevices?.getUserMedia === 'function'
    ? null
    : '当前浏览器不支持录音，请使用文字或截图。';
}

export function encodeWav(chunks: Float32Array[], inputRate: number): Blob {
  const total = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
  const ratio = inputRate / TARGET_SAMPLE_RATE;
  const outLength = Math.floor(total / ratio);
  const samples = new Int16Array(outLength);
  let chunkIndex = 0;
  let offset = 0;
  const readAt = (position: number): number => {
    let chunk = chunks[chunkIndex];
    while (chunk && position - offset >= chunk.length) {
      offset += chunk.length;
      chunkIndex += 1;
      chunk = chunks[chunkIndex];
    }
    return chunk?.[position - offset] ?? 0;
  };
  // 降采样：每个输出样本取对应输入区间的平均，只保存原始声音，不调用转写服务
  for (let i = 0; i < outLength; i += 1) {
    const start = Math.floor(i * ratio);
    const end = Math.min(total, Math.floor((i + 1) * ratio));
    let sum = 0;
    for (let p = start; p < end; p += 1) sum += readAt(p);
    const value = end > start ? sum / (end - start) : 0;
    samples[i] = Math.max(-1, Math.min(1, value)) * 0x7fff;
  }
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const writeString = (at: number, text: string) => {
    for (let i = 0; i < text.length; i += 1)
      view.setUint8(at + i, text.charCodeAt(i));
  };
  writeString(0, 'RIFF');
  view.setUint32(4, 36 + samples.length * 2, true);
  writeString(8, 'WAVE');
  writeString(12, 'fmt ');
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);
  view.setUint16(22, 1, true);
  view.setUint32(24, TARGET_SAMPLE_RATE, true);
  view.setUint32(28, TARGET_SAMPLE_RATE * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeString(36, 'data');
  view.setUint32(40, samples.length * 2, true);
  new Int16Array(buffer, 44).set(samples);
  return new Blob([buffer], { type: 'audio/wav' });
}

type Recording = {
  stream: MediaStream;
  context: AudioContext;
  processor: ScriptProcessorNode;
  source: MediaStreamAudioSourceNode;
  analyser: AnalyserNode;
  chunks: Float32Array[];
  levelTimer: number;
  limitTimer: number;
};

function startError(reason: unknown): string {
  const name = reason instanceof DOMException ? reason.name : '';
  if (name === 'NotAllowedError' || name === 'SecurityError')
    return '未获得麦克风权限，请在浏览器中允许本站访问后重试。';
  if (name === 'NotFoundError') return '没有找到可用的麦克风。';
  if (name === 'NotReadableError') return '麦克风无法访问，请检查是否被其他程序占用。';
  return '打开麦克风失败，请重试。';
}

/**
 * 16 kHz 单声道 WAV 编码；麦克风仅由显式录音动作打开。
 * 每次取消、换账号、卸载均使未决授权失效，迟到的 MediaStream 立即停止。
 */
export function useWavRecorder({
  maxSeconds,
  onLimit,
}: {
  maxSeconds: number;
  onLimit?: () => void;
}) {
  const [state, setState] = useState<RecorderState>('idle');
  const [levels, setLevels] = useState<number[]>(() =>
    Array(LEVEL_HISTORY).fill(0),
  );
  const [elapsedMs, setElapsedMs] = useState(0);
  const [error, setError] = useState<string | null>(null);
  const recordingRef = useRef<Recording | null>(null);
  const pending = useRef(false);
  const generation = useRef(0);
  const mounted = useRef(true);
  const onLimitRef = useRef(onLimit);
  onLimitRef.current = onLimit;

  const teardown = useCallback(() => {
    generation.current += 1;
    pending.current = false;
    const recording = recordingRef.current;
    recordingRef.current = null;
    if (!recording) return;
    window.clearInterval(recording.levelTimer);
    window.clearTimeout(recording.limitTimer);
    recording.processor.onaudioprocess = null;
    recording.source.disconnect();
    recording.processor.disconnect();
    recording.analyser.disconnect();
    for (const track of recording.stream.getTracks()) track.stop();
    void recording.context.close().catch(() => undefined);
  }, []);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      teardown();
    };
  }, [teardown]);

  const start = useCallback(async (): Promise<boolean> => {
    if (recordingRef.current || pending.current || !mounted.current)
      return false;
    setError(null);
    const unavailable = microphoneUnavailableReason();
    if (unavailable) {
      setError(unavailable);
      return false;
    }
    pending.current = true;
    setState('starting');
    const attempt = ++generation.current;
    let stream: MediaStream | undefined;
    let context: AudioContext | undefined;
    try {
      stream = await navigator.mediaDevices.getUserMedia({
        audio: {
          channelCount: 1,
          echoCancellation: true,
          noiseSuppression: true,
        },
      });
      if (!mounted.current || generation.current !== attempt) {
        stream.getTracks().forEach((track) => {
          track.stop();
        });
        return false;
      }
      context = new AudioContext();
      await context.resume();
      if (!mounted.current || generation.current !== attempt) {
        stream.getTracks().forEach((track) => {
          track.stop();
        });
        await context.close();
        return false;
      }
      const source = context.createMediaStreamSource(stream);
      const analyser = context.createAnalyser();
      analyser.fftSize = 512;
      // 保留源分支的 PCM 采集方式，输出统一 WAV，避免不同浏览器编码格式差异。
      const processor = context.createScriptProcessor(4096, 1, 1);
      const chunks: Float32Array[] = [];
      const maximum = maxSeconds * context.sampleRate;
      let sampleCount = 0;
      processor.onaudioprocess = (event) => {
        const samples = event.inputBuffer
          .getChannelData(0)
          .slice(0, Math.max(0, maximum - sampleCount));
        if (samples.length) {
          chunks.push(samples);
          sampleCount += samples.length;
        }
      };
      source.connect(analyser);
      source.connect(processor);
      processor.connect(context.destination);
      const sample = new Float32Array(analyser.fftSize);
      const rate = context.sampleRate;
      const levelTimer = window.setInterval(() => {
        analyser.getFloatTimeDomainData(sample);
        const level = Math.min(
          1,
          Math.sqrt(
            sample.reduce((sum, value) => sum + value * value, 0) /
              sample.length,
          ) * 6,
        );
        setLevels((previous) => [...previous.slice(1), level]);
        setElapsedMs(Math.min(maxSeconds * 1000, (sampleCount / rate) * 1000));
      }, 70);
      const limitTimer = window.setTimeout(
        () => onLimitRef.current?.(),
        maxSeconds * 1000,
      );
      recordingRef.current = {
        stream,
        context,
        source,
        analyser,
        processor,
        chunks,
        levelTimer,
        limitTimer,
      };
      pending.current = false;
      setLevels(Array(LEVEL_HISTORY).fill(0));
      setElapsedMs(0);
      setState('recording');
      return true;
    } catch (reason) {
      stream?.getTracks().forEach((track) => {
        track.stop();
      });
      if (context && context.state !== 'closed')
        void context.close().catch(() => undefined);
      if (mounted.current && generation.current === attempt) {
        teardown();
        setState('idle');
        setError(startError(reason));
      }
      return false;
    }
  }, [maxSeconds, teardown]);

  const stop = useCallback((): { blob: Blob; durationMs: number } | null => {
    const recording = recordingRef.current;
    if (!recording) return null;
    const { chunks } = recording;
    const rate = recording.context.sampleRate;
    teardown();
    setState('idle');
    if (!chunks.length) {
      setError('没有录到声音，请重新录制。');
      return null;
    }
    const samples = chunks.reduce((sum, chunk) => sum + chunk.length, 0);
    return {
      blob: encodeWav(chunks, rate),
      durationMs: Math.round((samples / rate) * 1000),
    };
  }, [teardown]);

  const cancel = useCallback(() => {
    teardown();
    if (mounted.current) setState('idle');
  }, [teardown]);
  return {
    state,
    recording: state === 'recording',
    levels,
    elapsedMs,
    error,
    start,
    stop,
    cancel,
  };
}
export type WavRecorder = ReturnType<typeof useWavRecorder>;
