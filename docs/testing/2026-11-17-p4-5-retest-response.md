# 开发侧处置记录：P4.5 联合复测（Conditional Pass）

> **状态**：Fixed · Ready for joint retest（OBS-015/016/017 已修、DT/发送字段已暴露；OBS-018 与真人回归/运维项待测试侧）  
> **日期**：2026-11-17  
> **基线**：`HEAD=24922c7`（含 `0778c1b` 修复）  
> **对应测试**：[P4.5 联合复测报告（测试侧）](2026-11-17-p4-5-retest-report.md)  
> **对应运维**：[P4.5 联合复测（运维侧执行记录）](../operations/2026-11-17-p4-5-retest-run.md)  
> **对应计划**：[P4.5 联合复测计划](2026-11-17-p4-5-retest-plan.md)

---

## 1. 结论

接受测试/运维的 **Conditional Pass**。

本轮确认：

- 代理 gateway 冲突**已不复现**，根因是测试装置使用旧 staging 插件；
- 同 commit/config 下 **<10s READY**；
- `BUG-P4.5-PROXY-WS` 关闭；
- `--duration 60` 坏代理 **60.1s** 退出，原 151s 延迟关闭；
- DingTalk `lastConnectedAt` 已正常；
- health/offline/Tier1-only 通过；
- 245/245 回归。

但 P4.5 不能签 Go，因为：

1. P4 真人回归未完成（`/ask` `/remember` `/memory` `/forget` `/reset`、DM、guild、钉钉消息）；开发侧已完成代码修复，真人步骤仍待执行；
2. DingTalk `lastEventAt` 未触发真实消息；
3. `sendFailures` 受控探针首次 flaky，未形成稳定用例；
4. systemd/Docker/24h soak/secret 轮换仍 BLOCKED；
5. 新增 `OBS-015`/`016`/`017`/`018` 需要收敛。

---

## 2. 处置总表

| ID | 级别 | 处置 | 责任侧 | 关闭条件 |
| --- | --- | --- | --- | --- |
| BUG-P4.5-PROXY-WS | High | **关闭**：旧 staging 导致，重 staging 后 <10s READY | Test/Ops | 已完成 |
| BUG-P4.5-DT-TIMESTAMPS | Medium | **部分关闭**：lastConnectedAt 通过；lastEventAt 待真实消息 | DingTalk/Test | 收到消息后非 null |
| OBS-009 duration 延迟 | 中 | **关闭**：60.1s | CLI/Host | 已完成 |
| OBS-010 phase 诊断 | 中 | 保留；phase 仍可能误导 | Discord transport | 见 OBS-015 |
| OBS-015 phase 非真实阶段 | 中 | 接受；phase 改为连接状态/最后异常 | Discord transport | NO_PROXY 下不误报 |
| OBS-016 静默断连 lastError=null | 高/运维阻断 | 接受；Disconnected 异常写 LastError | Discord transport | 断连有原因 |
| OBS-017 取消信息退化 | 中 | 接受；取消单独标记，不伪装失败 | CLI/Host | canceled 可区分 |
| OBS-018 sendFailures 缺注入 | 中 | 接受；正式 live probe + 重试/预热 | Test/Discord | 稳定用例 |
| FLAKE-001 probe 首次找不到 upstream | 中 | 接受；等待激活/重试/输出 Failed | Test | 不再 flaky |
| P4 真人回归 | 阻塞 | 待人工 | Test | 命令/DM/guild/钉钉全绿 |
| systemd/Docker/24h/secret | 阻塞 | BLOCKED | Ops | 实机/权限完成 |

---

## 3. 代理冲突关闭

### 3.1 结论

`BUG-P4.5-PROXY-WS` **关闭**。

测试复测：

- 旧 staging 插件导致 60s 未 ready；
- 重新 staging `c131e73`/`24922c7` 后：
  - 启动 ~5s READY；
  - 120s 采样全部 healthy；
  - duration 120 正常退出；
- 运维复测：
  - 120s/60s 两轮均 <10s READY；
  - 无 phase 失败。

### 3.2 需要保留的约束

- 测试 staging 必须与 HEAD 同步；
- 代理链路仍需 `readyTimeoutSeconds>=90` 作为保守值；
- 生产部署必须固定代理变量；
- 不允许用旧 `plugins/all` 直接复测。

### 3.3 验收

- 同 commit/config 下 gateway READY；
- DM/guild 真实消息仍待补；
- 旧 staging 问题写入测试规范，避免再次误判。

---

## 4. DingTalk timestamps

### 4.1 现状

- `lastConnectedAt` 非 null；
- `lastEventAt` 仍为 null（实例在线 1h 无真实钉钉消息）；
- `lastDisconnectAt`/`reconnectCount`/`sendFailures` 字段存在。

### 4.2 验收

- 发一条真实钉钉群 @bot 消息；
- `lastEventAt` 非 null；
- 断连后 `lastDisconnectAt` 非 null；
- `lastError` 有脱敏原因（见 OBS-016）。

---

