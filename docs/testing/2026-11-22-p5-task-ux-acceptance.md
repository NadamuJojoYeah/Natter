# 2026-11-22 · P5.3 task trace 与 task UX 独立验收

> **执行**：qa（task-22）
> **基线**：`HEAD=6093558`（P5.3 落地：`6280423` trace/可观测性、`b97e7cf` /status /tasks /cancel、`6093558` 实现测试）
> **被测对象**：`ITaskStore.AppendEventAsync`（InMemory + Sqlite）、`TaskTracePayload` 白名单、`AiTaskWorker` 的 started/step/tool/final 写入、`ChatCommandRouter` 的 `/status` `/tasks` `/cancel`、`TaskMetrics` 新字段
> **新增测试**：`tests/Natter.Framework.Tests/P5TaskUxAcceptanceTests.cs`（**16 个用例**，与实现测试 `P5TaskUxTests.cs` 分开）
> **关联**：[P5 follow-up 回归](2026-11-21-p5-followup-regression.md)、[P5 验收计划](2026-11-18-p5-acceptance.md)

---

## 1. 结论

| 项 | 结果 |
| --- | --- |
| 新增验收用例 | **16/16 通过**（连跑 3 次全绿） |
| 框架全量 | **537/537 通过**（521 基线 + 16；6 次运行中 1 次出现**既有**用例 flaky，见 §5） |
| WebConsole 回归 | **303/303 通过** |
| trace 序列（InMemory / Sqlite） | **通过**：`started → step → tool → step → final`，seq 单调且唯一；Sqlite 重开后仍完整 |
| trace 内容隔离 | **通过**：store 事件、日志、`/status`、`/tasks` 均不含 prompt / 工具输入 / 工具输出 / secret 哨兵 |
| `AppendEventAsync` 失败 | **通过**：任务照常完成、结果只发一次、日志只含 kind 与异常类型 |
| `/status` | **通过**：全字段、无 trace 降级 `trace: none`、结果 500 字截断、他人/不存在任务不泄漏 |
| `/tasks` | **通过**：`status:`/`limit:` 过滤、limit 上限 50、默认 20 行、3500 字符行数保护、空列表文案 |
| `/cancel` | **通过**：六类状态文案 + 非 owner 权限；取消后 `/status` 显示 `Cancelled` 与 `- final` |
| metrics | **通过**：ToolCalls/ToolTimeouts/DurationSamples/Total/Max/Average 随真实任务变化，旧字段无回归 |
| **缺陷** | **1 个 Medium**（重复 `EnqueueAsync` 会把运行中任务判 `Failed` 并污染 metrics），见 §4 |

判定：**P5.3 的验收要求全部满足（16/16）**；同时发现 1 个**既有**的重放缺陷（非 P5.3 引入、非产品正常路径），建议单独立项修复，见 §4。

---

## 2. 覆盖矩阵（对应 task-22 的 8 项要求）

| # | 要求 | 验收用例 |
| --- | --- | --- |
| 1 | 真实 InMemory + Sqlite 事件序列与 seq 单调 | `In_memory_trace_runs_started_step_tool_step_final_with_monotonic_seq`、`Sqlite_trace_is_ordered_persisted_and_rejects_unsafe_payloads` |
| 2 | 哨兵串扫描（store / 日志 / `/status` / `/tasks`） | `Trace_store_events_status_and_task_list_never_leak_task_content` |
| 3 | 抛错 store 装饰器下任务不受影响 | `Failing_append_event_keeps_the_task_successful_and_logs_no_content`、`Dispatched_task_completes_once_when_trace_writes_throw_and_status_degrades` |
| 4 | `/status` 全字段 / 降级 / 截断 / 不泄漏 | `Status_reports_every_p53_field_for_a_real_dispatched_task`、`Status_truncates_a_long_result_to_500_characters`、`Status_without_trace_degrades_and_never_leaks_other_or_missing_tasks`、`Status_reports_the_error_code_and_message_for_a_failed_task` |
| 5 | `/tasks` 过滤、上限、空列表 | `Tasks_filters_by_status_and_clamps_the_limit_to_fifty`、`Tasks_defaults_to_twenty_rows_and_guards_the_total_output_size`、`Tasks_reports_the_empty_message_and_rejects_an_unknown_status_filter` |
| 6 | `/cancel` 六类文案与权限、取消后 trace | `Cancel_returns_one_distinct_message_per_state_and_permission`、`Cancel_then_status_shows_cancelled_and_the_final_trace` |
| 7 | metrics 新字段与旧字段 | `Metrics_follow_a_real_task_run_and_keep_the_legacy_fields`、`Metrics_count_a_tool_timeout_and_the_task_fails_with_tool_timeout` |
| 8 | 回归 | 框架 537/537、WebConsole 303/303（§6） |

