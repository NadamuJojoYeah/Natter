"use strict";

const ui = {
  login: document.getElementById("login"),
  dashboard: document.getElementById("dashboard"),
  loginForm: document.getElementById("login-form"),
  token: document.getElementById("token"),
  loginError: document.getElementById("login-error"),
  statusLine: document.getElementById("status-line"),
  subtitle: document.getElementById("subtitle"),
  overview: document.getElementById("overview"),
  checks: document.getElementById("checks"),
  upstreams: document.getElementById("upstreams"),
  profiles: document.getElementById("profiles"),
  plugins: document.getElementById("plugins"),
  gaps: document.getElementById("gaps"),
  config: document.getElementById("config"),
  audit: document.getElementById("audit"),
  logs: document.getElementById("logs"),
  refresh: document.getElementById("refresh"),
  logout: document.getElementById("logout"),
  configDoc: document.getElementById("config-doc"),
  configLoad: document.getElementById("config-load"),
  configValidate: document.getElementById("config-validate"),
  configSave: document.getElementById("config-save"),
  configStatus: document.getElementById("config-status"),
  configText: document.getElementById("config-text"),
  configDiff: document.getElementById("config-diff"),
  configUndo: document.getElementById("config-undo"),
  commandName: document.getElementById("command-name"),
  commandArgs: document.getElementById("command-args"),
  commandRun: document.getElementById("command-run"),
  commandStatus: document.getElementById("command-status"),
  commandOutput: document.getElementById("command-output"),
  roleBadge: document.getElementById("role-badge"),
  logLevel: document.getElementById("log-level"),
  logSource: document.getElementById("log-source"),
  logQ: document.getElementById("log-q"),
  logLimit: document.getElementById("log-limit"),
  logQuery: document.getElementById("log-query"),
  logPrev: document.getElementById("log-prev"),
  logNext: document.getElementById("log-next"),
  logExportText: document.getElementById("log-export-text"),
  logExportJsonl: document.getElementById("log-export-jsonl"),
  logStatus: document.getElementById("log-status"),
  logEntries: document.getElementById("log-entries"),
};

let csrf = null;
let configEtag = null;
let configDocumentId = null;
let adminStatus = null;
let currentRole = "viewer";
let rolePermissions = { read: true, write: false, command: false };
let logOffset = 0;
let logHasMore = false;
let logNextOffset = null;
const LOG_PAGE_SIZE_DEFAULT = 200;

async function api(path, options = {}) {
  const method = options.method || "GET";
  const headers = { Accept: "application/json" };

  if (options.body !== undefined) {
    headers["Content-Type"] = "application/json";
  }

  if (csrf && method !== "GET") {
    headers["X-WebConsole-CSRF"] = csrf;
  }

  const response = await fetch(path, {
    method,
    headers,
    body: options.body === undefined ? undefined : JSON.stringify(options.body),
    credentials: "same-origin",
    cache: "no-store",
  });

  let payload = null;
  if (response.status !== 204) {
    try {
      payload = await response.json();
    } catch {
      payload = null;
    }
  }

  if (!response.ok) {
    const error = new Error(payload?.error?.message || `HTTP ${response.status}`);
    error.status = response.status;
    error.code = payload?.error?.code;
    error.detail = payload?.detail;
    error.result = payload?.result;
    throw error;
  }

  return payload;
}

function node(tag, text, className) {
  const element = document.createElement(tag);
  if (text !== undefined && text !== null && text !== "") {
    element.textContent = String(text);
  }
  if (className) {
    element.className = className;
  }
  return element;
}

function badge(status) {
  return node("span", status, `badge ${status}`);
}

