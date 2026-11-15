# 开发侧处置记录：2026-11-15 P2 验收测试（No-Go）

> **状态**：Fixed · Ready for test-side retest（4 High 代码修复完成；真实 DM 回复仍需人工复测）  
> **类型**：测试配对处置记录，不是长期设计文档  
> **对应测试**：[P2 验收测试](2026-11-15-p2-acceptance.md)  
> **对应计划**：[P2 Acceptance Test Plan](P2_ACCEPTANCE.md)  
> **基线**：`HEAD=f114f7e`；测试侧新增测试资产，产品代码未修改  
> **归档**：4 个 High 缺陷关闭、Gate A/B/D/E/F/G 复测通过后，与测试记录一并归档

---

## 1. 结论

接受测试侧的 **No-Go（暂缓签收）**；开发侧已完成 4 个 High 与 3 个 Medium 的代码收敛，等待测试侧从干净 HEAD 复跑 Gate A/B/D/E/F/G。

### 1.1 开发修复摘要

- `BUG-P2-SIGINT`：CLI 入口注册 `PosixSignalRegistration`（SIGINT/SIGTERM），并重置后台进程继承的 `SIG_IGN`；`scripts/p2-signal-smoke.sh` 验证 INT/TERM 均 5s 内 exit=0。
- `BUG-P2-OVERRIDE`：`ProfileResolver` 在 override 存在时直接返回该 profile，不再打分；`RuntimeProfileStore.ReloadAsync` 保留 override；override provider 缺失时启动/校验失败。
- `BUG-P2-DM-ALLOWLIST`：DM/Group DM 跳过 guild/channel allowlist，user allowlist 仍生效；`DiscordDmAllowlistTests` 已翻转为修复回归。
- `BUG-P2-DM-SEND`：`DiscordSocketTransport` 缓存入站 `IMessageChannel`（TTL 30min、容量 500），发送优先取缓存；`discord status` 增加 `sendFailures` / `lastSendError`。
- `OBS-P2-HEALTH`：空 upstream → `unknown`；断连/provider 缺失/插件失败 → `degraded`；CLI 与 `/health` 共用 `RuntimeHealth`。
- `OBS-P2-STATE-PERMS`：state 目录 0700，DB/WAL/SHM 0600（Windows 跳过）。
- `OBS-P2-DIAG`：serve/health 输出失败插件 `id/state/phase/code/message`，并对 key 类字符串脱敏。
- `OBS-P2-PLUGINCOUNT`：文档明确 `--no-plugins` 禁用所有插件加载，包括显式 `--plugin-dir`。

当前框架回归：**214/214**。真实 Tier 1 E2E：**3/3**；DingTalk Stream handshake：**2/2**；Spark MaaS：**7/7**。


当前 P2 不能进入 P3，原因不是“功能不够”，而是 4 个 High 缺陷直接影响产品底座：

1. `BUG-P2-SIGINT`：非 TTY 场景 SIGINT 无法优雅停止；
2. `BUG-P2-OVERRIDE`：`--profile` 不能强制覆盖 match；
3. `BUG-P2-DM-ALLOWLIST`：DM 被 guild/channel allowlist 静默丢弃；
4. `BUG-P2-DM-SEND`：DM 回复发送 `not_found`。

另有 3 个 Medium 观测需要在签收前处理或明确豁免：

- `OBS-P2-HEALTH`：空 upstream 仍报 healthy；
- `OBS-P2-STATE-PERMS`：state 目录/数据库权限过宽；
- `OBS-P2-DIAG`：serve 不打印插件失败原因。

已确认真实通过的部分应保留为基线证据：

- Discord → DeepSeek guild @mention：2s，reply 引用正确；
- DingTalk Stream → Spark：群 @，11 字纯文本回复；
- 双 upstream 同时在线，profile 路由不串；
- 真实会话重启恢复（2 sessions / 4 messages）；
- SQLite source/actor 隔离；
- Tier 2 缺失不阻塞 Tier 1 启动；
- 日志无密钥。

---

## 2. 处置总表

