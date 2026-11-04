export interface ApiResult<T> {
  success: boolean;
  code: string;
  message: string;
  data: T | null;
}

export interface ProviderDescriptor {
  id: string;
  name: string;
  status: string;
  supportsIpv4: boolean;
  supportsIpv6: boolean;
  enabled: boolean;
  licenseConfirmed: boolean;
  licenseReviewMode?: 'manual' | null;
  ready: boolean;
  version: string | null;
  message: string | null;
  licenseUrl: string | null;
  attribution: string | null;
}

export interface IpFields {
  countryCode?: string | null;
  country?: string | null;
  continentCode?: string | null;
  continent?: string | null;
  region?: string | null;
  city?: string | null;
  isp?: string | null;
  asn?: string | number | null;
  asName?: string | null;
  asDomain?: string | null;
  latitude?: number | null;
  longitude?: number | null;
  postalCode?: string | null;
  timeZone?: string | null;
}

export interface ProviderResult {
  id: string;
  name: string;
  status: string;
  version: string | null;
  fields: IpFields;
  elapsedMs: number;
  message: string | null;
  licenseUrl: string | null;
  attribution: string | null;
}

export interface QueryResult {
  ip: string;
  addressType: string;
  status: string;
  sources: ProviderResult[];
}

export interface ApiResponse<T> {
  httpStatus: number;
  result: ApiResult<T>;
}

export interface ConsoleAccess {
  allowed: boolean;
  initialized: boolean;
  authenticated: boolean;
  user: ConsoleUser | null;
  message?: string;
}

export interface ConsoleUser {
  id: string;
  account: string;
  displayName: string;
}

export interface ConsoleCsrf {
  token: string;
}

export interface ConsoleLoginRequest {
  account: string;
  password: string;
}
