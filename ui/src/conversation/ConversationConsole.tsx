import { useCallback, useEffect, useState } from "react";
import { Alert, Button, Card, Col, Descriptions, Input, Layout, List, Row, Select, Space, Tag, Typography } from "antd";

import { LoginPrompt } from "../shared/login";
import { EmptyState, ErrorState, LoadingState } from "../shared/states";
import { formatTime, truncationText } from "../shared/format";
import type { ConversationMessage, ConversationSession } from "../shared/types";
import { MessageReplay } from "./views/MessageReplay";

interface SessionListResponse {
  sessions?: ConversationSession[];
  items?: ConversationSession[];
  cursor?: string | null;
  available?: boolean;
  gapCode?: string | null;
}

interface MessageListResponse {
  messages?: ConversationMessage[];
  nextAfter?: number | null;
  /** Composite (createdAt,id) cursor for the next page; preferred over nextAfter. */
  nextCursor?: string | null;
  items?: ConversationMessage[];
  available?: boolean;
  gapCode?: string | null;
}

const PAGE_SIZE = 20;
const MESSAGE_PAGE = 50;

/**
 * ConversationConsole (port of legacy `app.js`): read-only replay with `chat.read` only. The token
 * lives in memory for this tab, never in storage, and a 401 returns to the login gate with the code.
 */
