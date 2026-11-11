import { requestApi } from '../../services/ip-location/api';
import FeedbackWidget from './vendor/FeedbackWidget';
import { createFeedbackTransport } from './vendor/transport';
import { SupportRequestError } from './vendor/types';

const publicTransport = createFeedbackTransport(async (path, method, body, signal) => {
  const result = await requestApi<unknown>(path, signal, { method, body });
  if (!result.result.success || result.httpStatus < 200 || result.httpStatus >= 300) {
    throw new SupportRequestError(result.result.message || '反馈服务请求失败。', result.httpStatus);
  }
  return result.result.data;
});

/** 公共访客可主动提交反馈，或凭回执查询单条记录。 */
export default function ProductFeedback() {
  return <FeedbackWidget product="ip" scope="anonymous-tab" transport={publicTransport} />;
}
