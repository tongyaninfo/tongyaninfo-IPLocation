import { describe, expect, it, vi } from 'vitest';
import { getProviders, queryIp, queryVisitorIp, requestApi } from '../src/services/ip-location/api';
import { activateDatabase, getDatabases, getSettings, saveDatabaseOrder, saveSettings, setDatabaseState } from '../src/services/console/api';
import { apiResponse, databases, settings } from './consoleFixtures';
import type { QueryResult } from '../src/types/ip-location';

describe('公开接口响应边界', () => {
  it('默认访客与手动IPv6查询使用同一根参数合同并保持no-store和取消信号', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(apiResponse({ ip: '1.1.1.1', sources: [] })));
    vi.stubGlobal('fetch', fetchMock);
    const controller = new AbortController();
    await queryVisitorIp(controller.signal);
    await queryIp('2001:4860:4860::8888', controller.signal);
    expect(fetchMock.mock.calls.map(([url]) => url)).toEqual([
      '/?db=all&format=json', '/?ip=2001%3A4860%3A4860%3A%3A8888&db=all&format=json',
    ]);
    for (const [, options] of fetchMock.mock.calls) {
      expect(options.cache).toBe('no-store');
      expect(options.headers.Accept).toBe('application/json');
      expect(options.signal).toBe(controller.signal);
    }
  });
  it('保留 503 中的真实 IP 和单源状态，不作为网络失败丢弃', async () => {
    const data: QueryResult = { ip: '203.0.113.5', addressType: 'documentation', status: 'unavailable', sources: [{ id: 'dbip', name: 'DB-IP Lite', status: 'notReady', version: null, fields: {}, elapsedMs: 0, message: '数据文件尚未配置。', licenseUrl: null, attribution: null }] };
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ success: false, code: 'DB_NOT_READY', message: '没有已就绪来源。', data }), { status: 503 })));
    const response = await requestApi<QueryResult>('/?db=all&format=json');
    expect(response.httpStatus).toBe(503);
    expect(response.result.data?.ip).toBe('203.0.113.5');
    expect(response.result.data?.sources[0].status).toBe('notReady');
  });

  it('拒绝非合同响应，不把任意对象当成成功结果', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(JSON.stringify({ message: 'proxy response' }))));
    await expect(requestApi('/api/providers')).rejects.toThrow('返回格式不正确');
  });

  it('网络异常使用可展示信息，不暴露底层连接细节', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('private upstream details')));
    await expect(requestApi('/api/providers')).rejects.toThrow('暂时无法连接查询服务');
  });
});


describe('CZ88来源编码兼容', () => {
  it('旧公开来源与查询响应统一为CZ88，保留地区原文和网络描述', async () => {
    const legacyProvider = { ...databases.providers[0], id: 'chunzhen', name: '纯真 QQWry' };
    const data: QueryResult = { ip: '203.0.113.5', addressType: 'public', status: 'success', sources: [{ id: 'chunzhen', name: '纯真 QQWry', status: 'success', version: null, fields: { region: '中国–安徽–蚌埠', isp: '示例网络组织' }, elapsedMs: 1, message: null, licenseUrl: null, attribution: null }] };
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(apiResponse(path === '/api/providers' ? [legacyProvider] : data))));
    expect((await getProviders()).result.data?.map(provider => provider.id)).toEqual(['CZ88']);
    const result = (await queryVisitorIp()).result.data!;
    expect(result.sources[0].id).toBe('CZ88');
    expect(result.sources[0].fields).toEqual({ region: '中国–安徽–蚌埠', isp: '示例网络组织' });
  });

  it('旧后台目录与默认来源编码统一为CZ88', async () => {
    const legacyProvider = { ...databases.providers[0], id: 'chunzhen', name: '纯真 QQWry' };
    vi.stubGlobal('fetch', vi.fn((path: string) => Promise.resolve(apiResponse(path.endsWith('/settings')
      ? { ...settings, providers: [legacyProvider], query: { ...settings.query, defaultDatabase: 'chunzhen' } }
      : { ...databases, providers: [legacyProvider], defaultDatabase: 'chunzhen' }))));
    const catalog = await getDatabases();
    expect(catalog.defaultDatabase).toBe('CZ88'); expect(catalog.providers.map(provider => provider.id)).toEqual(['CZ88']);
    const config = await getSettings();
    expect(config.query.defaultDatabase).toBe('CZ88'); expect(config.providers.map(provider => provider.id)).toEqual(['CZ88']);
  });

  it('来自旧状态的后台写入也发送CZ88编码', async () => {
    const fetchMock = vi.fn((path: string, _options?: RequestInit) => Promise.resolve(apiResponse(path.endsWith('/settings') ? settings : {})));
    vi.stubGlobal('fetch', fetchMock);
    await saveDatabaseOrder({ providerIds: ['chunzhen', 'dbip'], revision: 1 }, 'controlled-token');
    await setDatabaseState('chunzhen', { enabled: true, revision: 0, defaultDatabase: 'chunzhen' }, 'controlled-token');
    await activateDatabase('chunzhen', { versionId: '1', revision: 1 }, 'controlled-token');
    await saveSettings({ revision: 1, allowedNetworks: ['127.0.0.1'], query: { ...settings.query, defaultDatabase: 'chunzhen' } }, 'controlled-token');
    const calls = fetchMock.mock.calls;
    expect(JSON.parse(String(calls[0][1]?.body)).providerIds).toEqual(['CZ88', 'dbip']);
    expect(calls[1][0]).toBe('/api/console/databases/CZ88/state');
    expect(JSON.parse(String(calls[1][1]?.body)).defaultDatabase).toBe('CZ88');
    expect(calls[2][0]).toBe('/api/console/databases/CZ88/activate');
    expect(JSON.parse(String(calls[3][1]?.body)).query.defaultDatabase).toBe('CZ88');
  });
});
