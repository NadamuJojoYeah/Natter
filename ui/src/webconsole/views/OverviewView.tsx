import { Card, Col, Descriptions, Row, Space, Statistic, Table, Tag, Typography } from "antd";
import type { ColumnsType } from "antd/es/table";
import { EmptyState, ErrorState, LoadingState } from "../../shared/states";
import { truncationText } from "../../shared/format";
import type { AuditEntry, GapInfo, ProfileInfo, Snapshot, UpstreamInfo } from "../../shared/types";

export interface OverviewViewProps {
  phase: "loading" | "ready" | "error";
  snapshot: Snapshot | null;
  error: { code: string; message: string } | null;
  reload: () => void;
  /** Recent tasks from /api/trace; null means "not fetched / not granted". */
  recentTasks?: Array<{ taskId: string; status: string; elapsedMs: number; updatedAt: string }> | null;
}

const UPSTREAM_COLUMNS: ColumnsType<UpstreamInfo> = [
  { title: "source", dataIndex: "source", key: "source" },
  {
    title: "connected",
    dataIndex: "connected",
    key: "connected",
    render: (value: boolean) => <Tag color={value ? "green" : "gold"}>{value ? "connected" : "down"}</Tag>,
  },
  { title: "reconnects", dataIndex: "reconnectCount", key: "reconnectCount", align: "right" },
  { title: "sendFailures", dataIndex: "sendFailures", key: "sendFailures", align: "right" },
  { title: "lastError", dataIndex: "lastError", key: "lastError", render: (value: string | null) => value ?? "—" },
];

const PROFILE_COLUMNS: ColumnsType<ProfileInfo> = [
  { title: "profile", dataIndex: "profile", key: "profile" },
  { title: "provider", dataIndex: "provider", key: "provider" },
  {
    title: "available",
    dataIndex: "available",
    key: "available",
    render: (value: boolean) => <Tag color={value ? "green" : "gold"}>{String(value)}</Tag>,
  },
  { title: "tier", dataIndex: "supportTier", key: "supportTier", align: "right" },
];

const AUDIT_COLUMNS: ColumnsType<AuditEntry> = [
  { title: "time", dataIndex: "timestamp", key: "timestamp", render: (value: string) => new Date(value).toLocaleString() },
  { title: "action", dataIndex: "action", key: "action" },
  { title: "user", dataIndex: "user", key: "user", render: (value: string | null) => value ?? "—" },
  { title: "ip", dataIndex: "ip", key: "ip", render: (value: string | null) => value ?? "—" },
  { title: "target", dataIndex: "target", key: "target", render: (value: string | null) => value ?? "—" },
  {
    title: "result",
    dataIndex: "result",
    key: "result",
    render: (value: string) => (
      <Typography.Text type={value === "ok" ? "success" : value === "denied" ? "danger" : "warning"}>{value}</Typography.Text>
    ),
  },
  { title: "ms", dataIndex: "durationMs", key: "durationMs", align: "right" },
];

