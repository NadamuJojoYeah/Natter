# P3 Context & Memory Implementation Spec

> **状态**：P3.1–P3.5 foundation implemented（host prompt/context/memory pipeline + SQLite summary/memory + explicit remember/forget + NPS contribution 激活/回滚；真实 Tier 1 记忆验收待后续）
> **日期**：2026-11-15
> **前置**：P2 已 Go（serve / profiles / SQLite 会话 / provider 路由 / health）
> **目标**：在不破坏 P2 的前提下，把 Prompt、Context、Memory 做成可替换插件，并先交付一套可用的默认实现。
> **关联**：[P2 开发文档](P2_RUNTIME_PROFILE.md)、[Context & Memory 插件设计](CONTEXT_MEMORY_PLUGIN.md)、[Harness 架构](HARNESS_ARCHITECTURE.md)、[AI Provider 设计](AI_PROVIDER_DESIGN.md)
> **验收计划**：[P3 Acceptance Test Plan](testing/P3_ACCEPTANCE.md)
> **当前结论**：Closed · Go（P3 验收已归档）。
> **下一步**：[P4 Tier 1 产品化](P4_TIER1_PRODUCTIZATION.md)。

---

## 0. 一句话目标

> **P3 完成后，`profiles.json` 可以选择 prompt / context / memory 策略；默认实现用 SQLite 保存摘要和显式记忆；Discord+DeepSeek 走完整上下文，DingTalk+Spark 保持 smoke 兼容。**

P3 的重点不是“上向量库”，而是：

1. 把上下文组装从 provider 插件里抽出来；
2. 把 prompt/persona 从硬编码配置变成可版本化策略；
3. 把长期记忆做成 host-owned、可导出/删除的数据；
4. 保证 P2 的 profile/provider 路由不被破坏。

---

## 1. 现状与缺口

### 1.1 已有

- P2 `RuntimeProfile`：match / provider / history / supportTier；
- P2 `IConversationStore`：SQLite sessions/messages；
- P2 `ProfileAwareChatResponder`：按 profile 路由 provider；
- P2 `ProfileContext`：responder 调用期间的 ambient profile；
- 共享 `AiChatResponder`、`AiConversationStore`（正在迁移到 host store）；
- Discord/DeepSeek 一等；DingTalk/Spark 二等；
- 真实 Discord DM/guild、钉钉群、双 upstream、重启恢复已验证。

### 1.2 缺口

- `IPromptProvider` 不存在；
- `IAiContextAssembler` 是 `Natter.Feature.Ai` 里的 recent-window 实现，不是可替换 host 契约；
- 没有 summary 持久化；
- 没有 memory_items；
- 没有 `remember` / `forget`；
- ~~没有 prompt/context/memory 的 NPS contribution~~（本轮已声明并支持激活/回滚）；
- `profiles.json` 只有 provider/history；
- P2 的 system prompt 仍在 provider 插件 config 里；
- 上下文预算只有 `history.maxCharacters`，没有 memory/summary 预算。

---

## 2. 设计约束

1. **不破坏 P2 schema**：旧的 `profiles.json`（无 prompt/context/memory 字段）必须继续工作；
2. **Host 拥有事实源**：messages/summary/memory 由 host 持久化；
3. **插件只提供策略**：prompt/context/memory 实现可替换；
4. **默认实现必须可用**：不装第三方插件也能跑 Tier 1；
5. **Tier 2 只做 smoke**：DingTalk/Spark 不驱动 P3 功能；
6. **不先上向量库**：P3 默认 SQLite + recent + summary + explicit memory；
7. **不自动记忆模型推测**：v1 只做显式记忆；自动提取后续阶段；
8. **可删除/可导出**：memory 数据属于用户/workspace；
9. **scope 优先于相关性**：DM/guild/source 隔离是硬约束；
10. **context 必须校验**：插件不能绕过 token/char 预算和权限。

---

## 3. 目标架构

