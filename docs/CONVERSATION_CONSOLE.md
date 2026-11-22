# Conversation Console 插件设计（会话回放与 Playground）

> **状态**：设计已定 · P1 待实现
> **日期**：2026-11-22
> **定位**：产品面插件，独立于 WebConsole（管理面）。只读回放优先，Playground 后置。
> **关联**：[Harness 责任边界](HARNESS_RESPONSIBILITY.md)、[WebConsole UI 方向](WEBCONSOLE_UI_DIRECTION.md)、[插件规范](PLUGIN_SPEC.md)

---

## 0. 一句话

> 一个可选安装的插件：**看真实会话**（排查「bot 为什么这么答」），以及**自己开个会话试 prompt**，两者都不改变 bot 对真实用户的行为。

---

## 1. 为什么不做进 WebConsole

WebConsole 目前暴露的用户内容为**零**：只有 health / upstreams / providers / plugins / config / commands / logs / audit，全是系统元数据。

一旦加入会话回放，它的威胁模型就变了：

```text
现在    admin 能看系统状态、能改配置、能跑只读命令
之后    admin 还能读用户对话内容
```

「能不能读用户对话」应该是一个**独立部署开关**，而不是运维控制台自带能力。

给公司跑 bot 的运维，可能完全不想让管理面板具备读客户对话的能力。做成独立插件，不装就没有。

### 反方观点（记录在案）

WebConsole 是 loopback + admin token，能拿到 admin token 的人本来就能跑 `natter memory list`。所以「放进去也无所谓」成立。

本设计仍选择独立插件，理由是**可卸载性**：部署者应当能明确选择「我不提供会话查看能力」。

---

## 2. 数据来源：必须是 host 端口，不能直连 DB

`IConversationStore` 的契约注释已经声明：

```csharp
/// <summary>Host-owned conversation persistence. Plugins must not own this source of truth.</summary>
```

但它的 API 只支持按 key 读写，没有枚举与搜索：

```text
GetOrCreateAsync(key) / GetRecentAsync(key, …) / AppendTurnAsync(key, …)
GetAfterAsync(key, …) / ClearAsync(key) / CountAsync()
```

因此需要新增一个**只读查询端口**，由 host 实现，插件消费。

### 2.1 为什么不让插件读 SQLite

| 原因 | 说明 |
| --- | --- |
| schema 所有权 | `SqliteSchema` 是唯一迁移 owner；插件读表结构等于第二份契约 |
| 权限治理 | 直连文件绕过 NPS 权限层 |
| 可替换性 | 换存储实现（未来可能上 FTS 或外部库）时插件不用改 |
| 字段演进 | `Source` 已从字符串改为结构化列；插件不该感知这个细节 |

### 2.2 端口契约（P1 最小集）

```csharp
public sealed record ConversationSummary(
    string SessionId,
    string SourceKey,
    string Protocol,
    string Platform,
    string ConversationId,
    string? GuildId,
    string? ActorId,
    int MessageCount,
    DateTimeOffset FirstMessageAt,
    DateTimeOffset LastMessageAt);

public sealed record ConversationFilter(
    string? SourceKey,
    string? ActorId,
    DateTimeOffset? Since,
    DateTimeOffset? Until,
    string? Query,          // 内容检索，P1 用 LIKE，P2 再评估 FTS
    int Limit,
    string? Cursor);

public sealed record ConversationPage(
    IReadOnlyList<ConversationSummary> Items,
    string? NextCursor);

public interface IConversationQuery
{
    ValueTask<ConversationPage> ListAsync(ConversationFilter filter, CancellationToken ct = default);

    ValueTask<IReadOnlyList<StoredMessage>> GetMessagesAsync(
        string sessionId, long afterMessageId, int limit, CancellationToken ct = default);
}
```

约束：

- 只读。端口上**不出现任何写方法**，用类型系统表达意图；
- 所有方法必须支持上限裁剪（`limit` 有硬上限，默认 200）；
- 返回 `StoredMessage`（既有类型），不新造消息模型；
- 不给插件 `SessionId` 之外的写入口（不能 `ClearAsync`）。

### 2.3 权限

权限词汇表里 `chat.read` 已存在，直接复用，不新增权限。

未授予 `chat.read` 的插件无法获得 `IConversationQuery`（服务不存在，与既有 admin 桥接语义一致）。

---

## 3. 分期

### P1 只读回放（本阶段）

```text
插件：Natter.Plugin.ConversationConsole
页面：会话列表 + 会话详情
能力：按 source / actor / 时间过滤，按内容搜索，分页加载消息
认证：独立 token（不复用 WebConsole token）
审计：谁在何时查看了哪个会话，写入插件 audit JSONL
```

**不做**：发消息、修改历史、删除会话、导出原始附件。

### P2 Playground（后续）

