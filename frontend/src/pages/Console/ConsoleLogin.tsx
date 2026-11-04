import { Alert, Button, Input } from 'antd';
import { useState } from 'react';
import type { FormEvent } from 'react';
import type { ConsoleLoginRequest } from '../../types/ip-location';
import { Icon } from '../../components/Icon';

interface ConsoleLoginProps {
  busy: boolean;
  error: string;
  onSubmit: (credentials: ConsoleLoginRequest) => Promise<void>;
}

export function ConsoleLogin({ busy, error, onSubmit }: ConsoleLoginProps) {
  const [account, setAccount] = useState('');
  const [password, setPassword] = useState('');
  const [validationError, setValidationError] = useState('');

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (busy) return;
    if (!account.trim() || !password) {
      setValidationError('请输入管理员账号和密码。');
      return;
    }
    setValidationError('');
    const credentials = { account: account.trim(), password };
    // 密码只在本次提交中使用，提交后立即移出表单状态；离开页面也不会持久化。
    setPassword('');
    await onSubmit(credentials);
  }

  return <section className="console-gate-card console-login-card" aria-labelledby="console-login-title">
    <div className="console-login-heading"><span className="gate-icon"><Icon name="lock" size={32} /></span><h1 id="console-login-title">管理员登录</h1><p>登录通研信息 IP 查询管理控制台</p></div>
    <form onSubmit={submit} aria-label="管理员登录表单">
      <label htmlFor="console-account">管理员账号</label>
      <Input id="console-account" name="username" autoComplete="username" maxLength={128} value={account} onChange={event => setAccount(event.target.value)} disabled={busy} placeholder="输入管理员账号" size="large" />
      <label htmlFor="console-password">密码</label>
      <Input.Password id="console-password" name="password" autoComplete="current-password" maxLength={1024} value={password} onChange={event => setPassword(event.target.value)} disabled={busy} placeholder="输入密码" size="large" />
      {validationError || error ? <Alert type="error" showIcon title={validationError || error} role="alert" /> : null}
      <Button type="primary" htmlType="submit" size="large" block loading={busy}>登录管理控制台</Button>
    </form>
    <p className="console-login-note"><Icon name="shield" size={14} />每次管理请求均由服务端核验身份与来源权限。</p>
  </section>;
}
