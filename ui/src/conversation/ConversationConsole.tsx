import { Layout, Space, Table, Tag, Typography } from "antd";
import { EmptyState, ErrorState, LoadingState } from "../shared/states";
import { useAsync } from "../shared/useAsync";
import { request } from "../shared/api";
import type { ColumnsType } from "antd/es/table";

interface ConversationRow {
  key: string;
  conversationId: string;
  updatedAt: string;
  messageCount: number;
}

interface ConversationsPayload {
  conversations?: Array<{ id: string; updatedAt: string; messageCount?: number }>;
  items?: Array<{ id: string; updatedAt: string; messageCount?: number }>;
  available?: boolean;
  gapCode?: string | null;
}

const COLUMNS: ColumnsType<ConversationRow> = [
  { title: "会话", dataIndex: "conversationId", key: "conversationId" },
  { title: "更新时间", dataIndex: "updatedAt", key: "updatedAt" },
  {
    title: "消息数",
    dataIndex: "messageCount",
    key: "messageCount",
    align: "right",
  },
];

/**
 * P1 foundation only. The conversation console keeps `chat.read` and nothing else: it never sees
 * admin contracts, which is why it stays a separate SPA from the WebConsole.
 */
export function ConversationConsole() {
  const conversations = useAsync((signal) => request<ConversationsPayload>("/api/conversations?limit=50", { signal }));

  const rows: ConversationRow[] = (conversations.data?.conversations ?? conversations.data?.items ?? []).map((item) => ({
    key: item.id,
    conversationId: item.id,
    updatedAt: item.updatedAt,
    messageCount: item.messageCount ?? 0,
  }));

  return (
    <Layout style={{ minHeight: "100vh", padding: 24 }}>
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <Typography.Title level={3} style={{ margin: 0 }}>
          Natter Conversation Console
        </Typography.Title>
        <Tag color="blue">chat.read only</Tag>

        {conversations.phase === "loading" ? <LoadingState /> : null}
        {conversations.phase === "error" && conversations.error ? (
          <ErrorState
            code={conversations.error.code}
            message={conversations.error.message}
            hint="需要 chat.read 权限；宿主未桥接 IConversationQuery 时为 501。"
            onRetry={conversations.reload}
          />
        ) : null}
        {conversations.phase === "ready" && rows.length === 0 ? <EmptyState reason="no-data" /> : null}
        {conversations.phase === "ready" && rows.length > 0 ? (
          <Table<ConversationRow> size="small" columns={COLUMNS} dataSource={rows} pagination={{ pageSize: 20 }} />
        ) : null}
      </Space>
    </Layout>
  );
}
