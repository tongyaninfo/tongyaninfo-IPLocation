import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import FeedbackWidget from '../src/components/ProductFeedback/vendor/FeedbackWidget';
import { createFeedbackTransport } from '../src/components/ProductFeedback/vendor/transport';
import type { SupportRequest } from '../src/components/ProductFeedback/vendor/transport';
import type { FeedbackSubmission, FeedbackTransport } from '../src/components/ProductFeedback/vendor/types';

beforeEach(() => { sessionStorage.clear(); });

function adapter(submit: FeedbackTransport['submit']): FeedbackTransport {
  return { upload: vi.fn(), submit, lookup: vi.fn(), lookupByRequest: vi.fn() };
}

describe('产品反馈用户流程', () => {
  it('显式提交正文，不发送账号、原始环境或 AI 字段；只保存最小回执', async () => {
    const submit = vi.fn(async (_request: FeedbackSubmission) => ({ id: '9007199254740993', status: 'pending' }));
    render(<FeedbackWidget product="ip" scope="test" transport={adapter(submit)} />);
    fireEvent.click(screen.getByRole('button', { name: '意见反馈' }));
    fireEvent.change(screen.getByRole('textbox', { name: /问题描述/ }), { target: { value: '查询页面出现异常，请检查' } });
    expect(submit).not.toHaveBeenCalled();
    fireEvent.click(screen.getAllByRole('button', { name: '提交反馈' })[1]);
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(1));
    const request = submit.mock.calls[0][0];
    expect(request.description).toBe('查询页面出现异常，请检查');
    expect(request.receiptToken).toMatch(/^[a-f0-9]{64}$/);
    expect(Object.keys(request).sort()).toEqual(['attachmentIds', 'category', 'clientRequestId', 'description', 'receiptToken', 'title']);
    await screen.findByText('9007199254740993');
    const metadata = sessionStorage.getItem('tongyan.feedback.receipt.ip.test')!;
    expect(metadata).not.toContain(request.description);
    expect(JSON.parse(metadata)).toEqual({ product: 'ip', clientRequestId: request.clientRequestId, receiptToken: request.receiptToken, feedbackId: '9007199254740993' });
  });

  it('网络结果未知时重试原请求快照，不新增反馈意图', async () => {
    const submit = vi.fn<FeedbackTransport['submit']>().mockRejectedValueOnce(new Error('网络中断')).mockResolvedValueOnce({ id: '101', status: 'pending' });
    render(<FeedbackWidget product="ip" scope="retry" transport={adapter(submit)} />);
    fireEvent.click(screen.getByRole('button', { name: '意见反馈' }));
    fireEvent.change(screen.getByRole('textbox', { name: /问题描述/ }), { target: { value: '测试原请求' } });
    fireEvent.click(screen.getAllByRole('button', { name: '提交反馈' })[1]);
    await screen.findByRole('alert');
    expect((screen.getByRole('textbox', { name: /问题描述/ }) as HTMLTextAreaElement).disabled).toBe(true);
    fireEvent.click(screen.getByRole('button', { name: '重试本次提交' }));
    await waitFor(() => expect(submit).toHaveBeenCalledTimes(2));
    expect(submit.mock.calls[1][0]).toEqual(submit.mock.calls[0][0]);
  });

  it('回执查询仅用 POST 正文，不泄漏到 URL，并过滤内部备注', async () => {
    const request = vi.fn<SupportRequest>(async () => ({ ticket: { id: '123', status: 'processing' }, messages: [
      { visibility: 'public', body: '已开始排查' }, { visibility: 'internal', body: '内部信息不可展示' },
    ] }));
    const transport = createFeedbackTransport(request);
    const token = 'ab'.repeat(32);
    const result = await transport.lookup('123', token, new AbortController().signal);
    expect(request.mock.calls[0]).toEqual(['/api/support/feedback/123/lookup', 'POST', { receiptToken: token }, expect.any(AbortSignal)]);
    expect(result.publicReply).toBe('已开始排查');
    expect(JSON.stringify(result)).not.toContain('内部信息不可展示');
  });

  it('上传最终图片保留稳定附件ID，FormData不另附原图', async () => {
    const request = vi.fn<SupportRequest>(async () => ({ attachmentId: 'stable-attachment' }));
    const transport = createFeedbackTransport(request);
    const file = new File(['final pixels'], 'feedback.png', { type: 'image/png' });
    await transport.upload('client-attachment', file, new AbortController().signal);
    const [url, method, body] = request.mock.calls[0] as unknown as [string, string, FormData];
    expect(url).toBe('/api/support/feedback/attachments/client-attachment');
    expect(method).toBe('POST');
    expect(Array.from(body.keys())).toEqual(['file']);
    expect(body.get('file')).toBe(file);
  });

  it('刷新后使用最小待确认回执恢复原请求，不重新提交正文', async () => {
    const metadata = { product: 'ip', clientRequestId: '11111111-2222-4333-8444-555555555555', receiptToken: 'cd'.repeat(32) };
    sessionStorage.setItem('tongyan.feedback.receipt.ip.recover', JSON.stringify(metadata));
    const submit = vi.fn<FeedbackTransport['submit']>();
    const transport = adapter(submit);
    transport.lookupByRequest = vi.fn(async () => ({ id: '456', status: 'pending' }));
    render(<FeedbackWidget product="ip" scope="recover" transport={transport} />);
    fireEvent.click(screen.getByRole('button', { name: '意见反馈' }));
    fireEvent.click(screen.getByRole('button', { name: '反馈回执' }));
    fireEvent.click(screen.getByRole('button', { name: '查询原提交结果' }));
    await screen.findByText('456');
    expect(submit).not.toHaveBeenCalled();
    expect(transport.lookupByRequest).toHaveBeenCalledWith(metadata.clientRequestId, metadata.receiptToken, expect.any(AbortSignal));
    expect(JSON.parse(sessionStorage.getItem('tongyan.feedback.receipt.ip.recover')!).feedbackId).toBe('456');
  });
});