```text
ChatOrchestrator
  │
  ▼
ProfileAwareChatResponder
  │ profile
  ▼
AiChatResponder
  │
  ├── IPromptProvider         (host/插件)
  ├── IContextAssembler       (host/插件)
  ├── IMemoryStore            (host/插件)
  ├── ISummaryStore           (host/插件)
  └── IChatClient             (M.E.AI provider)
```

Host 负责：

- `IConversationStore`（P2 已有）；
- `ISummaryStore`；
- `IMemoryStore`；
- `IContextValidator`；
- profile/component registry；
- scope/预算/版本。

插件负责：

- prompt 模板/版本；
- context 组装策略；
- memory 存储/检索/提取/摘要；
- 不能直接访问其他 source 的 memory。

---

## 4. RuntimeProfile 扩展

### 4.1 兼容策略

- `schemaVersion` 仍为 **1**；
- `prompt` / `context` / `memory` 都是**可选字段**；
- 缺省时使用 host 默认实现，保持 P2 行为；
- 旧 DeepSeek/Spark config 的 `systemPrompt` 继续作为 fallback；
- 新 profile 字段优先于插件 config。

### 4.2 新字段

```json
{
  "id": "tier1-default",
  "supportTier": 1,
  "match": { "protocols": ["discord"], "platforms": ["discord"] },
  "ai": {
    "provider": "natter.feature.ai.deepseek#deepseek",
    "model": "deepseek-flash"
  },
  "prompt": {
    "provider": "host#static",
    "version": "default"
  },
  "context": {
    "assembler": "host#recent-window",
    "maxInputCharacters": 12000,
    "maxMemoryItems": 8,
    "maxSummaryCharacters": 1200
  },
  "memory": {
    "store": "host#sqlite",
    "retriever": "host#recent",
    "extractor": "host#explicit"
  },
  "history": {
    "scope": "conversation",
    "maxMessages": 20,
    "maxCharacters": 24000
  },
  "policy": { "enabled": true }
}
```

### 4.3 Ref 格式

统一使用：

```text
<owner>#<id>
```

例如：

```text
host#static
host#recent-window
host#sqlite
host#explicit
natter.memory.sqlite#sqlite
natter.context.rag#rag
```

规则：

- `host#...` 为内置实现；
- `pluginId#contributionId` 为 NPS 插件实现；
- 启动时校验引用存在；
- Tier 1 缺失 → 启动失败；
- Tier 2 缺失 → 禁用对应 profile 或明确失败（与 P2 provider 策略一致）；
- 同一个 profile 中 prompt/assembler/store 各一个 active；retriever 可多个（P3 先一个）。

### 4.4 Profile 校验

新增：

- `prompt.provider` 引用存在；
- `context.assembler` 引用存在；
- `memory.store` 引用存在；
- `context.maxInputCharacters > 0`；
- `context.maxMemoryItems >= 0`；
- `context.maxSummaryCharacters >= 0`；
- `history.scope` 只允许 `conversation` / `user`；
- 不允许 profile 内嵌 secret；
- 旧 profile 缺省字段自动补默认值。

---

## 5. Host 契约

### 5.1 `IPromptProvider`

```csharp
public interface IPromptProvider
{
    ValueTask<PromptBundle> GetAsync(
        PromptRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record PromptRequest(
    RuntimeProfile Profile,
    Source Source,
    Conversation Conversation,
    User User,
    string? Locale,
    IReadOnlyDictionary<string, string> Variables);

public sealed record PromptBundle(
    string SystemPrompt,
    string? Persona,
    string Version,
    IReadOnlyList<PromptSection>? Sections);
```

规则：

- `SystemPrompt` 必须非空或 host 提供默认；
- 模板变量由 provider 自己转义；
- 安全策略由 host 追加，provider 不能移除；
- `Version` 参与 context cache key；
- 默认 `host#static` 从 profile/插件 config 读取。

### 5.2 `IContextAssembler`

