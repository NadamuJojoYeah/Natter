# P5 Agent Task Runtime & Tools — Development Spec

> **状态**：P5.0–P5.2 foundation implemented（AgentTask/SQLite store、TaskDispatcher、clock tool、/task/status/tasks/cancel、lease/cancel/timeout、health task metrics；AI/tool 扩展与真实 Tier1 仍待后续）  
> **日期**：2026-11-17  
> **前置**：P4 功能 Go；P4.5 Conditional Pass（运维项并行收口）  
> **目标**：在现有 chat harness 上增加可持久化、可取消、可恢复的 AgentTask 与 Tool 执行面；普通聊天仍走低延迟 ChatResponder。  
> **关联**：[Harness 架构](HARNESS_ARCHITECTURE.md)、[P4 Tier 1 产品化](P4_TIER1_PRODUCTIZATION.md)、[Context & Memory](P3_CONTEXT_MEMORY.md)、[运维手册](OPERATIONS_RUNBOOK.md)

---

## 0. 一句话目标

> **P5 完成后，用户可以用 `/task` 提交一个需要工具/多步执行的任务；TaskWorker 异步执行，结果回到原会话；任务可取消、可超时、可重启恢复，且不阻塞普通聊天。**

P5 不是“每条消息都进 Worker”，也不是“第三个平台”。

---

## 1. 为什么现在做

P2/P3/P4 已经有：

- 多平台 transport；
- profile/provider 路由；
- SQLite 会话/记忆；
- slash 命令 UX；
- memory/context；
- serve/health/单实例/备份。

缺的是：

- 长任务；
- 工具调用；
- 任务状态、取消、恢复；
- Worker 隔离；
- watchdog。

P5 把这些补上，但不改变现有 chat 路径。

---

## 2. 范围

### 2.1 In scope

- `AgentTask` 模型与 SQLite 持久化；
- `TaskWorker` 运行时；
- `ITool` / `IToolPolicy` / 工具注册；
- `/task`、`/status`、`/cancel`、`/tasks`；
- 任务 lease、超时、取消、重启恢复；
- task-scoped context/memory；
- 只读工具优先；
- health/metrics/log；
- Tier 1 真实任务验收；
- Tier 2 smoke。

### 2.2 Out of scope

- 第三平台/Android/市场；
- 多实例集群；
- 向量检索/GraphRAG；
- 自动模型记忆提取；
- shell/文件写入/浏览器自动化工具（后续）；
- 多 Agent 协作；
- 插件市场；
- Worker 直接对用户发消息。

---

## 3. 架构位置

```text
Interaction / Message
        │
ChatOrchestrator
        │
        ├── ChatCommandRouter
        │      └── /task /status /cancel /tasks
        │
        ├── ChatResponder（普通聊天，低延迟）
        │
        └── TaskDispatcher
               ├── TaskStore
               ├── WorkerRuntime
               │      ├── IChatClient
               │      ├── ITool
               │      └── TaskContext
               └── TaskSupervisor（lease/watchdog）
        │
        ▼
ChatCommand → transport
```

原则：

- **普通聊天不创建 Task**；
- Task 结果由控制面统一回复；
- Worker 不直接调 Discord SDK；
- Task 状态独立于聊天历史；
- 任务上下文不污染普通 memory。

---

## 4. 核心模型

### 4.1 AgentTask

```csharp
public sealed record AgentTask
{
    public required string TaskId { get; init; }
    public required string SessionId { get; init; }
    public required Source Source { get; init; }
    public required string ConversationId { get; init; }
    public string? GuildId { get; init; }
    public string? ActorId { get; init; }

    public required string Prompt { get; init; }
    public required AgentTaskStatus Status { get; init; }

    public string? LeaseOwner { get; init; }
    public DateTimeOffset? LeaseExpiresAt { get; init; }

    public string? ResultText { get; init; }
    public string? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public int Version { get; init; }
}
```

### 4.2 状态机

