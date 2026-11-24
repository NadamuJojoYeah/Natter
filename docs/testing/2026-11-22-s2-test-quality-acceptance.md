# 2026-11-22 · S2 第二批：测试与文档质量（F1/F2/F3/F5/F6/F7/F8/F9）

> **执行**：qa（task-48）
> **基线**：`HEAD=0728db7` + 工作区未提交改动（本批 engineer 改 `Application/Chat`、webconsole 改 `plugins/WebConsole`；本轮未碰 src/plugins）
> **范围**：8 项测试/文档质量修复（F4 归 webconsole）

---

## 1. 结论

| 项 | 结论 | 证据 |
| --- | --- | --- |
| **F1** harness 重抄产品 ETag | **已修** | `FileAdminConfigStore.ETagOf` 改为委托产品 `AdminETag.Compute`，删除重抄的 SHA256 算法；新增对拍用例 `Harness_etags_match_the_product_implementation`（4 组输入） |
| **F2** 预算断言只判上界 | **已修** | 断言 `EstimatedCharacters ∈ [1,80]`、消息非空、首条为 system 且含 "system"、总字符 == EstimatedCharacters、user 消息不掺入外部内容 |
| **F3** 串行化只看 MaxActive | **已修** | 用 `TaskCompletionSource` 门替代 `Delay(50)`：gate 关闭期间第二个请求**未进入模型**（`Calls==1`）、开门后两请求**各恰好一次**（`Calls==2`）、顺序 = 到达顺序、`WaitAsync` 防死锁 |
| **F5** 缺 WaitAsync/超时/blame-hang | **已修** | 关键并发用例加 `[Fact(Timeout=20_000)]` + `WaitAsync`；新增 `tests/Natter.Framework.Tests/xunit.runner.json`（`longRunningTestSeconds=30`）；新增 `docs/testing/TEST_EXECUTION.md`（`--blame-hang` 本地/CI 用法、挂起排查顺序、Android 人工步骤） |
| **F6** 覆盖盲区 | **部分补齐** | 新增签名失败路径用例 `Invalid_signature_is_rejected_instead_of_silently_trusted`（乱码 `package.sig` + 受信公钥 → `signature-invalid`，且不落成 legacy warning）；ALC 卸载沿用既有 observation 口径（§4），Android 人工步骤写入 `TEST_EXECUTION.md` §5，**不伪造** |
| **F7** 三个 P5 文件重叠 | **已合并** | 删除 6 个纯重复用例（详见 §2），三文件从 **71 → 65** 用例，覆盖面不降（被删项由更严格的验收版覆盖） |
| **F8** 文档口径矛盾 | **已修** | `LIVE_TEST_PLAN.md` 状态行改为「已验证 / 已验证（人工）/ 待人工·未执行」三档；`P4_TIER1_PRODUCTIZATION.md` 的 `24h soak` 行改为**未完成**并引用 `passive-soak-24h.md` §3.1「累计有效运行」口径与三项护栏 |
| **F9** README 写死 264 | **已修** | README 改为「用例数不在此写死，以 CI 实时输出为准」；新增防漂移用例 `Readme_does_not_hardcode_a_stale_test_count`（禁止"264 个框架级测试"字样、要求含 CI 口径说明） |

| 回归 | 结果 |
| --- | --- |
| 框架全量（Debug，重建后 `--no-build`） | **855/855**，连跑 **3 次**全绿 |
| WebConsole 全量 | **333/333**（含 F1 对拍 4 例） |
| Phase23Acceptance 子集 | **48/48** |

---

## 2. F7 重叠清单与合并前后对比

### 2.1 重叠清单