```csharp
public interface IContextAssembler
{
    ValueTask<ContextBundle> AssembleAsync(
        ContextRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record ContextRequest(
    RuntimeProfile Profile,
    Source Source,
    Conversation Conversation,
    User User,
    string UserText,
    IReadOnlyList<StoredMessage> RecentMessages,
    IReadOnlyList<MemoryItem> Memories,
    ConversationSummary? Summary,
    ContextBudget Budget);

public sealed record ContextBundle(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> UsedMemoryIds,
    int EstimatedCharacters,
    string Version,
    ContextDiagnostics Diagnostics);
```

Host 校验：

- system message 只能有一个；
- 角色顺序合法；
- 不超过 `maxInputCharacters`；
- 不包含未授权 memory；
- 不包含 secret/PII；
- 来源可追踪；
- 默认 `host#recent-window` 输出：system + summary + memory + recent + current user。

### 5.3 `IMemoryStore`

```csharp
public interface IMemoryStore
{
    ValueTask<MemoryItem> WriteAsync(
        MemoryWriteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<MemoryItem>> SearchAsync(
        MemoryQuery query,
        CancellationToken cancellationToken = default);

    ValueTask DeleteAsync(
        MemoryDeleteRequest request,
        CancellationToken cancellationToken = default);

    ValueTask<MemoryExport> ExportAsync(
        MemoryExportRequest request,
        CancellationToken cancellationToken = default);

    ValueTask ImportAsync(
        MemoryImportRequest request,
        CancellationToken cancellationToken = default);
}
```

默认实现：`SqliteMemoryStore`。

规则：

- scope 过滤在 store 层和 host 层双重执行；
- 只返回未过期、未删除、有权限的 item；
- `DeleteAsync` 必须传播到 summary/cache invalidation；
- `ExportAsync` 使用标准 envelope；
- `ImportAsync` 不覆盖更高版本/更新记忆，冲突策略显式。

### 5.4 `ISummaryStore`

```csharp
public interface ISummaryStore
{
    ValueTask<ConversationSummary?> GetLatestAsync(
        ConversationKey key,
        CancellationToken cancellationToken = default);

    ValueTask SaveAsync(
        ConversationSummary summary,
        CancellationToken cancellationToken = default);

    ValueTask InvalidateAsync(
        ConversationKey key,
        string reason,
        CancellationToken cancellationToken = default);
}
```

`ConversationSummary`：

```csharp
public sealed record ConversationSummary(
    string SummaryId,
    ConversationKey Key,
    long FromMessageId,
    long ToMessageId,
    string Text,
    int Version,
    string PromptVersion,
    DateTimeOffset CreatedAt);
```

### 5.5 后续契约

P3 暂不开放：

- `IMemoryExtractor`
- `IMemoryRetriever`
- `ISummarizer` 插件

v1 用 host 默认：

- explicit memory；
- recent retrieval；
- deterministic/LLM summary 先由 host 选择。

P3.1 再拆，避免接口爆炸。

---

## 6. 数据库与迁移

### 6.1 SQLite 版本

使用 `PRAGMA user_version` 管理 schema：

```text
0 -> P2 sessions/messages
1 -> summaries
2 -> memory_items
```

启动时按版本迁移；迁移必须幂等。

### 6.2 summaries

```sql
CREATE TABLE IF NOT EXISTS summaries (
    summary_id      TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    from_message_id INTEGER NOT NULL,
    to_message_id   INTEGER NOT NULL,
    text            TEXT NOT NULL,
    version         INTEGER NOT NULL,
    prompt_version  TEXT NOT NULL,
    created_at      TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_summaries_session
    ON summaries(session_id, version DESC);
```

### 6.3 memory_items

```sql
CREATE TABLE IF NOT EXISTS memory_items (
    memory_id       TEXT PRIMARY KEY,
    scope_type      TEXT NOT NULL,   -- user / conversation / guild / global
    scope_id        TEXT NOT NULL,
    subject_id      TEXT NULL,
    kind            TEXT NOT NULL,   -- preference / fact / decision / commitment / task_result
    text            TEXT NOT NULL,
    data_json       TEXT NULL,
    confidence      REAL NOT NULL,
    sensitivity     TEXT NOT NULL,   -- normal / private / sensitive
    user_confirmed  INTEGER NOT NULL,
    source_message_ids_json TEXT NULL,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    expires_at      TEXT NULL,
    version         INTEGER NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_memory_scope
    ON memory_items(scope_type, scope_id, updated_at DESC);
```

