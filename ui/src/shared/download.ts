// Download helper (task-72 ⑥): fetch → Blob → object URL → <a download>. No `location.assign`, so the
// behaviour is observable in tests and works in environments without navigation.

export interface DownloadDependencies {
  fetchImpl?: typeof fetch;
  createObjectUrl?: (blob: Blob) => string;
  revokeObjectUrl?: (url: string) => void;
  createAnchor?: () => HTMLAnchorElement;
  scheduleRevoke?: boolean;
}

export interface DownloadResult {
  fileName: string;
  bytes: number;
  url: string;
  /** Diagnostics bundles report their own accounting through response headers. */
  truncated?: boolean;
  files?: number;
}

/** Extracts the attachment filename, preferring RFC 5987 `filename*=` over plain `filename=`. */
export function fileNameFromDisposition(disposition: string | null, fallback: string): string {
  if (!disposition) {
    return fallback;
  }

  const extended = /filename\*=UTF-8''([^;]+)/i.exec(disposition);
  if (extended) {
    try {
      return decodeURIComponent(extended[1].trim().replace(/^"|"$/g, ""));
    } catch {
      // fall through to the plain parameter
    }
  }

  const plain = /filename="?([^";]+)"?/i.exec(disposition);
  return plain ? plain[1].trim() : fallback;
}

/**
 * Downloads `path` as a blob. Throws an Error carrying `code` when the response is not ok, so callers
 * can render the same error block as the rest of the console.
 */
export async function downloadFile(path: string, fallbackName: string, deps: DownloadDependencies = {}): Promise<DownloadResult> {
  const fetchImpl = deps.fetchImpl ?? globalThis.fetch;
  if (typeof fetchImpl !== "function") {
    throw Object.assign(new Error("当前环境不支持 fetch"), { code: "download.unsupported" });
  }

  const response = await fetchImpl(path, { credentials: "same-origin", cache: "no-store" });
  if (!response.ok) {
    const payload = await response.json().catch(() => null);
    throw Object.assign(new Error(payload?.error?.message ?? `HTTP ${response.status}`), {
      code: payload?.error?.code ?? `http.${response.status}`,
      status: response.status,
    });
  }

  const blob = await response.blob();
  const fileName = fileNameFromDisposition(response.headers.get("Content-Disposition"), fallbackName);

  const createUrl = deps.createObjectUrl ?? ((value: Blob) => URL.createObjectURL(value));
  const revokeUrl = deps.revokeObjectUrl ?? ((value: string) => URL.revokeObjectURL(value));
  const url = createUrl(blob);

  const createAnchor =
    deps.createAnchor
    ?? (() => {
      const anchor = document.createElement("a");
      anchor.rel = "noopener";
      return anchor;
    });

  const anchor = createAnchor();
  anchor.href = url;
  anchor.download = fileName;
  anchor.click();

  if (deps.scheduleRevoke !== false) {
    globalThis.setTimeout(() => revokeUrl(url), 0);
  } else {
    revokeUrl(url);
  }

  const filesHeader = response.headers.get("X-WebConsole-Diag-Files");
  return {
    fileName,
    bytes: blob.size,
    url,
    truncated: response.headers.get("X-WebConsole-Diag-Truncated") === "true",
    files: filesHeader && Number.isFinite(Number(filesHeader)) ? Number(filesHeader) : undefined,
  };
}
