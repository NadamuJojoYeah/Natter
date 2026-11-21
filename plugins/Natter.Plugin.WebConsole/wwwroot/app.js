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
};

let csrf = null;
let configEtag = null;
let configDocumentId = null;
let adminStatus = null;

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

  ui.logs.textContent = snapshot.logs?.available
    ? "宿主日志已启用。"
    : `宿主日志不可用：${snapshot.logs?.reason || snapshot.logs?.gapCode || "unknown"}（使用审计流水替代）`;
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

  if (!entries.length) {
    ui.configUndo.appendChild(node("p", "没有可恢复的内存快照（宿主备份请按路径手动恢复）。", "empty"));
    return;
  }

  for (const entry of entries) {
    const row = node("div", undefined, "undo-row");
    row.appendChild(node("span", `${entry.undoId} · ${formatTime(entry.recordedAt)} · ${entry.sizeBytes} B`, "muted"));
    if (entry.backupPath) {
      row.appendChild(node("span", ` 宿主备份：${entry.backupPath}`, "doc"));
    }
    const button = node("button", "回滚到此处");
    button.type = "button";
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
    setConfigStatus(
      `已保存 · etag=${payload.eTag} · 备份=${payload.backupPath || "（新建，无备份）"} · ${describeReloadHint(payload)}`,
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
  } else if (!writeEnabled) {
    setConfigStatus("配置编辑只读：readOnly=false 且 allowConfigWrite=true 才能保存。", true);
  } else {
    setConfigStatus(`宿主配置已接入（${snapshot.config?.writeGapCode || "可写"}）。`, false);
  }

  const commandAvailable = Boolean(adminStatus.commandRunnerAvailable);
  const commandEnabled = Boolean(adminStatus.commandEnabled);
  ui.commandRun.disabled = !commandAvailable || !commandEnabled;
  ui.commandName.disabled = !commandEnabled;
  ui.commandArgs.disabled = !commandEnabled;
  ui.commandStatus.textContent = commandEnabled
    ? (commandAvailable
      ? `允许命令：${(adminStatus.commandAllowlist || []).join(", ") || "（空）"} · 超时 ${adminStatus.commandTimeoutSeconds}s`
      : `命令控制台不可用：${adminStatus.commandGapCode || "host.admin_command.unavailable"}`)
    : "命令控制台已由插件配置禁用。";
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
