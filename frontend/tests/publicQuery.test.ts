import { describe, expect, it } from 'vitest';
import { buildPublicQueryPath, isRootQueryRequest } from '../src/utils/publicQuery';

describe('根参数查询URL', () => {
  it('省略IP及格式时不补造地址或强制添加格式，旧纯真来源统一编码', () => {
    expect(buildPublicQueryPath()).toBe('/');
    expect(buildPublicQueryPath({ db: 'all' })).toBe('/?db=all');
    expect(buildPublicQueryPath({ format: 'txt' })).toBe('/?format=txt');
    expect(buildPublicQueryPath({ ip: '1.1.1.1' })).toBe('/?ip=1.1.1.1');
    expect(buildPublicQueryPath({ ip: '2001:4860::8888', db: 'chunzhen', format: 'xml' }))
      .toBe('/?ip=2001%3A4860%3A%3A8888&db=CZ88&format=xml');
  });

  it('参数值中的分隔符只属于原参数，不产生额外参数或shell单引号', () => {
    const path = buildPublicQueryPath({ ip: "invalid&db=other'", db: 'all', format: 'json' });
    const url = new URL(path, 'http://localhost');
    expect(url.searchParams.get('ip')).toBe("invalid&db=other'");
    expect(url.searchParams.getAll('db')).toEqual(['all']);
    expect(path).not.toContain("'");
  });

  it.each(['/?ip=1.1.1.1', '/?ip=', '/?db=all', '/?FORMAT=JSON', '/?IP=1.1.1.1', '/?Db=',
    '/?utm=x&format=txt', '/?db=dbip&db=all', '/?%69p=1.1.1.1'])('显式根查询转交后端检查：%s', path => {
    expect(isRootQueryRequest(path)).toBe(true);
  });

  it.each(['/', '/?v=tag', '/?utm_source=test', '/?notip=x', '/api/providers?db=all', '/search/1.1.1.1?db=all'])
    ('普通首页或其他路径不作为新根查询：%s', path => {
      expect(isRootQueryRequest(path)).toBe(false);
    });
});
