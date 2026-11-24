import { useState } from "react";
import { Alert, Button, Card, Input, Segmented, Space, Typography } from "antd";
import { EmptyState, LoadingState } from "../../shared/states";
import { formatBytes, truncationText } from "../../shared/format";
import { request } from "../../shared/api";
import { canCommand } from "../session";
import type { AdminStatus, CommandResponse } from "../../shared/types";

export interface CommandsViewProps {
  admin: AdminStatus;
  role: string;
  permissions: { command: boolean };
  readOnly: boolean;
  allowCommandConsole: boolean;
  onUnauthorized: (error: { code: string; message: string }) => void;
}

/** Port of legacy `views/commands.js`: allowlist, disabled reasons and output accounting. */
export function CommandsView(props: CommandsViewProps) {
  const [name, setName] = useState("");
  const [args, setArgs] = useState("");
  const [phase, setPhase] = useState<"idle" | "running" | "ready" | "error">("idle");
  const [result, setResult] = useState<CommandResponse | null>(null);
  const [error, setError] = useState<{ code: string; message: string } | null>(null);
  const [mode, setMode] = useState<"stdout" | "stderr">("stdout");

  const allowed = canCommand(props.role, props.permissions, props.admin.commandEnabled && props.allowCommandConsole, props.readOnly);

  const run = () => {
    if (!name.trim()) {
      setPhase("error");
      setError({ code: "command.missing", message: "请先输入命令（需与 commandAllowlist 精确匹配）。" });
      return;
    }

    setPhase("running");
    request<CommandResponse>("/api/commands", {
      method: "POST",
      body: { command: name.trim(), args: args.split(/\s+/).filter(Boolean) },
    })
      .then((payload) => {
        setResult(payload);
        setPhase("ready");
        setError(null);
      })
      .catch((cause: { code?: string; message?: string; status?: number; result?: CommandResponse }) => {
        if (cause.status === 401) {
          props.onUnauthorized({ code: cause.code ?? "auth.required", message: cause.message ?? "会话已过期" });
          return;
        }
        setError({ code: cause.code ?? "ui.error", message: cause.message ?? "命令执行失败" });
        setPhase("error");
        if (cause.result) {
          setResult(cause.result);
        }
      });
  };

  const output = mode === "stdout" ? result?.stdout ?? "" : result?.stderr ?? "";

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Card size="small" title="命令台">
        {!props.admin.commandRunnerAvailable ? (
          <EmptyState reason="host-unavailable" detail={props.admin.commandGapCode ?? "host.admin_command.unavailable"} />
        ) : !props.admin.commandEnabled || props.readOnly ? (
          <EmptyState reason="disabled" detail="readOnly=true 或 allowCommandConsole=false。" />
        ) : !allowed ? (
          <EmptyState reason="no-permission" detail="命令执行需要 admin 角色（服务端强制）。" />
        ) : (
          <>
            <Alert
              type="info"
              showIcon
              message={`允许命令：${props.admin.commandAllowlist.join(", ") || "（空）"}`}
              description={`超时 ${props.admin.commandTimeoutSeconds}s；命令必须与 allowlist 精确匹配。`}
              style={{ marginBottom: 12 }}
            />
            <Space.Compact style={{ width: "100%" }}>
              <Input
                style={{ width: "35%" }}
                placeholder="command（如 health）"
                value={name}
                onChange={(event) => setName(event.target.value)}
              />
              <Input
                placeholder="args（空格分隔）"
                value={args}
                onChange={(event) => setArgs(event.target.value)}
                onPressEnter={run}
              />
              <Button type="primary" loading={phase === "running"} onClick={run}>
                执行
              </Button>
            </Space.Compact>
          </>
        )}
      </Card>

      <Card
        size="small"
        title="输出"
        extra={
          result ? (
            <Segmented
              size="small"
              value={mode}
              onChange={(value) => setMode(value as "stdout" | "stderr")}
              options={[
                { value: "stdout", label: `stdout (${result.stdout?.length ?? 0})` },
                { value: "stderr", label: `stderr (${result.stderr?.length ?? 0})` },
              ]}
            />
          ) : null
        }
      >
        {phase === "idle" ? <EmptyState reason="no-data" detail="还没有执行过命令。" /> : null}
        {phase === "running" ? <LoadingState rows={4} /> : null}
        {error ? (
          <Alert
            type="error"
            showIcon
            message={<Typography.Text copyable={{ text: error.code }}>{error.code}</Typography.Text>}
            description={error.message}
            style={{ marginBottom: result ? 12 : 0 }}
          />
        ) : null}
        {result ? (
          <>
            <Space wrap style={{ marginBottom: 8 }}>
              <Typography.Text>{`exit=${result.exitCode}`}</Typography.Text>
              <Typography.Text type="secondary">{`${result.durationMs}ms`}</Typography.Text>
              <Typography.Text type="secondary">{`原始 ${formatBytes(result.outputBytes)}`}</Typography.Text>
              {result.truncated ? <Typography.Text type="warning">宿主已截断</Typography.Text> : null}
              {result.timedOut ? <Typography.Text type="danger">超时</Typography.Text> : null}
            </Space>
            <pre style={{ margin: 0, maxHeight: 360, overflow: "auto", background: "#0b1220", padding: 8, borderRadius: 4 }}>
              {output || "（空）"}
            </pre>
            {truncationText({
              original: result.outputBytes,
              shown: new TextEncoder().encode(output).length,
              unit: "字节",
              extra: result.timedOut ? "命令超时" : undefined,
            })}
          </>
        ) : null}
      </Card>
    </Space>
  );
}
