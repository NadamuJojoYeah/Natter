// Centralized client state (UI-DIRECTION §4.2: no mutable globals scattered across modules).
// Views are pure functions of this snapshot; the entry module is the only writer.

const listeners = new Set();

export const state = {
  csrf: null,
  role: "viewer",
  permissions: { read: true, write: false, command: false },
  readOnly: true,
  allowConfigWrite: false,
  allowCommandConsole: false,
  snapshot: null,
  snapshotState: "loading", // loading | ready | error
  snapshotError: null,
  logs: { offset: 0, hasMore: false, nextOffset: null, pageSize: 200, state: "loading", error: null, payload: null },
  config: { documentId: null, etag: null, state: "idle", error: null, diff: null, undo: [] },
  commands: { state: "idle", error: null, result: null },
  trace: { text: "", limit: 10 },
  diag: { state: "idle", error: null, last: null },
};

/** Applies a partial patch and notifies subscribers once. */
export function update(patch) {
  Object.assign(state, patch);
  for (const listener of listeners) {
    listener(state);
  }
}

export function patchLogs(patch) {
  update({ logs: { ...state.logs, ...patch } });
}

export function patchConfig(patch) {
  update({ config: { ...state.config, ...patch } });
}

export function patchCommands(patch) {
  update({ commands: { ...state.commands, ...patch } });
}

export function patchTrace(patch) {
  update({ trace: { ...state.trace, ...patch } });
}

export function patchDiag(patch) {
  update({ diag: { ...state.diag, ...patch } });
}

export function subscribe(listener) {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

/** Session helpers keep role/permission derivation in one place. */
export function applySession(session) {
  if (!session || !session.authenticated) {
    return;
  }

  update({
    csrf: session.csrf ?? state.csrf,
    role: session.role || "viewer",
    permissions: session.permissions || { read: true, write: false, command: false },
  });
}

export function canWrite() {
  return state.role === "admin" && state.permissions.write === true && state.readOnly === false;
}

export function canCommand() {
  return state.role === "admin" && state.permissions.command === true && state.readOnly === false;
}

/** Which of the four empty reasons applies to a section the host did not provide. */
export function emptyReasonFor(section) {
  if (!section) {
    return "no-data";
  }
  if (section.status === 501 || section.gapCode) {
    return "host-unavailable";
  }
  if (section.reason === "role") {
    return "no-permission";
  }
  if (section.reason === "disabled") {
    return "disabled";
  }

  return "no-data";
}
