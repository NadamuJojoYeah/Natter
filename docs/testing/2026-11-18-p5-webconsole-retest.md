# 2026-11-18 · P5 + WebConsole 修复复测（测试侧）

> **基线**：`HEAD=1593283`（修复 `15ba749` / `2e2e6a5` / `a3ab3c0`）
> **证据**：`artifacts/retest/2026-11-18-2022/`

## 1. 回归基线

| 项 | 结果 |
| --- | --- |
| 构建 | 通过 |
| 框架回归 | **263/263** |
| WebConsole 单测 | **124/124** |

## 2. P5：BUG-P5-SCHEMA-COMMANDS（已关闭）

- 重新 staging 最新构建后，`plugins config validate natter.transport.discord`（9 命令）→ **`valid:true`**；
- 启动 P5 实例：`healthy`、7 plugins、双 upstream connected；
- guild commands 注册 **9 个**：`ask/remember/forget/memory/reset/task/status/tasks/cancel`；
- health `tasks={created:0,completed:0,failed:0,cancelled:0,active:0}`；
- **P5 真实 slash 待人工**（`/task` `/status` `/tasks` `/cancel`）。

### 2.1 真实 `/task` 复测失败：BUG-P5-ROUTER-WIRING（High）

用户 20:33 在 Discord 连续两次 `/task`，均收到 ephemeral：

```text
Task runtime is not available.
```

根因（代码）：`src/Natter.Infrastructure/Chat/ChatCompositionModule.cs` 注册 `ChatCommandRouter` 时**只传了 6 个构造参数**：

```csharp
new ChatCommandRouter(
    IMemoryStore, ISummaryStore, IConversationStore, ChatCommandFactory,
    IContextComponentCatalog, IProfileMemoryStoreResolver);
```

而 `ChatCommandRouter` 的第 7/8 个可选参数 `ITaskStore? tasks = null`、`ITaskDispatcher? taskDispatcher = null` 未注入 → 两者恒为 null → `TryTaskAsync` 直接返回 `Task runtime is not available.`。

- `ITaskStore` / `ITaskDispatcher` 本身已在 DI 注册（`ChatCompositionModule` 96-125 行）；
- health 也有 `tasks` 字段（TaskMetrics 注册）；
- 但路由拿不到它们，所以 `/task` `/status` `/tasks` `/cancel` 全部不可用。

**影响**：P5 真实命令链路完全阻塞（P5-010~018、P5-080~084），与 schema 无关，是 DI 接线遗漏。
**建议**：注册 `ChatCommandRouter` 时注入 `provider.GetRequiredService<ITaskStore>()` 与 `ITaskDispatcher`；或把这两个参数改为非可选并补一条「router 能处理 /task」的集成测试。
**证据**：`artifacts/retest/2026-11-18-2022/evidence/p5-serve3.log`、用户截图、代码行 127-133。
**落库核对**：实例 21:25 正常到期退出（exit 0）；期间 `sessions=0`、`messages=0`，SQLite 中无 task 行——两次 `/task` 都被路由提前拦截，未创建任务。

## 3. WebConsole 三个 Medium

| 缺陷 | 复测结果 |
| --- | --- |
| BUG-WC-TRUSTED-PROXY | **关闭（测试场景修正）**：`trustedProxies` 校验的是连接 remote IP；loopback 测试的 remote 是 127.0.0.1（受信），`X-Forwarded-*` 被采纳是预期行为。未受信 remote 无法在当前环境用 loopback 模拟。 |
| BUG-WC-PATH-ID | **关闭**：URL 穿越 `POST /api/config/../etc/passwd/validate` 被拒绝（404 `route.not_found`，不读文件）；body 的 `path` 字段本就不参与路由。错误码与文档 400 `config.id_invalid` 有偏差（Low）。 |
| BUG-WC-CMD-ARGS | **关闭**：请求字段是 `args`（此前测试误用 `arguments`）。`args=9` → 403 `command.invalid`（"args exceeds 8 items."）；含换行 → 403（"args contain control characters."）；合法命令仍返回 501 `host.admin_command.unavailable`。 |

## 4. P5 lease 修复

`a3ab3c0 fix: 让 P5 lease 覆盖 task 执行窗口`；框架 263/263 通过（含新增回归）。真实长任务 lease 仍待人工/运维。

## 5. 结论

- P5 schema 阻塞解除；`BUG-P5-ROUTER-WIRING` 已由工程修复（`ChatCompositionModule` 注入 `ITaskStore`/`ITaskDispatcher`，新增 router 测试，框架 **264/264**）。
- **真实链路复测通过**（2026-11-21 15:16）：
  - `/task prompt:用一句话说明你能做什么` → ephemeral `Task accepted: task_ee6bde68abf54543`；
  - 结果回原频道：「我能帮你简洁地回答各类问题、处理文本任务并提供实用建议。」（28 字）；
  - `/tasks` → `No active tasks.`；
  - `agent_tasks` 1 行 `Completed`、`result_text` 完整、`version=2`；`task_events` 3 条；
  - health `tasks={created:1,completed:1,failed:0,cancelled:0,active:0}`；
  - session 独立：`1123031326354841665#task:task_ee6bde68abf54543`。
- **P5 判定：Go**（`/ask` 不建 task、`/status`、`/cancel` 未在本次截图/数据中出现，列为未测；`/task` 主链路已闭环）。
- WebConsole：3 Medium 已关闭（见 §3），Phase 1 可 Go。
- WebConsole 3 Medium 全部关闭；Phase 1 可 Go（Phase 2/3 仍 501 语义）。