function formatTime(value) {
  if (!value) {
    return "—";
  }
  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

function applySession(session) {
  if (!session || !session.authenticated) {
    return;
  }

  currentRole = session.role || "viewer";
  rolePermissions = session.permissions || { read: true, write: false, command: false };
  ui.roleBadge.textContent = currentRole;
  ui.roleBadge.className = `badge ${currentRole === "admin" ? "ok" : "degraded"}`;
}

function applyWritePermission() {
  const canWrite = currentRole === "admin" && rolePermissions.write === true;
  const canCommand = currentRole === "admin" && rolePermissions.command === true;

  if (ui.configSave) {
    ui.configSave.disabled = ui.configSave.disabled || !canWrite;
  }
  if (ui.commandRun && !canCommand) {
    ui.commandRun.disabled = true;
  }
}

function showLogin(message) {
  ui.dashboard.hidden = true;
  ui.login.hidden = false;
  ui.loginError.textContent = message || "";
  ui.token.focus();
}

function showDashboard() {
  ui.login.hidden = true;
  ui.dashboard.hidden = false;
}

function unavailableNote(section) {
  const box = node("p", undefined, "empty");
  box.textContent = `不可用：${section.reason || section.gapCode || "unknown"}`
    + (section.gapCode ? ` (${section.gapCode})` : "");
  return box;
}

function cell(value) {
  const td = node("td");
  if (value instanceof Node) {
    td.appendChild(value);
  } else {
    td.textContent = value === null || value === undefined || value === "" ? "—" : String(value);
  }
  return td;
}

function makeTable(headers, rows) {
  const table = node("table");
  const head = node("thead");
  const headRow = node("tr");
  for (const header of headers) {
    headRow.appendChild(node("th", header));
  }
  head.appendChild(headRow);
  table.appendChild(head);

  const body = node("tbody");
  for (const row of rows) {
    const tr = node("tr");
    for (const value of row) {
      tr.appendChild(cell(value));
    }
    body.appendChild(tr);
  }
  table.appendChild(body);
  return table;
}

function renderOverview(snapshot) {
  const self = snapshot.self;
  const items = [
    ["health", badge(self.healthStatus)],
    ["server", `${self.serverState}${self.serverUrl ? " " + self.serverUrl : ""}`],
    ["runtime", self.serverRuntime],
    ["host source", self.hostDataSource],
    ["uptime", `${self.uptimeSeconds}s`],
    ["bind", `${self.bind}:${self.port}`],
    ["token", self.tokenConfigured ? "configured" : "missing"],
    ["read only", String(self.readOnly)],
    ["config write", self.allowConfigWrite && !self.readOnly ? "enabled" : "disabled"],
    ["command console", self.allowCommandConsole && !self.readOnly ? "enabled" : "disabled"],
    ["session", `${self.sessionMinutes} min`],
    ["plugin", `${self.pluginId} ${self.version} · hostApi ${self.hostApi}`],
  ];

  ui.overview.replaceChildren();
  for (const [label, value] of items) {
    ui.overview.appendChild(node("dt", label));
    const dd = node("dd");
    if (value instanceof Node) {
      dd.appendChild(value);
    } else {
      dd.textContent = value;
    }
    ui.overview.appendChild(dd);
  }
}

function renderChecks(checks) {
  ui.checks.replaceChildren();
  for (const check of checks) {
    const li = node("li");
    const left = node("span");
    left.appendChild(node("strong", check.name));
    if (check.detail) {
      left.appendChild(node("span", ` · ${check.detail}`, "muted"));
    }
    li.appendChild(left);
    li.appendChild(badge(check.status));
    ui.checks.appendChild(li);
  }
}

function renderUpstreams(section) {
  ui.upstreams.replaceChildren();

  if (!section.available) {
    ui.upstreams.appendChild(unavailableNote(section));
    return;
  }

  if (!section.data.length) {
    ui.upstreams.appendChild(node("p", "没有 upstream。", "empty"));
    return;
  }

  ui.upstreams.appendChild(makeTable(
    ["source", "connected", "lastConnectedAt", "lastDisconnectAt", "reconnects", "sendFailures", "lastError"],
    section.data.map((upstream) => [
      upstream.source,
      badge(upstream.connected ? "ok" : "degraded"),
      formatTime(upstream.lastConnectedAt),
      formatTime(upstream.lastDisconnectAt),
      upstream.reconnectCount,
      upstream.sendFailures,
      upstream.lastError,
    ]),
  ));
}

function renderProfiles(section) {
  ui.profiles.replaceChildren();

  if (!section.available) {
    ui.profiles.appendChild(unavailableNote(section));
    return;
  }

  if (!section.data.length) {
    ui.profiles.appendChild(node("p", "没有 profile/provider。", "empty"));
    return;
  }

  ui.profiles.appendChild(makeTable(
    ["profile", "provider", "available", "tier"],
    section.data.map((profile) => [
      profile.profile,
      profile.provider,
      badge(profile.available ? "ok" : "degraded"),
      profile.supportTier,
    ]),
  ));
}

function renderPlugins(section) {
  ui.plugins.replaceChildren();

  if (!section.available) {
    ui.plugins.appendChild(unavailableNote(section));
    return;
  }

  const data = section.data;
  ui.plugins.appendChild(node(
    "p",
    `activated=${data.activated} failed=${data.failed} source=${data.source}`,
    "muted",
  ));

  if (data.failures.length) {
    ui.plugins.appendChild(node("h3", "失败插件"));
    ui.plugins.appendChild(makeTable(
      ["id", "state", "phase", "code", "message"],
      data.failures.map((failure) => [
        failure.id, failure.state, failure.phase, failure.code, failure.message,
      ]),
    ));
  }

  if (data.nodes.length) {
    ui.plugins.appendChild(node("h3", "插件图"));
    ui.plugins.appendChild(makeTable(
      ["id", "version", "kind", "state", "dependsOn", "blockedBy"],
      data.nodes.map((item) => [
        item.id,
        item.version,
        item.kind,
        item.state,
        (item.dependsOn || []).join(", "),
        (item.blockedBy || []).join(", "),
      ]),
    ));
  }

  if (data.contributions.length || data.permissions.length) {
    ui.plugins.appendChild(node(
      "p",
      `contributions=${data.contributions.length} permissions=${data.permissions.length}`,
      "muted",
    ));
  }
}

function renderGaps(gaps) {
  ui.gaps.replaceChildren();
  for (const gap of gaps) {
    const li = node("li");
    li.appendChild(node("div", gap.code, "code"));
    li.appendChild(node("div", gap.summary));
    li.appendChild(node("div", `影响面：${gap.area} · 宿主改动：${gap.hostChange}`, "doc"));
    li.appendChild(node("div", gap.doc, "doc"));
    ui.gaps.appendChild(li);
  }
}

function renderAudit(snapshot) {
  ui.audit.replaceChildren();
  const entries = snapshot.audit || [];

  if (!entries.length) {
    ui.audit.appendChild(node("p", "暂无审计记录。", "empty"));
    return;
  }

  ui.audit.appendChild(makeTable(
    ["time", "action", "user", "ip", "target", "result", "ms"],
    entries.slice().reverse().map((entry) => [
      formatTime(entry.timestamp),
      entry.action,
      entry.user,
      entry.ip,
      entry.target,
      entry.result,
      entry.durationMs,
    ]),
  ));
}

function renderConfig(config) {
  ui.config.textContent = JSON.stringify(config?.values ?? {}, null, 2)
    + `\n\n// redacted=${(config?.redacted || []).length}`
    + ` write=${config?.writeEnabled ? "enabled" : "disabled"}`
    + (config?.writeGapCode ? ` (${config.writeGapCode})` : "");
}

function render(snapshot) {
  const self = snapshot.self;
  ui.subtitle.textContent = `${self.pluginId} ${self.version} · ${self.kind} · hostApi ${self.hostApi}`;

  ui.statusLine.className = `status ${self.healthStatus}`;
  ui.statusLine.textContent = `health=${self.healthStatus} · server=${self.serverState}`
    + (self.serverUrl ? ` ${self.serverUrl}` : "")
    + ` · host=${self.hostDataSource}`;

  renderOverview(snapshot);
  renderChecks(snapshot.checks || []);
  renderUpstreams(snapshot.upstreams);
  renderProfiles(snapshot.profiles);
  renderPlugins(snapshot.plugins);
  renderGaps(snapshot.gaps || []);
  renderAudit(snapshot);
  renderConfig(snapshot.config);
  fillDocumentSelect(snapshot);
  applyAdminAvailability(snapshot);
  applyWritePermission();
  queryLogs({ resetOffset: true }).catch((error) => setLogStatus(`日志加载失败：${error.message}`, true));

  ui.logs.textContent = snapshot.logs?.available
    ? "宿主日志已启用。"
    : `宿主日志不可用：${snapshot.logs?.reason || snapshot.logs?.gapCode || "unknown"}（使用审计流水替代）`;
}


function setLogStatus(message, isError) {
  ui.logStatus.textContent = message;
  ui.logStatus.className = isError ? "error" : "muted";
}

function logQueryString(includeOffset) {
  const parts = [];
  const limit = ui.logLimit.value || String(LOG_PAGE_SIZE_DEFAULT);

  parts.push(`limit=${encodeURIComponent(limit)}`);
  if (includeOffset && logOffset > 0) {
    parts.push(`offset=${encodeURIComponent(String(logOffset))}`);
  }
  if (ui.logLevel.value) {
    parts.push(`level=${encodeURIComponent(ui.logLevel.value)}`);
  }
  if (ui.logSource.value.trim()) {
    parts.push(`source=${encodeURIComponent(ui.logSource.value.trim())}`);
  }
  if (ui.logQ.value.trim()) {
    parts.push(`q=${encodeURIComponent(ui.logQ.value.trim())}`);
  }

  return parts.join("&");
}

function renderLogEntries(payload) {
  ui.logEntries.replaceChildren();

  const section = payload.hostLogs || {};
  if (!section.available) {
    ui.logEntries.appendChild(node(
      "p",
      `宿主日志不可用：${section.reason || section.gapCode || "unknown"}（审计流水仍可在下方查看）`,
      "empty",
    ));
    return;
  }

  const entries = section.data || [];
  if (!entries.length) {
    ui.logEntries.appendChild(node("p", "没有匹配的日志行。", "empty"));
    return;
  }

  ui.logEntries.appendChild(makeTable(
    ["time", "level", "source", "message"],
    entries.map((entry) => [
      formatTime(entry.timestamp),
      badge(entry.level === "Error" || entry.level === "Critical" ? "error" : entry.level === "Warning" ? "degraded" : "ok"),
      entry.source,
      entry.message,
    ]),
  ));
}

async function queryLogs(options = {}) {
  const reset = options.resetOffset === true;
  if (reset) {
    logOffset = 0;
  }

  setLogStatus("查询中…", false);
  try {
    const payload = await api(`/api/logs?${logQueryString(true)}`);
    renderLogEntries(payload);
    logHasMore = payload.hasMore === true;
    logNextOffset = payload.nextOffset ?? null;

    const start = payload.total === 0 ? 0 : (payload.offset || 0) + 1;
    const end = (payload.offset || 0) + (payload.returned || 0);
    setLogStatus(
      `匹配 ${payload.total} 条，显示 ${start}–${end}（每页 ${payload.limit}，order=${payload.query?.order || "desc"}）`
        + (logHasMore ? "，还有更多" : ""),
      false,
    );

    ui.logPrev.disabled = (payload.offset || 0) === 0;
    ui.logNext.disabled = !logHasMore;
  } catch (error) {
    setLogStatus(`${error.code || "error"}：${error.message}`, true);
    ui.logPrev.disabled = true;
    ui.logNext.disabled = true;
  }
}

function exportLogs(format) {
  const url = `/api/logs/export?format=${encodeURIComponent(format)}&${logQueryString(false)}`;
  setLogStatus(`已开始导出：${url}（内容已脱敏，单次最多 5000 行 / 1 MiB）`, false);

  // Same-origin navigation is what triggers the attachment download in a browser; tests run
  // without a location object, so guard it instead of failing the render.
  const location = globalThis?.location;
  if (location && typeof location.assign === "function") {
    location.assign(url);
  }
}

function nextLogPage() {
  if (!logHasMore) {
    return;
  }

  const limit = Number(ui.logLimit.value || LOG_PAGE_SIZE_DEFAULT);
  logOffset = logNextOffset ?? logOffset + limit;
  queryLogs().catch((error) => setLogStatus(`翻页失败：${error.message}`, true));
}

function previousLogPage() {
  const limit = Number(ui.logLimit.value || LOG_PAGE_SIZE_DEFAULT);
  logOffset = Math.max(0, logOffset - limit);
  queryLogs().catch((error) => setLogStatus(`翻页失败：${error.message}`, true));
}

function setConfigStatus(message, isError) {
  ui.configStatus.textContent = message;
  ui.configStatus.className = isError ? "error" : "muted";
}

function parseArgs(text) {
  return String(text || "")
    .split(/\s+/)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

function describeReloadHint(response) {
  if (!response) {
    return "—";
  }
  if (response.restartRequired) {
    return response.reloadHint === "restart-plugin"
      ? "保存后需重启宿主以重新加载插件配置"
      : "保存后需重启宿主以生效";
  }
  return response.reloadHint === "reload-profiles" ? "profiles 已热重载" : "无需重启";
}

function fillDocumentSelect(snapshot) {
  const documents = (snapshot.config?.documents || []).map((doc) => doc.id);
  const fallback = ["profiles", `plugin:${snapshot.self.pluginId}`];
  const ids = documents.length ? documents : fallback;
  const current = ui.configDoc.value || fallback[0];

  ui.configDoc.replaceChildren();
  for (const id of ids) {
    const option = document.createElement("option");
    option.value = id;
    option.textContent = id;
    ui.configDoc.appendChild(option);
  }

  ui.configDoc.value = ids.includes(current) ? current : ids[0];
  configDocumentId = ui.configDoc.value;
}

function renderUndo(entries) {
  ui.configUndo.replaceChildren();
  ui.configUndo.appendChild(node(
    "p",
    "会话内快照：每文档最多 8 条、全局 32 条、30 分钟后失效；控制台不读取宿主备份内容，"
      + "宿主备份需在服务器上手动恢复（路径由保存响应给出）。",
    "doc",
  ));

  if (!entries.length) {
    ui.configUndo.appendChild(node("p", "当前没有可回滚的会话内快照。", "empty"));
    return;
  }

  const canRollback = currentRole === "admin" && rolePermissions.write === true;

  for (const entry of entries) {
    const row = node("div", undefined, "undo-row");
    row.appendChild(node("span", `${entry.undoId} · ${formatTime(entry.recordedAt)} · ${entry.sizeBytes} B`, "muted"));
    if (entry.backupPath) {
      row.appendChild(node("span", ` 宿主备份（手动恢复）：${entry.backupPath}`, "doc"));
    }
    const button = node("button", canRollback ? "回滚到此处" : "回滚（需 admin）");
    button.type = "button";
    button.disabled = !canRollback;
    button.addEventListener("click", () => rollbackConfig(entry.undoId));
    row.appendChild(button);
    ui.configUndo.appendChild(row);
  }
}

function renderValidation(validation) {
  const lines = [
    `valid=${validation.valid} source=${validation.source || "?"} etag=${validation.eTag || "?"}`,
    `size=${validation.sizeBytes} bytes secret=${validation.containsSecretLiteral}`,
  ];

  for (const issue of validation.issues || []) {
    lines.push(`- ${issue.pointer || "$"} [${issue.code}] ${issue.message}`);
  }

  const diff = validation.diff;
  if (diff) {
    lines.push("", `diff +${diff.added} -${diff.removed}${diff.truncated ? " (截断)" : ""}`);
    lines.push(...(diff.lines || []).slice(0, 60));
  }

  ui.configDiff.textContent = lines.join("\n");
}

async function loadConfigDocument() {
  if (!configDocumentId) {
    return;
  }

  setConfigStatus("加载中…", false);
  try {
    const document = await api(`/api/config/${encodeURIComponent(configDocumentId)}`);
    configEtag = document.exists ? document.eTag : null;
    ui.configText.value = document.content || "";
    ui.configDiff.textContent = "";
    setConfigStatus(
      `${document.exists ? "已加载" : "文档不存在（保存将创建）"} · etag=${document.eTag} · `
        + `source=${document.source} · ${describeReloadHint(document)}`,
      false,
    );
    await refreshUndo();
  } catch (error) {
    setConfigStatus(`${error.code || "error"}：${error.message}`, true);
  }
}

async function refreshUndo() {
  if (!configDocumentId) {
    return;
  }
  try {
    const payload = await api(`/api/config/${encodeURIComponent(configDocumentId)}/undo`);
    renderUndo(payload.entries || []);
  } catch {
    renderUndo([]);
  }
}

async function validateConfig() {
  if (!configDocumentId) {
    return;
  }

  setConfigStatus("校验中…", false);
  try {
    const payload = await api(`/api/config/${encodeURIComponent(configDocumentId)}/validate`, {
      method: "POST",
      body: { content: ui.configText.value },
    });
    renderValidation(payload.validation);
    setConfigStatus(payload.validation.valid ? "校验通过。" : "校验未通过，见下方问题列表。", !payload.validation.valid);
  } catch (error) {
    setConfigStatus(`${error.code || "error"}：${error.message}`, true);
  }
}

async function saveConfig() {
  if (!configDocumentId) {
    return;
  }

  setConfigStatus("保存中…", false);
  try {
    const payload = await api(`/api/config/${encodeURIComponent(configDocumentId)}`, {
      method: "POST",
      body: { content: ui.configText.value, etag: configEtag },
    });
    configEtag = payload.eTag;
    const backup = payload.backupPath
      ? `宿主备份 ${payload.backupPath}（服务器端手动恢复）`
      : "新建文档，无宿主备份";
    setConfigStatus(
      `已保存 · etag=${payload.eTag} · ${backup} · ${describeReloadHint(payload)}`
        + (payload.undoId ? ` · 可回滚快照 ${payload.undoId}` : ""),
      false,
    );
    renderValidation({ valid: true, source: "host", eTag: payload.eTag, sizeBytes: 0, containsSecretLiteral: false, issues: [], diff: payload.diff });
    await refreshUndo();
  } catch (error) {
    if (error.status === 409 && error.code === "config.conflict") {
      const current = error.detail?.conflictETag;
      if (current) {
        configEtag = current;
      }
      setConfigStatus(`内容已被他人修改（当前 etag=${current || "?"}）。请重新加载后再保存。`, true);
      return;
    }
    setConfigStatus(`${error.code || "error"}：${error.message}`, true);
  }
}

async function rollbackConfig(undoId) {
  if (!configDocumentId) {
    return;
  }

  setConfigStatus("回滚中…", false);
  try {
    const payload = await api(`/api/config/${encodeURIComponent(configDocumentId)}/rollback`, {
      method: "POST",
      body: { etag: configEtag, undoId },
    });
    const saved = payload.saved || {};
    configEtag = saved.eTag || configEtag;
    setConfigStatus(`已回滚到快照 ${undoId} · etag=${saved.eTag || "?"}`, false);
    await loadConfigDocument();
  } catch (error) {
    if (error.code === "config.undo_empty") {
      setConfigStatus("没有可回滚的会话内快照（可能已超过 30 分钟或已被使用）；请用宿主备份路径手动恢复。", true);
      return;
    }
    setConfigStatus(`${error.code || "error"}：${error.message}`, true);
  }
}

function applyAdminAvailability(snapshot) {
  adminStatus = snapshot.admin || {};
  const configAvailable = Boolean(adminStatus.configStoreAvailable);
  const writeEnabled = Boolean(snapshot.config?.writeEnabled);

  for (const element of [ui.configLoad, ui.configValidate]) {
    element.disabled = !configAvailable;
  }
  ui.configSave.disabled = !configAvailable || !writeEnabled;
  ui.configDoc.disabled = !configAvailable;

  if (!configAvailable) {
    setConfigStatus(`配置编辑不可用：${snapshot.config?.writeGapCode || "host.admin_config.unavailable"}`, true);
  } else if (currentRole !== "admin") {
    setConfigStatus("viewer 只读会话：可以查看与校验，保存/回滚需要 admin 角色。", true);
  } else if (!writeEnabled) {
    setConfigStatus("只读模式：readOnly=true（统一返回 console.read_only），或 allowConfigWrite=false。", true);
  } else {
    setConfigStatus(`宿主配置已接入（${snapshot.config?.writeGapCode || "可写"}）。`, false);
  }

  const commandAvailable = Boolean(adminStatus.commandRunnerAvailable);
  const commandEnabled = Boolean(adminStatus.commandEnabled);
  ui.commandRun.disabled = !commandAvailable || !commandEnabled;
  ui.commandName.disabled = !commandEnabled;
  ui.commandArgs.disabled = !commandEnabled;
  if (currentRole !== "admin") {
    ui.commandStatus.textContent = "viewer 只读会话：命令执行需要 admin 角色（服务端强制）。";
  } else if (commandEnabled) {
    ui.commandStatus.textContent = commandAvailable
      ? `允许命令：${(adminStatus.commandAllowlist || []).join(", ") || "（空）"} · 超时 ${adminStatus.commandTimeoutSeconds}s`
      : `命令控制台不可用：${adminStatus.commandGapCode || "host.admin_command.unavailable"}`;
  } else {
    ui.commandStatus.textContent = "命令控制台已禁用：readOnly=true 或 allowCommandConsole=false。";
  }
  ui.commandStatus.className = commandEnabled && commandAvailable ? "muted" : "error";
}

async function runCommand() {
  const command = ui.commandName.value.trim();
  if (!command) {
    ui.commandOutput.textContent = "请先输入命令（需与 commandAllowlist 精确匹配）。";
    return;
  }

  ui.commandRun.disabled = true;
  ui.commandOutput.textContent = "运行中…";
  try {
    const payload = await api("/api/commands", {
      method: "POST",
      body: { command, args: parseArgs(ui.commandArgs.value) },
    });
    ui.commandOutput.textContent = [
      `exit=${payload.exitCode} duration=${payload.durationMs}ms bytes=${payload.outputBytes}`
        + `${payload.truncated ? " (output 已截断)" : ""}`,
      payload.stdout ? `--- stdout ---\n${payload.stdout}` : "",
      payload.stderr ? `--- stderr ---\n${payload.stderr}` : "",
    ].filter(Boolean).join("\n");
  } catch (error) {
    const result = error.result;
    const detail = result
      ? `exit=${result.exitCode} duration=${result.durationMs}ms${result.timedOut ? " (超时)" : ""}\n`
        + `${result.stdout || ""}${result.stderr ? "\n" + result.stderr : ""}`
      : "";
    ui.commandOutput.textContent = `${error.code || "error"}：${error.message}\n${detail}`;
  } finally {
    ui.commandRun.disabled = false;
  }
}

async function load() {
  ui.statusLine.textContent = "loading…";
  try {
    const snapshot = await api("/api/snapshot");
    render(snapshot);
  } catch (error) {
    if (error.status === 401) {
      csrf = null;
      showLogin("会话已过期，请重新登录。");
      return;
    }
    throw error;
  }
}

async function bootstrap() {
  try {
    const session = await api("/api/session");
    csrf = session.csrf;
    applySession(session);
    showDashboard();
    await load();
  } catch (error) {
    if (error.status === 401) {
      showLogin();
      return;
    }
    showLogin(error.message);
  }
}

ui.loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  ui.loginError.textContent = "";
  const button = ui.loginForm.querySelector("button");
  button.disabled = true;

  try {
    const session = await api("/api/session", { method: "POST", body: { token: ui.token.value } });
    csrf = session.csrf;
    applySession(session);
    ui.token.value = "";
    showDashboard();
    await load();
  } catch (error) {
    ui.loginError.textContent = error.message;
  } finally {
    button.disabled = false;
  }
});

