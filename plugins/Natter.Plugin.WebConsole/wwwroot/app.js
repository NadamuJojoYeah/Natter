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
};

let csrf = null;

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

  ui.logs.textContent = snapshot.logs?.available
    ? "宿主日志已启用。"
    : `宿主日志不可用：${snapshot.logs?.reason || snapshot.logs?.gapCode || "unknown"}（使用审计流水替代）`;
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
