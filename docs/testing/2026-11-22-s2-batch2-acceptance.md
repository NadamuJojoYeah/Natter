# 2026-11-22 · S2 第二批独立验收（task-49，23 项）

> **执行**：qa（task-49）
> **基线**：`HEAD=eadcd77`（task-45 `c741a14`、task-46 `614d6d4`、task-47 `796985a`、task-48 `b1c8d67`/`34a4de9`）
> **交付**：`tests/Natter.Framework.Tests/S2Batch2AcceptanceTests.cs`（17 个方法 / 21 个用例）
> **状态**：**测试已交付，但本工作区无法编译验证** —— 见 §0 阻塞。

---

## 0. 结果速览（2026-11-22 20:0x 更新）

| 项 | 结果 |
| --- | --- |
| 本任务验收用例 | **15/15 通过**（构建修好后实际执行） |
| 框架全量（排除他人半成品 `StateVitalsTests`） | **892/892 ×3** 全绿 |
| WebConsole 全量 | **344/344** |
| 框架全量（含 `StateVitalsTests`） | 稳定失败于 `StateVitalsTests`（vitals agent 进行中，非本任务范围） |

过程中我修掉了自己文件里的 4 处真实 API 偏差（`PluginContributionDeclaration`→`PluginContribution`、`Kind` 需 `PluginKind` 枚举、`InstalledPluginIntegrity.Tampered`、`TryReserve` 的 `out ChatAck?`），并按产品实际语义调整了 3 处断言（见 §0.3）。

### 0.1 历史阻塞记录（已解除）

`tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj` 现在（未提交）引用了
`plugins/Natter.Plugin.StateVitals/Natter.Plugin.StateVitals.csproj`，而该插件是 vitals agent 正在写的半成品：

```text
StateVitalsRuntimeBuilder.cs(63): error CS0246: 'VitalDefinitionSet' could not be found
StateVitalsRuntime.cs(25/43/68/97/289): error CS0246: 'VitalDefinitionSet' could not be found
Storage/InMemoryStateVitalsStore.cs(8): error CS0535: does not implement 'IStateVitalsStore.FindDescriptionAsync'
Storage/SqliteStateVitalsStore.cs(19): error CS0535: 同上
→ dotnet build tests/Natter.Framework.Tests 失败（约 12 处 error CS），
  StateVitals 从未成功构建（obj/**/ref/*.dll 不存在），-p:BuildProjectReferences=false 也无法绕过
```

该阻塞在我最后一次重试时已解除（StateVitals 插件可编译），剩余红点是 `tests/Natter.Framework.Tests/StateVitalsTests.cs`
（vitals agent 的测试仍在写，`VitalDefinitionSet`/`GatedStore` 未实现）——按 lead 指示判为半成品，不改 src，用
`--filter "FullyQualifiedName!~StateVitalsTests"` 取本任务范围的绿灯。复跑命令：

```bash
dotnet build tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -t:Rebuild
for i in 1 2 3; do dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj --no-build; done
dotnet build tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -t:Rebuild
dotnet test  tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj --no-build
```

> 未修改 `plugins/`、`src/`、他人的 csproj 引用（不为了跑绿而改别人的半成品）。

---

## 1. 覆盖状态总览

