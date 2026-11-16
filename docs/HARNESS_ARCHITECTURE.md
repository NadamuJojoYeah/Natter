# Natter Harness Architecture

> **状态**：Accepted direction · 第一阶段已交付（Session/Task 未完整；共享 responder、ContextAssembler/MemoryStore 边界、第二 transport/provider 已落地）  
> **日期**：2026-11-15  
> **定位**：Natter 作为多平台 chat/agent harness 的目标架构。本文定义控制面、执行面、Session/Task、上下文缓存与长期记忆的边界；不是 Super-agent 的复刻。  
> **关联**：[架构设计](ARCHITECTURE.md)、[NLMP](NLMP.md)、[NPS v1](PLUGIN_SPEC.md)、[AI Provider 设计](AI_PROVIDER_DESIGN.md)、[Discord transport](DISCORD_PLUGIN_DESIGN.md)

---

## 0. 决策摘要

1. **Natter 是多平台 chat/agent harness，不是 coding agent runtime。**
2. 采用 Super-agent 的**控制面/执行面分离**思想，但 v1 不拆进程、不要求每条消息创建 Worker。
3. 普通聊天走低延迟 `ChatResponder`；只有长任务、工具链、多步规划才进入 `TaskWorker`。
4. 显式引入 `ConversationSession` 与 `AgentTask`，不再把“会话历史”当成唯一状态。
5. 触发策略先做确定性 Fast Path；语义 Slow Path 后置，不默认用 LLM 判断每条消息。
6. **上下文缓存不是记忆。** 缓存是可丢弃的派生数据；记忆是持久、可审计、可删除的事实与历史。
7. 记忆分四层：会话短期状态、episodic 消息、semantic 记忆、task 工作记忆。
8. 第一版记忆持久化从 **append-only 消息 + 增量摘要 + 显式记忆项**开始，不先上向量库。
9. 多平台通过 NLMP 和 canonical identity 映射；不自动合并跨平台用户。
10. 主动行为、全权限 Worker、进程隔离、Android/市场都属于后续阶段。

---

## 1. 目标与非目标

### 1.1 目标

- 让 Discord、Satori、Slack 等 transport 复用同一套控制面；
- 让普通聊天和长任务使用不同执行路径；
- 让会话状态、任务状态、记忆可持久化、可恢复；
- 让上下文构建可解释、可预算、可缓存；
- 让插件继续通过 NPS 扩展，而不是改 core；
- 让多平台/多 provider 的差异留在 adapter。

### 1.2 非目标

- 复刻 Super-agent 的 GitHub/Feishu coding agent；
- 每条消息都 Router → Worker；
- 默认主动发消息；
- 全权限工具执行；
- 一开始就做多进程/容器 Worker；
- 一开始就做向量数据库和复杂 RAG；
- 自动把 Discord/Slack 用户合并成同一身份；
- 替代 NPS 插件系统。

---

## 2. Super-agent 的借鉴与取舍

