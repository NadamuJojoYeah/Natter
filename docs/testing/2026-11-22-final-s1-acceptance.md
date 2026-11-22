# 2026-11-22 · 最终 6 项 S1 独立验收 + 网关看门狗只读核验

> **执行**：qa（task-35）
> **基线**：`HEAD=5251cfb`（测试运行时的 HEAD；修复合集起于 `1eb283a`，其后仅有 `5251cfb` 的样例配置改动，不影响本报告结论。修复合集 `cb537e3` 并发/生命周期、`359f0d0` Satori 转义、`7a6f054`/`2bf08f8` CLI 启动策略、`27357e1`/`06629d7` 插件信任；task-36 看门狗 `c81c4c4`/`1eb283a`）
> **新增测试**：`tests/Natter.Framework.Tests/FinalS1AcceptanceTests.cs`（**32 个用例**）
> **关联**：[审查修复验收](2026-11-22-review-acceptance.md)、[代码审查报告](../CODE_REVIEW_2026-11-22.md)

---

## 1. 结论

| 项 | 结论 | 关键证据 |
| --- | --- | --- |
| **S1-05** 会话一致性 | **通过** | 同 key 并发峰值 **1**、不同 key 峰值 **2**；同会话两轮并发后历史 `[first, second]` 各一次，第二轮模型调用看到 `first`（不再读旧历史） |
| **S1-06** 编排器 | **通过** | 同会话 `created → updated` 严格保序；跨会话并发峰值 ≥2；容量 1 时 12 条事件 `enqueued+dropped=12` 且日志含 `queue full`；`StopAsync` 排空（completed=2、failed=1、drainTimeout=0）；处理器异常计入 `FailedCount=1` |
| **S1-07** 任务结果发送 | **通过** | 租约未取得 → **零消息** + `SkippedResults=1`；任务已被别处推进 → 零消息且不覆盖既有结果；真失败 → 恰好一条 `Task failed.`；并发 3 次 Enqueue 幂等 → 1 条消息、`created=1/completed=1/failed=0/active=0` |
| **S1-03** Satori 转义 | **通过** | `<at type="all"/>` → 序列化 `&lt;at type="all"/&gt;`，输出无 `<at ` 标签；`<img>`/`<br>`/`&` 全部转义；结构化 mention 保留；纯文本 SHA-256 逐字节一致 |
| **S1-11** CLI 启动策略 | **通过** | 5 类只读命令 `StartHost=false`/`TakeInstanceLock=false`；5 类 host 命令 `Runtime+StartHost+Lock`；`--offline`/`--no-host` 均 `Degraded=true` 且 Note 含 "running without one / live state will be unavailable"；serve 持锁时只读命令 exit 0 且 **hostStarts=0**，`upstreams` exit **3** + `HL-CLI-HOST-BUSY` + `--no-host` 提示 |
| **S1-12** 插件信任 | **通过** | signed/unsigned × requireSigned(true/false) 四组合符合预期；篡改在两种 requireSigned 下都被拒（`plugin.integrity-mismatch`）；未验签可按基线加载但显式标注 `local(unverified)` + `plugin.integrity-unverified` 警告；缺清单 → `plugin.integrity-manifest-missing`（不静默当可信） |
| **task-36** 网关看门狗（只读核验） | **通过** | 断连超阈值 → 重建成功、connected 恢复、`WatchdogTriggerCount` 递增且 attempt 归零；退避 80ms→160ms 递增；`maxAttempts=2` 后 `ReconnectExhausted`/`RequiresManualIntervention=true` 且计数不再增长；`DisconnectAsync` 后 `WatchdogTask=null`；日志含 `lastCode` 不含 token |

| 回归 | 结果 |
| --- | --- |
| 新增验收 | **32/32**（连跑 3 次全绿） |
| Framework 全量 | **727/727**（695 基线 + 32，连跑 3 次全绿） |
| WebConsole | **314/314** |

**判定：全部通过，可签。**

---

## 2. S1-05 会话一致性

```text
AiConversationGate: 同 key 2 请求 → 峰值并发 1（串行）
                    不同 key 2 请求 → 峰值并发 2（并行）
```