ui.configDoc.addEventListener("change", () => {
  configDocumentId = ui.configDoc.value;
  configEtag = null;
  ui.configText.value = "";
  ui.configDiff.textContent = "";
  setConfigStatus(`已选择 ${configDocumentId}，点击“加载”读取当前内容。`, false);
  renderUndo([]);
});

ui.configLoad.addEventListener("click", () => {
  loadConfigDocument().catch((error) => setConfigStatus(`加载失败：${error.message}`, true));
});

ui.configValidate.addEventListener("click", () => {
  validateConfig().catch((error) => setConfigStatus(`校验失败：${error.message}`, true));
});

ui.configSave.addEventListener("click", () => {
  saveConfig().catch((error) => setConfigStatus(`保存失败：${error.message}`, true));
});

ui.commandRun.addEventListener("click", () => {
  runCommand().catch((error) => {
    ui.commandOutput.textContent = `运行失败：${error.message}`;
    ui.commandRun.disabled = false;
  });
});

ui.logQuery.addEventListener("click", () => {
  queryLogs({ resetOffset: true }).catch((error) => setLogStatus(`查询失败：${error.message}`, true));
});

ui.logPrev.addEventListener("click", previousLogPage);
ui.logNext.addEventListener("click", nextLogPage);
ui.logExportText.addEventListener("click", () => exportLogs("text"));
ui.logExportJsonl.addEventListener("click", () => exportLogs("jsonl"));

ui.refresh.addEventListener("click", () => {
  load().catch((error) => {
    ui.statusLine.className = "status error";
    ui.statusLine.textContent = `刷新失败：${error.message}`;
  });
});

ui.logout.addEventListener("click", async () => {
  try {
    await api("/api/session", { method: "DELETE" });
  } catch {
    // A dead session still returns to the login screen.
  }
  csrf = null;
  showLogin("已退出。");
});

bootstrap();