参考：[Super-agent 架构拆解](https://news.qiniu.com/archives/1793499698327)、[dsh-superagent-prototype](https://github.com/deepseek-ai/dsh-superagent-prototype)

### 2.1 借鉴

| 原则 | Natter 采用方式 |
| --- | --- |
| 控制面/执行面分离 | 逻辑分层；v1 同进程 |
| 事件驱动 | NLMP 已经是事件模型 |
| Fast/Slow Path | 先确定性过滤，后语义策略 |
| Router 维护全局连续性 | Session Registry + canonical identity |
| Worker 上下文隔离 | TaskWorker 可选，不用于普通聊天 |
| 上下文缓存化 | ContextAssembler + 摘要 + 检索 |
| 存算分离 | Session/Task/Memory 持久化 |
| Watchdog | 心跳、卡死任务、重启恢复 |
| 异步工具 | 长任务 start/stop，后续阶段 |

### 2.2 不照搬

- **不把 Router/Worker 做成每条消息的进程拓扑**：聊天延迟不允许。
- **不让 Worker 绕过策略直接订阅**：权限、限流、审计仍在控制面。
- **不默认主动找活**：社交平台主动消息需要显式 opt-in。
- **不给 Worker 全权限**：工具白名单、预算、超时、审计。
- **不把 coding agent 的 task 完成时间指标套到聊天上**：Super-agent 的 p95 是分钟级，聊天是秒级。

---

## 3. 目标架构

```text
Discord / Satori / Slack / 其他平台
        │
        ▼
Transport adapters（NPS plugin）
        │ NLMP events
        ▼
NLMP ingress / dedupe / normalization
        │
        ▼
Trigger Policy
  ├── Fast Path：allowlist / mention / DM / bot-self / rate limit / dedupe
  └── Slow Path：语义分类（可选，后置）
        │
        ▼
Session Registry
  ├── ConversationSession
  ├── Actor / identity mapping
  └── Active task refs
        │
        ▼
Control Plane（ChatOrchestrator 演进）
  ├── 选择 responder / worker
  ├── 统一回复
  ├── 限流 / 审计 / 幂等
  └── 任务生命周期
        │
        ├── ChatResponder（同步、低延迟）
        │      └── AI provider（M.E.AI / IChatClient）
        │
        └── TaskWorker（可选、长任务、工具）
               ├── 隔离上下文
               ├── 工具白名单
               ├── checkpoint / cancellation
               └── 结果回控制面
        │
        ▼
NLMP egress
        │
        ▼
Transport adapters
```

### 3.1 分层职责

| 层 | 职责 | 不负责 |
| --- | --- | --- |
| Transport adapter | 平台连接、事件映射、命令发送、能力声明 | 会话策略、AI、记忆 |
| NLMP ingress | 规范化、去重、排序、Source/Conversation | 平台 SDK |
| Trigger Policy | 是否处理、是否回复、是否起任务 | 生成回复 |
| Session Registry | 会话/身份/任务索引 | 模型调用 |
| Control Plane | 路由、生命周期、回复、审计 | 平台 API、工具执行 |
| ChatResponder | 短回复、slash、低延迟 | 长任务、全权限工具 |
| TaskWorker | 长任务、工具、多步规划 | 直接对用户说话 |
| Context/Memory | 上下文组装、摘要、记忆读写 | 平台发送 |
| AI provider | 模型请求/响应、usage、错误 | 会话策略 |
| State store | 会话、消息、摘要、记忆、任务 | 业务决策 |

### 3.2 v1 部署形态

v1 保持**单进程、逻辑分层**：

```text
NatterHost
├── transports + NPS plugins
├── ChatOrchestrator / policy
├── responders
└── stores
```

后续再按需拆：

- Worker 进程/容器；
- 独立 memory service；
- 独立 provider gateway；
- 多实例 + 分布式 lease。

不要为了“架构看起来像 Super-agent”提前拆进程。

---

## 4. 核心模型

### 4.1 ConversationSession

```csharp
public sealed record ConversationSession
{
    public required string SessionId { get; init; }
    public required Source Source { get; init; }
    public required string ConversationId { get; init; }
    public string? GuildId { get; init; }
    public string? ActorId { get; init; }

    public string? SummaryId { get; init; }
    public long LastMessageSequence { get; init; }
    public long Version { get; init; }

    public IReadOnlyList<string> ActiveTaskIds { get; init; }
    public SessionPolicyState Policy { get; init; }
}
```

职责：

- 标识一个平台会话的连续性；
- 关联最近消息、摘要、记忆、任务；
- 保存版本号，支持乐观并发；
- 不直接保存完整 prompt。

### 4.2 AgentTask

```csharp
public sealed record AgentTask
{
    public required string TaskId { get; init; }
    public required string SessionId { get; init; }
    public required AgentTaskStatus Status { get; init; }
    public string? WorkerId { get; init; }
    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public string? ResultSummary { get; init; }
    public string? ErrorCode { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
}
```

状态：

```text
Pending → Running → WaitingForInput → Completed
                    ↘ Failed
                    ↘ Cancelled
```

规则：

- 普通聊天不创建 `AgentTask`；
- 只有长任务/工具/多步规划才创建；
- Task 不直接发平台消息，结果回控制面；
- 同一 Session 的 task 可并行，但会话控制面串行更新。

### 4.3 Session Actor

每个 `(source, conversationId)` 一个逻辑 actor：

- 串行处理该会话的控制面事件；
- 避免并发 AI 调用、历史交错、重复回复；
- 允许多个 task 并行，但状态更新按版本串行；
- v1 用 in-process `SemaphoreSlim`/Channel；多实例时用 DB lease。

---

## 5. 触发策略

### 5.1 Fast Path

确定性规则，先挡掉大部分噪音：

- bot/self 消息；
- guild/channel/user allowlist；
- DM / mention / reply；
- 重复 event id；
- 每用户/频道频率；
- 平台能力不支持的交互；
- 空消息/纯附件策略。

输出：

```text
Ignore | Respond | StartTask | Queue
```

### 5.2 Slow Path

只对“不确定是否值得处理”的事件使用：

- 语义分类；
- 主动巡检；
- 复杂路由；
- 多意图拆分。

约束：

- 不默认每条消息调用 LLM；
- 有预算/超时；
- 结果可缓存；
- 失败时回退到 Fast Path 的保守策略。

---

## 6. 执行面

### 6.1 ChatResponder

适用：

- 普通消息；
- slash；
- 短问答；
- 单轮/少轮工具调用。

要求：

- 低延迟；
- 只返回 `ChatCommand`；
- 不直接调平台 API；
- 不持有跨请求可变状态；
- 会话状态通过 store/context 获取。

### 6.2 TaskWorker

适用：

- 长任务；
- 多步规划；
- 代码/文件/网络工具；
- 需要 checkpoint/恢复；
- 结果需要汇总。

要求：

- 隔离上下文；
- 工具白名单；
- 预算/超时/取消；
- 不直接对用户说话；
- 结果通过控制面回复；
- 有 lease/watchdog。

### 6.3 什么时候升级为 Worker

满足任一条件：

- 预计执行超过 N 秒；
- 需要多轮工具调用；
- 需要独立工作目录/沙箱；
- 需要并行多个 task；
- 需要在会话结束后继续执行。

否则留在 ChatResponder。

---

## 7. 上下文缓存

> Prompt/Context/Memory 的插件化设计见 [Context & Memory 插件](CONTEXT_MEMORY_PLUGIN.md)。

### 7.1 先区分上下文、缓存、记忆

| 概念 | 生命周期 | 是否可重建 | 例子 |
| --- | --- | --- | --- |
| Context | 单次请求 | 是 | 本次 prompt 的消息/摘要/记忆 |
| Cache | 短/中期 | 是 | provider prompt cache、摘要缓存、热会话 |
| Memory | 长期 | 否（是事实源） | 用户偏好、长期事实、历史消息 |

**缓存丢了可以重建；记忆丢了就是数据丢失。**

### 7.2 上下文组装

```text
ContextAssembler
  ├── stable system/policy
  ├── persona（带版本）
  ├── tool schema（稳定）
  ├── conversation summary（带版本）
  ├── retrieved memories（scope + top-K）
  ├── recent messages（token 预算）
  └── current user message
```

建议预算（可配置，不是硬编码）：

| 部分 | 预算 |
| --- | --- |
| system/policy/persona | 10–15% |
| tool schema | 5–10% |
| summary | 10–15% |
| retrieved memory | 15–20% |
| recent messages | 35–45% |
| current message | 5–10% |
| output reserve | 15–25% |

原则：

- 最近消息保留完整保真度；
- 更早消息摘要化/落盘；
- 摘要和记忆带版本/来源；
- 动态内容放后面，稳定前缀放前面；
- 不把时间戳/随机 ID 放进稳定前缀。

### 7.3 三层缓存

#### A. Provider prompt cache

DeepSeek 等 provider 的上下文缓存：

- 稳定前缀更容易命中；
- 关注 `prompt_cache_hit_tokens` / miss；
- 不保证命中；
- 切换 provider/model 会失效；
- 不要把用户隐私放进跨用户共享前缀。

#### B. Context assembly cache

可缓存：

- conversation summary；
- token 计数；
- 最近窗口快照；
- embedding/retrieval 结果（按 scope）。

Key 至少包含：

```text
(source, conversationId, summaryVersion, personaVersion,
 promptVersion, model, memorySnapshotVersion)
```

失效条件：

- persona/prompt 版本变化；
- 新消息；
- 摘要更新；
- 记忆写入；
- 权限/工具集变化；
- 模型切换。

#### C. Semantic response cache（可选，默认关闭）

只适合：

- 公共、非个性化问题；
- 稳定 FAQ；
- 低风险工具结果。

不适合：

- 个性化回复；
- 含用户隐私；
- 需要最新状态；
- 多轮上下文。

默认不要用它替代记忆。

### 7.4 摘要缓存

- 每 N 条消息或 token 阈值触发；
- 增量摘要，不重写全部历史；
- 保留：决定、偏好、未解决问题、关键实体；
- 丢弃：寒暄、重复内容、无关细节；
- 摘要带 `fromMessageId/toMessageId/version`；
- 摘要失败不能阻塞回复，降级为最近窗口。

### 7.5 缓存失效与并发

- Session 有 `Version`；
- 写消息/摘要/记忆时递增；
- Context key 带版本；
- 乐观并发，冲突重试；
- 不允许旧摘要覆盖新摘要；
- 多实例用 DB row version/lease。

---

## 8. 记忆持久化

> 记忆存储、提取、检索的插件边界见 [Context & Memory 插件](CONTEXT_MEMORY_PLUGIN.md)。

### 8.1 四层记忆

| 层 | 内容 | 生命周期 | 存储 |
| --- | --- | --- | --- |
| Working | 当前 task 的计划、工具结果 | 分钟/小时 | task store / memory |
| Short-term | 当前会话最近消息、摘要 | 天/周 | session/message/summary store |
| Episodic | 历史消息、事件 | 长期/按保留策略 | append-only message store |
| Semantic | 用户偏好、事实、关系 | 长期 | memory store |
| Procedural | persona、规则、工具定义 | 版本化 | config/plugin |

### 8.2 作用域

```text
Global（bot 配置）
  └── Guild/Workspace
        └── Conversation（channel/thread/DM）
              └── User/Actor
```

规则：

- DM 的私人记忆不能泄漏到 guild；
- guild 共享记忆不能自动变成用户私人记忆；
- 跨平台身份默认不合并；
- 只有显式 link 后才共享用户级记忆；
- 读记忆先按 scope 过滤，再按相关性排序。

### 8.3 数据模型

```csharp
public sealed record MemoryItem
{
    public required string MemoryId { get; init; }
    public required MemoryScope Scope { get; init; }
    public required string ScopeId { get; init; }
    public string? SubjectId { get; init; }

    public required MemoryKind Kind { get; init; }
    public required string Text { get; init; }
    public string? StructuredJson { get; init; }

    public double Confidence { get; init; }
    public MemorySensitivity Sensitivity { get; init; }
    public bool UserConfirmed { get; init; }

    public IReadOnlyList<string> SourceMessageIds { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public DateTimeOffset? LastUsedAt { get; init; }
    public long Version { get; init; }
}
```

`MemoryKind`：

- Preference；
- Fact；
- Relationship；
- Decision；
- Commitment；
- TaskResult；
- ChannelNorm。

### 8.4 写入策略

优先级：

1. 用户显式说“记住…”；
2. 用户明确偏好/事实；
3. 高置信度摘要；
4. 模型推断（默认不自动写）。

规则：

- 不保存密钥、token、密码、支付信息；
- 敏感信息需要确认；
- 冲突时保留最新 + 来源 + confidence；
- 去重/合并，不无限追加；
- 用户可删除/导出；
- 记录 provenance（source message ids）。

### 8.5 读取策略

- scope 过滤；
- 相关性 + 最近使用 + confidence；
- top-K，token 上限；
- 不把整段历史塞进 prompt；
- 不跨用户/跨频道泄漏；
- 记忆项带来源，便于解释。

### 8.6 存储选择

| 场景 | 建议 |
| --- | --- |
| 单机/桌面/Android | SQLite |
| 服务端单实例 | SQLite 或 Postgres |
| 多实例 | Postgres + Redis（热缓存/lease） |
| 大对象/附件 | 对象存储 |
| 测试 | In-memory |

最小表：

```text
conversations
messages
summaries
memory_items
tasks
task_events
usage_records
```

消息表 append-only；摘要/记忆可更新但带版本。

### 8.7 一致性与恢复

- 消息先落库，再发事件；
- 会话序列号单调递增；
- 摘要基于消息范围生成；
- 写记忆与写消息分开事务/补偿；
- 幂等键防重复；
- 多实例用 lease/row version；
- 记忆服务不可用时降级为最近消息，不阻塞回复；
- 写失败进入重试队列，不丢消息。

### 8.8 保留与隐私

- 每类数据有 TTL；
- 用户删除/导出；
- 加密 at rest；
- PII 最小化；
- 日志不记录正文/记忆内容；
- 跨平台 link 需要用户确认；
- 记忆检索结果不进入公共日志。

---

## 9. 与现有 Natter 的映射

| 目标组件 | 当前 | 下一步 |
| --- | --- | --- |
| Transport | Discord transport + NPS | 新平台作为 transport plugin |
| NLMP ingress | RxChatEventHub / ChatIngressLifecycle | 保持 |
| Control plane | ChatOrchestrator | 加 TriggerPolicy、SessionRegistry |
| Responder | IChatResponder + DeepSeek | 保持，加 ContextAssembler |
| Task worker | 无 | 长任务阶段引入 |
| Session | 无显式模型 | 先落 ConversationSession |
| Memory | DeepSeek 内存历史 | 抽 IConversationStore + IMemoryStore |
| Context | 最近消息 | ContextAssembler + summary |
| Provider | M.E.AI DeepSeek | 第二 provider 前拆 ai.provider |
| Persistence | 内存/文件 | SQLite → Postgres |
| Ops | CLI/status | serve + health + metrics |

---

## 10. 可观测性

每个请求带：

```text
eventId
sessionId
taskId（如有）
source
conversationId
providerRequestId
```

指标：

- ingress rate / drop rate；
- policy decision distribution；
- responder latency p50/p95；
- provider latency / error rate；
- prompt tokens / completion tokens / cache hit rate；
- summary tokens / compression ratio；
- memory retrieval count / hit rate；
- task duration / failure / retry；
- queue depth / lease expiry。

日志不记录：

- API key；
- 完整 prompt；
- 完整记忆内容；
- 用户隐私正文。

---

## 11. 安全

- 平台消息是不可信输入；
- 工具调用必须白名单 + 预算 + 审计；
- Worker 不默认拿到宿主权限；
- 记忆 scope 强制过滤；
- 跨平台身份 link 必须显式；
- 主动行为必须 opt-in；
- 限流/防刷/防自循环；
- 插件权限是治理，不是沙箱；
- 高权限 Worker 需要 out-of-process 宿主。

---

## 12. 失败与恢复

| 故障 | 行为 |
| --- | --- |
| Provider 429/5xx | 有界重试 + 降级文案 |
| Provider 超时 | 取消 + 错误回复 |
| 记忆库不可用 | 最近消息降级，不阻塞 |
| 摘要失败 | 使用最近窗口，稍后重试 |
| Worker 卡死 | lease 过期 + watchdog 取消 |
| 进程重启 | 从 session/task store 恢复 |
| 重复事件 | ingress + command idempotency |
| 平台断线 | transport 重连；控制面不重复回复 |

---

## 13. 实施阶段

### Phase 0：现状（已完成）

- Discord transport；
- DeepSeek responder；
- NLMP；
- NPS；
- live 测试 L1–L5。

### Phase 1：生产化底座

> P2 的具体开发规格见 [P2 Runtime & Profile Foundation](P2_RUNTIME_PROFILE.md)。

- `serve`；
- 配置目录/secret；
- SQLite/文件 store；
- health/metrics；
- graceful shutdown；
- session actor。

### Phase 2：上下文与摘要

> P3 的实现规格见 [P3 Context & Memory](P3_CONTEXT_MEMORY.md)。

- `ContextAssembler`；
- token budget；
- 增量摘要；
- provider cache 指标；
- 会话版本/并发。

### Phase 3：第二平台

> P4 的产品化计划见 [P4 Tier 1 Productization](P4_TIER1_PRODUCTIZATION.md)。

> 钉钉 + 星火的验证计划见 [第二上游/消费者验证](SECOND_ADAPTER_VALIDATION.md)。

- 新 transport plugin；
- canonical identity；
- 复用 ChatResponder；
- capability 降级；
- 验证 core 是否需要改。

### Phase 4：Task Worker

- `AgentTask`；
- worker runtime；
- 工具白名单；
- checkpoint/lease/watchdog；
- 结果回控制面。

### Phase 5：长期记忆

- `MemoryItem`；
- 显式记忆写入；
- scope 过滤；
- SQLite/Postgres；
- 删除/导出/保留策略。

### Phase 6：检索增强

- embedding；
- hybrid retrieval；
- rerank；
- 记忆冲突/衰减；
- 成本/隐私评估。

### Phase 7：进程隔离与生态

> 依赖与类型演进策略见 [Dependency & Type Evolution Policy](DEPENDENCY_EVOLUTION_POLICY.md)。

- out-of-process worker；
- Android；
- 插件市场；
- 多实例。

---

## 14. 验收标准

- [ ] 普通聊天不创建 Worker；
- [ ] Session/Task 模型有持久化；
- [ ] 同一会话并发消息串行，回复不乱序；
- [ ] 重启后会话/任务可恢复；
- [ ] ContextAssembler 有 token 预算和版本；
- [ ] 摘要失败不阻塞回复；
- [ ] 记忆 scope 隔离，DM 不泄漏到 guild；
- [ ] 用户可删除记忆；
- [ ] 第二平台只新增 transport，不改 core；
- [ ] provider 切换不改 responder；
- [ ] 日志无 key/prompt/隐私正文；
- [ ] 长任务有 lease/watchdog/取消；
- [ ] 主动行为默认关闭。

---

## 15. 开放问题

- canonical identity 是否跨平台合并，合并的确认流程；
- 摘要用哪个模型，成本/质量如何权衡；
- 记忆写入是“模型提议 + 用户确认”还是规则提取；
- SQLite 是否足够支撑服务端单实例；
- Task Worker 的隔离级别：进程、容器还是仅逻辑隔离；
- 跨平台会话是否共享上下文；
- 记忆检索是否引入 embedding；
- 多实例的 lease 方案；
- 记忆保留/删除的合规要求；
- 是否支持用户导出全部记忆。

---

## 16. 一句话结论

**控制面负责“要不要处理、交给谁、怎么回复”，执行面负责“生成或执行”；Session/Task 负责连续性，Context/Memory 负责“这次看到什么、长期记住什么”。**

Super-agent 的 Router/Worker 可以作为长任务阶段的参考，但 Natter 的第一优先级是：**Session/Task 显式化、上下文可预算、记忆可持久化、平台可替换**。


## 附：P3 后的共享契约

P3 的 `ContextBundle` 在 host contract 中暴露 `Microsoft.Extensions.AI.ChatMessage`，因此
`Microsoft.Extensions.AI` 与 `Microsoft.Extensions.AI.Abstractions` 已加入
`PluginContractAssemblies`。插件与 host 必须共享同一 M.E.AI 类型身份；provider SDK 仍由
插件私有加载。这个调整解决了文件插件加载时的 `ContextBundle.get_Messages` MissingMethod。
