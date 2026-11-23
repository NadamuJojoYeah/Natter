import { useEffect, useState } from "react";
import { Button, Card, Input, Select, Space, Tag, Table, Typography } from "antd";
import { EmptyState, ErrorState, LoadingState } from "../../shared/states";
import { formatBytes, formatTime, truncationText } from "../../shared/format";
import { request } from "../../shared/api";
import type { LogEntry, LogQueryResponse } from "../../shared/types";

export function LogsView({ onUnauthorized }: { onUnauthorized: (error: { code: string; message: string }) => void }) {
  const [level, setLevel] = useState<string>("");
  const [source, setSource] = useState("");
  const [query, setQuery] = useState("");
  const [limit, setLimit] = useState(200);
  const [offset, setOffset] = useState(0);
  const [phase, setPhase] = useState<"loading" | "ready" | "error">("loading");
  const [payload, setPayload] = useState<LogQueryResponse | null>(null);
  const [error, setError] = useState<{ code: string; message: string } | null>(null);

  const load = (nextOffset: number) => {
    setPhase("loading");
    const parameters = new URLSearchParams({ limit: String(limit) });
    if (nextOffset > 0) parameters.set("offset", String(nextOffset));
    if (level) parameters.set("level", level);
    if (source.trim()) parameters.set("source", source.trim());
    if (query.trim()) parameters.set("q", query.trim());

    request<LogQueryResponse>(`/api/logs?${parameters.toString()}`)
      .then((result) => {
        setPayload(result);
        setOffset(result.offset ?? nextOffset);
        setPhase("ready");
        setError(null);
      })
      .catch((cause: { code?: string; message?: string; status?: number }) => {
        if (cause.status === 401) {
          onUnauthorized({ code: cause.code ?? "auth.required", message: cause.message ?? "会话已过期" });
          return;
        }
        setError({ code: cause.code ?? "ui.error", message: cause.message ?? "日志查询失败" });
        setPhase("error");
      });
  };

  useEffect(() => {
    load(0);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const entries: LogEntry[] = payload?.hostLogs?.data ?? [];
  const shownCharacters = entries.reduce((total, entry) => total + (entry.message?.length ?? 0), 0);

  const exportLogs = (format: "text" | "jsonl") => {
    const parameters = new URLSearchParams({ format, limit: String(limit) });
    if (level) parameters.set("level", level);
    if (source.trim()) parameters.set("source", source.trim());
    if (query.trim()) parameters.set("q", query.trim());
    const url = `/api/logs/export?${parameters.toString()}`;
    const location = globalThis.location;
    if (location && typeof location.assign === "function") {
      location.assign(url);
    }
  };

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Card size="small">
        <Space wrap>
          <Select
            style={{ width: 120 }}
            value={level}
            onChange={setLevel}
            options={[
              { value: "", label: "全部级别" },
              { value: "Error", label: "Error" },
              { value: "Warning", label: "Warning" },
              { value: "Information", label: "Information" },
              { value: "Debug", label: "Debug" },
            ]}
          />
          <Input style={{ width: 180 }} placeholder="source" value={source} onChange={(event) => setSource(event.target.value)} />
          <Input style={{ width: 220 }} placeholder="全文搜索 q" value={query} onChange={(event) => setQuery(event.target.value)} />
          <Select
            style={{ width: 120 }}
            value={limit}
            onChange={setLimit}
            options={[100, 200, 500, 1000].map((value) => ({ value, label: `每页 ${value}` }))}
          />
          <Button type="primary" onClick={() => load(0)}>
            查询
          </Button>
          <Button onClick={() => exportLogs("text")}>导出 text</Button>
          <Button onClick={() => exportLogs("jsonl")}>导出 jsonl</Button>
        </Space>
      </Card>

      <Card
        size="small"
        title="宿主日志"
        extra={
          payload ? (
            <Typography.Text type="secondary">
              匹配 {payload.total} 条 · 显示 {payload.offset + 1}–{payload.offset + payload.returned}
            </Typography.Text>
          ) : null
        }
      >
        {phase === "loading" ? <LoadingState rows={6} /> : null}
        {phase === "error" && error ? (
          <ErrorState code={error.code} message={error.message} hint="检查宿主日志契约是否桥接。" onRetry={() => load(offset)} />
        ) : null}
        {phase === "ready" && payload ? (
          payload.hostLogs?.available ? (
            entries.length === 0 ? (
              <EmptyState reason="no-data" detail="没有匹配的日志行。" />
            ) : (
              <>
                <Table
                  size="small"
                  rowKey={(row, index) => `${row.timestamp}-${index}`}
                  dataSource={entries}
                  pagination={false}
                  columns={[
                    { title: "time", dataIndex: "timestamp", key: "timestamp", width: 170, render: (value: string) => formatTime(value) },
                    {
                      title: "level",
                      dataIndex: "level",
                      key: "level",
                      width: 100,
                      render: (value: string) => (
                        <Tag color={value === "Error" || value === "Critical" ? "red" : value === "Warning" ? "gold" : "green"}>{value}</Tag>
                      ),
                    },
                    { title: "source", dataIndex: "source", key: "source", width: 180 },
                    { title: "message", dataIndex: "message", key: "message", render: (value: string) => <span style={{ whiteSpace: "pre-wrap" }}>{value}</span> },
                  ]}
                />
                <Typography.Paragraph type="secondary" style={{ marginTop: 8, marginBottom: 0 }}>
                  {truncationText({ original: shownCharacters, shown: shownCharacters, unit: "字符", extra: `每页上限 ${formatBytes(1024 * 1024)}` })}
                </Typography.Paragraph>
                <Space style={{ marginTop: 8 }}>
                  <Button disabled={offset === 0} onClick={() => load(Math.max(0, offset - limit))}>
                    上一页
                  </Button>
                  <Button disabled={!payload.hasMore} onClick={() => load(payload.nextOffset ?? offset + limit)}>
                    下一页
                  </Button>
                </Space>
              </>
            )
          ) : (
            <EmptyState
              reason="host-unavailable"
              detail={payload.hostLogs?.gapCode ?? payload.hostLogs?.reason ?? "宿主日志不可用"}
            />
          )
        ) : null}
      </Card>
    </Space>
  );
}
