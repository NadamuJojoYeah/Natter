// WebConsole entry: session, routing, polling and event wiring only.
// Views stay pure (data → HTML); all mutable state lives in state.js (UI-DIRECTION §4.2).

import { describeError, download, request } from "./api.js";
import { applySession, canWrite, patchCommands, patchConfig, patchDiag, patchLogs, patchTrace, state, subscribe, update } from "./state.js";
import { escapeHtml, stateBlock } from "./util.js";
import { renderAudit, renderChecks, renderGaps, renderOverview, renderPlugins, renderProfiles, renderUpstreams } from "./views/overview.js";
import { parseStatusText, renderTimeline } from "./views/trace.js";
import { renderLogs } from "./views/logs.js";
import { renderConfigDocuments, renderConfigMessage, renderConfigStatus, renderUndo, renderValidation } from "./views/config.js";
import { renderCommandAvailability, renderCommandResult } from "./views/commands.js";
import { renderDiag } from "./views/diag.js";

const ui = {
  login: document.getElementById("login"),
  dashboard: document.getElementById("dashboard"),
  loginForm: document.getElementById("login-form"),
  token: document.getElementById("token"),
  loginError: document.getElementById("login-error"),
  roleBadge: document.getElementById("role-badge"),
  subtitle: document.getElementById("subtitle"),
  statusLine: document.getElementById("status-line"),
  refresh: document.getElementById("refresh"),
  logout: document.getElementById("logout"),
  overview: document.getElementById("overview"),
  checks: document.getElementById("checks"),
  upstreams: document.getElementById("upstreams"),
  profiles: document.getElementById("profiles"),
  plugins: document.getElementById("plugins"),
  gaps: document.getElementById("gaps"),
  audit: document.getElementById("audit"),
  traceInput: document.getElementById("trace-input"),
  traceParse: document.getElementById("trace-parse"),
  traceClear: document.getElementById("trace-clear"),
  traceView: document.getElementById("trace-view"),
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
  diagExport: document.getElementById("diag-export"),
  diagView: document.getElementById("diag-view"),
};

const POLL_INTERVAL_MS = 15_000;
let pollTimer = null;

// ------------------------------------------------------------------ session

function showLogin(message) {
  ui.dashboard.hidden = true;
  ui.login.hidden = false;
  ui.loginError.textContent = message || "";
  ui.token.focus();
}

function showDashboard() {
  ui.login.hidden = true;
  ui.dashboard.hidden = false;
  ui.roleBadge.textContent = state.role;
  ui.roleBadge.className = `badge ${state.role === "admin" ? "ok" : "degraded"}`;
}

function applyWritePermissions() {
  const write = canWrite();
  ui.configSave.disabled = ui.configSave.disabled || !write;
  if (!write) {
    ui.commandRun.disabled = true;
  }
}

// ------------------------------------------------------------------ snapshot

async function loadSnapshot() {
  ui.statusLine.textContent = "加载中…";
  try {
    const snapshot = await request("/api/snapshot");
    update({
      snapshot,
      snapshotState: "ready",
      snapshotError: null,
      readOnly: Boolean(snapshot.self?.readOnly),
      allowConfigWrite: Boolean(snapshot.self?.allowConfigWrite),
      allowCommandConsole: Boolean(snapshot.self?.allowCommandConsole),
    });
    renderSnapshot(snapshot);
    return snapshot;
  } catch (error) {
    const described = describeError(error);
    if (described.status === 401) {
      update({ csrf: null });
      showLogin("会话已过期，请重新登录。");
      return null;
    }

    update({ snapshotState: "error", snapshotError: described });
    ui.statusLine.className = "status error";
    ui.statusLine.textContent = `${described.code ?? "error"}：${described.message}`;
    ui.overview.innerHTML = stateBlock({ kind: "error", code: described.code, message: described.message });
    return null;
  }
}

