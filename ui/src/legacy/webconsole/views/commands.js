// Command console view: allowlist status, run result and explicit truncation accounting.

import { escapeHtml, formatBytes, stateBlock, truncationNote } from "../util.js";
import { canCommand, state as appState } from "../state.js";

export function renderCommandAvailability(adminStatus) {
  const commandAvailable = Boolean(adminStatus?.commandRunnerAvailable);
  const commandEnabled = Boolean(adminStatus?.commandEnabled);

  if (!commandEnabled) {
    return stateBlock({ kind: "empty", emptyReason: "disabled", message: "功能未启用：命令控制台已关闭（readOnly=true 或 allowCommandConsole=false）。" });
  }

  if (!commandAvailable) {
    if (appState.role !== "admin") {
      return stateBlock({ kind: "empty", emptyReason: "no-permission", message: "viewer 只读会话：命令执行需要 admin 角色（服务端强制）。" });
    }

    return stateBlock({
      kind: "empty",
      emptyReason: "host-unavailable",
      message: `命令控制台不可用：${adminStatus?.commandGapCode || "host.admin_command.unavailable"}`,
    });
  }

  const allowlist = (adminStatus.commandAllowlist || []).join(", ") || "（空）";
  return `<p class="muted">允许命令：${escapeHtml(allowlist)} · 超时 ${escapeHtml(adminStatus.commandTimeoutSeconds)}s`
    + ` · 可执行=${canCommand() ? "yes" : "no"}</p>`;
}

/** Renders one command result, always showing original vs displayed bytes. */
export function renderCommandResult(result, { state = "idle", error = null } = {}) {
  if (state === "running") {
    return stateBlock({ kind: "loading" });
  }

  if (state === "error") {
    const detail = error?.result
      ? `exit=${error.result.exitCode} duration=${error.result.durationMs}ms${error.result.timedOut ? " (超时)" : ""}`
      : null;
    return stateBlock({
      kind: "error",
      code: error?.code,
      message: error?.message,
      hint: detail,
    });
  }

  if (!result) {
    return stateBlock({ kind: "empty", emptyReason: "no-data", message: "还没有执行过命令。" });
  }

  const stdout = String(result.stdout ?? "");
  const stderr = String(result.stderr ?? "");
  const shown = stdout.length + stderr.length;
  const original = Number(result.outputBytes ?? shown);

  const head = `<p class="muted">exit=${escapeHtml(result.exitCode)} · duration=${escapeHtml(result.durationMs)}ms`
    + ` · 输出 ${escapeHtml(formatBytes(original))}${result.truncated ? " · 宿主已截断" : ""}</p>`;

  const body = (stdout ? `<h4>stdout</h4><pre>${escapeHtml(stdout)}</pre>` : "")
    + (stderr ? `<h4>stderr</h4><pre>${escapeHtml(stderr)}</pre>` : "");

  return head + body
    + truncationNote({ original, shown, unit: "字节", extra: result.timedOut ? "命令超时" : null });
}