### 6.4 messages 扩展

摘要需要稳定的 message id：

- `StoredMessage` 增加 `long MessageId`；
- `IConversationStore.GetRecentAsync` 保留；
- 新增 `GetAfterAsync(ConversationKey, long afterMessageId, int limit)`；
- SQLite 返回 `message_id`；
- 迁移不重写旧行，旧行按自增 id 使用。

### 6.5 数据边界

- `source_key` 必须进入 session/memory scope；
- DM 私人记忆不能进 guild；
- 跨平台身份默认不合并；
- memory export 只导出当前 scope；
- `DeleteAsync` 后 summary/cache 失效。

---

## 7. 默认实现

### 7.1 `HostStaticPromptProvider`

- 读取 profile `prompt` 配置；
- fallback 到 responder 插件 `systemPrompt`；
- 输出 `PromptBundle`；
- 版本默认为 `default`。

### 7.2 `RecentWindowContextAssembler`

从当前 `RecentWindowContextAssembler` 迁移到 host：

```text
system/prompt
  + summary（可选）
  + memory（最多 N 条）
  + recent messages
  + current user
```

预算：

- `maxInputCharacters`；
- `maxSummaryCharacters`；
- `maxMemoryItems`；
- 超预算时先裁 memory，再裁 summary，最后裁 recent；
- 不裁当前用户消息（超长则截断并记录）。

### 7.3 `SqliteMemoryStore`

- scope 过滤；
- `SearchAsync` 先按 scope，再按 updated_at，P3 不做 embedding；
- 写入去重：同 scope + 同 kind + 相似文本（先精确/大小写不敏感）；
- 删除/导出/导入；
- TTL/过期过滤；
- 不自动写模型推测。

### 7.4 显式记忆

提供 CLI 或聊天命令：

```text
/remember <text>
/forget <id|text>
```

实现方式：

- 通过 `chat.command` 或 CLI；
- scope 由当前 source/conversation/actor 决定；
- 用户确认后 `user_confirmed=true`；
- 只存显式文本，不做模型推断；
- 删除后触发 summary/cache invalidation。

### 7.5 Summary 策略

P3 先做最小：

- 当 recent messages 超过阈值时，生成 summary；
- 摘要只覆盖旧消息，不覆盖最近窗口；
- 摘要来源记录 `from/to message id`；
- 摘要失败时继续用 recent window，不阻塞回复；
- 摘要版本参与 context cache key。

摘要实现可以：

1. 先做确定性截断/抽取（不额外调模型）；
2. 或使用配置的 cheap provider；
3. 不论哪种，必须有 `ISummaryStore` 和版本。

P3 v1 推荐先用确定性摘要，避免引入第二次模型调用和成本；P3.1 再换 LLM 摘要。

---

## 8. NPS Contribution

新增类型：

```text
prompt.provider
context.assembler
memory.store
```

Manifest 示例：

```json
{
  "schemaVersion": 1,
  "id": "natter.memory.sqlite",
  "kind": "memory",
  "hostApi": ">=0.1.0 <1.0.0",
  "permissions": ["storage"],
  "contributions": [
    { "type": "memory.store", "id": "sqlite" }
  ]
}
```

规则：

- 一个 profile 只能选一个 prompt/assembler/store；
- 插件实现必须通过 host contract tests；
- `storage` 权限用于访问 memory DB；
- `network` 默认不授予；
- 第三方插件默认 out-of-process（后续）；
- 插件不能直接访问 `IConversationStore` 以外的 host 私有数据。

NPS `BuildPluginProvider` 增加 host 服务：

