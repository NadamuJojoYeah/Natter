#!/usr/bin/env node
// Renders the WebConsole dashboard against a fake DOM and a fixed snapshot.
// This is a UI-logic smoke test; it is not wired into `dotnet test`.
//
//   node tests/Natter.Plugin.WebConsole.Tests/ui-smoke.mjs
//
// It catches the class of bug where JS switches between `ui.*` and bare element
// globals, or passes a data array where a DOM element is expected.

import fs from "node:fs";
import vm from "node:vm";

const appJsPath = new URL("../../plugins/Natter.Plugin.WebConsole/wwwroot/app.js", import.meta.url);

class FakeNode {
  constructor(tag = "div") {
    this.tag = tag;
    this.children = [];
    this.textContent = "";
    this.className = "";
    this.hidden = false;
    this.disabled = false;
    this.value = "";
    this.type = "";
    this.placeholder = "";
    this.listeners = {};
  }

  appendChild(child) {
    this.children.push(child);
    return child;
  }

  replaceChildren(...children) {
    this.children = children;
  }

  addEventListener(type, handler) {
    this.listeners[type] = handler;
  }

  querySelector() {
    return new FakeNode("button");
  }

  focus() {
  }
}

const ids = [
  "login", "dashboard", "login-form", "token", "login-error", "status-line", "subtitle",
  "overview", "checks", "upstreams", "profiles", "plugins", "gaps", "config", "audit",
  "logs", "refresh", "logout",
  "config-editor", "config-doc", "config-load", "config-validate", "config-save",
  "config-status", "config-text", "config-diff", "config-undo",
  "command-console", "command-name", "command-args", "command-run", "command-status", "command-output",
  "role-badge", "log-level", "log-source", "log-q", "log-limit", "log-query", "log-prev", "log-next",
  "log-export-text", "log-export-jsonl", "log-status", "log-entries",
];
const elements = new Map(ids.map((id) => [id, new FakeNode()]));
const document = {
  getElementById: (id) => elements.get(id) ?? null,
  createElement: (tag) => new FakeNode(tag),
};

const snapshot = {
  generatedAt: "2026-11-18T02:30:00Z",
  self: {
    pluginId: "natter.tool.admin.webconsole",
    name: "Natter WebConsole",
    version: "0.1.0",
    kind: "tool",
    hostApi: "0.1.0",
    healthStatus: "degraded",
    serverState: "listening",
    serverUrl: "http://127.0.0.1:5090",
    bind: "127.0.0.1",
    port: 5090,
    readOnly: true,
    allowConfigWrite: false,
    allowCommandConsole: false,
    sessionMinutes: 60,
    maxLogLines: 500,
    tokenConfigured: true,
    startedAt: "2026-11-18T02:00:00Z",
    uptimeSeconds: 1800,
    serverRuntime: "httplistener",
    hostDataSource: "health-endpoint",
  },
  checks: [
    { name: "server", status: "ok", detail: "http://127.0.0.1:5090" },
    { name: "token", status: "ok", detail: null },
    { name: "static-assets", status: "ok", detail: "3 whitelisted assets" },
    { name: "audit", status: "ok", detail: null },
    { name: "host.health", status: "ok", detail: null },
    { name: "host.plugins", status: "degraded", detail: "host.plugin_diagnostics.unavailable" },
    { name: "host.admin_config", status: "ok", detail: "available" },
    { name: "host.admin_command", status: "ok", detail: "available" },
    { name: "host.logs", status: "ok", detail: "1 entries" },
  ],
  health: {
    available: true,
    observedAt: "2026-11-18T02:30:00Z",
    data: {
      status: "degraded",
      sampledAt: "2026-11-18T02:30:00Z",
      uptimeSeconds: 1800,
      startedAt: "2026-11-18T02:00:00Z",
      profiles: 2,
      conversations: 3,
      tasks: { created: 1, completed: 2, failed: 0, cancelled: 0, active: 1 },
      providers: [
        { id: "deepseek", available: true, profile: "tier1-default", supportTier: 1 },
        { id: "spark", available: false, profile: "tier2-default", supportTier: 2 },
      ],
      activatedPlugins: 3,
      failedPlugins: 1,
      pluginFailures: [
        { id: "natter.sample.broken", state: "Failed", phase: "activate", code: "boom", message: "activation failed" },
      ],
    },
  },
  plugins: {
    available: true,
    data: {
      activated: 3,
      failed: 1,
      failures: [
        { id: "natter.sample.broken", state: "Failed", phase: "activate", code: "boom", message: "activation failed" },
      ],
      nodes: [
        {
          id: "natter.sample.broken",
          version: "0.1.0",
          kind: "feature",
          state: "Failed",
          dependsOn: [],
          blockedBy: [],
        },
      ],
      contributions: [{ type: "cli.command", id: "web.status", pluginId: "natter.tool.admin.webconsole", priority: 0, status: "active" }],
      permissions: [{ pluginId: "natter.tool.admin.webconsole", requested: ["network"], granted: ["network"], denied: [] }],
      source: "health-endpoint",
    },
  },
  upstreams: {
    available: true,
    data: [
      {
        source: "discord:bot",
        connected: true,
        lastConnectedAt: "2026-11-18T02:00:00Z",
        lastDisconnectAt: null,
        reconnectCount: 1,
        lastError: null,
        sendFailures: 0,
        lastSendError: null,
      },
    ],
  },
  profiles: {
    available: true,
    data: [
      { profile: "tier1-default", provider: "deepseek", available: true, supportTier: 1 },
      { profile: "tier2-default", provider: "spark", available: false, supportTier: 2 },
    ],
  },
  logs: {
    available: true,
    data: [
      { timestamp: "2026-11-18T02:20:00Z", level: "Information", source: "Natter.Host", message: "host started" },
    ],
  },
  config: {
    source: "plugin.config",
    schemaPath: null,
    writeEnabled: true,
    writeGapCode: null,
    values: { port: 5090, token: "${secret:WEBCONSOLE_TOKEN}", readOnly: false },
    redacted: ["$.nested.password", "$.nested.apiKey"],
    documents: [
      { id: "profiles", exists: true, eTag: "sha256:aaa", lastModified: "2026-11-18T02:00:00Z", source: "config-directory", writable: true, reloadHint: "restart-host" },
      { id: "plugin:natter.tool.admin.webconsole", exists: false, eTag: "sha256:bbb", lastModified: null, source: "missing", writable: true, reloadHint: "restart-plugin" },
    ],
  },
  admin: {
    configStoreAvailable: true,
    configWriteEnabled: true,
    commandRunnerAvailable: true,
    commandEnabled: true,
    commandAllowlist: ["health", "plugins list"],
    commandTimeoutSeconds: 10,
    logReaderAvailable: true,
  },
  gaps: [
    "host.admin_diagnostics.unavailable",
    "host.plugin_diagnostics.unavailable",
    "host.admin_config.unavailable",
    "host.admin_command.unavailable",
    "host.logs.unavailable",
    "host.admin_kind.unavailable",
    "host.aspnetcore.unavailable",
  ].map((code) => ({
    code,
    area: "test",
    summary: `gap ${code}`,
    doc: "docs/WEBCONSOLE_HOST_API_GAP.md",
    hostChange: "host change",
  })),
  audit: [
    {
      timestamp: "2026-11-18T02:29:00Z",
      action: "auth.login",
      user: "admin",
      ip: "127.0.0.1",
      target: null,
      result: "ok",
      durationMs: 3,
    },
  ],
};

