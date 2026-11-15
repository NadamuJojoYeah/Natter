# Natter Context & Memory Plugin Design

> **状态**：P3.1–P3.4 初始实现已交付（host pipeline、SQLite summary/memory、显式 remember/forget、profile 字段；NPS prompt/context/memory contribution 激活与真实 Tier 1 记忆验收待后续）  
> **日期**：2026-11-15  
> **定位**：把 prompt / context / memory 做成可替换插件，但由 host 持有契约、数据、预算、安全与校验。  
> **关联**：[Harness 架构](HARNESS_ARCHITECTURE.md)、[P3 实现规格](P3_CONTEXT_MEMORY.md)、[NLMP](NLMP.md)、[NPS v1](PLUGIN_SPEC.md)、[AI Provider 设计](AI_PROVIDER_DESIGN.md)

---

## 0. 决策摘要

1. **Context / Prompt / Memory 是 Natter 最值得插件化的部分**，比 transport 和 provider 更有产品差异。
2. 但只插件化**策略**，不插件化**事实源**：消息日志、Session、Scope、Token 预算、安全校验仍归 host。
3. v1 只开放三个契约：`IPromptProvider`、`IContextAssembler`、`IMemoryStore`。
4. `IMemoryExtractor`、`IMemoryRetriever`、`ISummarizer` 后续再拆，避免一开始接口爆炸。
5. 用户选择的是**已安装、已审核的 ContextProfile**，不是上传任意 DLL。
6. 一个 profile 中 `prompt` / `context assembler` / `memory store` 各一个 active；retriever 可以多个组合。
7. Host 必须校验插件产出的 context：角色、token、scope、权限、来源、敏感信息。
8. 记忆数据属于用户/workspace，不属于插件；必须支持导出、删除、迁移。
9. 第三方/不可信 memory 插件默认 out-of-process，默认无网络。
10. 默认实现先做 SQLite + recent window + explicit memory，不先上向量库。

---

## 1. 目标与非目标

### 1.1 目标

- 用户可以切换不同上下文策略、prompt/persona、memory 实现；
- 插件可以实验新的摘要、检索、图记忆、RAG 策略；
- host 仍然能保证 scope、预算、隐私、审计和一致性；
- Discord、钉钉等 transport 复用同一套 context/memory；
- 换 memory 后端不丢数据、不锁死；
- 失败时能降级，不阻塞聊天。

### 1.2 非目标

- 插件直接实现整个 ChatOrchestrator；
- 插件绕过 host 直接读其他用户/其他 guild 的记忆；
- 用户上传任意代码；
- v1 就做多进程/容器插件隔离；
- v1 就做向量数据库、GraphRAG、自动记忆提取；
- 让模型自动写入所有推测内容；
- 把 provider prompt cache 当持久记忆。

---

## 2. 为什么这部分适合插件化

- 上下文策略研究变化快：最近窗口、摘要、分层摘要、RAG、图记忆、episodic/semantic 分离；
- 不同产品需要不同策略：客服、角色扮演、工作助手、社区 bot；
- 不同用户对记忆的接受度不同；
- 不同部署适合不同存储：SQLite、Postgres、Redis、向量库、外部 memory SaaS；
- transport/provider 会趋同，context/memory 是差异化。

但插件化必须解决四个问题：

1. **契约稳定**：不同实现能互换；
2. **数据可迁移**：不能被某个插件锁死；
3. **安全可验证**：不能跨 scope 泄漏；
4. **行为可解释**：出了问题时能知道用了哪些 context/memory。

---

## 3. Core / Plugin 边界

| 能力 | 归属 | 说明 |
| --- | --- | --- |
| Session / Task | Core | 连续性和生命周期 |
| Message append-only log | Core | 事实源 |
| Scope / 权限 / 隐私 | Core | 不可由插件绕过 |
| Token budget | Core | 防止上下文爆炸 |
| Context validation | Core | 角色、来源、scope、敏感信息 |
| Cache version / invalidation | Core | 一致性 |
| Prompt / Persona | **Plugin** | 内容与策略 |
| Context assembly | **Plugin** | 视图构建 |
| Memory store | **Plugin** | 持久化后端 |
| Memory extraction | **Plugin（后续）** | 什么值得记 |
| Memory retrieval | **Plugin（后续）** | 召回与排序 |
| Summary | **Plugin（后续）** | 增量/分层摘要 |
| Transport | Plugin（已有） | 平台适配 |
| AI provider | Plugin（已有） | M.E.AI |