```csharp
if (granted.Contains(PluginPermissions.Storage))
    services.AddSingleton(_hostProvider.GetRequiredService<IMemoryStore>());
```

`IContextAssembler` / `IPromptProvider` 由 host 的 profile-aware pipeline 暴露给 `AiChatResponder`，不要让 provider 插件自己选。

---

## 9. AI Responder 改造

当前 `AiChatResponder`：

```text
history.GetKey -> contextAssembler.AssembleAsync -> IChatClient
```

P3：

```text
ProfileContext.Current
  → IContextPipeline.AssembleAsync
       ├── IPromptProvider
       ├── ISummaryStore
       ├── IMemoryStore.Search
       └── IContextAssembler
  → IChatClient
```

要求：

- `AiChatResponder` 不再直接依赖 `AiResponderOptions.SystemPrompt` 作为唯一 prompt；
- `IAiContextAssembler` 保留为 compatibility adapter，内部调用 host pipeline；
- provider 插件只提供 `IChatClient`，不提供 context/memory 策略；
- DeepSeek/Spark 共用同一个 responder 和 pipeline；
- context 组装失败时降级 recent-window，不阻塞回复；
- memory 不可用时降级 recent-window；
- summary 不可用时降级 recent-window。

---

## 10. 缓存与版本

Context cache key 至少包含：

```text
source
conversation
profileId
profileVersion
promptVersion
assemblerVersion
memoryStoreVersion
memorySnapshotVersion
summaryVersion
model
```

规则：

- 新消息使 recent window 失效；
- memory 写入/删除使 memory snapshot 失效；
- summary 更新使 summary cache 失效；
- prompt/persona 变更使 prompt cache 失效；
- 删除 memory 必须使引用它的 summary stale；
- 不做 semantic response cache（P3 默认关闭）。

---

## 11. 安全与隐私

- memory scope 是安全边界；
- DM 不能读 guild 私人记忆；
- 用户 A 不能读用户 B；
- 跨平台身份不自动合并；
- `remember` 需用户显式触发；
- 敏感信息不自动写入；
- memory 内容视为不可信输入，不作为 system prompt；
- 导出/删除必须可用；
- 日志不记录 prompt/memory 正文；
- state DB 继续 0600/0700；
- 第三方 memory 插件默认无网络。

---

## 12. 测试

### 12.1 Contract tests

- `IPromptProvider`：system 唯一、变量转义、版本；
- `IContextAssembler`：角色、预算、scope、诊断；
- `IMemoryStore`：CRUD、scope、export/import、过期；
- `ISummaryStore`：版本、范围、失效；
- profile ref 校验、缺失 Tier 1/Tier 2 策略。

### 12.2 Golden context

固定输入 → 固定 context：

- recent only；
- summary + recent；
- memory + recent；
- 超预算裁剪顺序；
- prompt 版本变化。

### 12.3 泄漏测试

- DM 记忆不进 guild；
- 用户 A 不进用户 B；
- 跨 source 不共享；
- 未授权 memory 不进入 context。

### 12.4 持久化

- SQLite migration 0→1→2；
- 重启后 summary/memory 恢复；
- memory 删除后 summary/cache stale；
- 重复 event id 不重复写；
- export/import 往返。

### 12.5 真实链路

- Tier 1：Discord + DeepSeek，显式 `remember` → 下轮回复使用；
- Tier 1：guild/DM 隔离；
- Tier 2：DingTalk + Spark smoke；
- 重启后记忆保持；
- 删除记忆后不再召回。

---

## 13. 实施阶段

### P3.1：契约与 profile 扩展

- 新增 prompt/context/memory 接口；
- 扩展 `RuntimeProfile` 可选字段；
- 兼容旧 profiles；
- 默认 `host#static` / `host#recent-window` / `host#sqlite`；
- contract tests。

### P3.2：SQLite 存储

- summaries/memory_items 表；
- `StoredMessage.MessageId`；
- `ISummaryStore` / `SqliteMemoryStore`；
- migration/权限/export/delete。