function renderSnapshot(snapshot) {
  const self = snapshot.self;
  ui.subtitle.textContent = `${self.pluginId} ${self.version} · ${self.kind} · hostApi ${self.hostApi}`;
  ui.statusLine.className = `status ${self.healthStatus}`;
  ui.statusLine.textContent = `health=${self.healthStatus} · server=${self.serverState}`
    + (self.serverUrl ? ` ${self.serverUrl}` : "")
    + ` · host=${self.hostDataSource}`;

  ui.overview.innerHTML = renderOverview(snapshot);
  ui.checks.innerHTML = renderChecks(snapshot.checks || []);
  ui.upstreams.innerHTML = renderUpstreams(snapshot.upstreams);
  ui.profiles.innerHTML = renderProfiles(snapshot.profiles);
  ui.plugins.innerHTML = renderPlugins(snapshot.plugins);
  ui.gaps.innerHTML = renderGaps(snapshot.gaps || []);
  ui.audit.innerHTML = renderAudit(snapshot.audit || []);

  applyAdminAvailability(snapshot);
  applyWritePermissions();
  fillDocumentSelect(snapshot);
  queryLogs({ resetOffset: true }).catch(() => {});
}

function applyAdminAvailability(snapshot) {
  const admin = snapshot.admin || {};
  const configAvailable = Boolean(admin.configStoreAvailable);
  const writeEnabled = Boolean(snapshot.config?.writeEnabled);

  ui.configLoad.disabled = !configAvailable;
  ui.configValidate.disabled = !configAvailable;
  ui.configSave.disabled = !configAvailable || !writeEnabled;
  ui.configDoc.disabled = !configAvailable;

  ui.configStatus.innerHTML = renderConfigStatus({
    writeEnabled,
    gapCode: snapshot.config?.writeGapCode,
    adminAvailable: configAvailable,
  });

  ui.commandStatus.innerHTML = renderCommandAvailability(admin);
  const commandEnabled = Boolean(admin.commandEnabled) && Boolean(admin.commandRunnerAvailable) && state.role === "admin";
  ui.commandRun.disabled = !commandEnabled;
  ui.commandName.disabled = !commandEnabled;
  ui.commandArgs.disabled = !commandEnabled;
}

function fillDocumentSelect(snapshot) {
  const documents = snapshot.config?.documents || [];
  if (!documents.length) {
    ui.configDoc.innerHTML = renderConfigDocuments(
      [{ id: "profiles", exists: true }, { id: `plugin:${snapshot.self.pluginId}`, exists: true }],
      state.config.documentId,
    );
  } else {
    ui.configDoc.innerHTML = renderConfigDocuments(documents, state.config.documentId);
  }

  patchConfig({ documentId: ui.configDoc.value || "profiles" });
}

// ------------------------------------------------------------------ logs

