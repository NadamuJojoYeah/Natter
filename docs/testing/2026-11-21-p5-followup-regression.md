# 2026-11-21 · P5 Go 后遗留缺口回归（/status /tasks /cancel、lease、重启幂等、工具安全、Tier 2）

> **执行**：qa（task-3）
> **基线**：`HEAD=af74820`（工作区含 engineer/ops 的未提交改动，见 §7）
> **被测对象**：`ChatCommandRouter` 的 `/task` `/status` `/tasks` `/cancel`、`TaskDispatcher`、`InMemoryTaskStore`/`SqliteTaskStore`、`TaskRecoveryLifecycle`、`HttpFetchTool`/`ReadOnlyToolPolicy`/`DefaultTaskWorker`、`ChatCapabilityDegrader`
> **新增测试**：`tests/Natter.Framework.Tests/P5FollowupTests.cs`（31 个测试方法 / 46 个用例）
> **关联**：[P5 验收计划](2026-11-18-p5-acceptance.md)、[P5 + WebConsole 复测](2026-11-18-p5-webconsole-retest.md)

---

## 1. 结论

| 项 | 结果 |
| --- | --- |
| 基线框架回归（改动前） | **264/264** |
| 新增 P5 follow-up 用例 | **46/46 通过**（单类连跑 5 次全绿） |
| 全量框架回归（含工程师同期新增用例） | **360/360 通过**（§4.2，取 16:35 运行时点） |
| `/status` `/tasks` `/cancel` 语义 | **自动化通过**，其中「只列自己的 active」「非创建者不可取消」符合预期 |
| `/ask` 不创建 task | **通过**（slash `/ask` 与非 slash 交互都不落 task） |
| lease 续租 / 过期 | **通过**（in-memory + SQLite 双 store） |
| 重启幂等 / 结果只发一次 | **通过**（Completed 不重发、Cancelled 不重放、stale Running 只判 Expired 不重排） |
| 工具安全矩阵 | **通过**；私网/元数据地址**即使写进 allowlist 也会被拒绝**——`HttpFetchTool.IsBlockedAddress` → `address_not_allowed`，由 `d9f47ba`（2026-11-21 18:01）修复；本记录写于该修复之前，原判「allowlist 内私网仍可访问（OBS-4）」**已失效**，见 §2.4 与 §5 的 OBS-P5F-4 |
| 日志无 prompt/secret | **通过**（运行期捕获 + 全量源码静态扫描） |
| Tier 2 降级 | **通过**（无 slash 能力时交互回包被拒，任务结果仍以纯文本回投） |
| 真实 Discord `/status` `/tasks` `/cancel` `/ask` | **待人工**（§6），未执行、不计入通过 |

判定：**P5 遗留缺口的自动化部分可签（Go）**；真实 Discord 命令面与跨进程重启仍需人工补一次，见 §6。

---

## 2. 用例矩阵（`P5FollowupTests.cs`）

### 2.1 命令面（Gate B）

| 测试 | 覆盖的验收 ID | 断言要点 |
| --- | --- | --- |
| `Status_reports_state_version_and_truncates_long_results` | P5-013、P5-076 | 回复含 taskId/`Completed`/`v2`，900 字结果截到 500+`...` |
| `Status_and_cancel_report_unknown_tasks_instead_of_throwing` | P5-017 | 未知 task → `Task not found`，不抛异常 |
| `Task_commands_ask_for_arguments_when_missing` | P5-011 | 空 prompt / 缺参数 → usage，且不落 task |
| `Tasks_lists_only_the_callers_active_tasks` | P5-014、P5-071 | 只列本 session 的 active；他人 actor、跨 source、已 Completed 均不出现 |
| `Cancel_is_refused_for_non_creators_and_terminal_tasks` | P5-015/016/023、P5-070 | 陌生人取消被拒且状态不变；创建者可取消；终态后 cancel 返回 false，结果仍只发 1 次 |
| `Ask_and_non_slash_interactions_never_create_tasks` | P5-018 | `/ask` 返回 null 落给 AI；Button/Select 交互不造 task |
| `Unknown_slash_command_prints_help_without_touching_the_task_store` | — | 未知命令回 9 条帮助，不写库 |
| `Task_runtime_missing_still_replies_unavailable_for_all_four_commands` | P5-012 | 无 task runtime 时四个命令统一 `Task runtime is not available.` |
| `Host_router_serves_status_tasks_and_cancel_after_the_wiring_fix` | BUG-P5-ROUTER-WIRING 回归 | 宿主装配的 router 能真实服务 `/status` `/tasks` `/cancel` |

