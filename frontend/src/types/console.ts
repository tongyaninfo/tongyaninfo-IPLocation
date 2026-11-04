import type { ProviderDescriptor } from './ip-location';

export interface AccessQueueMetrics {
  received: number; accepted: number; written: number; dropped: number; writeFailures: number;
  suppressed?: number; queued: number; inFlight: number; isAccepting: boolean; lastSuccessUtc: string | null; lastFailureUtc: string | null;
}
export interface AuditRecord {
  id: string; occurredAtUtc: string; actor: string | null; sourceAddress: string; action: string;
  targetType: string; targetId: string | null; result: string; traceId: string; difference: unknown;
}
export interface ConsoleOverview {
  fromUtc: string; toUtc: string; capturedAtUtc: string; storageKind: string; startedAtUtc: string; retentionDays: number;
  totals: { requests: number; homeHtmlRequests: number; plainIpRequests: number; queryRequests: number; clientErrors: number; serverErrors: number; cancelledRequests: number; averageMilliseconds: number | null; p95Milliseconds: number | null };
  trend: { timeUtc: string; requests: number; queryRequests: number; errors: number }[];
  mcpTotals: { requests: number; queryRequests: number; httpErrors: number };
  mcpTrend: { timeUtc: string; requests: number; queryRequests: number; errors: number }[];
  queue: AccessQueueMetrics; notes: string[]; recentAudits: AuditRecord[];
}
/** 服务端清洗后的请求头及路径元数据；路径同时由访问记录顶层字段提供。 */
export interface RequestHeadersSnapshot {
  headers: Record<string, string[]>; truncated: boolean;
  requestPath?: string | null; requestPathTruncated?: boolean;
}
/** 后端按记录的来源IP解析出的来源快照，与用户提交的查询目标无关。 */
export interface SourceLocationSnapshot {
  provider: string; version: string | null; status: string; country: string | null; province: string | null;
  city: string | null; isp: string | null; rawLocation: string | null;
}
export interface AccessRecord {
  id: string; occurredAtUtc: string; sourceAddress: string; routeCategory: string; method: string; format: string;
  database: string | null; databaseVersion: string | null; statusCode: number; elapsedMilliseconds: number;
  isCancelled: boolean; clientCategory: string; traceId: string;
  /** 后端采集的原始路径及查询串；历史记录可缺省，超限时明确标记。 */
  requestPath?: string | null;
  requestPathTruncated?: boolean;
  requestHeaders?: RequestHeadersSnapshot | null;
  sourceLocation?: SourceLocationSnapshot | null;
  mcpContext?: McpCallContext | null;
}
/** MCP 只采集白名单字段；客户端名称和版本为匿名客户端自报，不能证明调用者身份。 */
export interface McpCallContext {
  protocolVersion: string | null; method: string | null; requestId: string | null; toolName: string | null;
  clientName: string | null; clientVersion: string | null; ip: string | null; database: string | null;
  resultCode: string | null; resultStatus: string | null; isError: boolean;
  sources: { id: string; version: string | null; status: string }[]; truncated: boolean;
  input?: unknown | null; output?: unknown | null; inputTruncated?: boolean; outputTruncated?: boolean;
  inputCaptured?: boolean; outputCaptured?: boolean;
}
export interface PagedResult<T> { items: T[]; total: number; page: number; pageSize: number }
export interface AccessRecords extends PagedResult<AccessRecord> {
  fromUtc: string; toUtc: string; retentionDays: number | null; queue: AccessQueueMetrics;
}
export interface DatabaseVersion {
  id: string; version: string; addressFamily: string; format: string; sizeBytes: number; sha256: string;
  status: string; active: boolean; createdAtUtc: string; licenseConfirmed: boolean; licenseReviewMode?: 'manual' | null;
}
export interface DatabaseUpload {
  id: string; state: string; phase: string; receivedBytes: number; errorCode: string | null;
  startedAtUtc: string; finishedAtUtc: string | null;
}
export interface ManagedProvider extends ProviderDescriptor {
  revision: number; versions: DatabaseVersion[]; uploads: DatabaseUpload[];
}
export interface ConsoleDatabases { providers: ManagedProvider[]; defaultDatabase: string; uploadMaxBytes: number; displayOrderRevision: number }
export interface QuerySettings { defaultDatabase: string; providerTimeoutMs: number; totalTimeoutMs: number; maxConcurrentSources: number }
export interface ConsoleBasicSettings { accessLogRetentionDays: number | null; retainFullSourceIp: boolean; sessionMinutes: number; queueCapacity: number }
export interface ConsoleSettings {
  basic?: ConsoleBasicSettings | null;
  revision: number; currentSourceAddress: string | null; query: QuerySettings; allowedNetworks: string[]; providers: ProviderDescriptor[];
  runtime: { databaseEnabled: boolean; retainFullSourceIp: boolean; recentEventRetentionDays: number | null; queueCapacity: number; sessionMinutes: number; trustedProxyCount: number; forwardLimit: number; queueCapacityRequiresRestart?: boolean };
}