```text
页面：独立聊天页
能力：选 profile / prompt preset，发消息，看模型回复与 trace
关键约束：写入独立会话命名空间，绝不写真实用户会话
```

会话 key 隔离方案：

```text
真实会话   SourceKey = "discord:discord:123"
Playground SourceKey = "playground:local"
```

这样即使在同一个 `natter.db` 里，也不会与真实会话混淆，且可按 `source_protocol=playground` 过滤排除。

### P3 fork 真实会话（可选，最后考虑）

把某个真实会话的上下文复制到 playground 继续试，**只读原会话、只写新会话**。

---

## 4. 认证与审计

### 4.1 独立 token

```json
{ "consoleToken": "${secret:CONVERSATION_CONSOLE_TOKEN}" }
```

理由：与 WebConsole 解耦。不想装这个插件的人不生成这个 secret；装了的人可以给它更短的会话有效期。

### 4.2 审计要求（不可省）

每条访问都记：

```json
{"at":"…","actor":"token:admin","action":"conversation.view","sessionId":"…","messageCount":42}
```

不记录消息正文。审计写入插件自己的 state 目录（沿用 WebConsole 的 `NATTER_STATE_DIR` 约定，见 task-15 的教训：**不要写插件目录**，只读挂载会失败）。

### 4.3 默认只读

即使将来有写能力，也要有 `readOnly` 开关且**默认 true**，与 WebConsole 保持一致语义。

---

## 5. 前端

### 5.1 P1 用 vanilla

P1 只需要：列表、过滤、分页、详情、搜索。这些用原生 API + 极少 DOM 操作就够，不值得引入工具链。

沿用 WebConsole 的零依赖部署形态：

```text
lib/Natter.Plugin.ConversationConsole.dll
wwwroot/{index.html,app.js,app.css}
```

### 5.2 P2 再评估框架

Playground 需要流式响应、Markdown、分支切换、折叠思考——这时候 vanilla 会明显吃力，可以考虑引入构建链。

判断标准：**它挂了不影响 bot 运行**，所以可以承受工具链成本。

但要先验证一件事（见 §6）。

### 5.3 从 chat-demo 借鉴什么

`deepseek-ai/chat-demo`（MIT）适合作为参考：

| 可借鉴 | 用在哪 |
| --- | --- |
| 流式回答的状态呈现 | P2 聊天页 |
| 折叠式「深度思考」区块 | 模型推理过程展示 |
| 消息分支 / 重新生成 | P2 |
| 空 / 错 / 载三态 | P1 就有用 |

不借鉴的部分：

| 不借鉴 | 原因 |
| --- | --- |
| API Key 存 localStorage | 我们的模型是服务端持有 secret |
| 浏览器作为状态源 | 会话真值必须在服务端 DB |
| IndexedDB 存会话 | 同上 |
| 浏览器直连第三方 API | 会绕过我们的认证/审计/脱敏边界 |

---

## 6. 实现风险（P2 前必须验证）

### HttpListener 流式输出

`WebConsole` 用的是 `HttpListener` + 静态文件。Playground 的流式聊天需要 SSE 或 chunked 传输。

**必须先做探针**确认 `HttpListener` 能否真正逐块 flush。如果不能：

- 体验会退化成「等完整回复再显示」；
- 需要评估改用 Kestrel 或其它宿主的成本。

这个风险在 P2 开工前解决，不要写到一半才发现。

### SQLite 并发

插件读、主进程写同一个 `natter.db`。

- 读用独立连接 + `ReadOnly` 意图；
- 必须设置 `busy_timeout`（`SqliteTaskStore` 已有 5000ms 先例）；
- 大数据量检索要分页，不能一次拉全表。

---

## 7. 验收标准（P1）

- [ ] 未授予 `chat.read` 时插件拿不到 `IConversationQuery`；
- [ ] 列表支持按 source / 时间过滤与分页，`limit` 有硬上限；
- [ ] 详情能加载指定会话的消息，按 `afterMessageId` 增量翻页；
- [ ] 搜索不返回其它会话的内容；
- [ ] 审计记录每次查看，且不含消息正文；
- [ ] 独立 token 生效，错误 token 返回 401；
- [ ] `readOnly=true` 时无任何写路径可达；
- [ ] 插件不直接引用 `Microsoft.Data.Sqlite`（数据只经端口）；
- [ ] 默认 profile 与既有 6 个插件行为完全不变；
- [ ] 不装该插件时宿主启动行为无变化。

---

## 8. 非目标

- 不做真实平台消息发送（一旦能发就是「以 bot 身份说话」，另一套授权）；
- 不做会话删除 / 编辑 / 明文导出；
- 不做多用户权限体系（单 token + admin 语义足够）；
- 不替代 WebConsole 的运维职责。

---

## 9. 一句话总结

> 会话回放是**诊断**，Playground 是**开发**；两者都放在独立插件里，默认不装，装了也只读真实数据、只写自己的会话。