原则：

> **插件提议，host 校验；插件存储，host 管 scope；插件召回，host 管预算。**

---

## 4. v1 契约

### 4.1 `IPromptProvider`

```csharp
public interface IPromptProvider
{
    ValueTask<PromptBundle> GetAsync(
        PromptRequest request,
        CancellationToken cancellationToken = default);
}
```

`PromptRequest`：

```csharp
public sealed record PromptRequest(
    Source Source,
    Conversation Conversation,
    Actor Actor,
    string? ProfileId,
    string? Locale,
    IReadOnlyDictionary<string, string> Variables);
```

`PromptBundle`：

```csharp
public sealed record PromptBundle(
    string SystemPrompt,
    string? Persona,
    string Version,
    IReadOnlyList<PromptSection>? Sections);
```

规则：

- system prompt 只能有一个；
- 不允许 prompt provider 注入工具调用结果；
- 模板变量必须转义；
- prompt 版本参与 context cache key；
- 安全策略由 host 追加，不由插件移除。

### 4.2 `IContextAssembler`

```csharp
public interface IContextAssembler
{
    ValueTask<ContextBundle> AssembleAsync(
        ContextRequest request,
        CancellationToken cancellationToken = default);
}
```

`ContextRequest`：

```csharp
public sealed record ContextRequest(
    Source Source,
    Conversation Conversation,
    Actor Actor,
    ChatEvent Trigger,
    int TokenBudget,
    PromptBundle Prompt,
    ConversationSummary? Summary,
    IReadOnlyList<MemoryItem> MemoryCandidates,
    ContextCapabilities Capabilities);
```

`ContextBundle`：

```csharp
public sealed record ContextBundle(
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<string> UsedMemoryIds,
    int EstimatedTokens,
    string Version,
    ContextDiagnostics Diagnostics);
```

Host 校验：

- system message 数量；
- 角色顺序；
- token 预算；
- memory scope；
- 是否包含未授权内容；
- 是否包含 secret/PII；
- 来源是否可追踪；
- 是否包含插件自定义的非法 content type。

### 4.3 `IMemoryStore`

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

职责：

- 持久化；
- scope 过滤；
- 检索；
- 删除/导出/导入；
- 版本。

不负责：

- 决定“什么值得记”；
- 决定“是否允许记”；
- 绕过 host 的 scope 策略；
- 直接把 memory 塞进 system prompt。

### 4.4 后续契约

```text
IMemoryExtractor   // 从对话提取候选记忆
IMemoryRetriever   // 召回/排序/rerank
ISummarizer        // 增量/分层摘要
```

v1 不开放这些接口，先用默认实现；等真实数据和使用场景稳定后再拆。

---

## 5. NPS Contribution

新增 contribution 类型：

```text
prompt.provider
context.assembler
memory.store
memory.extractor      // 后续
memory.retriever      // 后续
summarizer            // 后续
```

### 5.1 选择规则

- 一个 profile 中：
  - `prompt.provider`：1 个 active；
  - `context.assembler`：1 个 active；
  - `memory.store`：1 个 active；
  - `memory.retriever`：0..N，组成 pipeline；
- NPS dependency graph 负责加载顺序；
- Host 根据 `ContextProfile` 选择实现；
- 插件版本参与 profile 版本。

### 5.2 Manifest 示例

```json
{
  "schemaVersion": 1,
  "id": "natter.context.summary-window",
  "name": "Summary Window Context",
  "version": "0.1.0",
  "kind": "context",
  "hostApi": ">=0.1.0 <1.0.0",
  "entry": {
    "assembly": "lib/Natter.Context.SummaryWindow.dll",
    "type": "Natter.Context.SummaryWindow.SummaryWindowPlugin"
  },
  "permissions": ["chat.read"],
  "contributions": [
    { "type": "context.assembler", "id": "summary-window" }
  ]
}
```

Memory store 示例：

