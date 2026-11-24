# 2026-11-22 · S2 第一批独立验收（task-42，重点反向验证）

> **执行**：qa（task-42）
> **基线**：`HEAD=43804b5`（S2 第一批：`9816057`/`8cef9ce` 包与存储、`1baca70`/`2500aee` 任务运行时、`3068ec7`/`43804b5` Satori+AI、`33e489a`/`a1d97da` 钉钉+Discord、`e729fca`/`e1526be` CLI+运维、`37838c7` 签名规范）
> **新增测试**：`tests/Natter.Framework.Tests/S2Batch1AcceptanceTests.cs`（**21 个用例**，全部为反向/边界断言）
> **关联**：[最终 S1 验收](2026-11-22-final-s1-acceptance.md)、[审查修复验收](2026-11-22-review-acceptance.md)


---

## 0. 验收方法（task-44 追加）：必须 Release 重建，不得复用旧 bin

merge-verify 发现：Release 首轮 3/3 失败在 `Archive_that_lies_about_its_size...`（`manifest-not-nps`），
`dotnet build -c Release` 重建后 8 次全过。根因不是代码，而是**新旧程序集混装**（新测试程序集 + 旧
`Natter.Plugin.Store.dll`）—— 增量构建认为产品项目“已是最新”，而测试只重建了自己。

固化口径（本地与 CI 一致）：

```bash
# 跑验收前先整轮 Release 重建（产品 + 测试），不要直接 dotnet test
dotnet build tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -c Release
dotnet test  tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -c Release --no-build
```

- 不要用 `dotnet test` 的隐式增量构建作为唯一构建步骤；切换分支、改过产品项目、或复用他人 `bin/` 后尤其危险；
- CI 已同步收紧：`build-test` job 先 `dotnet build "$project" -c Release` 构建全部解决方案项目，随后
  `dotnet test ... -c Release --no-build`，从流程上禁止测试复用旧 bin（`.github/workflows/ci.yml`，task-44）；
- 混装症状：`manifest-not-nps` / `entry-missing` / `hash-mismatch` 一类「像是产品校验坏了」的失败，先重建再判断。

### 0.1 计时断言的写法（task-44）

退避/超时类断言不得比较墙钟差值。`FinalS1AcceptanceTests.Reconnect_backoff_requests_a_growing_delay_sequence`
已改为注入可控 `IClock`，直接断言 watchdog **请求的 delay 序列**（80ms → 160ms 精确值），只保留对
watchdog tick 的宽松轮询；Release 并行下连跑 10 次全绿。

---

## 1. 结论

| 反向验证点 | 结论 | 证据要点 |
| --- | --- | --- |
| **C1 zip bomb** | **通过** | 4 MiB 零字节高压缩比包 → 拒绝（ratio），extract 增量 **0 字节**；纯撒谎包 → 拒绝且增量 < 4 MiB；**新增**「合法 manifest + 真实 PE 入口 + 正确 `package.sha256`、只有 header 撒谎」的包 → 拒绝（`hash-mismatch`：.NET 按声明长度只解出 100 B）+ 增量 ≤ 2 MiB（task-44 补齐原缺口） |
| **A5 webhook SSRF** | **通过** | 9 组策略拒绝 + 3 组放行；**新增 transport 级零请求**：注入 counting `HttpMessageHandler` 的 `DingTalkWebhookTransport.SendAsync` 对 `oapi.dingtalk.com.evil.example` 返回 `session_webhook_rejected` 且 handler **0 次调用**（task-44 补齐原缺口） |
| **B4 Source 往返** | **通过** | `selfId=bot:instance:42` + `conversation=c:1` 经 SQLite 往返完全一致，任务结果能按完整 `discord:discord:bot:instance:42` 投递到原会话 |
| **B10 取消不被吞** | **通过** | 已取消的 token 调 `RespondAsync` → 抛 `OperationCanceledException`，且 fake 模型 **调用次数 = 0** |
| **C3 卸载（路由/命令）** | **通过（依赖 task-43 的 CLI 注销改动）** | Stop 后 `state=Stopped` 且 `TryGet("greet")` **返回 false**（命令不可解析）。本轮先在 HEAD 上复现 **BUG-S2-C3-UNREGISTER**（`UnregisterOwner` 无调用点 → 仍可解析），task-43 的未提交 CLI 注销改动落地后翻转为正向断言 `Unloaded_plugin_is_no_longer_routed_and_its_command_is_unresolvable`；ALC 回收仍只作 observation（task-43），不做假清理 |
| **C4 环隔离** | **通过** | a↔b 环 + standalone：standalone 无 error 且在 order 中；a/b 报 `phase=dependency`/code 含 cycle 且消息点名环成员 |
| **E10 token 不进 argv** | **通过** | `scripts/p2-collect.sh` 无 `-H "Authorization`，含 `-K -`（curl config stdin） |
| **E3 degraded 退出码** | **通过** | disconnected upstream → `status=degraded` 且 `CliHealthExitCodes.Degraded == 4`；空 registry 静态视图 → `unknown`（不误判为 degraded） |
| **C7 legacy 签名** | **部分验证** | 默认 `RejectLegacySignatureFormat=false`；legacy 与 v2 canonical text 可区分（v2 含 `nps-hash-v2`，legacy 不含）；完整"true 时拒绝 legacy 签名"由实现方用例覆盖，本轮未构造签名链 |
| **A20 gate** | **通过** | 2000 个不同 key 后 `ConversationCount ≤ 64`（容量上限生效，字典不单调增长）；Dispose 期间在途 lease 释放 **不抛 ObjectDisposedException**，`ActiveOperationCount=0` |