let sessionCalls = 0;
async function fetchStub(url, options = {}) {
  const respond = (status, payload) => ({
    ok: status >= 200 && status < 300,
    status,
    json: async () => payload,
  });

  if (url === "/api/session" && (options.method ?? "GET") === "GET") {
    sessionCalls++;
    return respond(200, {
      authenticated: true,
      csrf: "csrf-token",
      role: "admin",
      permissions: { read: true, write: true, command: true },
      viewerLoginEnabled: false,
    });
  }

  if (url === "/api/snapshot") {
    return respond(200, snapshot);
  }

  if (url === "/api/config/profiles") {
    return respond(200, {
      id: "profiles",
      exists: true,
      eTag: "sha256:aaa",
      lastModified: "2026-11-18T02:00:00Z",
      content: "{\"schemaVersion\":1}",
      contentRedacted: false,
      source: "config-directory",
      reloadHint: "restart-host",
      restartRequired: true,
      sizeBytes: 18,
    });
  }

  if (url === "/api/config/profiles/undo") {
    return respond(200, { id: "profiles", entries: [{ undoId: "undo-1", documentId: "profiles", recordedAt: "2026-11-18T02:10:00Z", sizeBytes: 18, eTag: "sha256:aaa", backupPath: ".backup/x/profiles.json" }] });
  }

  if (url === "/api/commands") {
    return respond(200, { command: "health", args: ["--json"], executed: true, exitCode: 0, stdout: "{\"status\":\"ok\"}", stderr: "", truncated: false, outputBytes: 15, durationMs: 12, timedOut: false });
  }

  if (url.startsWith("/api/logs?")) {
    return respond(200, {
      generatedAt: "2026-11-18T02:30:00Z",
      query: { limit: 200, offset: 0, order: "desc", includeAudit: true, levels: ["trace", "debug", "information", "warning", "error", "critical"] },
      total: 2,
      returned: 2,
      offset: 0,
      limit: 200,
      hasMore: false,
      nextOffset: null,
      hostLogs: {
        available: true,
        data: [
          { timestamp: "2026-11-18T02:29:00Z", level: "Information", source: "Natter.Host", message: "host started" },
          { timestamp: "2026-11-18T02:29:30Z", level: "Warning", source: "Natter.Upstream", message: "reconnect scheduled" },
        ],
      },
      audit: [],
    });
  }

  return respond(404, { error: { code: "not_found", message: "not found" } });
}

