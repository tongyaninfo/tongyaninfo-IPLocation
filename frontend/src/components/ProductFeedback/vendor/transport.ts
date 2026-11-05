import { SupportRequestError } from './types';
import type { FeedbackReceipt, FeedbackTransport, SupportStatus } from './types';

/** 只有产品请求层负责 Cookie/CSRF/会话；共享组件不引入各产品的认证实现。 */
export type SupportRequest = (path: string, method: 'GET' | 'POST' | 'PUT', body: unknown, signal: AbortSignal) => Promise<unknown>;
function object(value: unknown): Record<string, unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new SupportRequestError('反馈服务返回格式不正确。');
  return value as Record<string, unknown>;
}
function receipt(value: unknown): FeedbackReceipt {
  const data = object(value);
  const ticket = data.ticket ? object(data.ticket) : data;
  const id = ticket.feedbackId ?? ticket.id;
  if (typeof id !== 'string' || typeof ticket.status !== 'string') throw new SupportRequestError('回执返回格式不正确。');
  const messages = Array.isArray(data.messages) ? data.messages : [];
  const publicReplies = messages.map(object).filter(item => item.visibility === 'public' && typeof item.body === 'string').map(item => String(item.body));
  return { id, status: ticket.status, publicReply: publicReplies.join('\n\n') || (typeof data.reply === 'string' ? data.reply : null), updatedAt: typeof ticket.updatedAt === 'string' ? ticket.updatedAt : null };
}
function status(value: unknown): SupportStatus {
  const data = object(value);
  if (typeof data.installationId !== 'string' || typeof data.telemetryEnabled !== 'boolean' || typeof data.canManageTelemetry !== 'boolean')
    throw new SupportRequestError('运行设置返回格式不正确。');
  return { installationId: data.installationId, telemetryEnabled: data.telemetryEnabled, canManageTelemetry: data.canManageTelemetry,
    product: typeof data.product === 'string' ? data.product : '', productVersion: typeof data.productVersion === 'string' ? data.productVersion : '',
    lastHeartbeatAt: typeof data.lastHeartbeatAt === 'string' ? data.lastHeartbeatAt : null,
    errorCode: typeof data.errorCode === 'string' ? data.errorCode : null,
    lastHeartbeatError: typeof data.lastHeartbeatError === 'string' ? data.lastHeartbeatError : null };
}
export function createFeedbackTransport(request: SupportRequest, settingsPath = '/api/support'): FeedbackTransport {
  return {
    async upload(id, file, signal) {
      const body = new FormData(); body.append('file', file);
      const result = object(await request(`/api/support/feedback/attachments/${encodeURIComponent(id)}`, 'POST', body, signal));
      if (typeof result.attachmentId !== 'string') throw new SupportRequestError('附件上传返回格式不正确。');
      return { attachmentId: result.attachmentId };
    },
    submit: async (body, signal) => receipt(await request('/api/support/feedback', 'POST', body, signal)),
    lookup: async (id, receiptToken, signal) => receipt(await request(`/api/support/feedback/${encodeURIComponent(id)}/lookup`, 'POST', { receiptToken }, signal)),
    lookupByRequest: async (id, receiptToken, signal) => receipt(await request(`/api/support/feedback/by-request/${encodeURIComponent(id)}/lookup`, 'POST', { receiptToken }, signal)),
    status: async signal => status(await request(`${settingsPath}/status`, 'GET', undefined, signal)),
    setTelemetry: async (enabled, signal) => status(await request(`${settingsPath}/telemetry`, 'PUT', { enabled }, signal)),
  };
}