/** Port of legacy `views/overview.js`: health, upstreams, profiles, plugins, gaps, audit, recent tasks. */
export function OverviewView({ phase, snapshot, error, reload, recentTasks }: OverviewViewProps) {
  if (phase === "loading") {
    return <LoadingState />;
  }

  if (phase === "error" || !snapshot) {
    return (
      <ErrorState
        code={error?.code ?? "ui.unknown"}
        message={error?.message ?? "无法读取 snapshot。"}
        hint="确认宿主在运行且会话有效。"
        onRetry={reload}
      />
    );
  }

  const plugins = snapshot.plugins;
  const gaps: GapInfo[] = snapshot.gaps ?? [];

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Row gutter={[16, 16]}>
        <Col xs={24} lg={10}>
          <Card size="small" title="插件">
            <Descriptions size="small" column={1}>
              <Descriptions.Item label="id">{snapshot.self.pluginId}</Descriptions.Item>
              <Descriptions.Item label="version">{snapshot.self.version}</Descriptions.Item>
              <Descriptions.Item label="health">
                <Tag color={snapshot.self.healthStatus === "ok" ? "green" : "gold"}>{snapshot.self.healthStatus}</Tag>
              </Descriptions.Item>
              <Descriptions.Item label="server">{snapshot.self.serverState}</Descriptions.Item>
              <Descriptions.Item label="host source">{snapshot.self.hostDataSource}</Descriptions.Item>
              <Descriptions.Item label="uptime">{snapshot.self.uptimeSeconds}s</Descriptions.Item>
              <Descriptions.Item label="readOnly">{String(snapshot.self.readOnly)}</Descriptions.Item>
            </Descriptions>
          </Card>
        </Col>
        <Col xs={24} lg={14}>
          <Card size="small" title="计数与检查项">
            <Row gutter={16}>
              <Col span={8}>
                <Statistic title="plugins" value={plugins.data?.activated ?? 0} suffix="activated" />
              </Col>
              <Col span={8}>
                <Statistic title="failed" value={plugins.data?.failed ?? 0} />
              </Col>
              <Col span={8}>
                <Statistic title="recent tasks" value={recentTasks?.length ?? 0} />
              </Col>
            </Row>
            <Table
              size="small"
              style={{ marginTop: 12 }}
              rowKey="name"
              pagination={false}
              dataSource={snapshot.checks}
              columns={[
                { title: "检查", dataIndex: "name", key: "name" },
                {
                  title: "状态",
                  dataIndex: "status",
                  key: "status",
                  render: (value: string) => (
                    <Tag color={value === "ok" ? "green" : value === "error" ? "red" : "gold"}>{value}</Tag>
                  ),
                },
                { title: "详情", dataIndex: "detail", key: "detail", render: (value: string | null) => value ?? "—" },
              ]}
            />
          </Card>
        </Col>
      </Row>

      <Card size="small" title="Upstreams">
        {snapshot.upstreams.available ? (
          snapshot.upstreams.data?.length ? (
            <Table size="small" rowKey="source" pagination={false} dataSource={snapshot.upstreams.data} columns={UPSTREAM_COLUMNS} />
          ) : (
            <EmptyState reason="no-data" detail="当前没有 upstream。" />
          )
        ) : (
          <EmptyState reason="host-unavailable" detail={snapshot.upstreams.gapCode ?? snapshot.upstreams.reason ?? undefined} />
        )}
      </Card>

      <Card size="small" title="Profiles / Providers">
        {snapshot.profiles.available ? (
          snapshot.profiles.data?.length ? (
            <Table size="small" rowKey="profile" pagination={false} dataSource={snapshot.profiles.data} columns={PROFILE_COLUMNS} />
          ) : (
            <EmptyState reason="no-data" detail="当前没有 profile/provider。" />
          )
        ) : (
          <EmptyState reason="host-unavailable" detail={snapshot.profiles.gapCode ?? undefined} />
        )}
      </Card>

      {plugins.available && (plugins.data?.failures?.length ?? 0) > 0 ? (
        <Card size="small" title="失败插件">
          <Table
            size="small"
            rowKey="id"
            pagination={false}
            dataSource={plugins.data?.failures ?? []}
            columns={[
              { title: "id", dataIndex: "id", key: "id" },
              { title: "state", dataIndex: "state", key: "state" },
              { title: "phase", dataIndex: "phase", key: "phase" },
              { title: "code", dataIndex: "code", key: "code", render: (value: string) => <code>{value}</code> },
              { title: "message", dataIndex: "message", key: "message" },
            ]}
          />
        </Card>
      ) : null}

      <Card size="small" title="插件图">
        {plugins.available ? (
          <Table
            size="small"
            rowKey="id"
            pagination={false}
            dataSource={plugins.data?.nodes ?? []}
            columns={[
              { title: "id", dataIndex: "id", key: "id" },
              { title: "version", dataIndex: "version", key: "version" },
              { title: "kind", dataIndex: "kind", key: "kind" },
              { title: "state", dataIndex: "state", key: "state" },
              {
                title: "blockedBy",
                dataIndex: "blockedBy",
                key: "blockedBy",
                render: (value: string[]) => (value ?? []).join(", ") || "—",
              },
            ]}
          />
        ) : (
          <EmptyState reason="host-unavailable" detail={plugins.gapCode ?? undefined} />
        )}
      </Card>

      <Card size="small" title="宿主契约缺口">
        {gaps.length ? (
          <Space direction="vertical" size={8} style={{ width: "100%" }}>
            {gaps.map((gap) => (
              <Card key={gap.code} size="small" type="inner" title={<code>{gap.code}</code>}>
                <Typography.Paragraph style={{ marginBottom: 4 }}>{gap.summary}</Typography.Paragraph>
                <Typography.Text type="secondary">
                  影响面：{gap.area} · 宿主改动：{gap.hostChange} · {gap.doc}
                </Typography.Text>
              </Card>
            ))}
          </Space>
        ) : (
          <EmptyState reason="no-data" detail="没有未满足的宿主契约。" />
        )}
      </Card>

      <Card size="small" title="最近任务">
        {recentTasks?.length ? (
          <Table
            size="small"
            rowKey="taskId"
            pagination={false}
            dataSource={recentTasks}
            columns={[
              { title: "task", dataIndex: "taskId", key: "taskId" },
              { title: "status", dataIndex: "status", key: "status" },
              { title: "elapsed", dataIndex: "elapsedMs", key: "elapsedMs", align: "right", render: (value: number) => `${value}ms` },
              {
                title: "updated",
                dataIndex: "updatedAt",
                key: "updatedAt",
                render: (value: string) => new Date(value).toLocaleString(),
              },
            ]}
          />
        ) : (
          <EmptyState
            reason={recentTasks === null ? "host-unavailable" : "no-data"}
            detail="还没有任务，或宿主未授予 admin.read。"
          />
        )}
      </Card>

      <Card size="small" title="审计（最近）">
        {snapshot.audit?.length ? (
          <>
            <Table
              size="small"
              rowKey={(row) => `${row.timestamp}-${row.action}`}
              pagination={{ pageSize: 10 }}
              dataSource={snapshot.audit}
              columns={AUDIT_COLUMNS}
            />
            <Typography.Text type="secondary">
              {truncationText({ original: snapshot.audit.length, shown: Math.min(snapshot.audit.length, 10), unit: "行" })}
            </Typography.Text>
          </>
        ) : (
          <EmptyState reason="no-data" detail="暂无审计记录。" />
        )}
      </Card>
    </Space>
  );
}