```json
{
  "schemaVersion": 1,
  "id": "natter.memory.sqlite",
  "kind": "memory",
  "permissions": ["storage"],
  "contributions": [
    { "type": "memory.store", "id": "sqlite" }
  ]
}
```

权限建议：

- `chat.read`：context 需要读事件/会话；
- `storage`：memory 持久化；
- `network`：外部 memory 服务，默认不授予；
- `memory.read` / `memory.write` / `memory.delete`：更细粒度，可后续加；
- `secrets:<NAME>`：外部服务凭据。

---

## 6. ContextProfile

用户选择的不是任意组合，而是受控 profile。

```json
{
  "id": "long-term-assistant",
  "version": "1",
  "prompt": "natter.persona.concise@1.2.0",
  "assembler": "natter.context.summary-window@0.3.0",
  "memory": {
    "store": "natter.memory.sqlite@0.2.0",
    "extractor": "natter.memory.explicit@0.1.0",
    "retriever": "natter.memory.recent@0.1.0"
  },
  "budget": {
    "maxContextTokens": 12000,
    "maxMemoryItems": 8,
    "maxSummaryTokens": 1200
  }
}
```

### 6.1 预设

| Preset | 用途 |
| --- | --- |
| `simple` | 最近窗口，无长期记忆 |
| `summary` | 摘要 + 短期记忆 |
| `long-term` | 显式记忆 + 检索 |
| `rag` | 知识库检索 |
| `roleplay` | persona + 关系记忆 |
| `work` | 任务记忆 + 工具结果 |

### 6.2 选择范围

```text
Global 默认
  └── Workspace/Guild 管理员覆盖
        └── User 偏好（记忆开关、persona、保留策略）
```

普通用户不应选择“加载哪个第三方 DLL”，只选择：

- 是否启用记忆；
- 使用哪个 preset/persona；
- 记忆保留多久；
- 是否允许跨平台关联。

### 6.3 版本固定

- profile 固定插件版本；
- prompt/assembler/memory 版本参与 context cache key；
- 升级 profile 需要显式操作；
- 支持回滚到上一版 profile。

---

## 7. 组合 Pipeline

```text
PromptProvider
      │
SummaryStore
      │
MemoryRetriever(s)
      │
ContextAssembler
      │
Host Budget / Validator
      │
ChatMessages
```

规则：

- retriever 只返回候选，不直接拼 prompt；
- assembler 决定排布；
- host 决定最终预算和合法性；
- diagnostics 记录用了哪些 memory、哪些被裁剪；
- 任一插件超时/失败，降级到默认实现，不阻塞回复。

---

## 8. 数据所有权与迁移

### 8.1 所有权

- 记忆数据属于用户/workspace；
- 插件只是 processor；
- 插件不能把数据锁定在不可导出的格式；
- 插件必须支持删除；
- 删除必须传播到摘要/缓存；
- 第三方插件默认不能联网。

### 8.2 导出格式

建议 host 定义标准 JSON envelope：

```json
{
  "schemaVersion": 1,
  "exportedAt": "2026-11-15T00:00:00Z",
  "source": "discord",
  "scope": "user",
  "items": []
}
```

插件可以有自己的内部 schema，但必须能映射到标准 envelope。

### 8.3 迁移

- `export` → 转换 → `import`；
- 迁移期间双写可选；
- 迁移后校验条数/版本/scope；
- 失败可回滚；
- 不把迁移逻辑塞进 responder。

---

## 9. 安全与隐私

1. **Scope 强制过滤**：host 在传给 assembler 前先过滤；assembler 不能拿到未授权 memory。
2. **Memory 是不可信输入**：记忆内容也可能被 prompt injection 污染；不要直接放进 system。
3. **工具/Prompt 注入**：memory 内容作为数据，不作为指令。
4. **第三方插件**：默认 out-of-process；无网络；只给最小权限。
5. **敏感信息**：密码/token/支付信息不写入 memory。
6. **删除/导出**：用户可操作；删除后摘要/缓存失效。
7. **日志**：不记录完整 memory/prompt；只记录 id、token、scope、版本。
8. **跨平台**：默认不合并身份；显式 link 后才共享用户记忆。

---

## 10. 缓存与版本

Context/Memory 的缓存 key 至少包含：

