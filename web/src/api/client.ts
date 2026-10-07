import type { SessionInfo } from './types';

export class ApiError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
  }
}

interface ErrorPayload {
  error?: string;
  requestId?: string;
}

export interface RequestOptions {
  method?: string;
  json?: unknown;
  body?: BodyInit;
}

type FetchFn = (input: string, init?: RequestInit) => Promise<Response>;

export class ApiClient {
  private csrfToken: string | null = null;
  private unauthorized: () => void;
  private readonly fetchFn: FetchFn;

  constructor(onUnauthorized: () => void = () => undefined, fetchFn: FetchFn = (input, init) => fetch(input, init)) {
    this.unauthorized = onUnauthorized;
    this.fetchFn = fetchFn;
  }

  setUnauthorizedHandler(handler: () => void): void {
    this.unauthorized = handler;
  }

  clearSession(): void {
    this.csrfToken = null;
  }

  async loadSession(): Promise<SessionInfo> {
    const session = await this.request<SessionInfo>('/api/session');
    this.csrfToken = session.csrfToken;
    return session;
  }

  async request<T>(path: string, options: RequestOptions = {}): Promise<T> {
    const method = options.method ?? 'GET';
    const headers: Record<string, string> = {};
    if (method !== 'GET') {
      if (this.csrfToken === null) await this.loadSession();
      if (this.csrfToken !== null) headers['X-CSRF-Token'] = this.csrfToken;
    }
    let body = options.body;
    if (options.json !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(options.json);
    }
    const response = await this.fetchFn(path, { method, headers, body, credentials: 'same-origin' });
    const payload: unknown = await response.json().catch(() => ({}));
    if (!response.ok) {
      if (response.status === 401) {
        this.csrfToken = null;
        this.unauthorized();
      }
      const { error, requestId } = (payload ?? {}) as ErrorPayload;
      const reference = requestId ? ` Reference: ${requestId}.` : '';
      throw new ApiError((error || `Request failed (${response.status}).`) + reference, response.status);
    }
    return payload as T;
  }

  get<T>(path: string): Promise<T> {
    return this.request<T>(path);
  }

  post<T>(path: string, options: Omit<RequestOptions, 'method'> = {}): Promise<T> {
    return this.request<T>(path, { ...options, method: 'POST' });
  }

  put<T>(path: string, options: Omit<RequestOptions, 'method'> = {}): Promise<T> {
    return this.request<T>(path, { ...options, method: 'PUT' });
  }
}

export const api = new ApiClient();

export const isUnauthorized = (error: unknown): boolean => error instanceof ApiError && error.status === 401;

export const errorMessage = (error: unknown): string => (error instanceof Error ? error.message : String(error));