```text
Pending
  → Running
  → WaitingForInput（可选，P5 暂不启用）
  → Completed
  → Failed
  → Cancelled
  → Expired（lease 超时）
```

规则：

- 状态转换必须持久化；
- `Completed/Failed/Cancelled` 为终态；
- 终态不可回退；
- 每次转换递增 `Version`；
- 所有更新带 `WHERE version = @expected` 乐观锁。

### 4.3 任务事件

```text
task_events(
  task_id,
  seq,
  kind,          -- created/started/tool/result/failed/cancelled
  payload_json,  -- 脱敏
  created_at
)
```

用于审计和恢复，不用于 prompt 正文。

---

## 5. TaskStore

```csharp
public interface ITaskStore
{
    ValueTask<AgentTask> CreateAsync(AgentTask task, CancellationToken ct = default);
    ValueTask<AgentTask?> GetAsync(string taskId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentTask>> ListActiveAsync(string sessionId, CancellationToken ct = default);
    ValueTask<bool> TryAcquireLeaseAsync(string taskId, string owner, TimeSpan ttl, CancellationToken ct = default);
    ValueTask<bool> TransitionAsync(string taskId, AgentTaskStatus expected, AgentTaskStatus next, int expectedVersion, CancellationToken ct = default);
    ValueTask SaveResultAsync(string taskId, string resultText, CancellationToken ct = default);
    ValueTask<IReadOnlyList<AgentTask>> ClaimExpiredLeasesAsync(DateTimeOffset now, CancellationToken ct = default);
}
```

SQLite 表：

```sql
CREATE TABLE agent_tasks (
    task_id         TEXT PRIMARY KEY,
    session_id      TEXT NOT NULL,
    source_key      TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    guild_id        TEXT NULL,
    actor_id        TEXT NULL,
    prompt          TEXT NOT NULL,
    status          TEXT NOT NULL,
    lease_owner     TEXT NULL,
    lease_expires_at TEXT NULL,
    result_text     TEXT NULL,
    error_code      TEXT NULL,
    error_message   TEXT NULL,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    version         INTEGER NOT NULL
);

CREATE INDEX ix_agent_tasks_session ON agent_tasks(session_id, status);
```

迁移：

- 复用现有 SQLite `user_version`；
- 新增版本号；
- 迁移幂等；
- state 700 / DB 600。

---

## 6. WorkerRuntime

```csharp
public interface ITaskWorker
{
    ValueTask<TaskResult> ExecuteAsync(
        AgentTask task,
        TaskExecutionContext context,
        CancellationToken cancellationToken = default);
}

public sealed record TaskExecutionContext(
    RuntimeProfile Profile,
    Conversation SessionConversation,
    IReadOnlyList<MemoryItem> Memories,
    int MaxToolCalls,
    int MaxTokens,
    TimeSpan Timeout);
```

要求：

- 一个 task 对应一个隔离上下文；
- Worker 只能通过 `ITool` 访问外部能力；
- Worker 不直接发平台消息；
- Worker 结果只回控制面；
- Worker 不写普通 memory，除非显式工具/结果摘要策略允许；
- Worker 输出必须经过 host 校验/脱敏。

---

## 7. Tools

### 7.1 契约

```csharp
public interface ITool
{
    string Name { get; }
    string Description { get; }
    ToolCapabilities Capabilities { get; }
    ValueTask<ToolResult> ExecuteAsync(ToolRequest request, CancellationToken ct = default);
}

[Flags]
public enum ToolCapabilities
{
    None = 0,
    ReadOnly = 1,
    Network = 2,
    FileRead = 4,
    FileWrite = 8,
    Shell = 16,
}
```

### 7.2 策略

```csharp
public interface IToolPolicy
{
    bool IsAllowed(ITool tool, RuntimeProfile profile);
    int MaxCallsPerTask { get; }
    TimeSpan Timeout { get; }
}
```

默认：

