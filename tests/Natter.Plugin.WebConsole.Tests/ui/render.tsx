// Server-side rendering harness: renders the real React views with fixture data and prints one JSON
// object, so the C# tests assert on actual component output instead of grepping source.
//
// Bundled by esbuild (a vite dependency) and executed with node — see UiViewRenderTests.cs.

import { renderToStaticMarkup } from "react-dom/server";
import { OverviewView } from "../../../ui/src/webconsole/views/OverviewView";
import { TraceTimeline } from "../../../ui/src/webconsole/views/TraceView";
import { ConfigView, DiffBlock } from "../../../ui/src/webconsole/views/ConfigView";
import { CommandsView } from "../../../ui/src/webconsole/views/CommandsView";
import { DiagView } from "../../../ui/src/webconsole/views/DiagView";
import { LogsView } from "../../../ui/src/webconsole/views/LogsView";
import { LoginPrompt } from "../../../ui/src/shared/login";
import { EmptyState, ErrorState, LoadingState } from "../../../ui/src/shared/states";
import { MessageReplay } from "../../../ui/src/conversation/views/MessageReplay";
import type { Snapshot, TraceHeader, TraceEntry } from "../../../ui/src/shared/types";

const snapshot = {
  generatedAt: "2026-11-23T00:00:00Z",
  self: {
    pluginId: "natter.tool.admin.webconsole",
    name: "WebConsole",
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
    uptimeSeconds: 42,
    serverRuntime: "httplistener",
    hostDataSource: "live",
  },
  checks: [
    { name: "server", status: "ok", detail: "listening" },
    { name: "host.logs", status: "degraded", detail: "host.logs.unavailable" },
  ],
  health: { available: true, data: { status: "degraded", uptimeSeconds: 42 } },
  plugins: {
    available: true,
    data: {
      activated: 3,
      failed: 1,
      failures: [{ id: "natter.sample.bad", state: "Failed", phase: "configure", code: "configure-failed", message: "boom" }],
      nodes: [
        { id: "natter.tool.admin.webconsole", version: "0.1.0", kind: "tool", state: "Activated", blockedBy: [] },
      ],
      contributions: [],
      permissions: [],
      source: "host",
    },
  },
  upstreams: {
    available: true,
    data: [
      {
        source: "discord:discord:11",
        connected: false,
        lastConnectedAt: "2026-11-23T00:00:00Z",
        reconnectCount: 3,
        sendFailures: 1,
        lastError: "gateway closed",
      },
    ],
  },
  profiles: { available: true, data: [{ profile: "default", provider: "openai", available: true, supportTier: 1 }] },
  logs: { available: false, gapCode: "host.logs.unavailable" },
  config: {
    source: "host",
    schemaPath: "config.schema.json",
    writeEnabled: false,
    writeGapCode: "config.write_disabled",
    values: {},
    redacted: ["token"],
    documents: [{ id: "profiles", exists: true, eTag: "abc", source: "host", reloadHint: "none" }],
  },
  admin: {
    configStoreAvailable: true,
    configWriteEnabled: false,
    configGapCode: null,
    commandRunnerAvailable: true,
    commandEnabled: false,
    commandGapCode: null,
    commandAllowlist: ["health"],
    commandTimeoutSeconds: 10,
    logReaderAvailable: false,
  },
  gaps: [
    { code: "host.logs", summary: "宿主没有桥接日志读取", area: "logs", hostChange: "IAdminLogReader", doc: "docs/WEBCONSOLE_PLUGIN.md" },
  ],
  audit: [
    { timestamp: "2026-11-23T00:00:00Z", action: "snapshot.view", user: "admin", ip: "127.0.0.1", target: null, result: "ok", durationMs: 3 },
  ],
} as unknown as Snapshot;

const traceHeader: TraceHeader = {
  taskId: "task_abc123",
  sessionId: "session-1",
  source: "discord:discord:11",
  status: "Failed",
  version: 4,
  createdAt: "2026-11-23T00:00:00Z",
  updatedAt: "2026-11-23T00:00:12Z",
  elapsedMs: 12400,
  errorCode: "agent.timeout",
  errorMessage: "tool exceeded 10s",
  resultText: "partial result",
  resultTruncated: false,
};

