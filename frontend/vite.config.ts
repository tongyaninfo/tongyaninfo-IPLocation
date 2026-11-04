import { defineConfig } from 'vitest/config';
import react from '@vitejs/plugin-react';
import { readFileSync, realpathSync } from 'node:fs';
import { createRequire } from 'node:module';
import path from 'node:path';
import { normalizePath, type Plugin } from 'vite';
import { isRootQueryRequest } from './src/utils/publicQuery.ts';

const require = createRequire(import.meta.url);
const formPackageFile = require.resolve('@rc-component/form/package.json');
const formEntry = normalizePath(realpathSync(path.join(path.dirname(formPackageFile), 'es/index.js')));

/** 仅修正已核验的 Form 总入口副作用标记，不改变组件代码或其他包的摇树规则。 */
function formEntryTreeShaking(): Plugin {
  return {
    name: 'reviewed-form-entry-tree-shaking',
    apply: 'build',
    enforce: 'pre',
    buildStart() {
      const metadata = JSON.parse(readFileSync(formPackageFile, 'utf8')) as { version: string };
      // 1.8.6 的入口只为默认 RefForm 挂载静态方法；当前仅间接引用上下文。
      // 升级后重新核验入口语义，不把此次结论推广到其他版本或模块。
      if (metadata.version !== '1.8.6') this.error('Review @rc-component/form entry side effects before upgrading.');
    },
    transform(code, id) {
      if (normalizePath(id.split('?')[0]) !== formEntry) return;
      return { code, map: null, moduleSideEffects: false };
    },
  };
}

export default defineConfig({
  plugins: [formEntryTreeShaking(), react()],
  server: {
    port: 5173,
    strictPort: true,
    proxy: {
      // 开发代理不伪造访客头；真实地址识别始终由后端可信代理配置负责。
      '/api': { target: 'http://127.0.0.1:5080', changeOrigin: false, xfwd: false },
      '/search': { target: 'http://127.0.0.1:5080', changeOrigin: false, xfwd: false },
      '/mcp': { target: 'http://127.0.0.1:5080', changeOrigin: false, xfwd: false },
      '^/\\?': {
        target: 'http://127.0.0.1:5080', changeOrigin: false, xfwd: false,
        // 仅含 utm/v 等参数的首页继续交给 Vite；显式空值也须转交后端验证。
        bypass(request) { if (!isRootQueryRequest(request.url || '/')) return request.url || '/'; },
      },
    },
  },
  build: {
    // Chrome 79 不支持可选链、空值合并等语法，依赖也须一起降级，避免入口解析即白屏。
    target: 'chrome79',
    cssTarget: 'chrome79',
    // build.mjs 使用新版本目录；不发布 public 中保留的高清原图、旧图和说明文档。
    emptyOutDir: false,
    copyPublicDir: false,
    assetsInlineLimit: 0,
    sourcemap: false,
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./tests/setup.ts'],
    include: ['tests/**/*.test.{ts,tsx}'],
    clearMocks: true,
  },
});