### 2.2 lease（Gate C）

| 测试 | 覆盖的验收 ID | 断言要点 |
| --- | --- | --- |
| `Lease_renewal_keeps_the_owner_and_blocks_other_owners` | P5-025/027 | 同 owner 续租延长到期；他 owner 抢租失败；续租后过期清扫为空 |
| `Expired_lease_is_swept_and_can_no_longer_be_acquired` | P5-027 | Running + 过期 lease → `Expired`、version+1、不可再抢租 |
| `Dispatcher_lease_covers_the_worker_timeout_window` | P5-024/025、`a3ab3c0` 回归 | Running 时 `LeaseExpiresAt - now > 5m`，owner 为 `host:*`，清扫不会误杀 |
| `Sqlite_lease_survives_reopen_and_renews_for_the_same_owner` | P5-025/026 | 重开后 lease 持久化；他 owner 失败；同 owner 可续租 |
| `Sqlite_store_expires_a_running_task_after_its_lease_lapses` | P5-024/027 | SQL 路径同样清扫过期 Running |

### 2.3 重启幂等 / 结果只发一次（Gate C 尾部 + Gate F）

| 测试 | 覆盖的验收 ID | 断言要点 |
| --- | --- | --- |
| `Restart_recovery_requeues_pending_exactly_once_and_never_resends_completed` | P5-028/029/030、P5-065 | 首次启动只回收 Pending；事件序列 `created→running→completed`；重启后不重发、Completed 的旧结果保持原样 |
| `Cancelled_task_result_is_sent_once_and_never_replayed_after_restart` | P5-022/030 | 取消后只发一次 `Task cancelled.`；重启 recovery 不再入队、不再发 |
| `Recovery_expires_the_stale_running_lease_without_requeueing_it` | P5-029 | 崩溃遗留 Running → 只判 `Expired`，不重排；二次启动幂等 |

### 2.4 工具安全矩阵（Gate D + Gate G）

| 测试 | 覆盖的验收 ID | 断言要点 |
| --- | --- | --- |
| `Http_fetch_denies_by_default_and_matches_the_allowlist_exactly` | P5-043/072 | 未配置 allowlist 时全拒；大小写不敏感命中；子域、URL 内嵌 allowlist host 都不放行；只有放行那次真的发请求 |
| `Http_fetch_rejects_non_https_and_relative_urls`（8 组 InlineData） | P5-044 | `http/file/ftp/gopher/javascript/相对/协议相对/空` → `invalid_url`，零请求 |
| `Http_fetch_denies_loopback_private_and_metadata_hosts`（9 组 InlineData） | P5-073 | `127.0.0.1`、`localhost`、`169.254.169.254`、`10/192.168`、`[::1]`、`0.0.0.0`、`metadata.google.internal`、十进制 IP → `host_not_allowed`，零请求 |
| `Http_fetch_private_metadata_host_is_rejected_even_when_allowlisted` | P5-073 / OBS-4（**已修复**） | 私网/元数据地址即使写进 allowlist 也返回 `address_not_allowed` 且**零请求**（`d9f47ba` 起）。本记录原用例名 `Http_fetch_private_host_becomes_reachable_once_allowlisted_documented_gap` 已随修复改名并把断言反转 |
| `Tool_policy_decides_by_capability_not_by_name` | P5-048/072 | 按 capability 判定：Shell/FileWrite 一律拒，名字不可信；`MaxCallsPerTask=3` |
| `No_shell_or_file_write_tool_is_registered_in_the_host` | P5-048 | 宿主只注册 `clock`/`http-fetch`/`text-transform`，无 Shell/File* 能力；http 默认拒 |
| `Default_worker_reports_no_tool_when_network_tools_are_disabled` | P5-047/053 | `AllowNetworkTools=false` → `no_tool` 且零请求；开启后才走 http-fetch |
| `Task_failure_logs_carry_the_task_id_but_never_the_prompt_or_secrets` | P5-075 | 失败日志含 taskId，不含 prompt/secret 哨兵 |
| `Interaction_to_string_masks_the_reply_token` | P5-075 | `Interaction.ToString()` 把 reply token 打成 `***` |
| `Source_logging_never_interpolates_prompts_or_secrets` | P5-075 | 静态扫描 `src/**/*.cs` 全部 `.Log*` 语句，禁止出现 prompt/secret/token 等字样 |

