import { useEffect, useState } from "react";
import { Alert, Button, Card, Col, DatePicker, Empty, Input, Row, Select, Space, Statistic, Table, Tag, Timeline, Tooltip, Typography } from "antd";
import { EmptyState, ErrorState, LoadingState } from "../../shared/states";
import { formatDuration, formatTime } from "../../shared/format";
import { request } from "../../shared/api";
import type { TraceDetailResponse, TraceEntry, TraceHeader, TraceListResponse } from "../../shared/types";

const KIND_COLOR: Record<string, string> = {
  started: "blue",
  step: "gray",
  tool: "green",
  final: "purple",
};

/**
 * Port of legacy `views/trace.js`, but the data is structured: `/api/trace` returns the persisted
 * task events (kind/at/name/code/step/status) instead of the flattened `/status` text, so no text
 * parsing remains in the console (task-65).
 */
export function TraceView() {
  const [list, setList] = useState<TraceListResponse | null>(null);
  const [listPhase, setListPhase] = useState<"loading" | "ready" | "error">("loading");
  const [listError, setListError] = useState<{ code: string; message: string } | null>(null);

  const [filters, setFilters] = useState<{ source: string; status: string; session: string; since: string; until: string }>({
    source: "",
    status: "",
    session: "",
    since: "",
    until: "",
  });

  const [selected, setSelected] = useState<string | null>(null);
  const [detail, setDetail] = useState<TraceDetailResponse | null>(null);
  const [detailPhase, setDetailPhase] = useState<"loading" | "ready" | "error" | "idle">("idle");
  const [detailError, setDetailError] = useState<{ code: string; message: string } | null>(null);

  const [listGeneration, setListGeneration] = useState(0);
  const reloadList = () => setListGeneration((value) => value + 1);

  const buildListPath = () => {
    const parameters = new URLSearchParams({ limit: "50" });
    if (filters.source.trim()) parameters.set("source", filters.source.trim());
    if (filters.status.trim()) parameters.set("status", filters.status.trim());
    if (filters.session.trim()) parameters.set("session", filters.session.trim());
    if (filters.since) parameters.set("since", filters.since);
    if (filters.until) parameters.set("until", filters.until);
    return `/api/trace?${parameters.toString()}`;
  };

  useEffect(() => {
    let cancelled = false;
    setListPhase("loading");
    request<TraceListResponse>(buildListPath())
      .then((payload) => {
        if (cancelled) return;
        setList(payload);
        setListPhase("ready");
        if (!selected && payload.tasks.length > 0) {
          setSelected(payload.tasks[0].taskId);
        }
      })
      .catch((error: unknown) => {
        if (cancelled) return;
        setListError(error as { code: string; message: string });
        setListPhase("error");
      });
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [listGeneration]);

  const loadDetail = (taskId: string) => {
    setDetailPhase("loading");
    setDetailError(null);
    request<TraceDetailResponse>(`/api/trace/${encodeURIComponent(taskId)}`)
      .then((payload) => {
        setDetail(payload);
        setDetailPhase("ready");
      })
      .catch((error: unknown) => {
        setDetailError(error as { code: string; message: string });
        setDetailPhase("error");
      });
  };

  useEffect(() => {
    if (selected) {
      loadDetail(selected);
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [selected]);

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Alert
        type="info"
        showIcon
        message="结构化 trace"
        description="数据来自宿主持久化的 task_events（kind/at/name/code/step/status），不再解析 /status 文本。步骤级数据只有宿主任务的既有事件，没有额外造源。"
      />

      <Card size="small">
        <Space wrap>
          <Input
            style={{ width: 180 }}
            placeholder="source（protocol:platform:self）"
            value={filters.source}
            onChange={(event) => setFilters({ ...filters, source: event.target.value })}
          />
          <Select
            style={{ width: 150 }}
            value={filters.status || ""}
            onChange={(value) => setFilters({ ...filters, status: value })}
            options={[
              { value: "", label: "全部状态" },
              ...["Pending", "Running", "WaitingForInput", "Completed", "Failed", "Cancelled", "Expired"].map((value) => ({
                value,
                label: value,
              })),
            ]}
          />
          <Input
            style={{ width: 200 }}
            placeholder="sessionId"
            value={filters.session}
            onChange={(event) => setFilters({ ...filters, session: event.target.value })}
          />
          <DatePicker
            showTime
            placeholder="since"
            onChange={(value) => setFilters({ ...filters, since: value ? value.toISOString() : "" })}
          />
          <DatePicker
            showTime
            placeholder="until"
            onChange={(value) => setFilters({ ...filters, until: value ? value.toISOString() : "" })}
          />
          <Button type="primary" onClick={reloadList}>
            应用筛选
          </Button>
          <Button
            onClick={() => {
              setFilters({ source: "", status: "", session: "", since: "", until: "" });
              reloadList();
            }}
          >
            清空
          </Button>
        </Space>
      </Card>

      <Row gutter={[16, 16]}>
        <Col xs={24} lg={9}>
          <Card size="small" title="最近任务" extra={<Button size="small" onClick={() => reloadList()}>刷新</Button>}>
            {listPhase === "loading" ? <LoadingState rows={5} /> : null}
            {listPhase === "error" && listError ? (
              <ErrorState code={listError.code} message={listError.message} onRetry={() => setSelected(null)} />
            ) : null}
            {listPhase === "ready" && list ? (
              list.tasks.length === 0 ? (
                <EmptyState reason="no-data" detail="还没有任务记录。" />
              ) : (
                <Space direction="vertical" size={8} style={{ width: "100%" }}>
                  <Select
                    style={{ width: "100%" }}
                    value={selected ?? undefined}
                    onChange={(value) => setSelected(value)}
                    options={list.tasks.map((task) => ({
                      value: task.taskId,
                      label: `${task.taskId} · ${task.status} · ${formatDuration(task.elapsedMs)}`,
                    }))}
                  />
                  <Table
                    size="small"
                    rowKey="taskId"
                    pagination={{ pageSize: 10 }}
                    dataSource={list.tasks}
                    onRow={(record) => ({ onClick: () => setSelected(record.taskId) })}
                    columns={[
                      { title: "task", dataIndex: "taskId", key: "taskId" },
                      { title: "status", dataIndex: "status", key: "status" },
                      {
                        title: "elapsed",
                        dataIndex: "elapsedMs",
                        key: "elapsedMs",
                        align: "right",
                        render: (value: number) => formatDuration(value),
                      },
                    ]}
                  />
                </Space>
              )
            ) : null}
          </Card>
        </Col>

        <Col xs={24} lg={15}>
          <Card size="small" title="时间轴">
            {detailPhase === "idle" ? <Empty description="选择一个任务查看时间轴" /> : null}
            {detailPhase === "loading" ? <LoadingState rows={6} /> : null}
            {detailPhase === "error" && detailError ? (
              <ErrorState
                code={detailError.code}
                message={detailError.message}
                hint="任务可能已被清理，或宿主未授予 admin.read。"
                onRetry={() => selected && loadDetail(selected)}
              />
            ) : null}
            {detailPhase === "ready" && detail ? (
              <TraceTimeline header={detail.task} entries={detail.entries} />
            ) : null}
          </Card>
        </Col>

        <Col xs={24} lg={15}>
          <Card size="small" title="时间轴">
            {detailPhase === "idle" ? <Empty description="选择一个任务查看时间轴" /> : null}
            {detailPhase === "loading" ? <LoadingState rows={6} /> : null}
            {detailPhase === "error" && detailError ? (
              <ErrorState
                code={detailError.code}
                message={detailError.message}
                hint="任务可能已被清理，或宿主未授予 admin.read。"
                onRetry={() => selected && loadDetail(selected)}
              />
            ) : null}
            {detailPhase === "ready" && detail ? (
              <Space direction="vertical" size={12} style={{ width: "100%" }}>
                <Row gutter={12}>
                  <Col span={6}>
                    <Statistic title="task" value={detail.task.taskId} valueStyle={{ fontSize: 13 }} />
                  </Col>
                  <Col span={5}>
                    <Statistic title="status" value={detail.task.status} valueStyle={{ fontSize: 13 }} />
                  </Col>
                  <Col span={4}>
                    <Statistic title="version" value={detail.task.version} valueStyle={{ fontSize: 13 }} />
                  </Col>
                  <Col span={5}>
                    <Statistic title="elapsed" value={formatDuration(detail.task.elapsedMs)} valueStyle={{ fontSize: 13 }} />
                  </Col>
                  <Col span={4}>
                    <Statistic title="entries" value={detail.entries.length} valueStyle={{ fontSize: 13 }} />
                  </Col>
                </Row>

                <TraceTimeline header={detail.task} entries={detail.entries} />

                {detail.task.errorCode ? (
                  <Alert
                    type="error"
                    showIcon
                    message={<Typography.Text copyable={{ text: detail.task.errorCode }}>{detail.task.errorCode}</Typography.Text>}
                    description={detail.task.errorMessage}
                  />
                ) : null}

                {detail.task.resultText ? (
                  <Card size="small" type="inner" title="结果">
                    <Typography.Paragraph style={{ whiteSpace: "pre-wrap", marginBottom: 0 }}>
                      {detail.task.resultText}
                    </Typography.Paragraph>
                    {detail.task.resultTruncated ? (
                      <Typography.Text type="warning">结果已按 4000 字符截断（truncated=true）</Typography.Text>
                    ) : null}
                  </Card>
                ) : null}
              </Space>
            ) : null}
          </Card>
        </Col>
      </Row>
    </Space>
  );
}

export interface EntryDuration {
  ms: number;
  /** true when the value is inferred from the gap between events, not measured by the host. */
  estimated: boolean;
}

/**
 * Duration attribution (task-72 ①): the host writes a real `durationMs` for tool, step and final
 * events. Only when it is missing do we fall back to the inter-event gap — and that fallback is
 * flagged as an estimate, because the gap also contains queueing, network and model latency.
 */
export function durationOf(entries: TraceEntry[], index: number): EntryDuration {
  const measured = entries[index]?.durationMs;
  if (typeof measured === "number" && Number.isFinite(measured) && measured >= 0) {
    return { ms: measured, estimated: false };
  }

  if (index === 0) {
    return { ms: 0, estimated: false };
  }

  const previous = new Date(entries[index - 1].at).getTime();
  const current = new Date(entries[index].at).getTime();
  if (Number.isNaN(previous) || Number.isNaN(current)) {
    return { ms: 0, estimated: true };
  }

  return { ms: Math.max(0, current - previous), estimated: true };
}

/** Pure timeline (no fetch, no effects) so it can be rendered server-side in tests. */
export function TraceTimeline({ header, entries }: { header: TraceHeader; entries: TraceEntry[] }) {
  const durations = entries.map((_entry, index) => durationOf(entries, index));
  const maxDuration = Math.max(1, ...durations.map((value) => value.ms));

  const items = entries.map((entry, index) => {
    const duration = durations[index];
    const failed = entry.status === "failed" || entry.status === "error" || Boolean(entry.code);
    const ratio = Math.min(100, Math.round((duration.ms / maxDuration) * 100));

    return {
      color: failed ? "red" : KIND_COLOR[entry.kind] ?? "gray",
      children: (
        <Space direction="vertical" size={2} style={{ width: "100%" }}>
          <Space size={8} wrap>
            <Tag color={failed ? "red" : KIND_COLOR[entry.kind] ?? "default"}>{entry.kind}</Tag>
            {entry.name ? <Typography.Text>{entry.name}</Typography.Text> : null}
            {entry.step !== null && entry.step !== undefined ? (
              <Typography.Text type="secondary">step {entry.step}</Typography.Text>
            ) : null}
            {duration.ms > 0 ? (
              duration.estimated ? (
                <Tooltip title="宿主未记录该事件耗时；此处按相邻事件时间差估算，含排队/网络/模型时间">
                  <Tag color="default">{`${formatDuration(duration.ms)} 估算`}</Tag>
                </Tooltip>
              ) : (
                <Typography.Text type="secondary">{formatDuration(duration.ms)}</Typography.Text>
              )
            ) : null}
            <Typography.Text type="secondary">{formatTime(entry.at)}</Typography.Text>
          </Space>
          {entry.code ? (
            <Typography.Text type="danger" copyable={{ text: entry.code }}>
              {entry.code}
            </Typography.Text>
          ) : null}
          {duration.ms > 0 ? (
            <div
              aria-label={`duration bar ${duration.ms}${duration.estimated ? " estimated" : ""}`}
              style={{
                height: 4,
                width: `${Math.max(2, ratio)}%`,
                background: failed ? "#ef4444" : duration.estimated ? "#64748b" : "#38bdf8",
                borderRadius: 2,
              }}
            />
          ) : null}
        </Space>
      ),
    };
  });

  return (
    <Space direction="vertical" size={12} style={{ width: "100%" }}>
      <Row gutter={12}>
        <Col span={6}>
          <Statistic title="task" value={header.taskId} valueStyle={{ fontSize: 13 }} />
        </Col>
        <Col span={5}>
          <Statistic title="status" value={header.status} valueStyle={{ fontSize: 13 }} />
        </Col>
        <Col span={4}>
          <Statistic title="version" value={header.version} valueStyle={{ fontSize: 13 }} />
        </Col>
        <Col span={5}>
          <Statistic title="elapsed" value={formatDuration(header.elapsedMs)} valueStyle={{ fontSize: 13 }} />
        </Col>
        <Col span={4}>
          <Statistic title="entries" value={entries.length} valueStyle={{ fontSize: 13 }} />
        </Col>
      </Row>
      {entries.length === 0 ? (
        <EmptyState reason="no-data" detail="该任务没有持久化的 trace 事件。" />
      ) : (
        <Timeline items={items} />
      )}
      {header.errorCode ? (
        <Alert
          type="error"
          showIcon
          message={<Typography.Text copyable={{ text: header.errorCode }}>{header.errorCode}</Typography.Text>}
          description={header.errorMessage}
        />
      ) : null}
      {header.resultText ? (
        <Card size="small" type="inner" title="结果">
          <Typography.Paragraph style={{ whiteSpace: "pre-wrap", marginBottom: 0 }}>{header.resultText}</Typography.Paragraph>
          {header.resultTruncated ? <Typography.Text type="warning">结果已截断（resultTruncated=true）</Typography.Text> : null}
        </Card>
      ) : null}
    </Space>
  );
}
