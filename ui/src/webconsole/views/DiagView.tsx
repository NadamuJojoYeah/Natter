import { useState } from "react";
import { Alert, Button, Card, Space, Table, Typography } from "antd";
import { EmptyState } from "../../shared/states";
import { formatBytes } from "../../shared/format";
import { describeError } from "../../shared/api";
import type { DiagManifestFile } from "../../shared/types";

interface DiagState {
  fileName: string;
  totalBytes: number;
  truncated: boolean;
  files: DiagManifestFile[];
}

/** Port of legacy `views/diag.js`: one-shot bundle download plus manifest accounting. */
export function DiagView() {
  const [state, setState] = useState<"idle" | "running" | "ready" | "error">("idle");
  const [diag, setDiag] = useState<DiagState | null>(null);
  const [error, setError] = useState<{ code: string; message: string } | null>(null);

  const exportBundle = () => {
    setState("running");
    setError(null);

    fetch("/api/diagnostics", { credentials: "same-origin", cache: "no-store" })
      .then(async (response) => {
        if (!response.ok) {
          const payload = await response.json().catch(() => null);
          throw Object.assign(new Error(payload?.error?.message ?? `HTTP ${response.status}`), {
            code: payload?.error?.code ?? `http.${response.status}`,
          });
        }

        const blob = await response.blob();
        const disposition = response.headers.get("Content-Disposition") ?? "";
        const match = /filename="?([^";]+)"?/i.exec(disposition);
        const fileName = match ? match[1] : "natter-diag.zip";

        const location = globalThis.location;
        if (location && typeof location.assign === "function" && typeof URL.createObjectURL === "function") {
          const url = URL.createObjectURL(blob);
          const anchor = document.createElement("a");
          anchor.href = url;
          anchor.download = fileName;
          anchor.click();
          URL.revokeObjectURL(url);
        }

        setDiag({
          fileName,
          totalBytes: blob.size,
          truncated: response.headers.get("X-WebConsole-Diag-Truncated") === "true",
          files: [],
        });
        setState("ready");
      })
      .catch((cause: unknown) => {
        setError(describeError(cause));
        setState("error");
      });
  };

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Alert
        type="info"
        showIcon
        message="诊断包"
        description="manifest.json + health/upstreams/providers/plugins + config.redacted/ + logs.jsonl + audit.jsonl；每个文件走同一套脱敏，打包后再做一次明文哨兵自检，总量上限 8 MiB。"
      />
      <Card size="small">
        <Button type="primary" loading={state === "running"} onClick={exportBundle}>
          导出 natter-diag-*.zip
        </Button>
        {state === "error" && error ? (
          <Alert
            style={{ marginTop: 12 }}
            type="error"
            showIcon
            message={<Typography.Text copyable={{ text: error.code }}>{error.code}</Typography.Text>}
            description={`${error.message}${error.code === "webconsole.diag-sentinel-failed" ? "（脱敏漏网被拦下，导出被拒绝）" : ""}`}
          />
        ) : null}
        {state === "ready" && diag ? (
          <Space direction="vertical" size={8} style={{ width: "100%", marginTop: 12 }}>
            <Typography.Text>
              {`${diag.fileName} · ${formatBytes(diag.totalBytes)} · truncated=${String(diag.truncated)}`}
            </Typography.Text>
            {diag.files.length ? (
              <Table
                size="small"
                rowKey="path"
                pagination={false}
                dataSource={diag.files}
                columns={[
                  { title: "path", dataIndex: "path", key: "path" },
                  { title: "bytes", dataIndex: "bytes", key: "bytes", render: (value: number) => formatBytes(value) },
                  { title: "items", dataIndex: "items", key: "items", render: (value: number | null) => value ?? "—" },
                  { title: "truncation", dataIndex: "truncation", key: "truncation", render: (value: string | null) => value ?? "—" },
                ]}
              />
            ) : (
              <Typography.Text type="secondary">
                manifest 明细在 ZIP 里（浏览器不能读取已下载文件）；响应头已确认条目数与截断状态。
              </Typography.Text>
            )}
          </Space>
        ) : null}
        {state === "idle" ? <EmptyState reason="no-data" detail="尚未导出诊断包。" /> : null}
      </Card>
    </Space>
  );
}
