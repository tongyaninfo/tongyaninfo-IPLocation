import { describe, expect, it } from 'vitest';
import { browserStyleCompatibility } from '../src/utils/browserCompatibility';

describe('旧浏览器样式能力检测', () => {
  it('现代浏览器保留原样式优先级和逻辑属性', () => {
    expect(browserStyleCompatibility({ supports: () => true })).toEqual({
      legacySelectors: false, legacyLogicalProperties: false,
    });
  });

  it('Chrome 79缺失能力时启用选择器与布局属性降级', () => {
    expect(browserStyleCompatibility({ supports: () => false })).toEqual({
      legacySelectors: true, legacyLogicalProperties: true,
    });
  });

  it('按各项能力独立降级，不把新浏览器统一改为高优先级', () => {
    expect(browserStyleCompatibility({ supports: (property: string) => property !== 'inset' })).toEqual({
      legacySelectors: false, legacyLogicalProperties: true,
    });
  });

  it('CSS能力检测抛错时仍可启动页面', () => {
    expect(browserStyleCompatibility({ supports: () => { throw new Error('Unsupported detection'); } })).toEqual({
      legacySelectors: true, legacyLogicalProperties: true,
    });
  });
});
