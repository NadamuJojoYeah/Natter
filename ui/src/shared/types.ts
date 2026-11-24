// Wire types for the two consoles. Field names mirror the plugin JSON exactly (no renaming layer:
// a mismatch must be a compile error in the consumer, not a silent `undefined` on screen).

export interface Section<T> {
  available: boolean;
  data?: T | null;
  gapCode?: string | null;
  reason?: string | null;
  doc?: string | null;
}

export interface HealthCheck {
  name: string;
  status: string;
  detail?: string | null;
}

export interface SelfInfo {
  pluginId: string;
  name: string;
  version: string;
  kind: string;
  hostApi: string;
  healthStatus: string;
  serverState: string;
  serverUrl?: string | null;
  bind: string;
  port: number;
  readOnly: boolean;
  allowConfigWrite: boolean;
  allowCommandConsole: boolean;
  sessionMinutes: number;
  maxLogLines: number;
  tokenConfigured: boolean;
  uptimeSeconds: number;
  serverRuntime: string;
  hostDataSource: string;
}

export interface UpstreamInfo {
  source: string;
  connected: boolean;
  lastConnectedAt?: string | null;
  lastDisconnectAt?: string | null;
  reconnectCount: number;
  sendFailures: number;
  lastError?: string | null;
}

export interface ProfileInfo {
  profile: string;
  provider: string;
  available: boolean;
  supportTier: number;
}

export interface PluginFailure {
  id: string;
  state: string;
  phase: string;
  code: string;
  message: string;
}

export interface PluginNode {
  id: string;
  version: string;
  kind: string;
  state: string;
  dependsOn?: string[];
  blockedBy?: string[];
}

export interface PluginsInfo {
  activated: number;
  failed: number;
  failures: PluginFailure[];
  nodes: PluginNode[];
  contributions: unknown[];
  permissions: unknown[];
  source: string;
}

export interface GapInfo {
  code: string;
  summary: string;
  area: string;
  hostChange: string;
  doc: string;
}

export interface AuditEntry {
  timestamp: string;
  action: string;
  user?: string | null;
  ip?: string | null;
  target?: string | null;
  result: string;
  durationMs: number;
}

export interface AdminStatus {
  configStoreAvailable: boolean;
  configWriteEnabled: boolean;
  configGapCode?: string | null;
  commandRunnerAvailable: boolean;
  commandEnabled: boolean;
  commandGapCode?: string | null;
  commandAllowlist: string[];
  commandTimeoutSeconds: number;
  logReaderAvailable: boolean;
}

export interface ConfigPreview {
  source: string;
  schemaPath?: string | null;
  writeEnabled: boolean;
  writeGapCode?: string | null;
  values: unknown;
  redacted: string[];
  documents?: ConfigDocumentSummary[];
}

export interface ConfigDocumentSummary {
  id: string;
  exists: boolean;
  eTag?: string | null;
  lastModified?: string | null;
  source?: string;
  writable?: boolean;
  reloadHint?: string;
}

export interface Snapshot {
  generatedAt: string;
  self: SelfInfo;
  checks: HealthCheck[];
  health: Section<{ status: string; uptimeSeconds: number }>;
  plugins: Section<PluginsInfo>;
  upstreams: Section<UpstreamInfo[]>;
  profiles: Section<ProfileInfo[]>;
  logs: Section<LogEntry[]>;
  config: ConfigPreview;
  admin: AdminStatus;
  gaps: GapInfo[];
  audit: AuditEntry[];
}

export interface LogEntry {
  timestamp: string;
  level: string;
  source: string;
  message: string;
}

export interface LogQueryResponse {
  hostLogs: Section<LogEntry[]>;
  total: number;
  offset: number;
  returned: number;
  limit: number;
  hasMore: boolean;
  nextOffset?: number | null;
  query?: { order?: string };
}

export interface SessionPayload {
  authenticated: boolean;
  role: "admin" | "viewer";
  csrf: string;
  permissions: { read: boolean; write: boolean; command: boolean };
}

export interface ConfigDocument {
  id: string;
  exists: boolean;
  eTag?: string | null;
  lastModified?: string | null;
  content?: string | null;
  redacted?: boolean;
  source?: string;
  reloadHint?: string;
  restartRequired?: boolean;
  sizeBytes?: number;
}

export interface ConfigIssue {
  pointer?: string | null;
  code: string;
  message: string;
}

export interface ConfigDiff {
  added: number;
  removed: number;
  truncated: boolean;
  lines: string[];
}

export interface ConfigValidation {
  valid: boolean;
  source?: string;
  eTag?: string | null;
  sizeBytes: number;
  containsSecretLiteral: boolean;
  issues: ConfigIssue[];
  diff?: ConfigDiff | null;
}

export interface ConfigSaveResponse {
  eTag: string;
  backupPath?: string | null;
  undoId?: string | null;
  reloadHint?: string;
  restartRequired?: boolean;
  diff?: ConfigDiff | null;
}

export interface UndoEntry {
  undoId: string;
  recordedAt: string;
  sizeBytes: number;
  backupPath?: string | null;
}

export interface CommandResponse {
  executed: boolean;
  exitCode: number;
  durationMs: number;
  outputBytes: number;
  truncated: boolean;
  timedOut: boolean;
  stdout?: string;
  stderr?: string;
}

export interface CommandCapabilities {
  enabled: boolean;
  runnerAvailable: boolean;
  allowlist: string[];
  timeoutSeconds: number;
}

export interface TraceEntry {
  kind: string;
  at: string;
  name?: string | null;
  code?: string | null;
  step?: number | null;
  status?: string | null;
  /** Real duration measured by the host writer; null/absent means the UI must estimate. */
  durationMs?: number | null;
}

export interface TraceHeader {
  taskId: string;
  sessionId: string;
  source: string;
  status: string;
  version: number;
  createdAt: string;
  updatedAt: string;
  elapsedMs: number;
  errorCode?: string | null;
  errorMessage?: string | null;
  resultText?: string | null;
  resultTruncated: boolean;
}

export interface TraceFilter {
  source?: string;
  status?: string;
  session?: string;
  since?: string;
  until?: string;
}

export interface TraceListResponse {
  available: boolean;
  limit: number;
  count: number;
  filter?: TraceFilter | null;
  tasks: Array<{
    taskId: string;
    status: string;
    source: string;
    createdAt: string;
    updatedAt: string;
    elapsedMs: number;
    errorCode?: string | null;
    version: number;
  }>;
}

export interface TraceDetailResponse {
  available: boolean;
  task: TraceHeader;
  entries: TraceEntry[];
}

export interface DiagManifestFile {
  path: string;
  contentType: string;
  bytes: number;
  items?: number | null;
  shownItems?: number | null;
  truncation?: string | null;
}

export interface ConversationSession {
  sessionId: string;
  sourceKey?: string;
  messageCount?: number;
  lastMessageAt?: string | null;
  actorId?: string | null;
  queryScopeNotice?: string | null;
}

export interface ConversationMessage {
  messageId: number;
  actorId?: string | null;
  role?: string;
  text?: string;
  createdAt?: string | null;
}
