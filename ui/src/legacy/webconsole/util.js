// Pure helpers shared by every view. No DOM access, no imports — safe to unit test with node.
// UI-DIRECTION §4.2: util.js owns escaping, formatting, truncation markers and the state blocks.

/** Escapes text for HTML text/attribute context. Every dynamic value must pass through this. */
export function escapeHtml(value) {
  if (value === null || value === undefined) {
    return "";
  }

  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

/** Formats an ISO timestamp; falls back to the raw value when unparsable. */
export function formatTime(value) {
  if (!value) {
    return "—";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
}

/** HH:mm:ss.SSS for timeline rows. */
export function formatClock(value) {
  const date = value instanceof Date ? value : new Date(value);
  if (Number.isNaN(date.getTime())) {
    return "—";
  }

  const pad = (number, width = 2) => String(number).padStart(width, "0");
  return `${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}.${pad(date.getMilliseconds(), 3)}`;
}

/** Human duration: 12ms / 1.24s / 2m 5s. */
export function formatDuration(milliseconds) {
  if (milliseconds === null || milliseconds === undefined || Number.isNaN(milliseconds)) {
    return "—";
  }

  const value = Math.max(0, Number(milliseconds));
  if (value < 1000) {
    return `${Math.round(value)}ms`;
  }
  if (value < 60_000) {
    return `${(value / 1000).toFixed(2)}s`;
  }

  const minutes = Math.floor(value / 60_000);
  const seconds = Math.round((value % 60_000) / 1000);
  return `${minutes}m ${seconds}s`;
}

/** Byte count with a stable unit. */
export function formatBytes(bytes) {
  if (bytes === null || bytes === undefined || Number.isNaN(bytes)) {
    return "—";
  }

  const value = Math.max(0, Number(bytes));
  if (value < 1024) {
    return `${value} B`;
  }
  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KiB`;
  }

  return `${(value / (1024 * 1024)).toFixed(2)} MiB`;
}

/**
 * Truncation marker (UI-DIRECTION §3.2): truncating is allowed, hiding the original size is not.
 * Every truncating renderer must print this next to the shortened content.
 */
export function truncationNote({ original, shown, unit = "字符", extra = null }) {
  const hidden = Math.max(0, Number(original ?? 0) - Number(shown ?? 0));
  const parts = [
    `原文 ${Number(original ?? 0)} ${unit}`,
    `显示 ${Number(shown ?? 0)} ${unit}`,
  ];

  if (hidden > 0) {
    const hiddenText = unit === "行" ? `已截断 ${hidden} 行` : `已截断 ${hidden} ${unit}`;
    parts.push(hiddenText);
  }

  if (extra) {
    parts.push(extra);
  }

  return `<p class="truncation">${escapeHtml(parts.join(" · "))}</p>`;
}

/** Status tone used by badges: ok / degraded / error / unknown. */
export function statusTone(status) {
  const value = String(status ?? "").toLowerCase();
  if (value === "ok" || value === "completed" || value === "listening" || value === "activated") {
    return "ok";
  }
  if (value === "degraded" || value === "pending" || value === "running" || value === "external" || value === "warning") {
    return "degraded";
  }
  if (value === "error" || value === "failed" || value === "cancelled" || value === "unavailable") {
    return "error";
  }

  return "unknown";
}

export function badgeHtml(status, label = null) {
  const text = label ?? status ?? "—";
  return `<span class="badge ${statusTone(status)}">${escapeHtml(text)}</span>`;
}

/**
 * Three-state block (UI-DIRECTION §3.3). `emptyReason` must be one of the four kinds below so the
 * operator can tell "nothing happened" from "you cannot see it" from "it is switched off" from
 * "the host does not implement it".
 */
export const EMPTY_REASONS = Object.freeze({
  "no-data": "当前没有数据。",
  "no-permission": "需要 admin 角色。",
  disabled: "功能未启用（readOnly 或 allowConfigWrite=false）。",
  "host-unavailable": "宿主管理面不可用（501）。",
});

export function stateBlock({ kind, emptyReason = "no-data", message = null, code = null, hint = null }) {
  if (kind === "loading") {
    return `<div class="state loading" role="status" aria-busy="true">`
      + `<span class="skeleton"></span><span class="skeleton short"></span>`
      + `<p class="muted">加载中…</p></div>`;
  }

  if (kind === "error") {
    const codeHtml = code
      ? `<button type="button" class="code copyable" data-copy="${escapeHtml(code)}" title="复制错误码">${escapeHtml(code)}</button>`
      : "";
    return `<div class="state error" role="alert">${codeHtml}`
      + `<p>${escapeHtml(message ?? "请求失败。")}</p>`
      + (hint ? `<p class="doc">${escapeHtml(hint)}</p>` : "")
      + `<button type="button" class="retry" data-action="retry">重试</button></div>`;
  }

  const text = EMPTY_REASONS[emptyReason] ?? EMPTY_REASONS["no-data"];
  return `<div class="state empty" data-empty-reason="${escapeHtml(emptyReason)}">`
    + `<p>${escapeHtml(message ?? text)}</p>`
    + (emptyReason === "no-permission" ? `<p class="doc">viewer 只读：登录 admin 可解锁该区域。</p>` : "")
    + (emptyReason === "host-unavailable" ? `<p class="doc">宿主未桥接该契约，插件以 501 兜底。</p>` : "")
    + "</div>";
}

/** Deterministic HTML for a table (used by the tabular views). */
export function tableHtml(headers, rows) {
  const head = headers.map((header) => `<th>${escapeHtml(header)}</th>`).join("");
  const body = rows
    .map((row) => `<tr>${row.map((cell) => `<td>${cell === null || cell === undefined || cell === "" ? "—" : cell}</td>`).join("")}</tr>`)
    .join("");

  return `<table><thead><tr>${head}</tr></thead><tbody>${body}</tbody></table>`;
}

/** Clips a string to `max` characters and reports the original length for the marker. */
export function clip(value, max) {
  const text = value === null || value === undefined ? "" : String(value);
  return {
    original: text.length,
    shown: Math.min(text.length, max),
    text: text.length <= max ? text : text.slice(0, max),
    truncated: text.length > max,
  };
}