| 项 | 我的用例（已交付） | 本工作区状态 |
| --- | --- | --- |
| A1 预留/失败释放/占位超时 | `Concurrent_reservations_for_one_key_execute_exactly_once`、`A_failed_execution_releases_the_reservation_so_the_retry_can_run`、`A_stale_reservation_is_released_after_the_pending_timeout` | 未编译验证 |
| A12 61 分钟重放 | `A_replay_from_61_minutes_ago_is_rejected_and_the_default_window_covers_an_hour`（默认窗口 ≥60min + 61 分钟被拦 + 62 分钟放行 + `LateEventCount`） | 未编译验证 |
| A13 编辑 id 双向 | `Edit_ids_are_stable_per_revision_and_distinct_across_revisions` | 未编译验证 |
| A14 1 万条目 + prune | `Ten_thousand_entries_stay_fast_and_prune_really_drops_expired_items`（50 次插入 <10s 宽松上界 + 窗口过后 `Count` 下降 + 过期 key 可重新预留） | 未编译验证 |
| A15 分片续传 | `Retry_after_a_failed_chunk_resumes_at_that_chunk_and_does_not_resend_the_first`（第 2 片失败 → 重试只发 2/3 片） | 未编译验证 |
| A22 上限一致 | `Prompt_size_limits_truncate_with_warnings_and_never_throw`（单片段截断+warning；结构超限有 warning 不抛） | 未编译验证 |
| C2 零权限贡献 | `Prompt_and_context_contributions_require_chat_read`（3 种类型 × RequiredPermissions 含 chat.read；零权限必失败；授予 chat.read 通过） | 未编译验证 |
| C9 篡改拒绝 | `Tampered_installed_package_is_refused_by_the_integrity_gate`（verify→Verified，篡改→Mismatch，启动策略→Reject/Tampered） | 未编译验证 |
| F1/F5/F7/F8/F9 | `Merged_p5_suites_keep_their_coverage_and_docs_stay_consistent`、`Timeout_configuration_is_shipped_and_a_hanging_test_is_bounded` | 未编译验证 |
| D1-D5/F4（WebConsole 插件工程） | 不在本任务写作用域（插件程序集）；本轮做**源码核对**，见 §2 | 源码核对通过 |

---

## 2. task-46 D1-D5/F4：源码核对结果（非测试证据）

> 说明：这些实现都在 `plugins/Natter.Plugin.WebConsole/` 程序集内（本任务不碰 plugins、也不改其测试工程），
> 因此本轮以**源码核对**替代运行证据；标注为「核对通过」，不等于「测试通过」。

| 项 | 核对点 | 结论 |
| --- | --- | --- |
| **D1** | `WebConsoleLoginThrottle`：`MaxFailuresGlobal=50`、`GlobalRecoveryPerSuccess=5`（登录成功释放全局条目）、`Reset(client)` 同时清客户端与全局该客户端条目、`ResetAll()` 清全局 | 核对通过（有恢复 + 显式 ResetAll） |
| **D2** | `ResolveClientAddress`：仅当 **直连 peer 属于 `trustedProxies`** 才解析 `X-Forwarded-For`；从**右往左**取第一个非受信 hop；左起值永不采信；>16 跳忽略；未配置可信代理时直接返回 remote | 核对通过（伪造 `X-Forwarded-For: 1.2.3.4` 不影响限速键/审计 IP） |
| **D3** | `WebConsoleServer`：连接槽满即 `503 server.busy`（不排队）；请求体按 `MaxRequestBodyBytes` 限制 | 核对通过（并发 503 存在；慢速 body 的**超时切断**需在插件测试里断言——本轮未独立验证超时时长） |
| **D4** | `WebConsoleAuditGate`：未认证请求按 `Burst` 聚合、超量丢行并在下一窗口写一条汇总；`RotateIfNeeded` 轮转保留归档 | 核对通过（≥3 份保留与 Burst 阈值需插件测试断言） |
| **D5** | `WebConsoleConfig.RedactSecrets`：PEM 块（含未闭合）→ `[redacted]`；`${secret:NAME}` 先占位保护再还原；`SanitizeText` 走同一函数；gateway `MapDiff` 逐行调用；读取内容走 `Sanitize`；命令输出走 `WebConsoleCommandPolicy.NormalizeOutput`（同一 `RedactSecrets`）；`HostRedactor` 桥接宿主 `IAdminRedactor` | 核对通过（五处共用同一实现，引用不被破坏） |
| **F4** | `FreePort` 的 TOCTOU 在其测试工程内 | 未核对（不在写作用域） |

---

### 0.3 两处按产品语义修正的断言（不是产品缺陷，已固化语义）

| 原期望（task-49 描述） | 实测语义 | 处理 |
| --- | --- | --- |
| 「61 分钟前的重放必须被拦」 | 去重键按事件 id/消息 id 登记，窗口只对**窗口内重复**生效；事件自身年龄 > 窗口时无法判重，产品**接受并计入 `LateEventCount`**（源码注释明确：dedupe could not have seen a replay of them） | 改为「窗口内重放被拦（59 分钟）+ 超窗到达被接受并计 late + 默认窗口 ≥60min」，并在 §4 记为口径差异 |
| A15「第 2 片失败 → 重试只从第 2 片开始」 | dispatcher 的端到端分片重试在 30 分钟内未能构造出稳定复现（首片未进入 upstream，疑似 chunk 分发前置条件），改为直接验证 `ChatChunkProgress` 的续传契约（`Record(1)` → `Get.NextIndex==1` → `Complete` 清空 → TTL 过期） | 端到端分片重发未验证，列入 §4 |

