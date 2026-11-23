import { useState } from "react";
import { Alert, Card, Col, Descriptions, Layout, Row, Space, Statistic, Tag, Typography } from "antd";
import { EmptyState, ErrorState, LoadingState } from "../shared/states";
import { useAsync } from "../shared/useAsync";
import { request, setCsrfToken } from "../shared/api";
import { LatencyPanel } from "./charts/LatencyPanel";

interface SessionPayload {
  authenticated: boolean;
  role: "admin" | "viewer";
  csrf: string;
  permissions: { read: boolean; write: boolean; command: boolean };
}

interface SnapshotPayload {
  self: {
    pluginId: string;
    version: string;
    healthStatus: string;
    serverState: string;
    readOnly: boolean;
    allowConfigWrite: boolean;
    allowCommandConsole: boolean;
  };
  health: { available: boolean; gapCode?: string | null; data?: { uptimeSeconds?: number } | null };
  plugins: { available: boolean; gapCode?: string | null; data?: { activated?: number; failed?: number } | null };
  upstreams: { available: boolean; gapCode?: string | null; data?: unknown[] | null };
  admin: { configStoreAvailable: boolean; commandRunnerAvailable: boolean; logReaderAvailable: boolean };
}

/**
 * P1 foundation only: proves the pipeline end-to-end (React + AntD + lazy ECharts + three states).
 * The twelve production views are ported in follow-up tasks; the legacy vanilla implementation is
 * kept under `src/legacy/webconsole/` as the reference for that port.
 */
export function Dashboard() {
  const [session, setSession] = useState<SessionPayload | null>(null);

  const snapshot = useAsync(async (signal) => {
    // Login is interactive in the port; for the placeholder we rely on an existing session cookie.
    const current = await request<SessionPayload>("/api/session", { signal }).catch(() => null);
    if (current?.csrf) {
      setCsrfToken(current.csrf);
      setSession(current);
    }

    return request<SnapshotPayload>("/api/snapshot", { signal });
  });

  const data = snapshot.data;

  return (
    <Layout style={{ minHeight: "100vh", padding: 24 }}>
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <Typography.Title level={3} style={{ margin: 0 }}>
          Natter WebConsole
        </Typography.Title>
        <Alert
          type="info"
          showIcon
          message="前端地基（P1）：视图迁移进行中"
          description="本页只验证 React + Ant Design + ECharts 管线；overview/trace/logs/config/commands/diag 的 React 版本在后续任务里替换。旧的 vanilla 实现保留在 ui/src/legacy/webconsole/ 供对照。"
        />

        {snapshot.phase === "loading" ? <LoadingState /> : null}
        {snapshot.phase === "error" && snapshot.error ? (
          <ErrorState
            code={snapshot.error.code}
            message={snapshot.error.message}
            hint="确认宿主在运行，且 /api/snapshot 可访问。"
            onRetry={snapshot.reload}
          />
        ) : null}

        {snapshot.phase === "ready" && data ? (
          <Row gutter={[16, 16]}>
            <Col xs={24} lg={14}>
              <Card title="插件" size="small">
                <Descriptions size="small" column={1}>
                  <Descriptions.Item label="id">{data.self.pluginId}</Descriptions.Item>
                  <Descriptions.Item label="version">{data.self.version}</Descriptions.Item>
                  <Descriptions.Item label="health">
                    <Tag color={data.self.healthStatus === "ok" ? "green" : "gold"}>{data.self.healthStatus}</Tag>
                  </Descriptions.Item>
                  <Descriptions.Item label="server">{data.self.serverState}</Descriptions.Item>
                  <Descriptions.Item label="role">{session?.role ?? "viewer"}</Descriptions.Item>
                  <Descriptions.Item label="readOnly">{String(data.self.readOnly)}</Descriptions.Item>
                </Descriptions>
              </Card>
            </Col>
            <Col xs={24} lg={10}>
              <Card title="计数" size="small">
                <Row gutter={16}>
                  <Col span={8}>
                    <Statistic title="plugins" value={data.plugins.data?.activated ?? 0} suffix="activated" />
                  </Col>
                  <Col span={8}>
                    <Statistic title="failed" value={data.plugins.data?.failed ?? 0} />
                  </Col>
                  <Col span={8}>
                    <Statistic title="uptime" value={data.health.data?.uptimeSeconds ?? 0} suffix="s" />
                  </Col>
                </Row>
              </Card>
            </Col>
            <Col span={24}>
              <LatencyPanel />
            </Col>
            <Col span={24}>
              {data.upstreams.available ? (
                <Card size="small" title="upstreams">
                  <Typography.Text type="secondary">{data.upstreams.data?.length ?? 0} 个连接</Typography.Text>
                </Card>
              ) : (
                <EmptyState
                  reason={data.upstreams.gapCode ? "host-unavailable" : "no-data"}
                  detail={data.upstreams.gapCode ?? undefined}
                />
              )}
            </Col>
          </Row>
        ) : null}

        {snapshot.phase === "ready" && !data ? <EmptyState reason="no-data" /> : null}
      </Space>
    </Layout>
  );
}
