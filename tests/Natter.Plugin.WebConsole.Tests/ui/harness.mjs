// Node harness: imports the real view modules and renders them with fixture data.
// Views are pure (data → HTML string), so no DOM shim is needed; this file prints one JSON line
// per assertion group so the C# test can assert on real rendered output.
//
// Usage: node harness.mjs <wwwroot-dir>

import { pathToFileURL } from "node:url";
import path from "node:path";

const wwwroot = process.argv[2];
if (!wwwroot) {
  console.error("usage: harness.mjs <wwwroot>");
  process.exit(2);
}

const load = (relative) => import(pathToFileURL(path.join(wwwroot, relative)).href);

const util = await load("util.js");
const traceView = await load("views/trace.js");
const overview = await load("views/overview.js");
const logs = await load("views/logs.js");
const config = await load("views/config.js");
const commands = await load("views/commands.js");
const diag = await load("views/diag.js");

const statusText = [
  "task_abc123 Completed v4",
  "elapsed=12400ms step=3 tools=1/3 remaining=2",
  "result: " + "R".repeat(400),
  "trace:",
  "- started 10:48:33",
  "- step 10:48:33",
  "- tool 10:48:35",
  "- step 10:48:40",
  "- final 10:48:45",
].join("\n");

const errorText = [
  "task_bad Failed v2",
  "elapsed=900ms step=2 tools=2/3 remaining=1",
  "error=agent.timeout: tool exceeded 10s",
  "trace:",
  "- started 10:00:00",
  "- tool 10:00:01",
  "- final 10:00:02",
].join("\n");

const model = traceView.parseStatusText(statusText);
const report = {
  util: {
    escape: util.escapeHtml('<b a="1">&\'</b>'),
    duration: util.formatDuration(12400),
    bytes: util.formatBytes(2048),
    truncation: util.truncationNote({ original: 500, shown: 200 }),
    states: Object.keys(util.EMPTY_REASONS).length,
    stateBlocks: {
      noData: util.stateBlock({ kind: "empty", emptyReason: "no-data" }),
      noPermission: util.stateBlock({ kind: "empty", emptyReason: "no-permission" }),
      disabled: util.stateBlock({ kind: "empty", emptyReason: "disabled" }),
      hostUnavailable: util.stateBlock({ kind: "empty", emptyReason: "host-unavailable" }),
      error: util.stateBlock({ kind: "error", code: "auth.forbidden", message: "nope" }),
      loading: util.stateBlock({ kind: "loading" }),
    },
  },
  trace: {
    model,
    html: traceView.renderTimeline(model),
    limited: traceView.renderTimeline(model, { limit: 2 }),
    error: traceView.renderTimeline(traceView.parseStatusText(errorText)),
    result: traceView.renderResult("R".repeat(400)),
    empty: traceView.renderTimeline(null),
  },
  overview: {
    overview: overview.renderOverview({ self: {
      healthStatus: "ok", serverState: "listening", serverUrl: "http://127.0.0.1:5090", serverRuntime: "httplistener",
      hostDataSource: "live", uptimeSeconds: 42, bind: "127.0.0.1", port: 5090, tokenConfigured: true,
      readOnly: true, allowConfigWrite: false, allowCommandConsole: false, sessionMinutes: 60,
      pluginId: "natter.plugin.webconsole", version: "0.1.0", kind: "tool", hostApi: "0.1.0",
    } }),
    gaps: overview.renderGaps([{ code: "host.logs", summary: "no logs", area: "logs", hostChange: "bridge", doc: "doc" }]),
    emptySection: overview.renderUpstreams({ available: false, gapCode: "host.upstreams.unavailable" }),
    auditEmpty: overview.renderAudit([]),
  },
  logs: {
    ready: logs.renderLogs({ hostLogs: { available: true, data: [
      { timestamp: "2026-11-22T10:00:00Z", level: "Warning", source: "webconsole", message: "hello" },
    ] }, total: 1, offset: 0, returned: 1, limit: 200, hasMore: false }),
    unavailable: logs.renderLogs({ hostLogs: { available: false, gapCode: "host.logs.unavailable" } }),
    error: logs.renderLogs(null, { state: "error", error: { code: "host.logs.unavailable", message: "boom" } }),
  },
  config: {
    noPermission: config.renderConfigStatus({ writeEnabled: true }),
    disabled: config.renderConfigStatus({ writeEnabled: false }),
    validation: config.renderValidation({ valid: true, source: "host", eTag: "abc", sizeBytes: 10, containsSecretLiteral: false, issues: [], diff: { added: 2, removed: 1, truncated: true, lines: Array.from({ length: 90 }, (_, i) => `+ line ${i}`) } }),
  },
  commands: {
    result: commands.renderCommandResult({ exitCode: 0, durationMs: 12, outputBytes: 9000, stdout: "out", stderr: "", truncated: true }),
  },
  diag: {
    empty: diag.renderDiag({ state: "idle" }),
    ready: diag.renderDiag({ state: "ready", last: { fileName: "natter-diag-1.zip", totalBytes: 2048, maxTotalBytes: 8388608, truncated: true, redaction: "host", files: [{ path: "logs.jsonl", bytes: 10, items: 5, shownItems: 2, truncation: "shown=2 of 5" }] } }),
    error: diag.renderDiag({ state: "error", error: { code: "webconsole.diag-sentinel-failed", message: "refused" } }),
  },
};

process.stdout.write(JSON.stringify(report));
