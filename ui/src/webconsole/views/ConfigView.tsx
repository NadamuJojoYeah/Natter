import { useEffect, useState } from "react";
import { Alert, Button, Card, Descriptions, Input, Select, Space, Table, Tag, Typography } from "antd";
import { EmptyState, LoadingState } from "../../shared/states";
import { formatBytes, formatTime, truncationText } from "../../shared/format";
import { request } from "../../shared/api";
import { canWrite } from "../session";
import type {
  ConfigDiff,
  ConfigDocument,
  ConfigDocumentSummary,
  ConfigSaveResponse,
  ConfigValidation,
  UndoEntry,
} from "../../shared/types";

const DIFF_VISIBLE_LINES = 60;
const MESSAGE_CLIP = 400;

export interface ConfigViewProps {
  documents: ConfigDocumentSummary[];
  writeEnabled: boolean;
  configStoreAvailable: boolean;
  gapCode?: string | null;
  readOnly: boolean;
  role: string;
  permissions: { write: boolean };
  onUnauthorized: (error: { code: string; message: string }) => void;
}

/** Port of legacy `views/config.js`: ETag, diff, rollback and backup hints are all preserved. */
export function ConfigView(props: ConfigViewProps) {
  const [documentId, setDocumentId] = useState<string>(props.documents[0]?.id ?? "profiles");
  const [document, setDocument] = useState<ConfigDocument | null>(null);
  const [etag, setEtag] = useState<string | null>(null);
  const [content, setContent] = useState("");
  const [phase, setPhase] = useState<"idle" | "loading" | "ready" | "error">("idle");
  const [message, setMessage] = useState<string>("");
  const [messageIsError, setMessageIsError] = useState(false);
  const [validation, setValidation] = useState<ConfigValidation | null>(null);
  const [undo, setUndo] = useState<UndoEntry[]>([]);
  const [conflict, setConflict] = useState<string | null>(null);

  const writable = props.configStoreAvailable && props.writeEnabled && canWrite(props.role, props.permissions);

  const describeFailure = (cause: { code?: string; message?: string; status?: number }) => {
    if (cause.status === 401) {
      props.onUnauthorized({ code: cause.code ?? "auth.required", message: cause.message ?? "会话已过期" });
      return;
    }
    setMessage(`${cause.code ?? "error"}：${cause.message ?? "请求失败"}`);
    setMessageIsError(true);
  };

  const loadUndo = () => {
    request<{ entries: UndoEntry[] }>(`/api/config/${encodeURIComponent(documentId)}/undo`)
      .then((payload) => setUndo(payload.entries ?? []))
      .catch(() => setUndo([]));
  };

  const loadDocument = () => {
    setPhase("loading");
    setConflict(null);
    request<ConfigDocument>(`/api/config/${encodeURIComponent(documentId)}`)
      .then((payload) => {
        setDocument(payload);
        setEtag(payload.exists ? payload.eTag ?? null : null);
        setContent(payload.content ?? "");
        setPhase("ready");
        setMessageIsError(false);
        setMessage(
          `${payload.exists ? "已加载" : "文档不存在（保存将创建）"} · etag=${payload.eTag ?? "?"}`
            + ` · source=${payload.source ?? "?"} · ${payload.restartRequired ? "保存后需重启宿主" : "无需重启"}`,
        );
        loadUndo();
      })
      .catch((cause) => {
        setPhase("error");
        describeFailure(cause);
      });
  };

  useEffect(() => {
    if (props.configStoreAvailable) {
      loadDocument();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [documentId, props.configStoreAvailable]);

  const validate = () => {
    setPhase("loading");
    request<{ validation: ConfigValidation }>(`/api/config/${encodeURIComponent(documentId)}/validate`, {
      method: "POST",
      body: { content },
    })
      .then((payload) => {
        setValidation(payload.validation);
        setPhase("ready");
        setMessageIsError(!payload.validation.valid);
        setMessage(payload.validation.valid ? "校验通过。" : "校验未通过，见下方问题列表。");
      })
      .catch((cause) => {
        setPhase("error");
        describeFailure(cause);
      });
  };

  const save = () => {
    setPhase("loading");
    setConflict(null);
    request<ConfigSaveResponse>(`/api/config/${encodeURIComponent(documentId)}`, {
      method: "POST",
      body: { content, etag },
    })
      .then((payload) => {
        setEtag(payload.eTag);
        setPhase("ready");
        setMessageIsError(false);
        const backup = payload.backupPath
          ? `宿主备份 ${payload.backupPath}（服务器端手动恢复）`
          : "新建文档，无宿主备份";
        setMessage(
          `已保存 · etag=${payload.eTag} · ${backup} · ${payload.restartRequired ? "需重启宿主" : "无需重启"}`
            + (payload.undoId ? ` · 可回滚快照 ${payload.undoId}` : ""),
        );
        setValidation({
          valid: true,
          source: "host",
          eTag: payload.eTag,
          sizeBytes: 0,
          containsSecretLiteral: false,
          issues: [],
          diff: payload.diff ?? null,
        });
        loadUndo();
      })
      .catch((cause: { code?: string; message?: string; status?: number; detail?: { conflictETag?: string } }) => {
        setPhase("ready");
        if (cause.status === 409 && cause.code === "config.conflict") {
          // Never overwrite silently: keep the old ETag out of the next save until the operator reloads.
          setConflict(
            `内容已被他人修改（服务端 etag=${cause.detail?.conflictETag ?? "?"}）。请重新加载后再保存，未覆盖。`,
          );
          setMessageIsError(true);
          return;
        }
        describeFailure(cause);
      });
  };

  const rollback = (undoId: string) => {
    setPhase("loading");
    request<{ saved: { eTag?: string } }>(`/api/config/${encodeURIComponent(documentId)}/rollback`, {
      method: "POST",
      body: { etag, undoId },
    })
      .then((payload) => {
        setEtag(payload.saved?.eTag ?? etag);
        setMessageIsError(false);
        setMessage(`已回滚到快照 ${undoId} · etag=${payload.saved?.eTag ?? "?"}`);
        loadDocument();
      })
      .catch(describeFailure);
  };

  if (!props.configStoreAvailable) {
    return <EmptyState reason="host-unavailable" detail={props.gapCode ?? "host.admin_config.unavailable"} />;
  }

  return (
    <Space direction="vertical" size={16} style={{ width: "100%" }}>
      <Card size="small">
        <Space wrap>
          <Select
            style={{ width: 240 }}
            value={documentId}
            onChange={(value) => {
              setDocumentId(value);
              setValidation(null);
              setUndo([]);
            }}
            options={props.documents.map((doc) => ({ value: doc.id, label: `${doc.id}${doc.exists ? "" : " (不存在)"}` }))}
          />
          <Button onClick={loadDocument}>加载</Button>
          <Button onClick={validate}>校验</Button>
          <Button type="primary" disabled={!writable} onClick={save}>
            保存{writable ? "" : "（不可写）"}
          </Button>
        </Space>
        {!writable ? (
          <Alert
            style={{ marginTop: 12 }}
            type="warning"
            showIcon
            message={props.readOnly ? "功能未启用：readOnly=true" : "功能未启用：allowConfigWrite=false"}
            description={`当前角色 ${props.role}；写入需要 admin 且服务端允许，服务端始终强制校验。`}
          />
        ) : null}
        {message ? (
          <Typography.Paragraph style={{ marginTop: 12, marginBottom: 0 }} type={messageIsError ? "danger" : "secondary"}>
            {message.length > MESSAGE_CLIP ? `${message.slice(0, MESSAGE_CLIP)}…` : message}
          </Typography.Paragraph>
        ) : null}
        {message.length > MESSAGE_CLIP
          ? truncationText({ original: message.length, shown: MESSAGE_CLIP })
          : null}
        {conflict ? <Alert style={{ marginTop: 12 }} type="error" showIcon message={conflict} /> : null}
      </Card>

      {phase === "loading" ? <LoadingState rows={8} /> : null}

      <Card size="small" title="内容">
        <Input.TextArea
          value={content}
          onChange={(event) => setContent(event.target.value)}
          autoSize={{ minRows: 10, maxRows: 24 }}
          spellCheck={false}
          style={{ fontFamily: "ui-monospace, Menlo, monospace" }}
        />
        {document ? (
          <Descriptions size="small" style={{ marginTop: 12 }} column={3}>
            <Descriptions.Item label="etag">{document.eTag ?? "—"}</Descriptions.Item>
            <Descriptions.Item label="source">{document.source ?? "—"}</Descriptions.Item>
            <Descriptions.Item label="size">{formatBytes(document.sizeBytes ?? content.length)}</Descriptions.Item>
          </Descriptions>
        ) : null}
      </Card>

      <Card size="small" title="校验与 diff">
        {!validation ? (
          <EmptyState reason="no-data" detail="还没有校验结果。" />
        ) : (
          <Space direction="vertical" size={8} style={{ width: "100%" }}>
            <Space wrap>
              <Tag color={validation.valid ? "green" : "red"}>valid={String(validation.valid)}</Tag>
              <Tag>{`source=${validation.source ?? "?"}`}</Tag>
              <Tag>{`etag=${validation.eTag ?? "?"}`}</Tag>
              <Tag color={validation.containsSecretLiteral ? "gold" : "default"}>
                {`containsSecretLiteral=${validation.containsSecretLiteral}`}
              </Tag>
              <Typography.Text type="secondary">{formatBytes(validation.sizeBytes)}</Typography.Text>
            </Space>

            {validation.issues.length ? (
              <Table
                size="small"
                rowKey={(row) => `${row.pointer}-${row.code}`}
                pagination={false}
                dataSource={validation.issues}
                columns={[
                  { title: "pointer", dataIndex: "pointer", key: "pointer", render: (value: string | null) => value ?? "$" },
                  { title: "code", dataIndex: "code", key: "code" },
                  { title: "message", dataIndex: "message", key: "message" },
                ]}
              />
            ) : null}

            {validation.diff ? <DiffBlock diff={validation.diff} /> : <Typography.Text type="secondary">无 diff。</Typography.Text>}
          </Space>
        )}
      </Card>

      <Card size="small" title="可恢复快照">
        <Typography.Paragraph type="secondary">
          会话内快照：每文档最多 8 条、全局 32 条、30 分钟后失效；控制台不读取宿主备份内容，宿主备份需在服务器上手动恢复。
        </Typography.Paragraph>
        {undo.length === 0 ? (
          <EmptyState reason="no-data" detail="当前没有可回滚的会话内快照。" />
        ) : (
          <Table
            size="small"
            rowKey="undoId"
            pagination={false}
            dataSource={undo}
            columns={[
              { title: "undoId", dataIndex: "undoId", key: "undoId" },
              { title: "recordedAt", dataIndex: "recordedAt", key: "recordedAt", render: (value: string) => formatTime(value) },
              { title: "size", dataIndex: "sizeBytes", key: "sizeBytes", render: (value: number) => formatBytes(value) },
              { title: "backup", dataIndex: "backupPath", key: "backupPath", render: (value: string | null) => value ?? "—" },
              {
                title: "操作",
                key: "action",
                render: (_: unknown, row: UndoEntry) => (
                  <Button size="small" disabled={!writable} onClick={() => rollback(row.undoId)}>
                    回滚
                  </Button>
                ),
              },
            ]}
          />
        )}
      </Card>
    </Space>
  );
}

export function DiffBlock({ diff }: { diff: ConfigDiff }) {
  const lines = diff.lines ?? [];
  const shown = lines.slice(0, DIFF_VISIBLE_LINES);

  return (
    <Space direction="vertical" size={4} style={{ width: "100%" }}>
      <Typography.Text>
        {`diff +${diff.added} -${diff.removed}${diff.truncated ? "（宿主侧已截断）" : ""}`}
      </Typography.Text>
      <pre style={{ margin: 0, maxHeight: 320, overflow: "auto", background: "#0b1220", padding: 8, borderRadius: 4 }}>
        {shown.join("\n")}
      </pre>
      {truncationText({ original: lines.length, shown: shown.length, unit: "行" })}
    </Space>
  );
}
