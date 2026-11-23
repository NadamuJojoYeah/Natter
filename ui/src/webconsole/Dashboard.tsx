import { useCallback, useEffect, useMemo, useState } from "react";
import { Alert, Badge, Button, Card, Layout, Space, Spin, Tabs, Tag, Typography } from "antd";
import { request, setCsrfToken } from "../shared/api";
import { LoginPrompt } from "../shared/login";
import { EmptyState } from "../shared/states";
import type { SessionPayload, Snapshot, TraceListResponse } from "../shared/types";
import { OverviewView } from "./views/OverviewView";
import { TraceView } from "./views/TraceView";
import { LogsView } from "./views/LogsView";
import { ConfigView } from "./views/ConfigView";
import { CommandsView } from "./views/CommandsView";
import { DiagView } from "./views/DiagView";

type AuthState =
  | { kind: "checking" }
  | { kind: "anonymous"; error?: { code: string; message: string } }
  | { kind: "authenticated"; session: SessionPayload };

/**
 * WebConsole shell: login gate, snapshot polling and the six views. Every section renders through the
 * shared three-state components, and a 401 always returns to the login gate with the reason.
 */
export function Dashboard() {
  const [auth, setAuth] = useState<AuthState>({ kind: "checking" });
  const [snapshot, setSnapshot] = useState<Snapshot | null>(null);
  const [snapshotPhase, setSnapshotPhase] = useState<"loading" | "ready" | "error">("loading");
  const [snapshotError, setSnapshotError] = useState<{ code: string; message: string } | null>(null);
  const [recentTasks, setRecentTasks] = useState<TraceListResponse["tasks"] | null>(null);
  const [loginBusy, setLoginBusy] = useState(false);
  const [loginError, setLoginError] = useState<string | null>(null);

  const handleUnauthorized = useCallback((error: { code: string; message: string }) => {
    setCsrfToken(null);
    setAuth({ kind: "anonymous", error: { code: error.code, message: "会话已过期，请重新输入 token。" } });
  }, []);

  const loadSnapshot = useCallback(
    (signal?: AbortSignal) => {
      setSnapshotPhase("loading");
      request<Snapshot>("/api/snapshot", { signal })
        .then((payload) => {
          setSnapshot(payload);
          setSnapshotPhase("ready");
          setSnapshotError(null);
        })
        .catch((cause: { status?: number; code?: string; message?: string }) => {
          if (cause.status === 401) {
            handleUnauthorized({ code: cause.code ?? "auth.required", message: cause.message ?? "会话已过期" });
            return;
          }
          setSnapshotError({ code: cause.code ?? "ui.error", message: cause.message ?? "snapshot 读取失败" });
          setSnapshotPhase("error");
        });
    },
    [handleUnauthorized],
  );

  useEffect(() => {
    const controller = new AbortController();
    request<SessionPayload>("/api/session", { signal: controller.signal })
      .then((session) => {
        setCsrfToken(session.csrf);
        setAuth({ kind: "authenticated", session });
      })
      .catch((cause: { status?: number; code?: string; message?: string }) => {
        if (cause.status === 401) {
          setAuth({ kind: "anonymous" });
          return;
        }
        setAuth({ kind: "anonymous", error: { code: cause.code ?? "ui.error", message: cause.message ?? "无法连接宿主" } });
      });
    return () => controller.abort();
  }, []);

  useEffect(() => {
    if (auth.kind !== "authenticated") {
      return;
    }

    const controller = new AbortController();
    loadSnapshot(controller.signal);
    request<TraceListResponse>("/api/trace?limit=20", { signal: controller.signal })
      .then((payload) => setRecentTasks(payload.tasks))
      .catch(() => setRecentTasks(null));

    return () => controller.abort();
  }, [auth.kind, loadSnapshot]);

  const login = (token: string) => {
    setLoginBusy(true);
    setLoginError(null);
    request<SessionPayload>("/api/session", { method: "POST", body: { token } })
      .then((session) => {
        setCsrfToken(session.csrf);
        setAuth({ kind: "authenticated", session });
      })
      .catch((cause: { code?: string; message?: string }) => {
        setLoginError(`${cause.code ?? "auth.invalid"}：${cause.message ?? "登录失败"}`);
      })
      .finally(() => setLoginBusy(false));
  };

  const logout = () => {
    request("/api/session", { method: "DELETE" })
      .catch(() => undefined)
      .finally(() => {
        setCsrfToken(null);
        setAuth({ kind: "anonymous" });
        setSnapshot(null);
      });
  };

  const items = useMemo(() => {
    if (auth.kind !== "authenticated" || !snapshot) {
      return [];
    }

    return [
      {
        key: "overview",
        label: "概览",
        children: (
          <OverviewView
            phase={snapshotPhase}
            snapshot={snapshot}
            error={snapshotError}
            reload={() => loadSnapshot()}
            recentTasks={recentTasks}
          />
        ),
      },
      { key: "trace", label: "Trace", children: <TraceView /> },
      { key: "logs", label: "日志", children: <LogsView onUnauthorized={handleUnauthorized} /> },
      {
        key: "config",
        label: "配置",
        children: (
          <ConfigView
            documents={snapshot.config.documents ?? []}
            writeEnabled={snapshot.config.writeEnabled}
            configStoreAvailable={snapshot.admin.configStoreAvailable}
            gapCode={snapshot.config.writeGapCode ?? snapshot.admin.configGapCode}
            readOnly={snapshot.self.readOnly}
            role={auth.session.role}
            permissions={auth.session.permissions}
            onUnauthorized={handleUnauthorized}
          />
        ),
      },
      {
        key: "commands",
        label: "命令",
        children: (
          <CommandsView
            admin={snapshot.admin}
            role={auth.session.role}
            permissions={auth.session.permissions}
            readOnly={snapshot.self.readOnly}
            allowCommandConsole={snapshot.self.allowCommandConsole}
            onUnauthorized={handleUnauthorized}
          />
        ),
      },
      { key: "diag", label: "诊断包", children: <DiagView /> },
    ];
  }, [auth, snapshot, snapshotPhase, snapshotError, recentTasks, loadSnapshot, handleUnauthorized]);

  if (auth.kind === "checking") {
    return (
      <Layout style={{ minHeight: "100vh", display: "grid", placeItems: "center" }}>
        <Spin tip="正在确认会话…" />
      </Layout>
    );
  }

  if (auth.kind === "anonymous") {
    return (
      <Layout style={{ minHeight: "100vh" }}>
        <LoginPrompt
          title="Natter WebConsole"
          hint="Loopback 运维控制台。使用宿主 secret WEBCONSOLE_TOKEN 登录；会话过期时这里会重新要求输入。"
          error={auth.error ? `${auth.error.code}：${auth.error.message}` : loginError}
          busy={loginBusy}
          onSubmit={login}
        />
      </Layout>
    );
  }

  const role = auth.session.role;

  return (
    <Layout style={{ minHeight: "100vh", padding: 24 }}>
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <Space style={{ width: "100%", justifyContent: "space-between" }} wrap>
          <Space direction="vertical" size={0}>
            <Typography.Title level={3} style={{ margin: 0 }}>
              Natter WebConsole
            </Typography.Title>
            <Typography.Text type="secondary">
              {snapshot ? `${snapshot.self.pluginId} ${snapshot.self.version} · hostApi ${snapshot.self.hostApi}` : "加载中…"}
            </Typography.Text>
          </Space>
          <Space>
            <Badge status={role === "admin" ? "success" : "warning"} text={`role=${role}`} />
            {snapshot?.self.readOnly ? <Tag color="gold">readOnly</Tag> : null}
            {snapshot ? (
              <Tag color={snapshot.self.healthStatus === "ok" ? "green" : "gold"}>health={snapshot.self.healthStatus}</Tag>
            ) : null}
            <Button onClick={() => loadSnapshot()}>刷新</Button>
            <Button onClick={logout}>退出</Button>
          </Space>
        </Space>

        {snapshotPhase === "error" && snapshotError ? (
          <Alert type="error" showIcon message={snapshotError.code} description={snapshotError.message} />
        ) : null}

        {!snapshot ? (
          <Card size="small">
            <EmptyState reason="no-data" detail="还没有 snapshot 数据。" />
          </Card>
        ) : (
          <Tabs items={items} destroyOnHidden />
        )}
      </Space>
    </Layout>
  );
}