export function ConversationConsole() {
  const [token, setToken] = useState<string | null>(null);
  const [authError, setAuthError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const [phase, setPhase] = useState<"idle" | "loading" | "ready" | "error">("idle");
  const [sessions, setSessions] = useState<ConversationSession[]>([]);
  const [cursor, setCursor] = useState<string | null>(null);
  const [listError, setListError] = useState<{ code: string; message: string } | null>(null);

  const [filters, setFilters] = useState({ source: "", actor: "", q: "" });
  const [selected, setSelected] = useState<ConversationSession | null>(null);

  const [messages, setMessages] = useState<ConversationMessage[]>([]);
  const [messagePhase, setMessagePhase] = useState<"idle" | "loading" | "ready" | "error">("idle");
  const [messageError, setMessageError] = useState<{ code: string; message: string } | null>(null);
  const [nextAfter, setNextAfter] = useState<number | null>(null);
  const [nextCursor, setNextCursor] = useState<string | null>(null);

  const withAuth = useCallback(
    async <T,>(path: string, options: { method?: "GET" | "POST"; body?: unknown } = {}): Promise<T> => {
      if (!token) {
        throw Object.assign(new Error("token 未设置"), { code: "auth.missing", status: 401 });
      }

      const response = await fetch(path, {
        method: options.method ?? "GET",
        headers: {
          Accept: "application/json",
          Authorization: `Bearer ${token}`,
          ...(options.body === undefined ? {} : { "Content-Type": "application/json" }),
        },
        body: options.body === undefined ? undefined : JSON.stringify(options.body),
        credentials: "same-origin",
        cache: "no-store",
      });

      const payload = response.status === 204 ? null : await response.json().catch(() => null);
      if (!response.ok) {
        throw Object.assign(new Error(payload?.error?.message ?? `HTTP ${response.status}`), {
          code: payload?.error?.code ?? `http.${response.status}`,
          status: response.status,
          payload,
        });
      }

      return payload as T;
    },
    [token],
  );

  const loadSessions = useCallback(
    (nextCursor: string | null) => {
      if (!token) {
        return;
      }

      setPhase("loading");
      const parameters = new URLSearchParams({ limit: String(PAGE_SIZE) });
      if (nextCursor) parameters.set("cursor", nextCursor);
      if (filters.source.trim()) parameters.set("source", filters.source.trim());
      if (filters.actor.trim()) parameters.set("actor", filters.actor.trim());
      if (filters.q.trim()) parameters.set("q", filters.q.trim());

      withAuth<SessionListResponse>(`/api/sessions?${parameters.toString()}`)
        .then((payload) => {
          const items = payload.sessions ?? payload.items ?? [];
          setSessions((current) => (nextCursor ? [...current, ...items] : items));
          setCursor(payload.cursor ?? null);
          setPhase("ready");
          setListError(null);
        })
        .catch((cause: { code?: string; message?: string; status?: number }) => {
          if (cause.status === 401) {
            setAuthError(`${cause.code ?? "auth.invalid"}：token 无效或已过期，请重新输入。`);
            setToken(null);
            return;
          }
          setListError({ code: cause.code ?? "ui.error", message: cause.message ?? "会话列表加载失败" });
          setPhase("error");
        });
    },
    [filters, token, withAuth],
  );

  const loadMessages = useCallback(
    (session: ConversationSession, after: number, cursor: string | null = null) => {
      setMessagePhase("loading");
      const parameters = new URLSearchParams({ after: String(after), limit: String(MESSAGE_PAGE) });
      if (cursor) {
        parameters.set("cursor", cursor);
      }

      withAuth<MessageListResponse>(
        `/api/sessions/${encodeURIComponent(session.sessionId)}/messages?${parameters.toString()}`,
      )
        .then((payload) => {
          const next = payload.items ?? payload.messages ?? [];
          setMessages((current) => (after > 0 || cursor !== null ? [...current, ...next] : next));
          setNextAfter(payload.nextAfter ?? null);
          setNextCursor(payload.nextCursor ?? null);
          setMessagePhase("ready");
          setMessageError(null);
        })
        .catch((cause: { code?: string; message?: string; status?: number }) => {
          if (cause.status === 401) {
            setAuthError(`${cause.code ?? "auth.invalid"}：token 无效或已过期，请重新输入。`);
            setToken(null);
            return;
          }
          setMessageError({ code: cause.code ?? "ui.error", message: cause.message ?? "消息加载失败" });
          setMessagePhase("error");
        });
    },
    [withAuth],
  );

  useEffect(() => {
    if (token) {
      loadSessions(null);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [token]);

  const selectSession = (session: ConversationSession) => {
    setSelected(session);
    setMessages([]);
    setNextAfter(null);
    setNextCursor(null);
    loadMessages(session, 0);
  };

  if (!token) {
    return (
      <Layout style={{ minHeight: "100vh" }}>
        <LoginPrompt
          title="Natter Conversation Console"
          hint="只读会话回放（chat.read）。会话令牌保存在本页内存，不写 localStorage / cookie；401 会把这里重新显示。"
          error={authError}
          busy={busy}
          onSubmit={(value) => {
            setBusy(true);
            setAuthError(null);
            setToken(value);
            setBusy(false);
          }}
        />
      </Layout>
    );
  }

  return (
    <Layout style={{ minHeight: "100vh", padding: 24 }}>
      <Space direction="vertical" size={16} style={{ width: "100%" }}>
        <Space style={{ width: "100%", justifyContent: "space-between" }} wrap>
          <Typography.Title level={3} style={{ margin: 0 }}>
            Natter Conversation Console
          </Typography.Title>
          <Space>
            <Tag color="blue">chat.read only</Tag>
            <Button
              onClick={() => {
                setToken(null);
                setAuthError(null);
                setSessions([]);
                setSelected(null);
              }}
            >
              退出
            </Button>
          </Space>
        </Space>

        <Card size="small">
          <Space wrap>
            <Input
              style={{ width: 180 }}
              placeholder="source"
              value={filters.source}
              onChange={(event) => setFilters({ ...filters, source: event.target.value })}
            />
            <Input
              style={{ width: 180 }}
              placeholder="actor"
              value={filters.actor}
              onChange={(event) => setFilters({ ...filters, actor: event.target.value })}
            />
            <Input
              style={{ width: 220 }}
              placeholder="搜索 q"
              value={filters.q}
              onChange={(event) => setFilters({ ...filters, q: event.target.value })}
            />
            <Select
              style={{ width: 120 }}
              value={PAGE_SIZE}
              options={[{ value: PAGE_SIZE, label: `每页 ${PAGE_SIZE}` }]}
              disabled
            />
            <Button type="primary" onClick={() => loadSessions(null)}>
              应用筛选
            </Button>
          </Space>
        </Card>

        <Row gutter={[16, 16]}>
          <Col xs={24} lg={9}>
            <Card size="small" title={`会话列表（${sessions.length}）`}>
              {phase === "loading" && sessions.length === 0 ? <LoadingState rows={6} /> : null}
              {phase === "error" && listError ? (
                <ErrorState code={listError.code} message={listError.message} onRetry={() => loadSessions(null)} />
              ) : null}
              {phase === "ready" && sessions.length === 0 ? (
                <EmptyState reason="no-data" detail="没有匹配的会话；调整 source / actor / 搜索词后重试。" />
              ) : null}
              {sessions.length ? (
                <List
                  size="small"
                  dataSource={sessions}
                  renderItem={(session) => (
                    <List.Item
                      style={{ cursor: "pointer", background: selected?.sessionId === session.sessionId ? "#111c2e" : undefined }}
                      onClick={() => selectSession(session)}
                    >
                      <List.Item.Meta
                        title={session.sourceKey ?? session.sessionId}
                        description={
                          <Space size={8} wrap>
                            <span>{`${session.messageCount ?? 0} 条消息`}</span>
                            <span>{`最近 ${formatTime(session.lastMessageAt)}`}</span>
                            {session.actorId ? <span>{`actor ${session.actorId}`}</span> : null}
                          </Space>
                        }
                      />
                      {session.queryScopeNotice ? <Typography.Text type="warning">{session.queryScopeNotice}</Typography.Text> : null}
                    </List.Item>
                  )}
                />
              ) : null}
              {cursor ? (
                <Button block style={{ marginTop: 8 }} onClick={() => loadSessions(cursor)}>
                  加载更多
                </Button>
              ) : null}
            </Card>
          </Col>

          <Col xs={24} lg={15}>
            <Card size="small" title="消息回放">
              {!selected ? <EmptyState reason="no-data" detail="从左侧选择一个会话。" /> : null}
              {selected ? (
                <Space direction="vertical" size={12} style={{ width: "100%" }}>
                  <Descriptions size="small" column={2}>
                    <Descriptions.Item label="session">{selected.sessionId}</Descriptions.Item>
                    <Descriptions.Item label="source">{selected.sourceKey ?? "—"}</Descriptions.Item>
                    <Descriptions.Item label="messages">{selected.messageCount ?? 0}</Descriptions.Item>
                    <Descriptions.Item label="last">{formatTime(selected.lastMessageAt)}</Descriptions.Item>
                  </Descriptions>
                  {messagePhase === "loading" && messages.length === 0 ? <LoadingState rows={6} /> : null}
                  {messagePhase === "error" && messageError ? (
                    <ErrorState
                      code={messageError.code}
                      message={messageError.message}
                      onRetry={() => loadMessages(selected, nextAfter ?? 0, nextCursor)}
                    />
                  ) : null}
                  {messagePhase === "ready" && messages.length === 0 ? (
                    <EmptyState reason="no-data" detail="该会话还没有消息。" />
                  ) : null}
                  <MessageReplay messages={messages} />
                  {nextAfter || nextCursor ? (
                    <Space>
                      <Button onClick={() => loadMessages(selected, nextAfter ?? 0, nextCursor)}>加载更多消息</Button>
                      <Typography.Text type="secondary">
                        {truncationText({ original: selected.messageCount ?? messages.length, shown: messages.length, unit: "条" })}
                      </Typography.Text>
                    </Space>
                  ) : null}
                  {authError ? <Alert type="error" showIcon message={authError} /> : null}
                </Space>
              ) : null}
            </Card>
          </Col>
        </Row>

      </Space>
    </Layout>
  );
}
