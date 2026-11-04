import { createRoot } from 'react-dom/client';
import App from './App';
import { supportsFlexGap } from './utils/browserCompatibility';
import 'antd/dist/reset.css';
import './styles/global.css';

const root = document.getElementById('root');
if (!root) throw new Error('应用根节点不存在。');
// 只给缺失 Flex gap 的浏览器加间距回退，新版浏览器保持原布局。
document.documentElement.dataset.flexGap = supportsFlexGap(document) ? 'supported' : 'legacy';
createRoot(root).render(<App />);
