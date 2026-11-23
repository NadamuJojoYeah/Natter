// Small pure formatters shared by both consoles (kept out of components so they are unit-testable
// and identical between the two permission worlds).

export function formatDuration(milliseconds: number | null | undefined): string {
  if (milliseconds === null || milliseconds === undefined || Number.isNaN(milliseconds)) {
    return "—";
  }

  const value = Math.max(0, milliseconds);
  if (value < 1000) {
    return `${Math.round(value)}ms`;
  }
  if (value < 60_000) {
    return `${(value / 1000).toFixed(2)}s`;
  }

  return `${Math.floor(value / 60_000)}m ${Math.round((value % 60_000) / 1000)}s`;
}

export function formatBytes(bytes: number | null | undefined): string {
  if (bytes === null || bytes === undefined || Number.isNaN(bytes)) {
    return "—";
  }

  const value = Math.max(0, bytes);
  if (value < 1024) {
    return `${value} B`;
  }
  if (value < 1024 * 1024) {
    return `${(value / 1024).toFixed(1)} KiB`;
  }

  return `${(value / (1024 * 1024)).toFixed(2)} MiB`;
}

export function formatTime(value: string | null | undefined): string {
  if (!value) {
    return "—";
  }

  const date = new Date(value);
  return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
}

/** "原文 N · 显示 M · 已截断 K" — truncation is allowed, hiding the original size is not. */
export function truncationText(input: { original: number; shown: number; unit?: string; extra?: string }): string {
  const unit = input.unit ?? "字符";
  const hidden = Math.max(0, input.original - input.shown);
  const parts = [`原文 ${input.original} ${unit}`, `显示 ${input.shown} ${unit}`];

  if (hidden > 0) {
    parts.push(unit === "行" ? `已截断 ${hidden} 行` : `已截断 ${hidden} ${unit}`);
  }

  if (input.extra) {
    parts.push(input.extra);
  }

  return parts.join(" · ");
}
