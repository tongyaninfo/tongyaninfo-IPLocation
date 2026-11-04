import { Button, Result, Skeleton } from 'antd';
import { Brand } from '../../components/Brand';
import { Icon } from '../../components/Icon';
import { ThemeSwitcher } from '../../components/ThemeSwitcher';
import type { useConsoleSession } from '../../models/useConsoleSession';
import { ConsoleLogin } from './ConsoleLogin';

interface ConsoleLoginPageProps {
  session: ReturnType<typeof useConsoleSession>;
  showLogin: boolean;
}

/** 登录与访问检查使用独立外壳，身份核验完成前不渲染管理导航或总览内容。 */
export function ConsoleLoginPage({ session, showLogin }: ConsoleLoginPageProps) {
  const { gate, detail } = session;
  const redirecting = gate === 'allowed' || (gate === 'unauthenticated' && !showLogin);
  return <div className="console-entry-page">
    <header className="console-entry-header"><Brand /><div className="console-entry-actions"><ThemeSwitcher /><a href="/" className="console-entry-home"><Icon name="arrow" size={15} />返回 IP 查询</a></div></header>
    <main className="console-entry-main"><div className="console-entry-content">
      {gate === 'checking' || redirecting ? <section className="console-gate-card console-entry-checking" aria-label="正在检查后台权限" role="status"><span className="gate-icon"><Icon name="shield" size={32} /></span><Skeleton active paragraph={{ rows: 3 }} /><p>{redirecting ? '正在进入管理页面…' : '正在核验访问权限…'}</p></section>
        : gate === 'unauthenticated' ? <ConsoleLogin busy={session.busy} error={session.actionError} onSubmit={session.login} />
          : <section className="console-gate-card"><Result
            status={gate === 'denied' ? '403' : gate === 'unavailable' ? 'warning' : 'info'}
            icon={gate === 'notInitialized' ? <span className="gate-icon"><Icon name="lock" size={38} /></span> : undefined}
            title={gate === 'denied' ? '当前访问来源不被允许' : gate === 'notInitialized' ? '管理后台尚未初始化' : '暂时无法核验访问权限'}
            subTitle={<div><p>{detail}</p><p>{gate === 'denied' ? '请联系管理员核对允许的 IP 或网段。' : gate === 'notInitialized' ? '请在服务器本地完成管理员初始化，再重新检查。' : '请确认服务可用后重试。'}</p></div>}
            extra={[<Button key="retry" onClick={() => void session.refresh()} icon={<Icon name="refresh" size={16} />}>重新检查</Button>, <Button key="home" type="primary" href="/">返回 IP 查询</Button>]} />
          </section>}
    </div></main>
    <footer className="console-entry-footer">通研信息 · IP 聚合查询管理控制台</footer>
  </div>;
}