### P3.3：AI responder 接入

- `IContextPipeline`；
- `AiChatResponder` 改用 host pipeline；
- `IAiContextAssembler` compatibility adapter；
- memory/summary 失败降级。

### P3.4：显式记忆与摘要

- `/remember` / `/forget`；
- 确定性 summary；
- context budget；
- invalidation。

### P3.5：NPS contribution

- `prompt.provider` / `context.assembler` / `memory.store`；
- 外部插件示例；
- 包校验/权限。

### P3.6：验收

- Tier 1 真实；
- Tier 2 smoke；
- 泄漏/删除/重启；
- 归档。

---

## 13.1 本轮实现状态

| 项 | 状态 | 文件 |
| --- | --- | --- |
| Prompt/Context/Memory 契约 | 已实现 | `Natter.Application/Chat/Context/ContextContracts.cs` |
| HostStaticPromptProvider | 已实现 | `DefaultContextImplementations.cs` |
| HostRecentWindowContextAssembler | 已实现 | 同上 |
| HostContextPipeline | 已实现 | `HostContextPipeline.cs` |
| InMemoryMemoryStore / InMemorySummaryStore | 已实现 | 同上 |
| SQLite memory_items / summaries | 已实现 | `SqliteContextStores.cs` |
| user_version 0→1→2 migration | 已实现 | 同上 |
| StoredMessage.MessageId / GetAfterAsync | 已实现 | `ConversationStore.cs` / `SqliteConversationStore.cs` |
| profile prompt/context/memory 可选字段 | 已实现，旧 profile 兼容 | `RuntimeProfiles.cs` |
| 组件 refs 选择 | host 组件 catalog；NPS prompt/context/memory contribution 注册/激活/回滚 | `ContextComponentCatalog.cs` / `NpsContributionRegistry.cs` |
| 确定性 summary | 已实现（旧消息窗口） | `HostContextPipeline.cs` |
| 显式 remember / forget / memory list | 已实现（CLI） | `MemoryCommands.cs` |
| NPS contribution 类型 | 已声明并支持注册/激活/回滚；测试覆盖 prompt contribution | `PluginContributionTypes.cs` |
| Tier 1 真实记忆召回 | 未执行（缺人工/真实测试脚本） | — |
| Tier 2 smoke | 共享 pipeline fake/契约测试通过，真实未重跑 | — |

测试：`Natter.Framework.Tests` **232/232**；P2 的 214 基线未回归。

## 14. 验收标准

- [ ] 旧 P2 profiles 仍可启动；
- [ ] 新 profile 能选择 prompt/context/memory；
- [ ] 默认实现无需第三方插件即可运行；
- [ ] 显式 `remember` 后下轮能召回；
- [ ] `forget` 后 summary/cache 失效；
- [ ] DM/guild/source 隔离；
- [ ] summary 失败不阻塞回复；
- [ ] memory 不可用降级 recent；
- [ ] context 不超预算；
- [ ] SQLite migration 幂等；
- [ ] state 0700/0600；
- [ ] 日志无 prompt/memory 正文；
- [ ] Tier 1 Discord+DeepSeek 通过；
- [ ] Tier 2 DingTalk+Spark smoke 通过；
- [ ] P2 214/214 不回归；
- [ ] 测试记录归档。

---

## 15. 明确不做

- 向量数据库/embedding；
- 自动模型记忆提取；
- semantic response cache；
- 多实例 memory 共享；
- 第三方 memory SaaS；
- Context/Memory UI；
- Task Worker；
- 第三个平台/Android；
- 为 Tier 2 加平台特判。

---

## 16. 一句话结论

**P3 不是“加一个 memory 表”，而是把 Prompt、Context、Memory 三层策略从 provider 插件中抽出来，交给 host 管理、profile 选择、插件实现。**

先做：

```text
契约 → SQLite summary/memory → 默认 recent-window/explicit memory
→ Tier 1 真实验收
```

不要先做向量库，也不要把 memory 写进 DeepSeek/Spark 插件。