### 2.5 Tier 2 降级

| 测试 | 断言要点 |
| --- | --- |
| `Tier2_source_without_slash_support_rejects_interaction_commands` | `SlashCommands=false` 的上游收到交互回包 → `unsupported`，且不向下游投递 |
| `Tier2_task_result_is_delivered_as_plain_text_without_interactions` | DingTalk 型能力下任务结果仍以 `MessageSendCommand` 纯文本回投，不依赖 slash/ephemeral |
| `Capability_degrader_drops_ephemeral_and_markdown_for_tier2` | `DegradeRespond` 去掉 ephemeral、markdown 转纯文本 |

### 2.6 health metrics

| 测试 | 覆盖的验收 ID | 断言要点 |
| --- | --- | --- |
| `Task_metrics_track_created_completed_and_active` | P5-063/064 | created=1、completed=1、active 归零 |

---

## 3. 关键行为证据

### 3.1 结果只发一次（重启）

```text
首次启动：只 Enqueue Pending(task_restart)，Completed 的历史 task 不重发
→ recorder 命令数 = 1，IdempotencyKey = task-result:<taskId>
→ task_events = created, running, completed
重启二次启动：ListRecoverable 为空 → 不再 Enqueue，recorder 仍 = 1
```

### 3.2 崩溃遗留 Running 不重排

```text
lease(crashed) → Running → 等 60ms 后 recovery.StartAsync
→ status = Expired（不 Enqueue、不发结果）
→ 再次 recovery.StartAsync 仍为空（幂等）
```

### 3.3 工具 URL 拒绝矩阵（实测错误码）

| 输入 | 结果 |
| --- | --- |
| `https://127.0.0.1:8080/admin` / `https://localhost/x` | `host_not_allowed`，零请求 |
| `https://169.254.169.254/latest/meta-data/` | `host_not_allowed`，零请求 |
| `https://10.0.0.7/x` / `https://192.168.1.10/x` / `https://[::1]/x` | `host_not_allowed`，零请求 |
| `https://metadata.google.internal/computeMetadata/v1/` | `host_not_allowed`，零请求 |
| `http://example.com/x` / `file:///etc/passwd` / `ftp://…` / `//example.com/x` | `invalid_url`，零请求 |
| `https://evil.example/?next=https://example.com` | `host_not_allowed`（URL 内嵌 host 不构成放行） |
| `https://www.example.com/x`（allowlist 只有 `example.com`） | `host_not_allowed`（精确匹配，无后缀通配） |
| `https://EXAMPLE.com/x`（allowlist `example.com`） | 放行（host 比较大小写不敏感） |

### 3.4 运行期实例（人工测试前提，只读采样 16:24）

```json
{"status":"healthy","uptimeSeconds":49,"profiles":2,"conversations":0}
tasks={"created":0,"completed":0,"failed":0,"cancelled":0,"active":0}
plugins={"activated":4,"failed":0,"failures":[]}
upstreams=[("discord:discord:1571154023493410836", true, null)]
```

实例在 `127.0.0.1:5099`，Discord connected、`tasks` 全 0（本轮未在该实例上执行真实命令）。

---

## 4. 回归命令与结果

### 4.1 新增用例单独跑（连跑 5 次）

```bash
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~P5FollowupTests"
```

```text
run 1: Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46
run 2: Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46
run 3: Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46
run 4: Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46
run 5: Passed! - Failed: 0, Passed: 46, Skipped: 0, Total: 46
```

### 4.2 全量框架回归

