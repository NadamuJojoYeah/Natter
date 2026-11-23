// Trace view: renders the flat `/status` text as a vertical timeline (UI-DIRECTION §3.1).
//
// The chat `/status` command is the only producer of this data, and it is text. The console therefore
// parses that exact text — no new backend API and no new fields — into a structured model and renders
// the timeline from the model. `parseStatusText` is exported so the entry module and the tests share
// one parser.

import { badgeHtml, clip, escapeHtml, formatClock, formatDuration, stateBlock, statusTone, truncationNote } from "../util.js";

const RESULT_SUMMARY_LENGTH = 200;
const DEFAULT_VISIBLE_ENTRIES = 10;
const STEP_NOTE = "协议来自 step 行的 kind（native/text）。";

/**
 * Parses the flattened status text:
 *
 *   task_abc Completed v4
 *   elapsed=1234ms step=3 tools=1/3 remaining=2
 *   error=agent.timeout: message
 *   result: …
 *   trace:
 *   - step 10:48:33
 *   - tool 10:48:35
 *   - final 10:48:45
 */
export function parseStatusText(text) {
  const source = String(text ?? "").replaceAll("\r\n", "\n").trim();
  if (!source) {
    return null;
  }

  const lines = source.split("\n");
  const header = lines[0].trim().split(/\s+/);
  const model = {
    taskId: null,
    status: null,
    version: null,
    elapsedMs: null,
    step: null,
    tools: { used: null, budget: null, remaining: null },
    error: null,
    result: null,
    entries: [],
    raw: source,
  };

  if (header.length >= 2 && /^task[_-]/i.test(header[0])) {
    model.taskId = header[0];
    model.status = header[1];
    const version = header.find((part) => /^v\d+$/i.test(part));
    model.version = version ? Number(version.slice(1)) : null;
  } else {
    // Not the expected header: still show the raw text rather than dropping it.
    return model;
  }

  for (const line of lines.slice(1)) {
    const trimmed = line.trim();
    if (!trimmed) {
      continue;
    }

    const elapsed = trimmed.match(/elapsed=(\d+)ms/);
    if (elapsed) {
      model.elapsedMs = Number(elapsed[1]);
    }

    const step = trimmed.match(/step=(\d+|-)/);
    if (step) {
      model.step = step[1] === "-" ? null : Number(step[1]);
    }

    const tools = trimmed.match(/tools=(\d+)\/(\d+)(?:\s+remaining=(\d+))?/);
    if (tools) {
      model.tools = {
        used: Number(tools[1]),
        budget: Number(tools[2]),
        remaining: tools[3] === undefined ? null : Number(tools[3]),
      };
    }

    const error = trimmed.match(/^error=([^:\s]+)(?::\s*(.*))?$/);
    if (error) {
      model.error = { code: error[1], message: error[2] ?? null };
    }

    const result = trimmed.match(/^result:\s*(.*)$/);
    if (result) {
      model.result = result[1];
    }

    const entry = trimmed.match(/^-\s+([a-zA-Z_-]+)\s+(\d{1,2}:\d{2}:\d{2}(?:\.\d+)?)?\s*(.*)$/);
    if (entry) {
      model.entries.push({
        kind: entry[1].toLowerCase(),
        at: entry[2] ?? null,
        note: entry[3] || null,
      });
    }
  }

  // Durations are not in the text; they are derived from consecutive timestamps so tool bars can
  // still show relative cost without inventing fields.
  let previous = null;
  for (const entry of model.entries) {
    const milliseconds = entry.at ? toMilliseconds(entry.at) : null;
    entry.durationMs = previous !== null && milliseconds !== null ? Math.max(0, milliseconds - previous) : null;
    if (milliseconds !== null) {
      previous = milliseconds;
    }
  }

  return model;
}

function toMilliseconds(clock) {
  const [hours, minutes, rest] = clock.split(":");
  const seconds = Number(rest);
  if (Number.isNaN(seconds)) {
    return null;
  }

  return ((Number(hours) * 60 + Number(minutes)) * 60 + seconds) * 1000;
}

function entryLabel(kind) {
  switch (kind) {
    case "started":
      return "started";
    case "step":
      return "step";
    case "tool":
      return "tool";
    case "final":
      return "final";
    default:
      return kind;
  }
}