| ID | 严重度 | 处置 | 责任侧 | 签收闸门 |
| --- | --- | --- | --- | --- |
| BUG-P2-SIGINT | High | 接受；改为 POSIX signal 注册 | CLI / Host | 必须关闭 |
| BUG-P2-OVERRIDE | High | 接受；override 强制优先，reload 保留 | Application / Profiles | 必须关闭 |
| BUG-P2-DM-ALLOWLIST | High | 接受；DM 绕过 guild/channel allowlist | Discord transport | 必须关闭 |
| BUG-P2-DM-SEND | High | 接受；缓存入站 channel / REST fallback | Discord transport | 必须关闭 |
| OBS-P2-HEALTH | Medium | 接受；区分 healthy/degraded/unknown | CLI / Health | 必须收敛 |
| OBS-P2-STATE-PERMS | Medium | 接受；state 0700、DB 0600 | Host / Persistence | 必须收敛 |
| OBS-P2-DIAG | Medium | 接受；serve/health 输出失败插件原因 | CLI / NPS | 必须收敛 |
| OBS-P2-HEALTH-FIELDS | Low | 接受；统一 health schema | CLI | 可后续 |
| OBS-P2-PLUGINCOUNT | Low | 文档说明或拆分 no-default 选项 | CLI | 可后续 |

---

## 3. BUG-P2-SIGINT

### 3.1 根因

`Program.cs` 只注册 `Console.CancelKeyPress`。  
非 TTY（stdout 重定向、容器、CI、nohup）下 SIGINT 没有触发该回调；SIGTERM 也没有显式注册，直接默认终止。

`ServeCommand` 的 token 传递链本身正常；问题在入口信号注册。

### 3.2 修复要求

- 在 CLI 入口注册 `PosixSignalRegistration`：
  - `SIGINT`；
  - `SIGTERM`；
- 回调里 `context.Cancel = true`，取消同一个 `CancellationTokenSource`；
- 保留 `Console.CancelKeyPress` 兼容 Windows 控制台；
- 只取消一次，避免双重 shutdown；
- `ServeCommand` 收到取消后正常返回；
- Host `StopAsync` 仍保持 10 秒兜底；
- 不改变 `serve --duration` 行为。

参考文件：

- `src/Natter.Cli/Program.cs`
- `src/Natter.Cli.Core/Cli/NatterCli.cs`
- `src/Natter.Cli.Core/Commands/RuntimeCommands.cs`

### 3.3 测试

- 非 TTY：后台启动 `serve`（`duration=0`），`kill -INT`，5s 内退出；
- 非 TTY：`kill -TERM`，5s 内退出；
- TTY：Ctrl+C 仍正常；
- `--duration 3` 仍按时退出；
- 进程退出码为 0；
- 日志有取消/shutdown 记录；
- 测试在 Linux 运行；Windows 用可用的等价信号测试或标注 skip。

---

## 4. BUG-P2-OVERRIDE

### 4.1 根因

`ProfileResolver.Resolve` 先按 match 打分；`OverrideProfile` 只在无 match 时使用。  
因此 `--profile tier2-dingtalk-spark` 对 Discord 消息仍然解析为 `tier1-default`。

另外 `RuntimeProfileStore.ReloadAsync` 重新加载后会丢失 override。

### 4.2 修复要求

- `--profile` 是**强制覆盖**，不是 fallback：
  - override 存在时，直接返回该 profile；
  - 仍校验 profile 存在、enabled、provider 可用；
  - 不再执行 match 打分。
- `RuntimeProfileStore`:
  - 保存 `_profileOverride`；
  - `ReloadAsync` 重新应用 override；
  - override 不存在时启动失败。
- Tier 1 override provider 缺失 → 启动失败；
- Tier 2 override provider 缺失 → 明确失败或禁用，不能悄悄 fallback 到 DeepSeek；建议显式失败，因为用户明确指定了 profile。
- 更新测试：
  - `Override_profile_does_not_force_when_a_match_exists` 翻转为期望 override；
  - 增加 reload 后 override 仍生效；
  - 增加 override + missing provider 的行为。

参考文件：

- `src/Natter.Application/Chat/Profiles/RuntimeProfiles.cs`
- `src/Natter.Application/Chat/ProfileAwareChatResponder.cs`
- `src/Natter.Application/Chat/RuntimeProfileValidator.cs`

---

## 5. BUG-P2-DM-ALLOWLIST