```bash
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

```text
改动前基线：Passed! - Failed: 0, Passed: 264, Total: 264
改动后全量：Passed! - Failed: 0, Passed: 360, Total: 360（含本任务 46 + engineer 同期新增）
```

> 全量总数随 engineer 同期在 `task-1` 新增的用例增长（264 → 356 → 357 → 359 → 360），因此以「本任务 +46、全绿」为准，绝对总数按运行时点记录。

---

## 5. 观察项（不阻塞签收，建议工程/运维确认）

| ID | 级别 | 内容 |
| --- | --- | --- |
| OBS-P5F-1 | Low | `ClaimExpiredLeasesAsync` 把任务判 `Expired` 时不清空 `lease_owner`/`lease_expires_at`（in-memory 与 SQLite 都是）。状态门禁已阻止再次抢租，属信息性残留。 |
| OBS-P5F-2 | Low | 取消/完成后的结果发送发生在后台 `finally` 中，晚于状态落库；自动化断言的观察窗口需等待式（本轮初版用固定 120ms 曾出现「状态已 Cancelled 但结果尚未投递」的假失败，改为等待后稳定）。若 `SendResultOnceAsync` 内 `store.GetAsync` 抛异常，`_resultsSent` 已置位会导致该任务结果永久丢失——本轮未复现，建议工程评估补偿。 |
| OBS-P5F-3 | Low | 既有用例 `TaskRuntimeTests.Cancel_before_terminal_state_marks_cancelled` 在全量并行负载下曾 1 次失败（`Assert.Single(dispatcher.Commands)` 时序），单独连跑 8 次全绿。建议把该断言改为等待式，避免 CI 偶发红。 |
| OBS-P5F-4 | Medium → **已修复（`d9f47ba`，2026-11-21 18:01）** | ~~SSRF 边界只有 host allowlist + HTTPS~~。现状：`HttpFetchTool` 在 allowlist 之外再加地址门禁——IP 字面量与 DNS 解析结果都要过 `IsBlockedAddress`（loopback/私网/链路本地/CGNAT/保留/组播/元数据、IPv6 `::1`/`fc00::/7`/`fe80::/10`），命中返回 `address_not_allowed` 且**零请求**；3xx 一律 `redirect_blocked`；宿主侧 hardened `HttpClient`（`AllowAutoRedirect=false`）并对实际连接地址再校验。回归用例：`P5FollowupTests.Http_fetch_private_metadata_host_is_rejected_even_when_allowlisted`、`ToolContractTests.Http_fetch_rejects_private_and_metadata_literals_without_a_request`、`Http_fetch_rejects_hostnames_that_resolve_to_private_addresses_without_a_request`。**运维仍应只把公网域名写进 `NATTER_HTTP_FETCH_HOSTS`**（纵深防御，不是替代） |

---

## 6. 待人工项（本轮未执行，不计入通过）

真实 Discord 命令无法由自动化执行；实例（`127.0.0.1:5099`，Discord connected）已就绪但本轮未在其上操作。

| 编号 | 待人工步骤 | 预期 |
| --- | --- | --- |
| M-1 | `/task prompt:<text>` 后立刻 `/status task:<id>` | 状态 `Running`/`Completed`、version 递增 |
| M-2 | `/tasks` | 只列自己的 active；完成后 `No active tasks.` |
| M-3 | 长任务期间 `/cancel task:<id>`（创建者） | `Cancel requested.`，频道只出现一条 `Task cancelled.` |
| M-4 | 用另一账号 `/cancel` 同一 task | `Task not found, completed, or not owned by you.` |
| M-5 | `/ask prompt:<text>` | 正常 AI 回复，`agent_tasks` 无新行、health `tasks.created` 不变 |
| M-6 | 任务执行中重启 `serve` | 重启后 `/tasks` 为空或状态 `Expired`，原频道**不出现第二条结果** |
| M-7 | 钉钉群普通文本 @ | 文本回复正常（Tier 2 不回归），不出现 slash 交互报错 |
| M-8 | 真实长任务（>2min）期间观察 health | 不被 watchdog 误判 `Expired`（lease 覆盖执行窗口） |

---

## 7. 未测 / 范围外

- 跨**进程**重启的重放：本轮用「新建 `TaskDispatcher` + `TaskRecoveryLifecycle` 共享同一 SQLite」模拟，未做真实 `serve` 进程重启（属 M-6）。
- 真实 AI provider 的 `/task` 结果质量：本轮用 fake worker / 直连 responder，不评价模型输出。
- 多工具 loop、多步 planner：P5 未实现，不在本任务范围。
- DNS rebinding / 重定向后私网跳转：**已由 `d9f47ba` 补上防护**（`AllowAutoRedirect=false` + 解析后地址校验 + `ConnectCallback` 校验实际连接地址，用例 `Http_fetch_rejects_hostnames_that_resolve_to_private_addresses_without_a_request`）；本轮未做真实 DNS rebinding 攻击验证，仍属未测（防护代码存在，不代表已抗攻击验证）。
- 基线工作区含 engineer（`src/Natter.Application/Admin`、`src/Natter.Infrastructure/Admin`）、ops（`deploy/`、`scripts/`）的未提交改动；本轮测试期间遇到两次 `src` 半成品编译失败，等待其恢复到 `Build succeeded` 后才取最终数据。
- 未执行 `git commit` / `git push`（按任务要求）。