const sandbox = {
  document,
  fetch: fetchStub,
  Node: FakeNode,
  console,
  Date,
  JSON,
  Number,
  String,
  Array,
  Promise,
  Error,
  URL,
};

vm.createContext(sandbox);
vm.runInContext(fs.readFileSync(appJsPath, "utf8"), sandbox, { filename: "app.js" });
await new Promise((resolve) => setTimeout(resolve, 50));

function assert(condition, message) {
  if (!condition) {
    throw new Error(`ui smoke failed: ${message}`);
  }
}

const checks = elements.get("checks").children;
const upstreams = elements.get("upstreams").children;
const gaps = elements.get("gaps").children;
const config = elements.get("config").textContent;
const logs = elements.get("logs").textContent;
const statusLine = elements.get("status-line");

assert(elements.get("dashboard").hidden === false, "dashboard should be visible");
assert(elements.get("login").hidden === true, "login should be hidden after authentication");
assert(statusLine.textContent.startsWith("health=degraded"), "status line should show degraded health");
assert(statusLine.className.includes("degraded"), "status class should be degraded");
assert(checks.length === 9, `expected 9 checks, got ${checks.length}`);
assert(upstreams.length === 1, "upstream section should contain a table");
assert(elements.get("profiles").children.length === 1, "profiles section should contain a table");
assert(elements.get("plugins").children.length >= 3, "plugins section should render counts, failures and nodes");
assert(gaps.length === 7, `expected 7 gaps, got ${gaps.length}`);
assert(config.includes("${secret:WEBCONSOLE_TOKEN}"), "config preview must keep secret references");
assert(config.includes("redacted=2"), "config preview must report redacted fields");
assert(elements.get("audit").children.length === 1, "audit section should contain a table");
assert(logs.includes("宿主日志已启用"), "logs section should report the host log tail");

// Phase 2/3 panels
const docSelect = elements.get("config-doc");
assert(docSelect.children.length === 2, `expected 2 config documents, got ${docSelect.children.length}`);
assert(docSelect.value === "profiles", "config document select should default to profiles");
assert(elements.get("config-save").disabled === false, "save must be enabled when the host store is writable");
assert(
  elements.get("config-status").textContent.includes("宿主配置已接入"),
  "config status should report the host admin surface",
);
assert(
  elements.get("command-status").textContent.includes("允许命令"),
  "command status should list the allowlist",
);
assert(elements.get("command-run").disabled === false, "command run must be enabled with a runner");

elements.get("config-load").listeners.click();
await new Promise((resolve) => setTimeout(resolve, 40));
assert(
  elements.get("config-text").value.includes("schemaVersion"),
  "loading a document should fill the editor",
);
assert(
  elements.get("config-undo").children.length === 2,
  "undo panel should render the retention note plus the snapshot row",
);

elements.get("command-name").value = "health";
elements.get("command-args").value = "--json";
elements.get("command-run").listeners.click();
await new Promise((resolve) => setTimeout(resolve, 40));
assert(
  elements.get("command-output").textContent.includes("status"),
  "command output should render stdout",
);

// Hardening (task-14): role badge, log filtering panel, export affordance.
assert(elements.get("role-badge").textContent === "admin", "role badge should show the session role");
assert(elements.get("log-entries").children.length === 1, "log panel should render one table");
assert(
  elements.get("log-status").textContent.includes("匹配 2 条"),
  "log status should report the filtered total",
);
assert(elements.get("log-prev").disabled === true, "prev page is disabled on the first page");
assert(elements.get("log-next").disabled === true, "next page is disabled when there is no more data");
assert(
  elements.get("config-undo").children.length >= 1,
  "undo panel should render its retention note",
);

elements.get("log-q").value = "reconnect";
elements.get("log-query").listeners.click();
await new Promise((resolve) => setTimeout(resolve, 40));
assert(
  elements.get("log-status").textContent.includes("匹配"),
  "querying logs should refresh the status line",
);

elements.get("log-export-jsonl").listeners.click();
assert(
  elements.get("log-status").textContent.includes("/api/logs/export?format=jsonl"),
  "export should build a redacted export URL",
);

assert(sessionCalls === 1, "bootstrap should call /api/session exactly once");

console.log(JSON.stringify({
  result: "ui smoke ok",
  checks: checks.length,
  gaps: gaps.length,
  statusLine: statusLine.textContent,
  configRedacted: config.includes("redacted=2"),
  configDocuments: docSelect.children.length,
  commandOutput: elements.get("command-output").textContent.includes("status"),
  role: elements.get("role-badge").textContent,
  logRows: elements.get("log-entries").children.length,
  logStatus: elements.get("log-status").textContent,
  auditTable: elements.get("audit").children.length,
}, null, 2));
