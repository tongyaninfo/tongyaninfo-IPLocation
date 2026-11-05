import { useContext, useMemo } from 'react';
import { ConsoleContext } from '../../models/ConsoleContext';
import { requestApi } from '../../services/ip-location/api';
import FeedbackWidget, { SupportSettings } from './vendor/FeedbackWidget';
import { createFeedbackTransport } from './vendor/transport';
import { SupportRequestError } from './vendor/types';

function transport(csrfToken?: string, recheck?: () => Promise<void>) {
  return createFeedbackTransport(async (path, method, body, signal) => {
    const result = await requestApi<unknown>(path, signal, { method, body, csrfToken });
    if (!result.result.success || result.httpStatus < 200 || result.httpStatus >= 300) {
      if (path.startsWith('/api/console/') && [401, 403].includes(result.httpStatus)) void recheck?.();
      throw new SupportRequestError(result.result.message || '反馈服务请求失败。', result.httpStatus);
    }
    return result.result.data;
  }, '/api/console/support');
}
const publicTransport = transport();

/** 公共访客只提交及逐条回执查询，不读取后台安装标识和遥测设置。 */
export default function ProductFeedback() {
  return <FeedbackWidget product="ip" scope="anonymous-tab" transport={publicTransport} />;
}

export function ProductTelemetrySettings() {
  const { csrfToken, recheck } = useContext(ConsoleContext);
  const adapter = useMemo(() => transport(csrfToken, recheck), [csrfToken, recheck]);
  return <div className="ty-feedback"><SupportSettings transport={adapter} /></div>;
}