真实 `AiChatResponder` + `AiConversationStore` + 带 80ms 延迟的 `IChatClient`，同会话并发两轮：

```text
历史 user 消息 = [first, second]       ← 每轮只追加一次，无重复的上一轮
历史 assistant = 2 条
第二轮模型调用看到的历史包含 "first"    ← 不是空历史/旧历史
```

修复点在 `AiChatResponder`：`history 读取 → 组装 → 模型 → 追加` 全窗口位于 per-conversation lease 内，出站发送在 lease 之外。

---

## 3. S1-06 编排器

| 场景 | 实测 |
| --- | --- |
| 同会话保序 | 处理顺序 `evt:m1:created → evt:m1:updated`（`ProcessEditedMessages=true`） |
| 跨会话并行 | 3 个会话并发处理峰值 **≥2**（`WorkerCount=4`） |
| 队列上限 | `QueueCapacity=1`、1 worker、发布 12 条 → `EnqueuedCount+DroppedCount=12`、`DroppedCount>0`、日志含 `queue full (capacity=1)` |
| StopAsync 排空 | 3 条在途（含 1 条抛异常）→ StopAsync 返回后 `completed=2`、`FailedCount=1`、`DrainTimeoutCount=0`、`Processed+Failed=3` |
| 异常计数 | 处理器抛 `InvalidOperationException` → `FailedCount=1`（不吞成静默失败） |

---

## 4. S1-07 任务结果发送

| 场景 | 结果 |
| --- | --- |
| 租约未取得（store 恒返回 false） | 发送命令 **0 条**、`SkippedResults=1`、`SentResultCount=0` |
| 任务已被别处推进（预置 Completed） | 发送 **0 条**、`SentResultCount=0`、store 中 `result_text` 保持 `owner result`（未被覆盖） |
| 真失败（worker 返回 `tool_timeout`） | 恰好 **1 条** `Task failed.`，任务状态 `Failed` |
| 幂等入队（task-23 回归） | 并发 `EnqueueAsync` ×3 → 1 条消息、`created=1/completed=1/failed=0/cancelled=0/active=0` |

实现对照：`terminalReached` 只在本次执行真正进入终态时为 true；否则 `SkippedResults++`，避免把 Pending/Running 映射成 `Task failed.` 并污染 `_resultsSent`。

---

## 5. S1-03 Satori 出站转义

| 输入 | 序列化输出 | 判定 |
| --- | --- | --- |
| `ping <at type="all"/> everyone` | `ping &lt;at type="all"/&gt; everyone` | 无 `<at ` 标签、无 mention 元素 |
| `<img src=x onerror=alert(1)><br>a & b` | `&lt;img …&gt;&lt;br&gt;a &amp; b` | 三个危险字符全部转义 |
| 纯文本 `Hello world — 你好, 100% & more; no markup at all.` | 元素文本与输入 **SHA-256 相同** | 逐字节不变 |
| `Content("hi ", Mentions: [User 123 Bob])` | 结构化 `SatoriMentionElement` 保留 | 未被误伤 |

（转义发生在序列化阶段：`SatoriTextElement.Text` 保持原文，`Format` 输出转义后的安全文本——这与"纯文本逐字节不变"的要求一致。）

---

## 6. S1-11 CLI 启动策略

```text
只读（StartHost=false, TakeInstanceLock=false, Degraded=false）
  plugins list / plugins config validate / plugins store list / doctor / version
需要 host（Runtime, StartHost=true, TakeInstanceLock=true）
  serve / health / upstreams / plugins graph / events watch
降级（StartHost=false, Degraded=true，Note 含 "running without one … live state will be unavailable"）
  health --offline → Note 点名 --offline
  upstreams --no-host → Note 点名 --no-host
```

锁冲突端到端（`.lock` 被 serve 占用、`OnHostStartedAsync` 计数 = 是否启动 host/网关）：

```text
plugins list --json  → exit 0，hostStarts=0（网关连接数为 0）
upstreams --json     → exit 3（CliHostStartupCodes.HostBusy），hostStarts=0
                       stderr 含 HL-CLI-HOST-BUSY 与 --no-host 建议
```