- 只允许 `ReadOnly`；
- 不允许 `Shell`/`FileWrite`；
- 网络工具需显式 profile 开启；
- 每 task 工具调用次数上限；
- 每次调用超时；
- 工具输出限长/脱敏。

### 7.3 P5 首批工具

建议只做：

- `clock`（当前时间）；
- `http-fetch`（只读、域名 allowlist、大小限制）；
- `text-transform`（纯函数）。

不做：

- shell；
- 文件写入；
- 浏览器自动化；
- 支付/账号操作。

---

## 8. 任务调度与控制面

### 8.1 `/task`

```text
/task prompt:<text>
```

流程：

1. 命令 router 创建 `AgentTask`（Pending）；
2. 3s 内 ephemeral ACK：`Task accepted: task_xxx`；
3. TaskDispatcher 入队；
4. Worker 执行；
5. 完成后控制面发送结果；
6. 失败/取消发送降级消息。

### 8.2 `/status` / `/tasks` / `/cancel`

- `/status <taskId>`：状态、耗时、结果摘要；
- `/tasks`：当前 session 的 active task；
- `/cancel <taskId>`：只有创建者/管理员；取消 token。

### 8.3 控制面

新增：

```csharp
public interface ITaskDispatcher
{
    ValueTask<string> EnqueueAsync(AgentTask task, CancellationToken ct = default);
}
```

TaskDispatcher 负责：

- 入队；
- 启动 Worker；
- 结果回 `ChatCommandFactory`；
- 失败/取消；
- 与 session actor 串行。

---

## 9. 并发、取消、恢复

### 9.1 lease

- 每个 Worker 运行前获取 lease；
- lease TTL 例如 2 分钟；
- 运行中续租；
- lease 过期的 Running task 由 supervisor 标记 `Expired`/重新排队；
- 多实例时 lease 防止重复执行。

### 9.2 取消

- `/cancel` 设置取消标记；
- Worker 的 `CancellationToken` 被取消；
- 工具必须尊重 cancellation；
- 已完成的 task 不能取消；
- 取消结果写 task_events。

### 9.3 重启恢复

- Pending 任务重新入队；
- Running + lease 未过期：等待 lease 过期；
- Running + lease 过期：标记 Expired 或重试；
- 已 Completed 不重复执行；
- 结果通过 idempotency key 防止重复发送。

---

## 10. Context / Memory

- Task 使用 task-scoped context；
- 不把 Worker 中间步骤写进普通聊天历史；
- 只把最终结果摘要写回会话；
- memory 读取 scope 与普通 responder 一致；
- 不自动把工具结果写入长期 memory；
- Task 自己的 checkpoint 存 task store，不存 memory store。

---

## 11. 安全

- Tool allowlist；
- 网络域名 allowlist；
- 文件/命令默认禁止；
- 每 task 预算；
- 工具输出脱敏；
- 不把 Discord 文本直接拼成 shell/URL；
- 高风险 tool 需要显式 profile 开启；
- Worker 不拿宿主 root DI；
- 第三方 Worker/Tool 后续 out-of-process；
- 日志不含 prompt/tool 原始输出/secret。

---

## 12. 可观测性

health 增加：

- active tasks；
- pending tasks；
- expired leases；
- failed tasks；
- task duration p50/p95；
- tool calls/errors；
- worker restarts。

指标：

- `agent_tasks_created_total`
- `agent_tasks_completed_total`
- `agent_tasks_failed_total`
- `agent_task_duration_seconds`
- `agent_tool_calls_total`
- `agent_tool_errors_total`

日志字段：

```text
taskId
sessionId
source
status
tool
duration
errorCode
```

不记录 prompt 正文/工具原始响应。

---

## 13. 测试

### 13.1 单元

- 状态机转换；
- 乐观锁；
- lease acquire/expire；
- cancellation；
- timeout；
- tool policy；
- context budget；
- 重启恢复。

### 13.2 Fake Worker/Tool