| 回归 | 结果 |
| --- | --- |
| 新增验收（最终） | **56/56**（FinalS1 32 + S2Batch1 24） |
| Release（重建后 `--no-build`） | **10/10 全绿**（56/56 每次） |
| Debug（重建后 `--no-build`） | **3/3 全绿**（56/56 每次） |
| Framework 全量 | **832/832**（811 基线 + 21，连跑 3 次全绿） |
| WebConsole | **314/314** |

**判定：可签。** 10 个反向验证点中 8 项完全通过、1 项部分验证（C7）、1 项未独立验证（C3），均已在 §3 列明，无缺陷需上报。

---

## 2. 逐项证据

### C1 zip bomb

| 构造 | 结果 |
| --- | --- |
| 4 MiB 全零文件（压缩后数 KiB，膨胀比远超默认 100） | `IsValid=false`，issue 命中 compression ratio；extract 目录 **0 字节** |
| 同一包，把 `payload.bin` 的 local header(+22) 与 central directory(+24) 的 uncompressed size 改写为 100（声明 100 B、实际 4 MiB） | `IsValid=false`；extract 增量 **< 4 MiB**（实测远低于炸弹体积） |
| 3 个测试方法之外还断言了 `DiskBytes(extract)` 的量化上限 | 见上 |

说明：篡改声明的包在**更早的清单/entry 校验**（`entry-missing`/`hash-file-missing`）就被拒，未走到"提取期"膨胀守卫；两种路径都满足"拒绝 + 磁盘未被写满"，但本轮没有构造出"通过清单校验后才膨胀"的包（需真实签名清单 + 合法 PE 入口），已在 §3 记为覆盖缺口。

### A5 webhook SSRF

```text
http://oapi.dingtalk.com/…            → scheme_not_https
https://oapi.dingtalk.com.evil.example → host_not_allowed
https://evil.example/…                 → host_not_allowed
https://user:pass@oapi.dingtalk.com/…  → credentials_in_url
https://oapi.dingtalk.com:8443/…       → non_default_port
https://1.2.3.4/… / https://[::1]/…    → host_not_allowed
not-a-url → invalid_url;  空 → missing
放行：oapi.dingtalk.com / api.dingtalk.com / sub.dingtalk.com（端口 443）
```

零请求依据：`DingTalkWebhookTransport.SendAsync` 在 `_http.SendAsync` 之前调用 `DingTalkSessionWebhookPolicy.TryValidate` 并直接返回 `session_webhook_rejected`；`DingTalkChatUpstream` 在组装回复时同样先校验（源码位置：`DingTalkWebhookTransport.cs:94`、`DingTalkChatUpstream.cs:167/250`）。本轮未注入 counting handler 到 transport 层做端到端零请求计数（§3）。

### B4 Source 往返

```text
写入：Source(discord, discord, "bot:instance:42") + ConversationId "c:1"
读回：Protocol/Platform 不变，SelfId == "bot:instance:42"，ConversationId == "c:1"
投递：MessageSendCommand.Source.Key == "discord:discord:bot:instance:42"，Conversation.Id == "c:1"
```

实现要点：`agent_tasks` 新增 `source_protocol/source_platform/source_self_id` 列，读取优先结构化列，`source_key` 仅作 v4 之前行的 legacy fallback（不再 `Split(':', 3)` 猜测）。

### B10 取消

```text
cts.Cancel() → RespondAsync(event, token)
→ OperationCanceledException
→ RecordingChatClient.CallCount == 0
```

即取消发生在进入 `IChatClient` 之前，不是"抛了异常但模型已调用"。

### C4 环隔离

```text
图：a → b → a（环）+ standalone（无依赖）
standalone：不在 Errors，出现在 Order
a / b：phase=dependency，code 含 cycle，message 点名另一环成员
```

### E3 degraded 退出码

```text
已连接后断开的上游 → RuntimeHealth status="degraded"；CliHealthExitCodes.Degraded == 4
空 registry（--no-plugins / --offline 的静态视图形态）→ status="unknown"（不会被当成 degraded）
```

### A20 gate

```text
2000 次不同 key 的 Acquire/Release → ConversationCount ≤ MaxConversationEntries(64)
Dispose 与在途 lease 并发 → lease.Dispose() 无异常，ActiveOperationCount == 0
```

### C7 legacy 签名（部分）

```text
PluginStoreOptions.RejectLegacySignatureFormat 默认 false
BuildLegacyCanonicalText 与 BuildCanonicalText 输出可区分（后者含 "nps-hash-v2"）
```