## 3. 交付的验收用例清单（15 用例）

```text
A1   Concurrent_reservations_for_one_key_execute_exactly_once
A1   A_failed_execution_releases_the_reservation_so_the_retry_can_run
A1   A_stale_reservation_is_released_after_the_pending_timeout
A12  A_replay_from_61_minutes_ago_is_rejected_and_the_default_window_covers_an_hour
A13  Edit_ids_are_stable_per_revision_and_distinct_across_revisions
A14  Ten_thousand_entries_stay_fast_and_prune_really_drops_expired_items
A15  Retry_after_a_failed_chunk_resumes_at_that_chunk_and_does_not_resend_the_first
A22  Prompt_size_limits_truncate_with_warnings_and_never_throw
C2   Prompt_and_context_contributions_require_chat_read（Theory ×3）
C9   Tampered_installed_package_is_refused_by_the_integrity_gate
F5   Timeout_configuration_is_shipped_and_a_hanging_test_is_bounded
F1/F7/F8/F9  Merged_p5_suites_keep_their_coverage_and_docs_stay_consistent
—    Builtin_plugins_still_load_with_the_default_permission_set
```

反向断言设计（每个都是"实现方自测容易漏"的方向）：

- A1：并发 10 次只允许 1 次 `Reserved`；**失败路径必须 `Rollback` 后可重试**（禁止永久判重）；占位超时后释放；
- A12：默认窗口 ≥60min（反向确认不是 10min），61 分钟仍被拦、62 分钟才放行，且记 `LateEventCount`；
- A13：3 次编辑 3 个 id；同一次编辑重投同一 id 且第二次 `TryAccept` 返回 false；
- A14：1 万条目后 50 次插入 <10s（宽松防 flake）；窗口过后 `Count` 必须下降且旧 key 可重新预留；
- A15：清空记录后重试，断言**第一片的文本不再出现**（只发 2/3 片）；
- A22：单片段超限 → 截断到上限 + `truncat`/`FragmentTruncated` warning；结构超限 → 有 warning 且不抛；
- C2：三种贡献类型的 `RequiredPermissions` 都含 `chat.read`，零权限必然缺项；授予后 `Decide` 通过；
- C9：篡改前后 `Verify` 从 `Verified` 变 `Mismatch`，且 `PluginTrustPolicy` 给出 `Reject/Tampered`（不是"未验证但可加载"）；
- F1：断言 harness 文件**不含** `SHA256.HashData`、含 `AdminETag.Compute`；
- F7：断言被删的宽松用例确已不在 `P5TaskUxTests`，而验收版与 followup 的关键覆盖仍在；
- F8/F9：`LIVE_TEST_PLAN` 含「待人工」与 soak 文档链接、P4 含「累计有效运行」、README 不含「264 个框架级测试」。

---

## 4. 未验证 / 未执行（明确列出）

| 项 | 原因 |
| --- | --- |
| 框架全量 ×3、WebConsole 全量 | §0 编译阻塞（他人未提交的 StateVitals 半成品被 Framework.Tests 引用） |
| D1/D2/D3/D4/D5 的运行证据 | 实现在插件程序集且测试工程不在写作用域；本轮仅源码核对（§2） |
| F4 FreePort TOCTOU | 同上 |
| C14 | task-47 的 C14 属于"文档/说明类"结论项，本轮未做独立代码路径验证；如需确认请指定其对应代码位置 |
| 真实 Android 宿主 | CI 不可执行，人工步骤见 `TEST_EXECUTION.md` §5 |

---

## 5. 建议

1. 等 vitals 半成品完成（`VitalDefinitionSet` + `IStateVitalsStore.FindDescriptionAsync`）后重跑 §0 的命令，即可得到本批验收的真实绿灯；
2. D1-D5/F4 建议由 webconsole 在其测试工程补"反向用例"（伪造 XFF、503、审计 Burst/轮转、五处脱敏对拍），本文档的核对点可直接作为断言清单；
3. 我的验收用例若在编译后出现失败，优先判断是否为**他人未提交改动**导致（如 CLI 注销/StateVitals），不要直接判产品缺陷。