- `/task` → Pending → Running → Completed；
- 工具调用次数上限；
- 工具失败 → Failed；
- 取消 → Cancelled；
- lease 过期 → Expired/重排；
- 结果只发一次。

### 13.3 真实 Tier 1

- `/task` + read-only tool；
- 结果回 Discord；
- 普通 `/ask` 不受影响；
- 重启后 task 状态；
- 取消；
- 超时。

### 13.4 Tier 2 smoke

- 钉钉 `/task` 不支持时，capability 降级；
- 不影响 Discord。

---

## 14. 实施阶段

### P5.0

- AgentTask / TaskStore / 状态机；
- SQLite migration；
- fake worker/tool；
- 245+ 回归。

### P5.1

- `/task` 创建 + ACK；
- 只读 `clock` 工具；
- 结果回原会话；
- 取消。

### P5.2

- lease/watchdog；
- timeout；
- 重启恢复；
- 幂等发送。

### P5.3

- `http-fetch` / 文本工具；
- tool policy；
- 预算；
- 输出脱敏。

### P5.4

- `/status` `/tasks` `/cancel`；
- health/metrics；
- 真实 Tier1 验收。

### P5.5

- Tier2 smoke；
- 文档/运维；
- P5 签收。

---

## 14.1 本轮实现状态

| 项 | 状态 | 文件 |
| --- | --- | --- |
| AgentTask/状态机 | 已实现 | `Application/Chat/Tasks` |
| SQLite migration 3 | 已实现（agent_tasks/task_events） | `SqliteContextStores.cs` / `SqliteTaskStore.cs` |
| ITaskStore | 已实现（内存 + SQLite） | 同上 |
| TaskDispatcher | 已实现（lease/cancel/timeout/结果回原会话） | `TaskDispatcher.cs` |
| Tool 契约/策略 | 已实现只读 clock tool + ReadOnlyToolPolicy | `Tools` |
| `/task` `/status` `/tasks` `/cancel` | 已实现并注册 Discord slash schema | `ChatCommandRouter` / `DiscordSocketTransport` |
| health task metrics | 已实现 created/completed/failed/cancelled/active | `RuntimeHealth` |
| 重启恢复 | Pending 重新入队、过期 Running 标记 Expired、30s lease watchdog | `TaskRecoveryLifecycle.cs` |
| 工具 | clock / text-transform / http-fetch（allowlist + 网络策略） | `Tools` |
| AI-backed worker | 已实现（复用 ProfileAware responder；无 provider 时降级 deterministic clock worker） | `AiTaskWorker.cs` |
| 真实 Tier1/Tier2 task | 未执行 | — |

测试：`Natter.Framework.Tests` **253/253**；AI worker 复用 ProfileAware，无 provider 时走 clock tool fallback。

## 15. 验收标准

- [ ] 普通 `/ask` 不创建 Task；
- [ ] `/task` 创建、执行、结果回原会话；
- [ ] 取消/超时/失败状态正确；
- [ ] 重启后 Pending/Running/Completed 状态正确；
- [ ] 工具权限/白名单生效；
- [ ] Worker 不直接发平台消息；
- [ ] 工具输出不进入普通 memory；
- [ ] task 结果只发送一次；
- [ ] health/metrics 可见；
- [ ] 245+ 回归；
- [ ] Tier1 真实任务通过；
- [ ] Tier2 smoke；
- [ ] 无密钥/正文泄漏。

---

## 16. P4.5 并行运维项

P5 设计/开发期间，P4.5 剩余项不能丢：

- Dockerfile 插件 staging + `.dockerignore`；
- systemd root 实机；
- 24h soak；
- OBS-019 `lastDisconnectError`；
- Discord 3s interaction 低延迟出口；
- secret 轮换。

P5 上线前必须收口。

---

## 17. 一句话

**P5 是 Agent Task Runtime，不是每条消息进 Worker，也不是第三个平台。**

先做：

```text
AgentTask + TaskStore + 一个只读 tool + /task + 取消/超时/重启
```

再考虑更多工具和多 Agent。