/** Renders the timeline HTML. `limit` implements "default 10 + 加载更多". */
export function renderTimeline(model, { limit = DEFAULT_VISIBLE_ENTRIES } = {}) {
  if (!model) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "还没有 /status 输出。" });
  }

  if (!model.taskId) {
    const clipped = clip(model.raw, 4000);
    return stateBlock({
      kind: "empty",
      emptyReason: "no-data",
      message: "无法识别 task 头行，按原始文本展示。",
    }) + `<pre class="raw">${escapeHtml(clipped.text)}</pre>`
      + (clipped.truncated ? truncationNote({ original: clipped.original, shown: clipped.shown }) : "");
  }

  const tools = model.tools;
  const toolsText = tools.used === null
    ? "—"
    : `${tools.used}/${tools.budget}${tools.remaining === null ? "" : `（剩余 ${tools.remaining}）`}`;

  const summary = `<header class="trace-head">`
    + `<span class="trace-id">${escapeHtml(model.taskId)}</span>`
    + badgeHtml(model.status ?? "unknown")
    + (model.version === null ? "" : `<span class="muted">v${escapeHtml(model.version)}</span>`)
    + `<span class="muted">elapsed ${escapeHtml(formatDuration(model.elapsedMs))}</span>`
    + `<span class="muted">step ${escapeHtml(model.step ?? "—")}</span>`
    + `<span class="muted">tools ${escapeHtml(toolsText)}</span>`
    + "</header>";

  const visible = model.entries.slice(0, Math.max(0, limit));
  const maxDuration = Math.max(1, ...model.entries.map((entry) => entry.durationMs ?? 0));

  const rows = visible.map((entry) => {
    const classes = ["trace-row", `kind-${entry.kind}`];
    if (entry.kind === "tool" && (entry.note && /fail|error/i.test(entry.note))) {
      classes.push("error");
    }
    if (model.error && entry.kind === "final") {
      classes.push("error");
    }

    const note = entry.note
      ? `<span class="trace-note">${escapeHtml(entry.note)}</span>`
      : "";
    const duration = entry.durationMs === null
      ? ""
      : `<span class="trace-duration">${escapeHtml(formatDuration(entry.durationMs))}</span>`
        + `<span class="trace-bar" style="--ratio:${(entry.durationMs / maxDuration).toFixed(3)}"></span>`;

    return `<li class="${classes.join(" ")}">`
      + `<span class="trace-dot" aria-hidden="true"></span>`
      + `<span class="trace-kind">${escapeHtml(entryLabel(entry.kind))}</span>`
      + `<span class="trace-at">${escapeHtml(entry.at ?? "—")}</span>`
      + note
      + duration
      + "</li>";
  }).join("");

  const more = model.entries.length > visible.length
    ? `<button type="button" class="trace-more" data-action="trace-more">加载更多（还有 ${model.entries.length - visible.length} 条）</button>`
    : "";

  const errorRow = model.error
    ? `<div class="trace-error" role="alert">`
      + `<button type="button" class="code copyable" data-copy="${escapeHtml(model.error.code)}">${escapeHtml(model.error.code)}</button>`
      + (model.error.message ? `<span>${escapeHtml(model.error.message)}</span>` : "")
      + "</div>"
    : "";

  const result = model.result
    ? renderResult(model.result)
    : "";

  return summary
    + `<ol class="trace-timeline">${rows}</ol>`
    + more
    + errorRow
    + result
    + `<p class="doc">${escapeHtml(STEP_NOTE)}</p>`;
}

/** Result text: 200-character summary plus the full text behind <details>. */
export function renderResult(text) {
  const summary = clip(text, RESULT_SUMMARY_LENGTH);
  const full = String(text ?? "");

  return `<section class="trace-result">`
    + `<h3>结果</h3>`
    + `<p class="result-summary">${escapeHtml(summary.text)}${summary.truncated ? "…" : ""}</p>`
    + truncationNote({ original: summary.original, shown: summary.shown })
    + (summary.truncated
      ? `<details><summary>展开全文</summary><pre class="raw">${escapeHtml(full)}</pre></details>`
      : "")
    + "</section>";
}

/** Convenience: parse + render in one call (used by the entry module). */
export function renderTrace(text, options = {}) {
  return renderTimeline(parseStatusText(text), options);
}

export const TRACE_DEFAULTS = Object.freeze({ visible: DEFAULT_VISIBLE_ENTRIES, resultSummary: RESULT_SUMMARY_LENGTH });
export { statusTone };
