// Same-origin fetch wrapper: normalizes errors into {status, code, message, detail, result}.
// Every request stays same-origin (CSP connect-src 'self'); no CDN, no third-party endpoint.

import { state } from "./state.js";

export class ApiError extends Error {
  constructor(message, { status = 0, code = null, detail = null, result = null } = {}) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.code = code;
    this.detail = detail;
    this.result = result;
  }
}

export async function request(path, options = {}) {
  const method = options.method || "GET";
  const headers = { Accept: options.accept || "application/json" };

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  if (state.csrf && method !== "GET") {
    headers["X-WebConsole-CSRF"] = state.csrf;
  }

  let response;
  try {
    response = await fetch(path, {
      method,
      headers,
      body: options.body === undefined ? undefined : JSON.stringify(options.body),
      credentials: "same-origin",
      cache: "no-store",
    });
  } catch (error) {
    throw new ApiError("网络不可达（宿主可能已停止）", { code: "network.unreachable", detail: String(error) });
  }

  let payload = null;
  if (response.status !== 204) {
    try {
      payload = await response.json();
    } catch {
      payload = null;
    }
  }

  if (!response.ok) {
    throw new ApiError(payload?.error?.message || `HTTP ${response.status}`, {
      status: response.status,
      code: payload?.error?.code ?? `http.${response.status}`,
      detail: payload?.detail ?? null,
      result: payload?.result ?? null,
    });
  }

  return payload;
}

/** Normalizes an unknown thrown value into {code, message, status}. */
export function describeError(error) {
  if (error instanceof ApiError) {
    return { code: error.code, message: error.message, status: error.status, detail: error.detail };
  }

  return { code: "ui.error", message: error?.message ? String(error.message) : "未知错误", status: 0, detail: null };
}

/** Downloads a same-origin attachment; guarded so tests can run without a location object. */
export function download(path) {
  const location = globalThis?.location;
  if (location && typeof location.assign === "function") {
    location.assign(path);
  }

  return path;
}