const traceEntries: TraceEntry[] = [
  { kind: "started", at: "2026-11-23T00:00:00Z", status: "Running" },
  { kind: "step", at: "2026-11-23T00:00:00.020Z", step: 1, name: "native" },
  { kind: "tool", at: "2026-11-23T00:00:00.120Z", name: "clock", status: "ok" },
  { kind: "final", at: "2026-11-23T00:00:12Z", code: "agent.timeout" },
];

const report = {
  overview: renderToStaticMarkup(
    <OverviewView phase="ready" snapshot={snapshot} error={null} reload={() => {}} recentTasks={[]} />,
  ),
  trace: renderToStaticMarkup(<TraceTimeline header={traceHeader} entries={traceEntries} />),
  traceEmpty: renderToStaticMarkup(
    <TraceTimeline header={{ ...traceHeader, errorCode: null, resultText: null }} entries={[]} />,
  ),
  configDisabled: renderToStaticMarkup(
    <ConfigView
      documents={[{ id: "profiles", exists: true, eTag: "abc" }]}
      writeEnabled={false}
      configStoreAvailable
      gapCode={null}
      readOnly
      role="viewer"
      permissions={{ write: false }}
      onUnauthorized={() => {}}
    />,
  ),
  configUnavailable: renderToStaticMarkup(
    <ConfigView
      documents={[]}
      writeEnabled={false}
      configStoreAvailable={false}
      gapCode="host.admin_config.unavailable"
      readOnly
      role="admin"
      permissions={{ write: true }}
      onUnauthorized={() => {}}
    />,
  ),
  diff: renderToStaticMarkup(
    <DiffBlock diff={{ added: 2, removed: 1, truncated: true, lines: Array.from({ length: 90 }, (_, i) => `+ line ${i}`) }} />,
  ),
  commandsNoPermission: renderToStaticMarkup(
    <CommandsView
      admin={{ configStoreAvailable: true, configWriteEnabled: true, commandRunnerAvailable: true, commandEnabled: true, commandAllowlist: ["health"], commandTimeoutSeconds: 10, logReaderAvailable: true }}
      role="viewer"
      permissions={{ command: false }}
      readOnly={false}
      allowCommandConsole
      onUnauthorized={() => {}}
    />,
  ),
  commandsDisabled: renderToStaticMarkup(
    <CommandsView
      admin={{ configStoreAvailable: true, configWriteEnabled: true, commandRunnerAvailable: true, commandEnabled: false, commandAllowlist: [], commandTimeoutSeconds: 10, logReaderAvailable: true }}
      role="admin"
      permissions={{ command: true }}
      readOnly
      allowCommandConsole={false}
      onUnauthorized={() => {}}
    />,
  ),
  diagIdle: renderToStaticMarkup(<DiagView />),
  logsUnavailable: renderToStaticMarkup(<LogsView onUnauthorized={() => {}} />),
  login: renderToStaticMarkup(
    <LoginPrompt title="Natter WebConsole" hint="使用 WEBCONSOLE_TOKEN 登录" error="auth.required：会话已过期" onSubmit={() => {}} />,
  ),
  states: {
    loading: renderToStaticMarkup(<LoadingState />),
    error: renderToStaticMarkup(<ErrorState code="auth.forbidden" message="需要 admin 角色" onRetry={() => {}} />),
    emptyNoData: renderToStaticMarkup(<EmptyState reason="no-data" />),
    emptyNoPermission: renderToStaticMarkup(<EmptyState reason="no-permission" />),
    emptyDisabled: renderToStaticMarkup(<EmptyState reason="disabled" />),
    emptyHost: renderToStaticMarkup(<EmptyState reason="host-unavailable" />),
  },
  messages: renderToStaticMarkup(
    <MessageReplay
      messages={[
        { messageId: 1, actorId: "user:7", text: "<img src=x onerror=alert(1)>", createdAt: "2026-11-23T00:00:00Z" },
        { messageId: 2, actorId: "bot", text: "hello", createdAt: "2026-11-23T00:00:01Z" },
      ]}
    />,
  ),
};

process.stdout.write(JSON.stringify(report));