---

## 3. 关键证据

### 3.1 trace 序列

```text
真实 AiTaskWorker（脚本化 gateway：Call(clock) → Answer）+ 真实 store
InMemory: created, started, step, tool, step, final
          seq 唯一且严格递增；final.payload = {"status":"completed","ms":…,"tools":1}
Sqlite  : 同上；重建 SqliteTaskStore 后事件仍完整（持久化）
```

- `tool` 事件 payload 只含 `name/ms/code/chars/truncated`；`final` 只含 `status/code/ms/tools`；
- `step` 事件 `step` 从 **0** 起（`step=0` 首个模型轮次），`/status` 显示最后一个 step 值（两轮 → `step=1`）。

### 3.2 内容隔离（哨兵扫描）

哨兵：`QA_PROMPT_SENTINEL-4a1b`、`QA_TOOL_INPUT_SENTINEL-9c2d`、`QA_TOOL_OUTPUT_SENTINEL-7e3f`、`sk-qa-secret-sentinel-5d8e`
（提示串与 secret 同时进入 prompt；工具输入/输出各自带哨兵）

| 通道 | 结果 |
| --- | --- |
| store 事件 payload | 4 个哨兵全部 0 次命中 |
| `/status` 输出 | 0 次 |
| `/tasks` 输出 | 0 次 |
| worker 日志 | 0 次 |
| payload 键名 | 仅 `status/budget/step/protocol/toolCall/name/ms/code/chars/truncated/tools`，无 `prompt/text/input/output/args/result/secret/token` |
| payload 值类型 | 全部为标量（string / number / bool） |

> 说明：工具输出作为 observation 回传给**模型**是产品语义，因此不在"隔离"断言范围内；被断言的四个通道（store/日志/`/status`/`/tasks`）均干净。

### 3.3 `AppendEventAsync` best-effort

```text
装饰器 store：AppendEventAsync 恒抛 InvalidOperationException("trace store down")
→ worker 结果 Ok，text=answer，AppendAttempts>0
→ 日志："task trace write failed kind=started error=InvalidOperationException"（不含任何内容哨兵）
→ 经真实 TaskDispatcher 运行：任务 Completed、结果只发 1 条、/status 降级为 "trace: none"
→ metrics.Completed=1（任务结局未被 trace 故障影响）
```

### 3.4 `/status`

真实 dispatcher 跑完任务后的完整回复形态：

```text
task-status Completed v2
elapsed=72ms step=1 tools=1/3 remaining=2
result: final answer
trace:
- started 09:52:0x
- step 09:52:0x
- tool 09:52:0x clock tool_ok
- step 09:52:0x
- final 09:52:0x completed
```

- 900 字结果 → `result: ` + 500 字 + `...`，整体仍受 1500 字上限；
- 无 trace → `trace: none` 且 `step=-`、`tools=0/3`；
- 他人任务与不存在任务都只回 `Task not found.`，不含 taskId/内容；
- 失败任务：`error=tool_timeout: Tool 'clock' timed out.`。

### 3.5 `/tasks`

| 场景 | 结果 |
| --- | --- |
| 59 个 active 任务 + `limit:999` | **50 行**（上限生效） |
| `status:running` + `limit:50` | 只 4 行（Running），无 Pending |
| 默认（无过滤） | **20 行** |
| 50 行长 id 任务 | 输出 ≤ 3503 字符并以 `...` 结尾（行数保护生效） |
| 无任务 / `status:waiting` | `No active tasks.` |
| `status:bogus` | `Invalid status filter 'bogus'. Use pending, running or waiting.` |

### 3.6 `/cancel`

| 输入 | 回复 |
| --- | --- |
| 不存在 | `Task not found.` |
| 他人任务 | `Not task owner.` |
| Completed / Failed / Cancelled / Expired | `Task already completed.` / `already failed.` / `already cancelled.` / `already expired.` |
| Running + dispatcher 接受 | `Cancel requested.` |
| Running + dispatcher 拒绝 | `Cancel is not available for this state.` |

终态与非 owner 场景 `RequestCancelAsync` 调用次数为 **0**（没有越过路由层）。
真实取消后 `/status`：`Cancelled`、`error=cancelled`、trace 含 `- final`，且只投递一条 `Task cancelled.`。

### 3.7 metrics

| 场景 | Snapshot |
| --- | --- |
| 真实任务（1 次工具调用） | created=1、completed=1、failed=0、cancelled=0、active=0、ToolCalls=1、ToolTimeouts=0、DurationSamples=1、Total=Max=Average |
| 工具超时（policy 30ms + 慢工具） | 任务 Failed（`tool_timeout`）、ToolTimeouts=1、final trace 的 `tools=0` |

