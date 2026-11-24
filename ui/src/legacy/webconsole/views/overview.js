// Overview view: health checks, upstreams, profiles, plugins, gaps, audit.
// Pure render functions only — no DOM access, no imports from app.js.

import { badgeHtml, escapeHtml, formatTime, stateBlock, statusTone, tableHtml } from "../util.js";
import { emptyReasonFor } from "../state.js";

export function renderOverview(snapshot) {
  const self = snapshot.self;
  const items = [
    ["health", badgeHtml(self.healthStatus)],
    ["server", `${escapeHtml(self.serverState)}${self.serverUrl ? " " + escapeHtml(self.serverUrl) : ""}`],
    ["runtime", escapeHtml(self.serverRuntime)],
    ["host source", escapeHtml(self.hostDataSource)],
    ["uptime", `${escapeHtml(self.uptimeSeconds)}s`],
    ["bind", `${escapeHtml(self.bind)}:${escapeHtml(self.port)}`],
    ["token", self.tokenConfigured ? "configured" : "missing"],
    ["read only", String(self.readOnly)],
    ["config write", self.allowConfigWrite && !self.readOnly ? "enabled" : "disabled"],
    ["command console", self.allowCommandConsole && !self.readOnly ? "enabled" : "disabled"],
    ["session", `${escapeHtml(self.sessionMinutes)} min`],
    ["plugin", `${escapeHtml(self.pluginId)} ${escapeHtml(self.version)} · hostApi ${escapeHtml(self.hostApi)}`],
  ];

  return "<dl>" + items
    .map(([label, value]) => `<dt>${escapeHtml(label)}</dt><dd>${value}</dd>`)
    .join("") + "</dl>";
}

export function renderChecks(checks) {
  if (!checks?.length) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "当前没有可用的健康检查项。" });
  }

  return "<ul class=\"checks\">" + checks.map((check) => {
    const detail = check.detail ? `<span class="muted"> · ${escapeHtml(check.detail)}</span>` : "";
    return `<li><span><strong>${escapeHtml(check.name)}</strong>${detail}</span>${badgeHtml(check.status)}</li>`;
  }).join("") + "</ul>";
}

function sectionOrState(section, render) {
  if (!section?.available) {
    return stateBlock({
      kind: "empty",
      emptyReason: emptyReasonFor(section),
      message: section?.reason || section?.gapCode
        ? `不可用：${section.reason || section.gapCode}`
        : null,
    });
  }

  return render(section.data ?? []);
}

export function renderUpstreams(section) {
  return sectionOrState(section, (rows) => {
    if (!rows.length) {
      return stateBlock({ kind: "empty", emptyReason: "no-data", message: "当前没有 upstream。" });
    }

    return tableHtml(
      ["source", "connected", "lastConnectedAt", "reconnects", "sendFailures", "lastError"],
      rows.map((upstream) => [
        escapeHtml(upstream.source),
        badgeHtml(upstream.connected ? "ok" : "degraded"),
        escapeHtml(formatTime(upstream.lastConnectedAt)),
        escapeHtml(upstream.reconnectCount),
        escapeHtml(upstream.sendFailures),
        escapeHtml(upstream.lastError),
      ]),
    );
  });
}

export function renderProfiles(section) {
  return sectionOrState(section, (rows) => {
    if (!rows.length) {
      return stateBlock({ kind: "empty", emptyReason: "no-data", message: "当前没有 profile/provider。" });
    }

    return tableHtml(
      ["profile", "provider", "available", "tier"],
      rows.map((profile) => [
        escapeHtml(profile.profile),
        escapeHtml(profile.provider),
        badgeHtml(profile.available ? "ok" : "degraded"),
        escapeHtml(profile.supportTier),
      ]),
    );
  });
}

export function renderPlugins(section) {
  return sectionOrState(section, (data) => {
    const head = `<p class="muted">activated=${escapeHtml(data.activated)} failed=${escapeHtml(data.failed)}`
      + ` source=${escapeHtml(data.source)}</p>`;

    const failures = data.failures?.length
      ? "<h3>失败插件</h3>" + tableHtml(
        ["id", "state", "phase", "code", "message"],
        data.failures.map((failure) => [
          escapeHtml(failure.id),
          badgeHtml(failure.state),
          escapeHtml(failure.phase),
          `<button type="button" class="code copyable" data-copy="${escapeHtml(failure.code)}">${escapeHtml(failure.code)}</button>`,
          escapeHtml(failure.message),
        ]),
      )
      : "";

    const nodes = data.nodes?.length
      ? "<h3>插件图</h3>" + tableHtml(
        ["id", "version", "kind", "state", "blockedBy"],
        data.nodes.map((node) => [
          escapeHtml(node.id),
          escapeHtml(node.version),
          escapeHtml(node.kind),
          badgeHtml(node.state),
          escapeHtml((node.blockedBy || []).join(", ")),
        ]),
      )
      : "";

    return head + failures + nodes;
  });
}

export function renderGaps(gaps) {
  if (!gaps?.length) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "没有未满足的宿主契约。" });
  }

  return "<ul class=\"gaps\">" + gaps.map((gap) => "<li>"
    + `<div class="code">${escapeHtml(gap.code)}</div>`
    + `<div>${escapeHtml(gap.summary)}</div>`
    + `<div class="doc">影响面：${escapeHtml(gap.area)} · 宿主改动：${escapeHtml(gap.hostChange)}</div>`
    + `<div class="doc">${escapeHtml(gap.doc)}</div></li>`).join("") + "</ul>";
}

export function renderAudit(entries) {
  if (!entries?.length) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "暂无审计记录。" });
  }

  return tableHtml(
    ["time", "action", "user", "ip", "target", "result", "ms"],
    entries.slice().reverse().map((entry) => [
      escapeHtml(formatTime(entry.timestamp)),
      escapeHtml(entry.action),
      escapeHtml(entry.user),
      escapeHtml(entry.ip),
      escapeHtml(entry.target),
      `<span class="tone-${statusTone(entry.result)}">${escapeHtml(entry.result)}</span>`,
      escapeHtml(entry.durationMs),
    ]),
  );
}