---

## 7. S1-12 插件信任

| 场景 | 决策 |
| --- | --- |
| 已验签 + requireSigned=true | 允许、`SignatureVerified=true`、Trust=Signed |
| 未验签 + requireSigned=true | 拒绝 `plugin.unsigned-rejected` |
| 未验签 + requireSigned=false | 允许、按基线 Trust、code `plugin.integrity-unverified`、**warnings 非空** |
| 篡改 + requireSigned=true/false | 都拒绝 `plugin.integrity-mismatch`（篡改优先于一切配置） |

完整性复核（真实 `package.sha256` 清单）：

```text
清单匹配 → plugin.integrity-verified
篡改 dll → plugin.integrity-mismatch（不是 verified，也不是"静默可信"）
目录无清单 → plugin.integrity-manifest-missing（Unverified）
```

默认信任选项下未验签包仍可加载（不破坏既有部署），但 `TrustLabel` 输出 `local(unverified)` 并带 `plugin.integrity-unverified` 警告——没有把"未校验"当成"可信"。

---

## 8. task-36 网关看门狗（fake transport，只读核验）

未连接真实 Discord、未触碰 `natter.service`；transport 为进程内 fake（`IDiscordTransport`）。

| 场景 | 实测 |
| --- | --- |
| 断连超阈值（threshold=30ms、tick=10ms） | watchdog 触发重建，`transport.ConnectCount≥2`、`IsConnected` 恢复 true、`ReconnectAttemptCount=0`（成功后重置） |
| 退避递增（initial=80ms） | 第 1 次失败后 `NextReconnectAt-now≈80ms`，第 2 次失败后 ≈160ms，严格递增 |
| maxAttempts 耗尽（=2） | `ReconnectExhausted=true`、`RequiresManualIntervention=true`、`ReconnectAttemptCount` 停在 2 且 300ms 内不再增长、`WatchdogTriggerCount=2` |
| DisconnectAsync 退出 | `WatchdogTask=null`（干净退出） |
| 日志 | 含 `watchdog`、`lastCode`；不含 `token`/`Bot ` 片段 |

---

## 9. 观察项（不阻塞）

| ID | 级别 | 内容 |
| --- | --- | --- |
| OBS-FINAL-TRUST-SIX | Info | 「默认配置 6 插件照常加载」本轮未重新拉起真实插件宿主，而是用默认 `PluginTrustOptions` 的决策矩阵 + `TrustLabel` 证明既有部署语义不变；真实 6 插件加载由框架 727（含 `PluginTrustTests` 等）与 WebConsole 314 / PromptLibrary 36 回归间接覆盖 |
| OBS-FINAL-GWCOUNT | Info | S1-11 的「网关连接数为 0」用 `OnHostStartedAsync` 计数证明"未启动 host"（因此不会建 gateway 连接），未起真实 gateway |
| OBS-FINAL-SATORI-ATTR | Info | Satori 转义只处理标签定界符 `<`/`>`/`&`；纯文本里的引号不转义（不构成元素注入），已在此记录以便后续按需收紧 |
| OBS-FINAL-WATCHDOG-FAKE | Info | task-36 用 fake transport 验证；`DiscordSocketTransport` 的真实 close code 提取与 Discord.Net 交互仍由实现方 `GatewayReconnectTests` 覆盖，本轮未连真实网关 |

---

## 10. 未测 / 范围外

- 真实 Discord 网关重建（按任务要求不连真实 Discord、不碰生产服务）；
- 真实 6 插件宿主启动（见 OBS-FINAL-TRUST-SIX）；
- `--no-host` 下各命令的实际输出内容（只验 plan 的 Note 与 Degraded）；
- 未部署、未重启、未执行 `git commit`/`push`，未修改 `src/`、`plugins/`、`scripts/`、`deploy/`。

---

## 11. 复现命令

```bash
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~FinalS1AcceptanceTests"     # 32/32（×3）
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj   # 727/727（×3）
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj  # 314/314
```