| 保留（更强） | 删除（重复） | 重复点 |
| --- | --- | --- |
| `P5TaskUxAcceptanceTests.Status_reports_every_p53_field_for_a_real_dispatched_task` + `Status_reports_the_error_code_and_message_for_a_failed_task` | `P5TaskUxTests.Status_reports_elapsed_step_tools_budget_error_and_recent_trace` | /status 的 elapsed/step/tools/error/trace |
| `P5TaskUxAcceptanceTests.Status_without_trace_degrades_and_never_leaks_other_or_missing_tasks` | `P5TaskUxTests.Status_degrades_without_trace_and_hides_other_tasks` | 无 trace 降级 + 他人任务隐藏 |
| `P5TaskUxAcceptanceTests.Tasks_filters_by_status_and_clamps_the_limit_to_fifty` / `Tasks_defaults_to_twenty_rows_and_guards_the_total_output_size` / `Tasks_reports_the_empty_message_and_rejects_an_unknown_status_filter` | `P5TaskUxTests.Tasks_lists_elapsed_tools_and_supports_status_limit_filters` | /tasks 过滤、上限、行数保护、空列表 |
| `P5TaskUxAcceptanceTests.Cancel_returns_one_distinct_message_per_state_and_permission` + `Cancel_then_status_shows_cancelled_and_the_final_trace` | `P5TaskUxTests.Cancel_distinguishes_terminal_states_and_permissions` | /cancel 六类文案与权限 |
| `P5TaskUxAcceptanceTests.Metrics_follow_a_real_task_run_and_keep_the_legacy_fields` + `Metrics_count_a_tool_timeout_and_the_task_fails_with_tool_timeout` | `P5TaskUxTests.Metrics_record_tool_calls_timeouts_and_durations` | metrics 新字段与旧字段 |
| `P5TaskUxAcceptanceTests.Status_reports_every_p53_field_for_a_real_dispatched_task`（含 500 字截断） | `P5FollowupTests.Status_reports_state_version_and_truncates_long_results` | /status 字段 + 结果截断 |

保留的独有用例：`P5TaskUxTests` 的 trace 顺序/白名单拒绝/AppendEvent 失败/default profile（4 例）；`P5FollowupTests` 的 lease/重启幂等/工具安全/Tier2（45 例）；`P5TaskUxAcceptanceTests` 全部 16 例（真实 dispatcher/Sqlite/日志通道）。

### 2.2 合并前后用例数

| 文件 | 合并前 | 合并后 | 说明 |
| --- | --- | --- | --- |
| `P5FollowupTests` | 46 | **45** | 删 1（status 截断与验收版重复） |
| `P5TaskUxTests` | 9 | **4** | 删 5（/status、/tasks、/cancel、metrics 与验收版重复） |
| `P5TaskUxAcceptanceTests` | 16 | **16** | 不删（覆盖更严格，作为"独立验收"保留） |
| **合计** | **71** | **65** | 减少 6 个纯重复；覆盖面由保留版本覆盖 |

---

## 3. 关键改动细节

### F1：不再重抄产品算法

```csharp
// before（harness 自己算）
=> "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();
// after（委托产品实现）
public static string ETagOf(string content) => AdminETag.Compute(content);
```

对拍用例（`ConfigPhase2AcceptanceTests`，Theory 4 组：空串 / `{}` / 空 profiles / 含中文与数组）：
`FileAdminConfigStore.ETagOf(x) == AdminETag.Compute(x)` 且 `AdminETag.Matches(x, harnessETag)`，并断言前缀为 `sha256:`。

### F2：预算不再"空也过"

```csharp
Assert.InRange(bundle.EstimatedCharacters, 1, 80);
Assert.NotEmpty(bundle.Messages);
Assert.Equal(ChatRole.System, bundle.Messages[0].Role);
Assert.Contains("system", bundle.Messages[0].Text);
Assert.Contains(bundle.Messages, m => m.Text is { Length: > 0 });
Assert.Equal(bundle.EstimatedCharacters, bundle.Messages.Sum(m => m.Text?.Length ?? 0));
```

**观察项 OBS-F2-BUDGET-DROPS-TURN（Low）**：80 字符预算下管线保留 system prompt 并把用户轮裁成**空串**。测试据此允许空 user 消息但断言其不含外来内容；产品侧是否应"至少保留用户轮"或"预算不足时报降级"值得产品决策（本轮不改 src）。

### F3：可控 gate

```text
gate 关闭 → 第二个 RespondAsync 未进入 FakeChatClient（Calls == 1）
gate 打开 → 两个请求都完成（Calls == 2，各一次），order == ["one","two"]
await Task.WhenAll(...).WaitAsync(10s)   // 死锁会失败而不是挂死
[Fact(Timeout = 20_000)]
```

