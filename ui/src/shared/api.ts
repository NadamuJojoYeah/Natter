// Same-origin API client shared by both consoles. No CDN, no external origin: the CSP is
// `connect-src 'self'` and the build must keep it that way.

export interface ApiError extends Error {
  status: number;
  code: string;
  detail?: unknown;
  result?: unknown;
}

export class ConsoleApiError extends Error implements ApiError {
  readonly status: number;

  readonly code: string;

  readonly detail?: unknown;

  readonly result?: unknown;

  constructor(message: string, options: { status?: number; code?: string; detail?: unknown; result?: unknown } = {}) {
    super(message);
    this.name = "ConsoleApiError";
    this.status = options.status ?? 0;
    this.code = options.code ?? `http.${options.status ?? 0}`;
    this.detail = options.detail;
    this.result = options.result;
  }
}

let csrfToken: string | null = null;

export function setCsrfToken(token: string | null): void {
  csrfToken = token;
}

export interface RequestOptions {
  method?: "GET" | "POST" | "DELETE";
  body?: unknown;
  /** Abort signal so views can cancel in-flight reads when they unmount. */
  signal?: AbortSignal;
}

export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const method = options.method ?? "GET";
  const headers: Record<string, string> = { Accept: "application/json" };

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  if (csrfToken && method !== "GET") {
    headers["X-WebConsole-CSRF"] = csrfToken;
  }

  let response: Response;
  try {
    response = await fetch(path, {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: "same-origin",
      cache: "no-store",
      signal: options.signal,
    });
  } catch (error) {
    throw new ConsoleApiError("网络不可达（宿主可能已停止）", {
      code: "network.unreachable",
      detail: String(error),
    });
  }

  let payload: unknown = null;
  if (response.status !== 204) {
    payload = await response.json().catch(() => null);
  }

  if (!response.ok) {
    const error = (payload as { error?: { code?: string; message?: string } } | null)?.error;
    throw new ConsoleApiError(error?.message ?? `HTTP ${response.status}`, {
      status: response.status,
      code: error?.code,
      detail: payload,
    });
  }

  return payload as T;
}

/** Normalizes anything thrown by the client into a shape the three-state views can render. */
export function describeError(error: unknown): { code: string; message: string; status: number } {
  if (error instanceof ConsoleApiError) {
    return { code: error.code, message: error.message, status: error.status };
  }

  return {
    code: "ui.error",
    message: error instanceof Error ? error.message : String(error),
    status: 0,
  };
}
