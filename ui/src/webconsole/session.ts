// Session helpers shared by the WebConsole views. Kept out of components so the permission rule has
// exactly one definition (the server enforces it again; this only hides controls).
export function canWrite(role: string, permissions: { write: boolean }): boolean {
  return role === "admin" && permissions.write === true;
}

export function canCommand(role: string, permissions: { command: boolean }, allowCommandConsole: boolean, readOnly: boolean): boolean {
  return role === "admin" && permissions.command === true && allowCommandConsole && !readOnly;
}
