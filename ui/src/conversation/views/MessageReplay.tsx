import { Empty, Space, Tag, Typography } from "antd";
import { formatTime } from "../../shared/format";
import type { ConversationMessage } from "../../shared/types";

/**
 * Message replay: plain text only (never innerHTML), with ids and timestamps intact so an operator can
 * correlate with logs. Incremental loading is handled by the parent.
 */
export function MessageReplay({ messages }: { messages: ConversationMessage[] }) {
  if (messages.length === 0) {
    return <Empty description="暂无消息" />;
  }

  return (
    <Space direction="vertical" size={10} style={{ width: "100%" }}>
      {messages.map((message) => (
        <div key={message.messageId} style={{ borderLeft: "3px solid #1f2937", paddingLeft: 10 }}>
          <Space size={8} wrap>
            <Tag>{`#${message.messageId}`}</Tag>
            <Typography.Text strong>{message.actorId ?? message.role ?? "unknown"}</Typography.Text>
            <Typography.Text type="secondary">{formatTime(message.createdAt)}</Typography.Text>
          </Space>
          <Typography.Paragraph style={{ margin: "4px 0 0", whiteSpace: "pre-wrap" }}>
            {message.text ?? "（空消息）"}
          </Typography.Paragraph>
        </div>
      ))}
    </Space>
  );
}
