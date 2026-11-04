/** 按实际CSS能力选择兼容样式；新版浏览器继续使用原有低优先级和逻辑属性。 */
export function browserStyleCompatibility(css: Pick<typeof CSS, 'supports'> | undefined = globalThis.CSS) {
  const supports = (property: string, value?: string) => {
    try {
      return value === undefined ? css?.supports(property) === true : css?.supports(property, value) === true;
    } catch {
      // 老浏览器不认识 selector(...) 检测语法时也按不支持处理。
      return false;
    }
  };
  return {
    legacySelectors: !supports('selector(:where(*))'),
    legacyLogicalProperties: !supports('inset', '0px') || !supports('margin-inline-start', '1px'),
  };
}

/** Grid 的 gap 支持不能证明 Flex 支持，使用真实布局测量，避免旧 Chrome 误报。 */
export function supportsFlexGap(document: Document): boolean {
  const probe = document.createElement('div');
  probe.style.cssText = 'position:absolute;visibility:hidden;top:-9999px;display:flex;flex-direction:column;row-gap:1px;';
  probe.appendChild(document.createElement('div'));
  probe.appendChild(document.createElement('div'));
  document.documentElement.appendChild(probe);
  const supported = probe.scrollHeight === 1;
  probe.remove();
  return supported;
}
