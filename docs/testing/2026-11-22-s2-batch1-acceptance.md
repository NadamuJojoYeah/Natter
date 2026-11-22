# 2026-11-22 · S2 第一批独立验收（task-42，重点反向验证）

> **执行**：qa（task-42）
> **基线**：`HEAD=43804b5`（S2 第一批：`9816057`/`8cef9ce` 包与存储、`1baca70`/`2500aee` 任务运行时、`3068ec7`/`43804b5` Satori+AI、`33e489a`/`a1d97da` 钉钉+Discord、`e729fca`/`e1526be` CLI+运维、`37838c7` 签名规范）
> **新增测试**：`tests/Natter.Framework.Tests/S2Batch1AcceptanceTests.cs`（**21 个用例**，全部为反向/边界断言）
> **关联**：[最终 S1 验收](2026-11-22-final-s1-acceptance.md)、[审查修复验收](2026-11-22-review-acceptance.md)

---

## 1. 结论

| 反向验证点 | 结论 | 证据要点 |
| --- | --- | --- |
| **C1 zip bomb** | **通过** | 4 MiB 零字节高压缩比包 → 拒绝（compression ratio 规则），extract 增量 **0 字节**；手工把 local/central header 的 uncompressed size 改成 100 的"撒谎包" → 拒绝且 extract 增量 **< 4 MiB** |
| **A5 webhook SSRF** | **通过** | 9 组恶意/畸形 webhook 全部拒绝并给出稳定原因码（`scheme_not_https`/`host_not_allowed`/`credentials_in_url`/`non_default_port`/`invalid_url`/`missing`）；`oapi.dingtalk.com.evil.example`、IP 直连、userinfo 全拒；3 组官方 host 仍放行 |
| **B4 Source 往返** | **通过** | `selfId=bot:instance:42` + `conversation=c:1` 经 SQLite 往返完全一致，任务结果能按完整 `discord:discord:bot:instance:42` 投递到原会话 |
| **B10 取消不被吞** | **通过** | 已取消的 token 调 `RespondAsync` → 抛 `OperationCanceledException`，且 fake 模型 **调用次数 = 0** |
| **C3 ALC 回收** | **未独立验证** | 本轮未做 WeakReference 回收验证，见 §3 说明 |
| **C4 环隔离** | **通过** | a↔b 环 + standalone：standalone 无 error 且在 order 中；a/b 报 `phase=dependency`/code 含 cycle 且消息点名环成员 |
| **E10 token 不进 argv** | **通过** | `scripts/p2-collect.sh` 无 `-H "Authorization`，含 `-K -`（curl config stdin） |
| **E3 degraded 退出码** | **通过** | disconnected upstream → `status=degraded` 且 `CliHealthExitCodes.Degraded == 4`；空 registry 静态视图 → `unknown`（不误判为 degraded） |
| **C7 legacy 签名** | **部分验证** | 默认 `RejectLegacySignatureFormat=false`；legacy 与 v2 canonical text 可区分（v2 含 `nps-hash-v2`，legacy 不含）；完整"true 时拒绝 legacy 签名"由实现方用例覆盖，本轮未构造签名链 |
| **A20 gate** | **通过** | 2000 个不同 key 后 `ConversationCount ≤ 64`（容量上限生效，字典不单调增长）；Dispose 期间在途 lease 释放 **不抛 ObjectDisposedException**，`ActiveOperationCount=0` |

| 回归 | 结果 |
| --- | --- |
| 新增验收 | **21/21** |
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
| **C7 完整签名链** | 需生成 ECDSA P-256 密钥、签名 v1/v2 canonical text 并构造包 | 实现方已覆盖；本轮验证了开关默认值与两种格式可区分 |
| **C1 "通过清单校验后再膨胀"** | 需要真实 `package.sha256` + 合法入口程序集，否则更早的校验短路 | 如需端到端，可用已签名样例包注入一个膨胀条目 |
| **A5 transport 级零请求计数** | 未向 `DingTalkWebhookTransport` 注入 counting handler | 源码位置已证明校验先于发送；可补 `HttpMessageHandler` 计数 |
| **task-40 其余（A2/A3/A4/A6/A10/A11/A16）**、task-39（A7/A8/A9/A17-A19）、task-38（B1-B9/B11）、task-37（C5/C6/C8/C10-C13/C15）、task-41（E1-E13 其余） | 由实现方 789/571/542/469/400 行测试与 832 全量回归覆盖；本轮按 lead 指定清单做反向抽验 | 后续如发现 flaky 或语义争议再逐项加锁 |

---

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