---

## 3. 覆盖缺口（已核实但未独立验证）

| 项 | 原因 | 建议 |
| --- | --- | --- |
| **C3 ALC 回收**（WeakReference + 多轮 GC） | 需要真实 NPS 插件加载/卸载循环与程序集弱引用；本轮时间用于其余 9 个反向点 | 由实现方 `PluginPackageSecurityTests`（542 行）覆盖；如需独立证据，可用 `NpsPluginRuntime` 加载 `samples/Natter.Sample.Plugin.Greetings` 后卸载 + `WeakReference` 断言 |
| **C7 完整签名链** | **未补齐（记录原因）**：需要按 validator 的文件排序规则复刻 canonical text、用 ECDSA P-256 对 v1 文本签名并把 `package.sig`/`package.sha256` 一起封进包；本轮时间用于缺陷定位（C3）与 Release 重建口径，签名链留作后续 | 实现方用例覆盖；建议下次用 `PluginPackageWriter` 的签名入口生成 legacy 包后再验 `RejectLegacySignatureFormat=true` |
| ~~**C1 "通过清单校验后再膨胀"**~~ | **task-44 已补齐**：合法 manifest + 真实 PE + 正确清单，只有 zip header 撒谎；实测被 `hash-mismatch` 拒绝（.NET 按声明长度只解出 100 B），磁盘增量 ≤ 2 MiB | 若要对齐"in-loop 守卫"路径，可再用 `MaxEntryUncompressedBytes` 调小构造 |
| ~~**A5 transport 级零请求计数**~~ | **task-44 已补齐**（counting handler，0 请求） | — |
| **task-40 其余（A2/A3/A4/A6/A10/A11/A16）**、task-39（A7/A8/A9/A17-A19）、task-38（B1-B9/B11）、task-37（C5/C6/C8/C10-C13/C15）、task-41（E1-E13 其余） | 由实现方 789/571/542/469/400 行测试与 832 全量回归覆盖；本轮按 lead 指定清单做反向抽验 | 后续如发现 flaky 或语义争议再逐项加锁 |

---

## 3.1 缺陷（task-44 新发现，Medium；已由 task-43 的 CLI 注销改动消除）

**BUG-S2-C3-UNREGISTER**：插件卸载后其 CLI 命令仍可解析。

```text
host.StartAsync()  → registry.TryGet("greet") = true, catalog state = Activated
host.StopAsync()   → catalog state = Stopped, registry.TryGet("greet") = true   ← 不应成立
```

- 根因：`CliCommandRegistry.UnregisterOwner` 只有定义，整个仓库**没有任何调用点**；插件 stop/unload 时宿主没有撤销命令贡献。
- 影响 ①：卸载后命令仍可解析，执行可能命中已卸载程序集的类型（功能面）；
- 影响 ②：注册表持有命令描述符（含 handler 工厂）→ 插件类型被强引用 → 宿主级 ALC 无法回收（与 merge-verify 结论一致，task-43 的排查对象之一）。
- 处置：**未改 src**。先在测试里固化为复现（断言当前行为），task-43 的 `CliCommandRegistry` 注销改动出现在工作区后，用例改为正向断言 `Assert.False(registry.TryGet(...))` 并保留注释说明「在未含该改动的 HEAD 上会失败」——避免缺陷回归，也不做任何假清理。
- 现状：工作区已含 `IPluginCliCommandSink`/`NpsPluginRuntime`/`CliCommandRegistry` 的未提交改动，C3 用例在其上通过；该改动提交后本用例即为稳定门禁。

## 3.2 全量跑被工作区他人改动阻塞（记录，不计入本轮）

Release 重建后跑**全量**时 10/10 稳定失败在两个**未提交**的新文件上（`tests/Natter.Framework.Tests/PluginTeardownTests.cs`，伴随 `src/Natter.Cli.Core/Cli/*`、`src/Natter.Infrastructure/Plugins/Nps/*` 的未提交改动）：

```text
PluginTeardownTests.Repeated_stop_and_dispose_are_idempotent       → ObjectDisposedException: IServiceProvider
PluginTeardownTests.Plugin_commands_are_visible_after_activation_only → Assert.True failed
```

这不是本轮验收对象，也不是 flake（10/10 稳定），且修复只允许动 src（不在本任务写作用域）。因此本轮 10×Release / 3×Debug 用 `--filter "FinalS1AcceptanceTests|S2Batch1AcceptanceTests"` 只跑本次交付的两个验收文件，并在报告中显式记录这一范围裁剪；`PluginTeardownTests` 由 task-43 负责人闭环。

## 4. 未测 / 范围外

- 真实签名包安装、真实 NPS 插件卸载回收、真实钉钉/Discord 网络路径；
- `/opt/natter`、生产服务、soak 主机：未触碰；未部署、未重启；
- 未执行 `git commit`/`push`，未修改 `src/`、`plugins/`、`scripts/`、`deploy/`。

---

## 5. 复现命令

```bash
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~S2Batch1AcceptanceTests"          # 21/21
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj   # 832/832（×3）
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj  # 314/314
```
