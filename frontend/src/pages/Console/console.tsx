import '../../styles/console.css';
import { useEffect, useState } from 'react';
import { Alert, Button, Tag } from 'antd';
import { Brand } from '../../components/Brand';
import { Icon } from '../../components/Icon';
import { ThemeSwitcher } from '../../components/ThemeSwitcher';
import { useConsoleSession } from '../../models/useConsoleSession';
import { consoleLoginPath, consoleReturnPath } from '../../utils/consoleNavigation';
import { ConsoleLoginPage } from './ConsoleLoginPage';
import { ConsoleContext } from '../../models/ConsoleContext';
import { OverviewPage } from './OverviewPage';
import { DatabasesPage } from './DatabasesPage';
import { AccessLogsPage } from './AccessLogsPage';
import { McpLogsPage } from './McpLogsPage';
import { AuditPage } from './AuditPage';
import { SettingsPage } from './SettingsPage';

const sections = [
  { key: 'overview', label: '运行总览', icon: 'chart', title: '运行总览', description: '查看查询服务、访问采集和数据版本的真实运行状态。' },
  { key: 'databases', label: 'IP 数据库', icon: 'database', title: 'IP 数据库', description: '按来源管理文件校验、版本与激活状态。' },
  { key: 'access-logs', label: '访问记录', icon: 'list', title: '访问记录', description: '公共请求采用有界队列非阻塞入队，由后台批量写入。' },
  { key: 'mcp-logs', label: 'MCP 调用审计', icon: 'terminal', title: 'MCP 调用审计', description: '查看公开 MCP 调用的受控上下文、业务结果和采集状态。' },
  { key: 'audit', label: '管理审计', icon: 'shield', title: '管理审计', description: '管理员关键操作使用独立可靠审计，不与尽力记录的访问队列混用。' },
  { key: 'settings', label: '系统设置', icon: 'settings', title: '系统设置', description: '可信代理、访问白名单及持久化设置由服务端统一控制。' },
];

export default function ConsolePage() {
  const session = useConsoleSession();
  const { gate } = session;
  const [route, setRoute] = useState(() => ({ pathname: window.location.pathname, search: window.location.search }));
  const isLogin = route.pathname === '/console/login';
  const pathSection = route.pathname.split('/')[2];
  const active = sections.find(section => section.key === pathSection) || sections[0];

  useEffect(() => {
    const updateRoute = () => setRoute({ pathname: window.location.pathname, search: window.location.search });
    window.addEventListener('popstate', updateRoute);
    return () => window.removeEventListener('popstate', updateRoute);
  }, []);

  useEffect(() => {
    let target: string | null = null;
    if (gate === 'unauthenticated' && !isLogin) target = consoleLoginPath(route.pathname);
    if (gate === 'allowed' && isLogin) target = consoleReturnPath(new URLSearchParams(route.search).get('returnTo'));
    if (target) {
      // 替换身份入口历史，返回键不会重新展示上一段登录表单；回址已经过内部路径白名单校验。
      window.history.replaceState(null, '', target);
      setRoute({ pathname: window.location.pathname, search: window.location.search });
    }
  }, [gate, isLogin, route.pathname, route.search]);

  if (gate !== 'allowed' || isLogin) return <ConsoleLoginPage session={session} showLogin={isLogin} />;

  return <div className="console-app"><aside className="console-sidebar"><Brand compact /><div className="console-space-label">WORKSPACE <span>管理工作台</span></div><nav aria-label="管理导航">{sections.map(section => <a key={section.key} href={`/console/${section.key}`} className={section.key === active.key ? 'selected' : ''} aria-current={section.key === active.key ? 'page' : undefined}><Icon name={section.icon} size={19} />{section.label}{section.key === active.key ? <span className="nav-current-dot" /> : null}</a>)}</nav><div className="console-sidebar-bottom"><Icon name="shield" size={20} /><strong>受保护的管理空间</strong><p>来源白名单 + 管理员身份<br />每次请求由服务端重新核验</p><a href="/">返回公共查询 <Icon name="arrow" size={14} /></a></div></aside>
    <div className="console-workspace"><header className="console-header"><span><span className="console-breadcrumb">工作台 / </span>{active.label}</span><Tag color="#013187" variant="filled">管理空间</Tag><ThemeSwitcher />{session.user ? <div className="console-session"><span className="console-session-name" title={session.user.account}>{session.user.displayName || session.user.account}</span><Button size="small" onClick={() => void session.logout()} loading={session.busy}>退出登录</Button></div> : null}<a href="/" className="console-public-link">公共站点 <Icon name="external" size={14} /></a></header><main className="console-content"><div className="console-title"><div><div className="eyebrow">TONGYAN CONSOLE</div><h1>{active.title}</h1><p>{active.description}</p></div><span className="console-title-icon"><Icon name={active.icon} size={30} /></span></div>
      <ConsoleContext.Provider value={{ csrfToken: session.csrfToken, recheck: session.refresh }}>{active.key === 'overview' ? <OverviewPage /> : active.key === 'databases' ? <DatabasesPage /> : active.key === 'access-logs' ? <AccessLogsPage /> : active.key === 'mcp-logs' ? <McpLogsPage /> : active.key === 'audit' ? <AuditPage /> : <SettingsPage />}</ConsoleContext.Provider>
      {gate === 'allowed' && session.actionError ? <Alert type="error" showIcon title="退出登录未完成" description={session.actionError} role="alert" /> : null}
      <p className="console-disclaimer"><Icon name="shield" size={14} />界面状态不代替服务端权限；后台 API、页面与专属资源均应经过同一访问检查。</p>
    </main></div>
  </div>;
}