```text
source
conversationId
profileId
profileVersion
promptVersion
assemblerVersion
memoryStoreVersion
memorySnapshotVersion
summaryVersion
model
```

失效条件：

- 新消息；
- 摘要更新；
- 记忆写入/删除；
- prompt/persona 变更；
- 插件版本变更；
- 模型切换；
- 权限/工具集变化。

规则：

- 缓存失效不能改变产品行为；
- 记忆删除必须使相关摘要/缓存 stale；
- provider prompt cache 只是优化，不是状态；
- 语义 response cache 默认关闭。

---

## 11. 测试

### 11.1 Contract tests

- `IPromptProvider`：system 唯一、变量转义、版本；
- `IContextAssembler`：角色合法、预算、scope、诊断；
- `IMemoryStore`：CRUD、scope、export/import、并发；
- Host validator：非法 context 被拒绝。

### 11.2 Golden snapshots

固定输入 → 固定 context 输出：

- 最近窗口；
- 摘要；
- 记忆检索；
- 裁剪；
- 版本变化。

### 11.3 泄漏测试

- DM 记忆不能进 guild；
- 用户 A 记忆不能进用户 B；
- 未授权 memory 不进入 context；
- 第三方插件无网络时不能外传。

### 11.4 失效测试

- 删除记忆 → 摘要 stale；
- prompt 变更 → cache miss；
- profile 升级 → 版本变化；
- 摘要失败 → 最近窗口降级。

### 11.5 插件替换测试

- 同一 profile 换 memory store，消息/记忆可迁移；
- 换 assembler，host 校验仍生效；
- 换 prompt，安全策略仍保留；
- 第三方插件失败，默认实现接管。

---

## 12. 实施阶段

### Phase 1：Host 契约 + 默认实现

- `IPromptProvider` / `IContextAssembler` / `IMemoryStore`；
- SQLite + recent window + explicit memory；
- contract tests / golden snapshots；
- 不开放第三方插件。

### Phase 2：NPS contribution + profile

- 加 `prompt.provider` / `context.assembler` / `memory.store`；
- `ContextProfile` 选择；
- simple/summary/long-term preset；
- host validator。

### Phase 3：提取/检索/摘要

- `IMemoryExtractor`；
- `IMemoryRetriever`；
- `ISummarizer`；
- 多 retriever pipeline；
- embedding/hybrid。

### Phase 4：第三方生态

- out-of-process memory 插件；
- 导出/导入/迁移工具；
- 兼容矩阵；
- 市场/评分/审核。

---

## 13. 验收标准

- [ ] Host 拥有 Session/Message/Scope/Budget/Validator；
- [ ] v1 三个契约可替换；
- [ ] 一个 profile 只能有一个 prompt/assembler/store；
- [ ] 插件不能绕过 scope；
- [ ] 插件不能绕过 token budget；
- [ ] 记忆可导出/导入/删除；
- [ ] 删除记忆使摘要/缓存失效；
- [ ] 插件失败降级默认实现；
- [ ] Discord/新平台共用同一 context/memory；
- [ ] 第三方插件默认无网络/out-of-process；
- [ ] 日志无 prompt/memory 正文和密钥。

---

## 14. 开放问题

- Prompt 是 content pack 还是插件？v1 建议 content pack + 可选 provider；
- 用户级 profile 是否允许覆盖 workspace 管理员策略；
- memory extractor 用规则还是模型；
- 记忆冲突/衰减策略；
- 是否引入 embedding/向量库；
- 第三方插件的进程隔离级别；
- 记忆导出的标准 schema；
- 跨平台身份 link 的确认流程；
- 多实例 session/memory 一致性；
- 成本/延迟预算如何按 profile 限制。

---

## 15. 一句话结论

**Context、Prompt、Memory 可以插件化，但插件化的是策略，不是事实源；开放的是选择，不是权限。**

Host 负责：

- 会话与消息事实；
- scope/权限/预算/校验；
- 缓存版本与失效；
- 导出/删除/审计。

Plugin 负责：

- 怎么组装 context；
- 用什么 prompt/persona；
- 存在哪里、怎么检索、怎么摘要。

这样既能吸收 context/memory 领域的新研究，又不会把 Natter 变成互不兼容的 prompt 实验场。