## 5. OBS-015：phase 不等于真实阶段

### 5.1 现象

`NO_PROXY=discord.com`（直连失败）时报：

```text
phase 'identify_ready'
```

看起来 REST/WS 都成功，实际是后台连接失败后 READY 超时。

### 5.2 修复

- phase 不应只由 `ConnectAsync` 的 await 点决定；
- 从 Discord.Net 连接状态/最后 `Disconnected` 异常推导；
- 超时消息附带：
  - `lastDisconnectedError`；
  - 最后一次连接状态；
  - 是否走代理；
- 日志/health 脱敏。

### 5.3 验收

- `NO_PROXY` 直连失败时，phase 指向 `ws_connect` 或 `rest_gateway`，不是 `identify_ready`；
- 代理成功时 phase 不误导；
- 错误信息能区分直连/代理/取消。

---

## 6. OBS-016：静默断连 lastError=null

### 6.1 现象

两处静默断连：

- 4.6h：`lastDisconnectAt=09:21:54Z`、`reconnectCount=2`、`lastError=null`；
- 1.5min：`lastDisconnectAt=09:33:05Z`、`reconnectCount=2`、10s 未恢复、`lastError=null`。

### 6.2 影响

能看到“断了”，看不到“为什么断”；OBS-007 仍不可解释。

### 6.3 修复

- `DiscordSocketTransport.OnDisconnectedAsync` 把异常消息（脱敏）传给 upstream；
- `DiscordChatUpstream` 写入 `LastError`；
- health 暴露 `lastDisconnectReason` / `lastError`；
- 不写 token/代理凭据；
- 补断流/代理重启测试。

### 6.4 验收

- 人为断流后 `lastError` 非 null；
- 能区分超时、代理拒绝、WebSocket 关闭；
- 重启可恢复。

---

## 7. OBS-017：取消信息退化

### 7.1 现象

坏代理 `duration=60` 到点取消：

```text
upstream-connect-failed: A task was canceled.
```

合法取消和真实失败混在一起。

### 7.2 修复

- 取消路径单独返回 `canceled`；
- 保留最后 phase/底层错误；
- 日志区分：
  - `canceled by duration`
  - `connect failed`
  - `SIGTERM`
- 不影响 health。

### 7.3 验收

- `duration=60` 日志显示 canceled，不是连接失败；
- SIGTERM 仍 131ms 优雅；
- 坏代理真实失败仍可诊断。

---

## 8. OBS-018 / FLAKE-001：sendFailures

### 8.1 现状

- 字段已进 health；
- 探针第 2 次通过：`sendFailures=1`、`lastSendError=not_found: channel 0`；
- 第 1 次 2m30s 后找不到 discord upstream。

### 8.2 修复

- 测试侧补回正式 `SendFailureProbeTests`；
- 启动后等待 upstream 激活，不要固定 90s 后 `First()`；
- 失败时输出 `plugins list --detail`；
- 加一次重试/预热；
- 生产侧不需要 `--fail-send`，测试用无效 channel 即可；
- 字段保持脱敏。

### 8.3 验收

- 连续 3 次探针稳定；
- `sendFailures` 递增；
- `lastSendError` 脱敏；
- 不影响正常消息。

---

## 9. P4 真人回归

代理 READY 后必须补：

- `/ask`；
- `/remember`；
- `/memory`；
- `/forget`；
- `/reset`；
- DM；
- guild @bot；
- DingTalk 群 @；
- 重启后 memory/session。

记录：

- 消息 id；
- 回复耗时；
- `sendFailures=0`；
- `lastEventAt`；
- 无重复回复。

---

## 10. 运维 BLOCKED 项

- systemd：root 实机、unit、restart、on-failure、SIGTERM；
- Docker：build/run/restart/persistence；
- 24h soak：loopback 采样，OBS-007 证据；
- secret 轮换：平台控制台权限。

这些不修产品，但在 P4.5 签收前必须完成或明确豁免。

---

## 11. 修复顺序

```text
1. P4 真人回归 + DingTalk lastEventAt
2. OBS-015 phase
3. OBS-016 lastError
4. OBS-017 canceled
5. OBS-018 / FLAKE-001 sendFailures probe
6. systemd / Docker
7. 24h soak
8. secret 轮换
9. P4.5 Go
```

---

## 12. 归档规则

本文是临时配对文档。

归档条件：

- `BUG-P4.5-PROXY-WS` 关闭（已完成）；
- `BUG-P4.5-DT-TIMESTAMPS` 完全关闭；
- OBS-015/016/017/018/FLAKE-001 收敛；
- P4 真人回归通过；
- 运维 systemd/Docker/24h/secret 完成；
- 双方 Go；
- 干净 HEAD 复跑。

---

## 13. 一句话

**代理冲突已关闭，duration/DingTalk health 也已通过；P4.5 现在卡在真人回归、sendFailures 稳定性、断连原因可见性和 systemd/Docker/24h。**

先补 P4 真人回归和 lastEventAt，再修 OBS-015/016/017，最后做运维收口；全部通过才签 Go。
