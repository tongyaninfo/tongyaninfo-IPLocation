/** 产品后端适配器；浏览器只调用本产品同源 API，不持有安装密钥。 */
export type FeedbackCategory = 'bug' | 'suggestion' | 'question' | 'other';
export interface FeedbackSubmission {
  clientRequestId: string;
  receiptToken: string;
  category: FeedbackCategory;
  title: string;
  description: string;
  attachmentIds: string[];
}
export interface FeedbackReceipt {
  id: string;
  status: string;
  reply?: string | null;
  publicReply?: string | null;
  updatedAt?: string | null;
}
export interface FeedbackTransport {
  upload: (id: string, file: File, signal: AbortSignal) => Promise<{ attachmentId: string }>;
  submit: (request: FeedbackSubmission, signal: AbortSignal) => Promise<FeedbackReceipt>;
  lookup: (id: string, receiptToken: string, signal: AbortSignal) => Promise<FeedbackReceipt>;
  lookupByRequest: (id: string, receiptToken: string, signal: AbortSignal) => Promise<FeedbackReceipt>;
}
export class SupportRequestError extends Error {
  constructor(message: string, public readonly status = 0) { super(message); }
}

/** 旧浏览器也从系统随机源产生标识；随机源不可用时停止提交，不降级 Math.random。 */
export function createRequestId(): string {
  const value = crypto.getRandomValues(new Uint8Array(16));
  value[6] = (value[6] & 15) | 64;
  value[8] = (value[8] & 63) | 128;
  const hex = Array.from(value, byte => byte.toString(16).padStart(2, '0')).join('');
  return `${hex.slice(0, 8)}-${hex.slice(8, 12)}-${hex.slice(12, 16)}-${hex.slice(16, 20)}-${hex.slice(20)}`;
}
export function createReceiptToken(): string {
  return Array.from(crypto.getRandomValues(new Uint8Array(32)), byte => byte.toString(16).padStart(2, '0')).join('');
}