### 5.1 根因

`DiscordEventMapper.MapMessage` 先执行：

```text
IsGuildAllowed(message.Guild?.Id)
IsChannelAllowed(message.Channel.Id)
```

DM 的 `Guild` 为 null；配置了非空 guild allowlist 时直接失败。  
即使 guild 通过，DM channel id 也不在 channel allowlist 中。

### 5.2 修复要求

- 先判断 conversation kind；
- `Direct` / `Group`（DM/Group DM）不应用 guild/channel allowlist；
- 用户 allowlist 仍然生效；
- guild/thread 仍按 guild/channel allowlist；
- 过滤时输出 debug 日志（source、conversation、原因），不记录正文；
- 不允许把 DM 当作 guild channel 做 allowlist。

### 5.3 测试

- 有 guild+channel allowlist 时，DM 仍映射；
- DM 用户不在 user allowlist → 仍丢弃；
- guild channel 不在 allowlist → 仍丢弃；
- thread 仍按 guild/channel 规则；
- `DiscordDmAllowlistTests` 从“复现缺陷”改为“修复回归”。

参考文件：

- `src/Natter.Chat.Discord/DiscordEventMapper.cs`
- `tests/Natter.Framework.Tests/DiscordDmAllowlistTests.cs`

---

## 6. BUG-P2-DM-SEND

### 6.1 根因

`DiscordSocketTransport.GetMessageChannelAsync` 只查 `_client.GetChannel(id)` 本地缓存。  
DM channel 未进入该缓存，REST `/users/@me/channels` 对 bot 返回 `[]`，于是发送阶段 `not_found`。

DM 消息已经到达宿主、完成路由和生成；失败只在发送阶段。

### 6.2 修复要求

优先级从高到低：

1. **缓存入站 channel**  
   `OnMessageReceivedAsync` / `OnInteractionCreatedAsync` 时，把 `message.Channel` / `interaction.Channel` 按 channel id 缓存；
2. `GetMessageChannelAsync` 先查该缓存；
3. 再查 `_client.GetChannel`；
4. 最后才尝试 REST fallback（如果可用）；
5. 缓存必须有上限/TTL，避免长跑内存增长；
6. 发送失败时：
   - `discord status` 暴露 `send_failures` / `last_send_error`；
   - 日志带 channel id 和错误码；
   - 不把完整消息正文写入日志。

不要用 `GET /users/@me/channels` 作为唯一方案：测试已证明 bot 可能拿不到 DM channel 列表。  
关键是把“入站时的 channel 对象”保留下来用于回复。

### 6.3 测试

- 真实 DM：收到 → 回复成功；
- guild channel 仍正常；
- thread/群组 DM 尽可能覆盖；
- 发送失败计数可见；
- 重启后没有历史 channel 时，行为明确（not_found + 诊断，而不是静默）；
- `DiscordSocketTransport` 的 channel 缓存有单元测试或可测 seam。

参考文件：

- `src/Natter.Chat.Discord/DiscordSocketTransport.cs`
- `src/Natter.Chat.Discord/DiscordChatUpstream.cs`
- `src/Natter.Cli.Core/Commands/RuntimeCommands.cs`

---

## 7. OBS 处置

### OBS-P2-HEALTH

当前 `upstreams.All(connected)` 对空集合为 true。  
修复：

```text
no upstreams        → unknown（或 degraded）
any disconnected    → degraded
all connected       → healthy
any provider unavailable → degraded
failed plugins > 0  → degraded
```

`natter health` 与 `/health` 使用同一个 snapshot builder，避免语义漂移。

### OBS-P2-STATE-PERMS

- state 目录创建时 Unix mode `0700`；
- `natter.db`、`-wal`、`-shm` 创建后收紧到 `0600`；
- 已存在的过宽权限启动时收紧或 warning；
- Windows 用 ACL/文档说明；
- P2-063 必须从“不通过”变为“通过”。

参考：`src/Natter.Infrastructure/Chat/SqliteConversationStore.cs`、Host state 初始化。

### OBS-P2-DIAG

`serve` 启动摘要增加：

```text
plugins.failed[] = { id, state, phase, code, message }
```

`health` 同样输出失败插件摘要。  
错误消息必须脱敏，不包含 secret、正文、完整 prompt。

