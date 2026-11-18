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
];
const elements = new Map(ids.map((id) => [id, new FakeNode()]));
const document = {
  getElementById: (id) => elements.get(id) ?? null,
  createElement: (tag) => new FakeNode(tag),
};

const snapshot = {
  generatedAt: "2026-11-18T02:30:00Z",
  self: {
    pluginId: "natter.plugin.webconsole",
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
    { name: "host.health", status: "ok", detail: null },
    { name: "host.plugins", status: "degraded", detail: "host.plugin_diagnostics.unavailable" },
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
      contributions: [{ type: "cli.command", id: "web.status", pluginId: "natter.plugin.webconsole", priority: 0, status: "active" }],
      permissions: [{ pluginId: "natter.plugin.webconsole", requested: ["network"], granted: ["network"], denied: [] }],
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
    available: false,
    gapCode: "host.logs.unavailable",
    reason: "宿主没有日志读取端口",
  },
  config: {
    source: "plugin.config",
    schemaPath: null,
    writeEnabled: false,
    writeGapCode: "host.admin_config.unavailable",
    values: { port: 5090, token: "${secret:WEBCONSOLE_TOKEN}", readOnly: true },
    redacted: ["$.nested.password", "$.nested.apiKey"],
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
    return respond(200, { authenticated: true, csrf: "csrf-token", role: "admin" });
  }

  if (url === "/api/snapshot") {
    return respond(200, snapshot);
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
assert(checks.length === 5, `expected 5 checks, got ${checks.length}`);
assert(upstreams.length === 1, "upstream section should contain a table");
assert(elements.get("profiles").children.length === 1, "profiles section should contain a table");
assert(elements.get("plugins").children.length >= 3, "plugins section should render counts, failures and nodes");
assert(gaps.length === 7, `expected 7 gaps, got ${gaps.length}`);
assert(config.includes("${secret:WEBCONSOLE_TOKEN}"), "config preview must keep secret references");
assert(config.includes("redacted=2"), "config preview must report redacted fields");
assert(elements.get("audit").children.length === 1, "audit section should contain a table");
assert(logs.includes("宿主日志不可用"), "logs section should explain the host gap");
assert(sessionCalls === 1, "bootstrap should call /api/session exactly once");

console.log(JSON.stringify({
  result: "ui smoke ok",
  checks: checks.length,
  gaps: gaps.length,
  statusLine: statusLine.textContent,
  configRedacted: config.includes("redacted=2"),
  auditTable: elements.get("audit").children.length,
}, null, 2));