function logQueryString(includeOffset) {
  const parts = [];
  const limit = ui.logLimit.value || "200";
  parts.push(`limit=${encodeURIComponent(limit)}`);
  if (includeOffset && state.logs.offset > 0) {
    parts.push(`offset=${encodeURIComponent(String(state.logs.offset))}`);
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

async function queryLogs({ resetOffset = false } = {}) {
  if (resetOffset) {
    patchLogs({ offset: 0 });
  }

  patchLogs({ state: "loading", error: null });
  ui.logEntries.innerHTML = renderLogs(null, { state: "loading" });

  try {
    const payload = await request(`/api/logs?${logQueryString(true)}`);
    patchLogs({
      state: "ready",
      payload,
      hasMore: payload.hasMore === true,
      nextOffset: payload.nextOffset ?? null,
      error: null,
    });
    ui.logEntries.innerHTML = renderLogs(payload);
    ui.logPrev.disabled = (payload.offset || 0) === 0;
    ui.logNext.disabled = payload.hasMore !== true;
    ui.logStatus.textContent = `匹配 ${payload.total} 条`;
    ui.logStatus.className = "muted";
  } catch (error) {
    const described = describeError(error);
    patchLogs({ state: "error", error: described });
    ui.logEntries.innerHTML = renderLogs(null, { state: "error", error: described });
    ui.logStatus.textContent = `${described.code ?? "error"}：${described.message}`;
    ui.logStatus.className = "error";
  }
}

function exportLogs(format) {
  const url = `/api/logs/export?format=${encodeURIComponent(format)}&${logQueryString(false)}`;
  download(url);
  ui.logStatus.textContent = `已开始导出：${url}（已脱敏，单次最多 5000 行 / 1 MiB）`;
  ui.logStatus.className = "muted";
}

// ------------------------------------------------------------------ config

async function loadConfigDocument() {
  const id = state.config.documentId;
  if (!id) {
    return;
  }

  ui.configStatus.innerHTML = stateBlock({ kind: "loading" });
  try {
    const document_ = await request(`/api/config/${encodeURIComponent(id)}`);
    patchConfig({ etag: document_.exists ? document_.eTag : null, state: "ready", error: null });
    ui.configText.value = document_.content || "";
    ui.configDiff.innerHTML = "";
    ui.configStatus.innerHTML = renderConfigMessage(
      `${document_.exists ? "已加载" : "文档不存在（保存将创建）"} · etag=${document_.eTag}`
      + ` · source=${document_.source} · ${describeReloadHint(document_)}`,
      false,
    );
    await refreshUndo();
  } catch (error) {
    const described = describeError(error);
    patchConfig({ state: "error", error: described });
    ui.configStatus.innerHTML = renderConfigMessage(`${described.code ?? "error"}：${described.message}`, true);
  }
}

function describeReloadHint(response) {
  if (!response) {
    return "—";
  }
  if (response.restartRequired) {
    return response.reloadHint === "restart-plugin" ? "保存后需重启宿主以重新加载插件配置" : "保存后需重启宿主以生效";
  }
  return response.reloadHint === "reload-profiles" ? "profiles 已热重载" : "无需重启";
}

async function refreshUndo() {
  const id = state.config.documentId;
  if (!id) {
    return;
  }

  try {
    const payload = await request(`/api/config/${encodeURIComponent(id)}/undo`);
    patchConfig({ undo: payload.entries || [] });
    ui.configUndo.innerHTML = renderUndo(payload.entries || []);
  } catch {
    ui.configUndo.innerHTML = renderUndo([]);
  }
}

async function validateConfig() {
  const id = state.config.documentId;
  if (!id) {
    return;
  }

  ui.configStatus.innerHTML = stateBlock({ kind: "loading" });
  try {
    const payload = await request(`/api/config/${encodeURIComponent(id)}/validate`, {
      method: "POST",
      body: { content: ui.configText.value },
    });
    ui.configDiff.innerHTML = renderValidation(payload.validation);
    ui.configStatus.innerHTML = renderConfigMessage(
      payload.validation.valid ? "校验通过。" : "校验未通过，见下方问题列表。",
      !payload.validation.valid,
    );
  } catch (error) {
    const described = describeError(error);
    ui.configDiff.innerHTML = stateBlock({ kind: "error", code: described.code, message: described.message });
    ui.configStatus.innerHTML = renderConfigMessage(`${described.code ?? "error"}：${described.message}`, true);
  }
}

async function saveConfig() {
  const id = state.config.documentId;
  if (!id) {
    return;
  }

  ui.configStatus.innerHTML = stateBlock({ kind: "loading" });
  try {
    const payload = await request(`/api/config/${encodeURIComponent(id)}`, {
      method: "POST",
      body: { content: ui.configText.value, etag: state.config.etag },
    });
    patchConfig({ etag: payload.eTag, state: "ready", error: null });
    const backup = payload.backupPath
      ? `宿主备份 ${payload.backupPath}（服务器端手动恢复）`
      : "新建文档，无宿主备份";
    ui.configStatus.innerHTML = renderConfigMessage(
      `已保存 · etag=${payload.eTag} · ${backup} · ${describeReloadHint(payload)}`
      + (payload.undoId ? ` · 可回滚快照 ${payload.undoId}` : ""),
      false,
    );
    ui.configDiff.innerHTML = renderValidation({
      valid: true,
      source: "host",
      eTag: payload.eTag,
      sizeBytes: 0,
      containsSecretLiteral: false,
      issues: [],
      diff: payload.diff,
    });
    await refreshUndo();
  } catch (error) {
    const described = describeError(error);
    if (described.status === 409 && described.code === "config.conflict") {
      const current = described.detail?.conflictETag;
      if (current) {
        patchConfig({ etag: current });
      }
      ui.configStatus.innerHTML = renderConfigMessage(
        `内容已被他人修改（当前 etag=${current || "?"}）。请重新加载后再保存。`,
        true,
      );
      return;
    }

    ui.configStatus.innerHTML = renderConfigMessage(`${described.code ?? "error"}：${described.message}`, true);
  }
}

async function rollbackConfig(undoId) {
  const id = state.config.documentId;
  if (!id) {
    return;
  }

  ui.configStatus.innerHTML = stateBlock({ kind: "loading" });
  try {
    const payload = await request(`/api/config/${encodeURIComponent(id)}/rollback`, {
      method: "POST",
      body: { etag: state.config.etag, undoId },
    });
    patchConfig({ etag: payload.saved?.eTag ?? state.config.etag });
    ui.configStatus.innerHTML = renderConfigMessage(`已回滚到快照 ${undoId} · etag=${payload.saved?.eTag || "?"}`, false);
    await loadConfigDocument();
  } catch (error) {
    const described = describeError(error);
    ui.configStatus.innerHTML = renderConfigMessage(`${described.code ?? "error"}：${described.message}`, true);
  }
}

// ------------------------------------------------------------------ commands

async function runCommand() {
  const command = ui.commandName.value.trim();
  if (!command) {
    ui.commandOutput.innerHTML = stateBlock({ kind: "empty", emptyReason: "no-data", message: "请先输入命令（需与 commandAllowlist 精确匹配）。" });
    return;
  }

  ui.commandRun.disabled = true;
  patchCommands({ state: "running", error: null });
  ui.commandOutput.innerHTML = renderCommandResult(null, { state: "running" });

  try {
    const payload = await request("/api/commands", {
      method: "POST",
      body: { command, args: parseArgs(ui.commandArgs.value) },
    });
    patchCommands({ state: "ready", result: payload, error: null });
    ui.commandOutput.innerHTML = renderCommandResult(payload, { state: "ready" });

    // A status-shaped command output feeds the trace timeline without any extra endpoint.
    if (/status/i.test(command) && payload.stdout) {
      applyTraceText(payload.stdout);
    }
  } catch (error) {
    const described = describeError(error);
    described.result = error?.result ?? null;
    patchCommands({ state: "error", error: described });
    ui.commandOutput.innerHTML = renderCommandResult(null, { state: "error", error: described });
  } finally {
    ui.commandRun.disabled = false;
    applyWritePermissions();
  }
}

function parseArgs(text) {
  return String(text || "")
    .split(/\s+/)
    .map((item) => item.trim())
    .filter((item) => item.length > 0);
}

// ------------------------------------------------------------------ trace

function applyTraceText(text) {
  patchTrace({ text });
  ui.traceInput.value = text;
  ui.traceView.innerHTML = renderTimeline(parseStatusText(text), { limit: state.trace.limit });
}

// ------------------------------------------------------------------ diagnostics

async function exportDiagnostics() {
  patchDiag({ state: "running", error: null });
  ui.diagView.innerHTML = renderDiag({ state: "running" });
  ui.diagExport.disabled = true;

  try {
    const response = await fetch("/api/diagnostics", { credentials: "same-origin", cache: "no-store" });
    if (!response.ok) {
      const payload = await response.json().catch(() => null);
      throw Object.assign(new Error(payload?.error?.message || `HTTP ${response.status}`), {
        code: payload?.error?.code ?? `http.${response.status}`,
      });
    }

    const blob = await response.blob();
    const fileName = parseFileName(response.headers.get("Content-Disposition")) ?? "natter-diag.zip";
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement("a");
    anchor.href = url;
    anchor.download = fileName;
    anchor.click();
    URL.revokeObjectURL(url);

    const last = {
      fileName,
      totalBytes: blob.size,
      maxTotalBytes: 8 * 1024 * 1024,
      truncated: response.headers.get("X-WebConsole-Diag-Truncated") === "true",
      redaction: "见 manifest.json",
      files: [],
    };
    patchDiag({ state: "ready", last, error: null });
    ui.diagView.innerHTML = renderDiag({ state: "ready", last });
  } catch (error) {
    const described = describeError(error);
    patchDiag({ state: "error", error: described });
    ui.diagView.innerHTML = renderDiag({ state: "error", error: described });
  } finally {
    ui.diagExport.disabled = false;
  }
}

function parseFileName(disposition) {
  const match = /filename="?([^";]+)"?/i.exec(disposition || "");
  return match ? match[1] : null;
}

