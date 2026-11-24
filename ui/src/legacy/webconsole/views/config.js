// Config view: document status, validation results and diff accounting (never silent truncation).

import { escapeHtml, clip, formatTime, stateBlock, truncationNote } from "../util.js";
import { canWrite, state as appState } from "../state.js";

const DIFF_VISIBLE_LINES = 60;

export function renderConfigDocuments(documents, currentId) {
  if (!documents?.length) {
    return "";
  }

  return documents.map((document) => `<option value="${escapeHtml(document.id)}"`
    + `${document.id === currentId ? " selected" : ""}>${escapeHtml(document.id)}`
    + `${document.exists ? "" : " (不存在)"}</option>`).join("");
}

export function renderConfigStatus({ document = null, writeEnabled = false, gapCode = null, adminAvailable = true } = {}) {
  if (!adminAvailable) {
    return stateBlock({
      kind: "empty",
      emptyReason: "host-unavailable",
      message: `配置编辑不可用：${gapCode || "host.admin_config.unavailable"}`,
    });
  }

  // "switched off" outranks "you lack the role": the feature is unavailable to everyone, not just
  // to this session, and the operator should see the real reason (UI-DIRECTION §3.3).
  if (!writeEnabled) {
    return stateBlock({
      kind: "empty",
      emptyReason: "disabled",
      message: appState.readOnly
        ? "功能未启用：readOnly=true（服务端统一返回 console.read_only）。"
        : "功能未启用：allowConfigWrite=false。",
    });
  }

  if (appState.role !== "admin") {
    return stateBlock({ kind: "empty", emptyReason: "no-permission", message: "viewer 只读会话：可以查看与校验，保存/回滚需要 admin 角色。" });
  }

  const parts = [`可写=${canWrite() ? "yes" : "no"}`];
  if (document) {
    parts.push(`etag=${document.eTag || "?"}`);
    parts.push(`source=${document.source || "?"}`);
    parts.push(`exists=${document.exists}`);
  }

  return `<p class="muted">${escapeHtml(parts.join(" · "))}</p>`;
}

/** Validation + diff, with "原文 N 行 / 显示 M 行 / 已截断 K 行". */
export function renderValidation(validation) {
  if (!validation) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "还没有校验结果。" });
  }

  const lines = [
    `valid=${validation.valid} source=${validation.source || "?"} etag=${validation.eTag || "?"}`,
    `size=${validation.sizeBytes} bytes secret=${validation.containsSecretLiteral}`,
  ];

  for (const issue of validation.issues || []) {
    lines.push(`- ${issue.pointer || "$"} [${issue.code}] ${issue.message}`);
  }

  const issues = (validation.issues || []).length;
  const diff = validation.diff;

  let diffHtml = "";
  if (diff) {
    const all = diff.lines || [];
    const shown = all.slice(0, DIFF_VISIBLE_LINES);
    diffHtml = `<h4>diff +${escapeHtml(diff.added)} -${escapeHtml(diff.removed)}</h4>`
      + `<pre>${escapeHtml(shown.join("\n"))}</pre>`
      + truncationNote({
        original: all.length,
        shown: shown.length,
        unit: "行",
        extra: diff.truncated ? "宿主侧 diff 已截断" : null,
      });
  }

  return `<pre class="config-summary">${escapeHtml(lines.join("\n"))}</pre>`
    + (issues ? "" : "")
    + diffHtml;
}

export function renderUndo(entries) {
  const head = `<p class="doc">会话内快照：每文档最多 8 条、全局 32 条、30 分钟后失效；`
    + `控制台不读取宿主备份内容，宿主备份需在服务器上手动恢复（路径由保存响应给出）。</p>`;

  if (!entries?.length) {
    return head + stateBlock({ kind: "empty", emptyReason: "no-data", message: "当前没有可回滚的会话内快照。" });
  }

  const allowed = canWrite();
  const rows = entries.map((entry) => `<div class="undo-row">`
    + `<span class="muted">${escapeHtml(entry.undoId)} · ${escapeHtml(formatTime(entry.recordedAt))} · ${escapeHtml(entry.sizeBytes)} B</span>`
    + (entry.backupPath ? `<span class="doc">宿主备份（手动恢复）：${escapeHtml(entry.backupPath)}</span>` : "")
    + `<button type="button" data-action="rollback" data-undo="${escapeHtml(entry.undoId)}"${allowed ? "" : " disabled"}>`
    + `${allowed ? "回滚到此处" : "回滚（需 admin）"}</button>`
    + "</div>").join("");

  return head + rows;
}

/** Saves/validate feedback line; long host messages are clipped but marked. */
export function renderConfigMessage(message, isError) {
  const clipped = clip(message ?? "", 400);
  return `<p class="${isError ? "error" : "muted"}">${escapeHtml(clipped.text)}</p>`
    + (clipped.truncated ? truncationNote({ original: clipped.original, shown: clipped.shown }) : "");
}