### OBS-P2-HEALTH-FIELDS

统一 `natter health --json` 与 `/health` 的字段：

- `status`；
- `uptimeSeconds`；
- `profiles`；
- `conversations`；
- `upstreams`；
- `providers`；
- `plugins`。

### OBS-P2-PLUGINCOUNT

`--no-plugins` 当前会连显式 `--plugin-dir` 一起禁用。  
两种处理任选：

1. 文档明确：`--no-plugins` 禁用所有插件加载；
2. 拆分 `--no-default-plugins`，只禁用默认目录。

P2 签收前至少文档写清楚，避免测试/部署误用。

---

## 8. 测试侧资产处置

保留：

- `tests/Natter.Framework.Tests/ProfileRoutingTests.cs`
- `tests/Natter.Framework.Tests/DiscordDmAllowlistTests.cs`
- `tests/Natter.Framework.Tests/SqliteConversationStoreTests.cs` 新增用例
- `tests/Natter.Framework.Tests/SparkChatClientTests.cs` 429 用例
- `scripts/p2-serve.sh`
- `scripts/p2-collect.sh`
- `docs/testing/2026-11-15-p2-acceptance.md`
- `docs/testing/P2_ACCEPTANCE.md`

规则：

- 测试资产可以保留；
- 当前“复现缺陷”的断言在修复后要翻转，不能直接删除；
- 测试侧不要修改产品代码；
- 修复 PR 必须带对应用例；
- 不把 `artifacts/p2/`、state、插件 staging、`.env` 提交。

---

## 9. 修复顺序

```text
1. BUG-P2-SIGINT
2. BUG-P2-OVERRIDE
3. BUG-P2-DM-ALLOWLIST
4. BUG-P2-DM-SEND
5. OBS-P2-HEALTH / DIAG / STATE-PERMS
6. 复跑 Gate A/B/C
7. 真实 DM + guild + DingTalk 复测
8. Gate F/G
9. P2 签收
```

为什么这个顺序：

- SIGINT 影响所有长驻/部署；
- override 影响所有 profile 路由；
- DM allowlist 与 DM send 是同一用户路径，先修过滤再修发送；
- health/state/diag 是签收前必须收敛的运维问题。

---

## 10. 复测闸门

修复后必须：

- [x] `ProfileRoutingTests` override 用例翻转并通过；
- [x] 非 TTY `kill -INT` / `kill -TERM` 5s 内退出（`scripts/p2-signal-smoke.sh`）；
- [x] 有 allowlist 时 DM 映射；
- [ ] 真实 DM 回复成功（缺人工 DM 测试者，待复测）；
- [x] guild @mention 仍成功（E2E 3/3）；
- [x] DingTalk Stream handshake / Spark MaaS 契约仍成功；
- [ ] 双 upstream 同时在线且路由不串（本轮未重跑真实双上游）；
- [ ] 重启后双 source 历史恢复（SQLite 单测通过，真实双 source 待重跑）；
- [x] `health` 空 upstream 不再报 healthy；
- [x] state 0700 / DB 0600；
- [x] serve/health 输出失败插件原因；
- [x] 框架回归全绿（214/214）；
- [ ] 日志/配置/state 权限扫描通过（待测试侧）；
- [ ] 测试记录归档并更新索引。

P2 签收前不进入 P3。

---

## 11. 归档规则

本文是临时配对文档。

归档条件：

- 4 个 High 关闭；
- 3 个 Medium 收敛或明确豁免；
- Gate A/B/D/E/F/G 复测通过；
- 测试负责人给出 Go；
- 干净 HEAD 复跑。

归档动作：

1. 与 [P2 验收测试](2026-11-15-p2-acceptance.md) 一起移入归档目录；
2. 标记 `Archived`；
3. 更新 [测试记录索引](README.md)；
4. 保留缺陷编号、修复提交 SHA、复测证据；
5. 不保留 state/插件 staging/凭据。

---

## 12. 一句话结论

**P2 的架构方向通过，但产品底座还有 4 个 High。**  
先把 SIGINT、profile override、DM allowlist、DM send 修完，再从干净 HEAD 复跑 Gate A/B/D/E/F/G；在这些关闭前，不进入 P3。