// ------------------------------------------------------------------ wiring

ui.loginForm.addEventListener("submit", async (event) => {
  event.preventDefault();
  ui.loginError.textContent = "";
  const button = ui.loginForm.querySelector("button");
  button.disabled = true;

  try {
    const session = await request("/api/session", { method: "POST", body: { token: ui.token.value } });
    applySession(session);
    ui.token.value = "";
    showDashboard();
    await loadSnapshot();
  } catch (error) {
    ui.loginError.textContent = describeError(error).message;
  } finally {
    button.disabled = false;
  }
});

ui.refresh.addEventListener("click", () => {
  loadSnapshot().catch((error) => {
    ui.statusLine.className = "status error";
    ui.statusLine.textContent = `刷新失败：${describeError(error).message}`;
  });
});

ui.logout.addEventListener("click", async () => {
  try {
    await request("/api/session", { method: "DELETE" });
  } catch {
    // A dead session still returns to the login screen.
  }
  update({ csrf: null });
  stopPolling();
  showLogin("已退出。");
});

ui.logQuery.addEventListener("click", () => queryLogs({ resetOffset: true }).catch(() => {}));
ui.logPrev.addEventListener("click", () => {
  const limit = Number(ui.logLimit.value || "200");
  patchLogs({ offset: Math.max(0, state.logs.offset - limit) });
  queryLogs().catch(() => {});
});
ui.logNext.addEventListener("click", () => {
  const limit = Number(ui.logLimit.value || "200");
  patchLogs({ offset: state.logs.nextOffset ?? state.logs.offset + limit });
  queryLogs().catch(() => {});
});
ui.logExportText.addEventListener("click", () => exportLogs("text"));
ui.logExportJsonl.addEventListener("click", () => exportLogs("jsonl"));

