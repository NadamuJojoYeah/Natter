// Logs view: filter summary, table, paging and redacted export links.

import { badgeHtml, escapeHtml, formatTime, stateBlock, tableHtml, truncationNote } from "../util.js";
import { emptyReasonFor } from "../state.js";

export function renderLogs(payload, { state = "ready", error = null } = {}) {
  if (state === "loading") {
    return stateBlock({ kind: "loading" });
  }

  if (state === "error") {
    return stateBlock({ kind: "error", code: error?.code, message: error?.message, hint: "检查宿主日志契约是否桥接。" });
  }

  const section = payload?.hostLogs ?? {};
  if (!section.available) {
    return stateBlock({
      kind: "empty",
      emptyReason: emptyReasonFor(section),
      message: section.reason || section.gapCode ? `宿主日志不可用：${section.reason || section.gapCode}` : null,
    });
  }

  const entries = section.data ?? [];
  if (!entries.length) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "没有匹配的日志行。" });
  }

  const offset = payload.offset ?? 0;
  const returned = payload.returned ?? entries.length;
  const started = payload.total === 0 ? 0 : offset + 1;
  const ended = offset + returned;
  const summary = `匹配 ${payload.total} 条，显示 ${started}–${ended}（每页 ${payload.limit}，order=${payload.query?.order || "desc"}）`;

  const table = tableHtml(
    ["time", "level", "source", "message"],
    entries.map((entry) => [
      escapeHtml(formatTime(entry.timestamp)),
      badgeHtml(entry.level === "Error" || entry.level === "Critical" ? "error" : entry.level === "Warning" ? "degraded" : "ok", entry.level),
      escapeHtml(entry.source),
      escapeHtml(entry.message),
    ]),
  );

  const shownBytes = entries.reduce((total, entry) => total + String(entry.message ?? "").length, 0);
  const note = truncationNote({
    original: payload.totalMessagesBytes ?? shownBytes,
    shown: shownBytes,
    unit: "字符",
    extra: "日志按行返回，单次上限 1 MiB",
  });

  return `<p class="muted">${escapeHtml(summary)}</p>` + table + note;
}

export function renderLogStateMessage(payload) {
  if (!payload) {
    return "尚未查询日志。";
  }

  return payload.hasMore === true ? "还有更多匹配行，可继续翻页。" : "已到最后一页。";
}