---

## 4. 缺陷（需 engineer 处理，本轮未改 src）

### BUG-P53-REPLAY-ENQUEUE（Medium，既有，非 P5.3 引入）

**复现**（`TaskDispatcher` + `InMemoryTaskStore` + 阻塞 worker）：

```csharp
await runtime.EnqueueAsync(task);                 // 第一次：Pending → Running，worker 阻塞
await runtime.EnqueueAsync(task);                 // 第二次：同一 AgentTask 再入队
```

**实测诊断**（原样输出）：

```text
cancelAccepted=False status=Failed error=task_failed commands=1
created=2 completed=0 failed=1 cancelled=0 active=1
```

**两个后果**：

1. **运行中的任务被误判 Failed**：第二次执行的 `TransitionAsync(Running, created.Version=0)` 版本冲突，
   落入 `TaskDispatcher` 的通用 `catch (Exception)`，把**当前仍在运行**的任务改成 `Failed`（`error=task_failed`），
   随后 `RequestCancelAsync` 返回 false（任务已是终态）。
2. **metrics 被污染**：`EnqueueAsync` 每次都会 `metrics.Created()`（`created=2`），但只有一次终态递减，
   所以 `active=1` 永久泄漏；`created` 虚高。

**归属**：两条路径在 `6280423`（P5.3）之前就存在；P5.3 新增的 `metrics.Duration()`/`metrics.Failed()`
同样落在 `if (current … not terminal)` 之外，属同一类结构问题（终态判断只包住 `TransitionAsync`），
因此本次观察可作为修复时的合并项，不在 P5.3 验收里算作回归。

**可触达性**：产品正常路径不重放 `Enqueue`（router 每次生成新 taskId），但 `TaskRecoveryLifecycle.StartAsync`
对同一批 Pending 任务重复启动（插件重载 / 双 lifecycle）时会把同一 Pending 入队两次，可命中该缺陷。

**建议**（不改测试以"通过"方式固化缺陷，故本用例仅存在于报告中）：
- `EnqueueAsync` 对已在 `_cancellations` 中的 taskId 幂等返回，或先尝试 `Transition(Running)` 再执行；
- `Created()` 与 `EnqueueAsync` 的重复调用解耦（或在 store 侧判断首次入队）；
- 终态判断与 metrics 递减放进同一 `if` 分支（`Duration`/`Cancelled`/`Failed` 一并受 `current` 终态保护）。

---

## 5. 观察项

| ID | 级别 | 内容 |
| --- | --- | --- |
| OBS-P53-FLAKY-HOST-WIRING | Low | 既有用例 `ChatCommandRouterTests.Host_composition_wires_task_runtime_into_router` 在 6 次全量并行运行中失败 1 次（单独跑与其余 5 次均通过）；与本任务新增用例无关，建议把该用例的宿主装配断言改为等待式 |
| OBS-P53-STEP-INDEX | Info | `step` 事件从 0 开始（首个模型轮次 `step=0`），`/status` 显示的是最后一个 step 值；若希望 UI 显示"第几步"需 +1，属语义选择，不算缺陷 |
| OBS-P53-OBSERVATION | Info | 工具输出会作为 observation 回传给模型（产品语义）；报告因此不把"模型侧 observation"纳入泄漏通道 |

---

## 6. 回归命令与结果

```bash
# 新增验收（16 用例，连跑 3 次）
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~P5TaskUxAcceptanceTests"
# Passed! - Failed: 0, Passed: 16, Total: 16   (×3)

# 框架全量
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
# Passed! - Failed: 0, Passed: 537, Total: 537   (6 次中 5 次；1 次为 §5 的既有 flaky)

# WebConsole
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj
# Passed! - Failed: 0, Passed: 303, Total: 303
```

P5 既有实现测试 `P5TaskUxTests.cs` 全绿（含在 537 内），无回归。

---

## 7. 未测 / 范围外

- 真实 Discord 上的 `/status` `/tasks` `/cancel` 呈现（本轮为 router 层 + 真实 store/dispatcher；Discord 端到端属人工项）。
- WebConsole 侧对新 metrics 字段的展示（不在 P5.3 范围，WebConsole 303/303 仅作回归）。
- 超长 trace 列表（>5 条）在 `/status` 的截断形态：只验了"取最近 5 条"的实现路径（`TakeLast(5)`），未构造单任务 >5 条 trace 的端到端展示。
- 生产部署、`/opt/natter`、soak 主机：按任务要求未触碰，未部署、未重启、未 commit/push。