### F5：超时与挂起取证

- `xunit.runner.json`：`longRunningTestSeconds=30`、`parallelizeTestCollections=true`、`maxParallelThreads=8`（已随构建复制到输出目录）；
- 文档 `docs/testing/TEST_EXECUTION.md`：标准命令、超时层级、`--blame-hang --blame-hang-timeout 10m [--blame-hang-dump-type mini|full]`、排查顺序、禁止写法（裸 Delay 同步、无取消的 TCS、lock 内 await）、Android 人工步骤。

### F6：签名失败路径

```text
合法 manifest + 真实 PE 入口 + 正确 package.sha256 + 64B 随机 package.sig + 受信公钥
→ IsValid=false，issue = signature-invalid，且没有 signature-legacy-format warning（不会被当成"兼容旧格式"放行）
```

ALC 卸载：沿用 `S2Batch1AcceptanceTests.Unloaded_plugin_is_no_longer_routed_and_its_command_is_unresolvable`（命令不可解析 PASS）与「ALC 可回收 = observation（task-43）」的既有口径，本轮未新增弱引用硬断言（避免假清理）。
Android：CI 只能编译 + 静态校验清单；真机步骤见 `TEST_EXECUTION.md` §5（未在真机验证前写成"编译通过 + 清单校验通过，真机待人工"）。

### F8：口径对齐

```text
LIVE_TEST_PLAN.md 状态行：
  已验证：L1–L4 真实 API（27/27）
  已验证（人工）：L5 闸门 2026-11-15 13:50 复测通过
  待人工/未执行：24h 被动 soak（按 passive-soak-24h §3.1 累计有效运行，当前无合格记录，不得写 PASS）

P4_TIER1_PRODUCTIZATION.md：
  | 24h soak | 未完成（Σ有效运行≥24h + 单段≥6h + 缺口归因 + RSS 漂移护栏）；现有 soak-once 0.3%、toolcheck 83.3%、p2 旧格式 0 样本均 FAIL，不得按 PASS 计 |
```

### F9：README

改为「用例数不在此写死，以 CI 实时输出为准」，并由 `Readme_does_not_hardcode_a_stale_test_count` 守住（禁止 "264 个框架级测试" 字样、要求含 CI 口径）。

---

## 4. 观察项（不阻塞）

| ID | 级别 | 内容 |
| --- | --- | --- |
| OBS-F2-BUDGET-DROPS-TURN | Low | 80 字符预算下用户轮被裁成空串（见 §3 F2）；建议产品决定"保底保留用户轮"或"预算不足时降级提示" |
| OBS-F6-ALC-HOST | Low | 宿主级 ALC 回收仍未硬断言：CLI 注册表等宿主单例强引用（task-43 排查）；unload 后"命令不可解析"已作为可观测 PASS 断言 |
| OBS-F48-INCREMENTAL | Info | 本轮发现**增量构建会跳过"源文件 mtime ≤ 产物 mtime"的重建**（容器时钟跳变时），导致跑旧程序集（表现为删除的用例仍在运行）。规避：改测试后 `dotnet build -t:Rebuild`（与 task-44 的 Release 重建口径同源）；已写入 `TEST_EXECUTION.md` §1/§6 |

---

## 5. 运行结果

```bash
dotnet build tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -t:Rebuild
for i in 1 2 3; do
  dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj --no-build
done
# Passed! 855/855  ×3

dotnet build tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -t:Rebuild
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj --no-build
# Passed! 333/333
```

P5 三文件合并后：`P5FollowupTests 45 + P5TaskUxTests 4 + P5TaskUxAcceptanceTests 16 = 65`（合并前 71）。

---

## 6. 未测 / 范围外

- Android 真机（CI 不可执行，步骤见 `TEST_EXECUTION.md` §5）；
- 宿主级 ALC 回收（observation，task-43）；
- 未碰 `WebConsoleServerTests.cs`（F4 归 webconsole）、`src/`、`plugins/`、`deploy/`、`scripts/`；未部署、未重启、未 commit/push。
