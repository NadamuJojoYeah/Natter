import { Alert, Button, Card, Skeleton, Space, Typography } from "antd";

/**
 * Four distinguishable empty reasons (UI-DIRECTION §3.3). Collapsing them into one vague sentence is
 * what made operators guess; each reason carries its own copy and, where useful, its own action.
 */
export type EmptyReason = "no-data" | "no-permission" | "disabled" | "host-unavailable";

export const EMPTY_COPY: Record<EmptyReason, { title: string; body: string }> = {
  "no-data": { title: "当前没有数据", body: "这一步已经执行，但没有返回任何条目。" },
  "no-permission": { title: "需要 admin 角色", body: "viewer 只读会话：登录 admin 可解锁该区域。" },
  disabled: { title: "功能未启用", body: "readOnly=true 或 allowConfigWrite=false；这是全局开关，不是本会话缺角色。" },
  "host-unavailable": { title: "宿主管理面不可用（501）", body: "宿主未桥接该契约，插件以 501 兜底。" },
};

export interface EmptyStateProps {
  reason: EmptyReason;
  detail?: string;
}

export function EmptyState({ reason, detail }: EmptyStateProps) {
  const copy = EMPTY_COPY[reason];
  return (
    <Card size="small" data-empty-reason={reason}>
      <Typography.Title level={5} style={{ marginTop: 0 }}>
        {copy.title}
      </Typography.Title>
      <Typography.Paragraph type="secondary" style={{ marginBottom: 0 }}>
        {detail ?? copy.body}
      </Typography.Paragraph>
    </Card>
  );
}

export interface ErrorStateProps {
  code: string;
  message: string;
  hint?: string;
  onRetry?: () => void;
}

export function ErrorState({ code, message, hint, onRetry }: ErrorStateProps) {
  return (
    <Alert
      type="error"
      showIcon
      message={<code>{code}</code>}
      description={
        <Space direction="vertical" size={4}>
          <span>{message}</span>
          {hint ? <Typography.Text type="secondary">{hint}</Typography.Text> : null}
          {onRetry ? (
            <Button size="small" onClick={onRetry}>
              重试
            </Button>
          ) : null}
        </Space>
      }
    />
  );
}

export function LoadingState({ rows = 4 }: { rows?: number }) {
  return (
    <Card size="small" aria-busy="true">
      <Skeleton active paragraph={{ rows }} title={false} />
    </Card>
  );
}
