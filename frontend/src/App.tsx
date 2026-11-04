import { lazy, Suspense } from 'react';
import { ConfigProvider, theme } from 'antd';
import { legacyLogicalPropertiesTransformer, StyleProvider } from '@ant-design/cssinjs';
import zhCN from 'antd/es/locale/zh_CN';
import HomePage from './pages/Home/HomePage';
import { ThemeProvider, useTheme } from './models/useTheme';
import { browserStyleCompatibility } from './utils/browserCompatibility';

const styleCompatibility = browserStyleCompatibility();
const legacyTransformers = [legacyLogicalPropertiesTransformer];

// 控制台独立 chunk，由后端同时保护 /assets/console-* 与 /console 入口。
const ConsolePage = lazy(() => import('./pages/Console/console'));

export default function App() {
  return <StyleProvider hashPriority={styleCompatibility.legacySelectors ? 'high' : 'low'}
    transformers={styleCompatibility.legacyLogicalProperties ? legacyTransformers : undefined}>
    <ThemeProvider><ThemedApp /></ThemeProvider>
  </StyleProvider>;
}

function ThemedApp() {
  const dark = useTheme()?.resolvedTheme === 'dark';
  const palette = dark
    ? { text: '#e7edf7', muted: '#aab9ce', border: '#30445f', canvas: '#0d1626', surface: '#17263b' }
    : { text: '#173047', muted: '#718297', border: '#e1e9ef', canvas: '#f7fafc', surface: '#ffffff' };
  const isConsole = window.location.pathname === '/console' || window.location.pathname.startsWith('/console/');
  return <ConfigProvider locale={zhCN} theme={{
    algorithm: dark ? theme.darkAlgorithm : theme.defaultAlgorithm,
    token: {
      colorPrimary: '#013187', colorInfo: dark ? '#9dc2ff' : '#013187', colorLink: dark ? '#9dc2ff' : '#013187',
      colorWarning: '#EF8200', borderRadius: 10,
      fontFamily: '-apple-system, BlinkMacSystemFont, "Segoe UI", "PingFang SC", "Microsoft YaHei", sans-serif',
      colorText: palette.text, colorTextSecondary: palette.muted, colorBorder: palette.border,
      colorTextPlaceholder: dark ? '#8b9fb8' : undefined, colorIcon: dark ? '#8b9fb8' : undefined,
      colorBgLayout: palette.canvas, colorBgContainer: palette.surface, colorBgElevated: palette.surface
    },
    components: {
      Button: { controlHeightLG: 52, fontWeight: 600, primaryShadow: '0 4px 12px rgba(1,49,135,.16)' },
      Input: { controlHeightLG: 52, activeShadow: dark ? '0 0 0 3px rgba(157,194,255,.15)' : '0 0 0 3px rgba(1,49,135,.08)' },
      Tabs: { inkBarColor: '#EF8200' },
      Table: { headerBg: dark ? '#1e324d' : '#f7f9fc', headerColor: palette.muted }
    }
  }}><Suspense fallback={<div className="app-loading" role="status"><span />正在载入管理空间…</div>}>{isConsole ? <ConsolePage /> : <HomePage />}</Suspense></ConfigProvider>;
}
