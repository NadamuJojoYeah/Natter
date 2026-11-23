// Diagnostics bundle view: export button, manifest summary and failure states.

import { escapeHtml, formatBytes, stateBlock } from "../util.js";

export function renderDiag({ state = "idle", error = null, last = null } = {}) {
  const head = `<p class="doc">一次带走整个现场：manifest / health / upstreams / providers / plugins /`
    + ` config.redacted/ / logs.jsonl / audit.jsonl。每个文件都走同一套脱敏，打包后再做一次明文哨兵自检；`
    + ` 总量上限 8 MiB，超出按 manifest 声明的截断处理。</p>`;

  if (state === "running") {
    return head + stateBlock({ kind: "loading" });
  }

  if (state === "error") {
    return head + stateBlock({
      kind: "error",
      code: error?.code,
      message: error?.message,
      hint: "若提示 diag-sentinel-failed，说明脱敏漏网被拦下，导出被拒绝。",
    });
  }

  if (!last) {
    return head + stateBlock({ kind: "empty", emptyReason: "no-data", message: "尚未导出诊断包。" });
  }

  const files = (last.files || []).map((file) => `<li>`
    + `<span class="code">${escapeHtml(file.path)}</span>`
    + `<span class="muted">${escapeHtml(formatBytes(file.bytes))}</span>`
    + (file.items === null || file.items === undefined ? "" : `<span class="muted">items=${escapeHtml(file.items)}/${escapeHtml(file.shownItems ?? file.items)}</span>`)
    + (file.truncation ? `<span class="tone-degraded">${escapeHtml(file.truncation)}</span>` : "")
    + "</li>").join("");

  return head
    + `<p class="muted">${escapeHtml(last.fileName)} · ${escapeHtml(formatBytes(last.totalBytes))}`
    + ` · 上限 ${escapeHtml(formatBytes(last.maxTotalBytes))}`
    + ` · redaction=${escapeHtml(last.redaction)}`
    + ` · truncated=${escapeHtml(last.truncated)}</p>`
    + `<ul class="diag-files">${files}</ul>`;
}