ui.configDoc.addEventListener("change", () => {
  patchConfig({ documentId: ui.configDoc.value, etag: null });
  ui.configText.value = "";
  ui.configDiff.innerHTML = "";
  ui.configStatus.innerHTML = renderConfigMessage(`已选择 ${ui.configDoc.value}，点击“加载”读取当前内容。`, false);
  ui.configUndo.innerHTML = renderUndo([]);
});
ui.configLoad.addEventListener("click", () => loadConfigDocument().catch(() => {}));
ui.configValidate.addEventListener("click", () => validateConfig().catch(() => {}));
ui.configSave.addEventListener("click", () => saveConfig().catch(() => {}));

ui.configUndo.addEventListener("click", (event) => {
  const button = event.target.closest("button[data-action='rollback']");
  if (!button) {
    return;
  }

  rollbackConfig(button.dataset.undo).catch(() => {});
});

ui.commandRun.addEventListener("click", () => runCommand().catch(() => {}));

ui.traceParse.addEventListener("click", () => applyTraceText(ui.traceInput.value));
ui.traceClear.addEventListener("click", () => {
  patchTrace({ text: "", limit: 10 });
  ui.traceInput.value = "";
  ui.traceView.innerHTML = renderTimeline(null);
});

// Load more is a delegated action so the view stays a pure string renderer.
ui.traceView.addEventListener("click", (event) => {
  const target = event.target;
  if (target.matches("[data-action='trace-more']")) {
    patchTrace({ limit: state.trace.limit + 10 });
    ui.traceView.innerHTML = renderTimeline(parseStatusText(state.trace.text), { limit: state.trace.limit });
    return;
  }

  const copyable = target.closest("[data-copy]");
  if (copyable) {
    navigator.clipboard?.writeText(copyable.dataset.copy ?? "");
  }
});

ui.diagExport.addEventListener("click", () => exportDiagnostics().catch(() => {}));

function startPolling() {
  stopPolling();
  pollTimer = setInterval(() => {
    if (!ui.dashboard.hidden) {
      loadSnapshot().catch(() => {});
    }
  }, POLL_INTERVAL_MS);
}

function stopPolling() {
  if (pollTimer !== null) {
    clearInterval(pollTimer);
    pollTimer = null;
  }
}

// Re-render the badge when the state changes; views stay pure.
subscribe(() => {
  ui.roleBadge.textContent = state.role;
  ui.roleBadge.className = `badge ${state.role === "admin" ? "ok" : "degraded"}`;
});

async function bootstrap() {
  try {
    const session = await request("/api/session");
    applySession(session);
    showDashboard();
    await loadSnapshot();
    startPolling();
  } catch (error) {
    const described = describeError(error);
    if (described.status === 401) {
      showLogin();
      return;
    }

    showLogin(described.message);
  }
}

// Exported for tests: the entry module is importable without a browser (no top-level DOM writes
// beyond element lookups that tolerate missing nodes).
export { applyTraceText, exportDiagnostics, loadSnapshot, renderSnapshot, state };

if (typeof document !== "undefined" && ui.loginForm) {
  bootstrap();
}
