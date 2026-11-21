# 开发侧处置记录：P4.5 修复验证（测试 + 运维联合）

> **状态**：Fixed · Ready for joint retest（代理冲突仍需受控复现；DingTalk 时间戳、duration、phase、sendFailures、Docker 模板已收敛）
> **复测计划**：[P4.5 联合复测计划](2026-11-17-p4-5-retest-plan.md)
> **日期**：2026-11-17
> **基线**：`HEAD=c131e73`
> **对应测试**：[P4.5 修复验证（测试报告）](2026-11-17-p4-5-test-report.md)
> **对应运维**：[P4.5 运维验收执行记录](../operations/2026-11-17-p4-5-ops-verification-run.md)
> **配套计划**：[测试交接](2026-11-17-p4-5-test-verification.md)、[运维交接](../operations/2026-11-17-p4-5-ops-verification.md)

---

## 1. 结论

测试与运维都执行了，但两边结论不能简单合并成“通过”：

- **测试**：No-Go（代理下 `serve` gateway 60s 未 ready，P4 回归被阻塞）；
- **运维**：有条件通过（代理下升级后 `connected=true`，但 17 项 root/长跑/平台项 BLOCKED）；
- **冲突点**：同一个 proxy 环境，测试说 gateway 失败，运维说 gateway 通过。这个必须先复现/对齐配置，不能直接签字。

因此当前判定：

> **P4.5 Conditional No-Go**：health/offline/Tier1-only/备份/升级可用，但代理 gateway 结果不一致，DingTalk health 字段缺失，失败态诊断/退出延迟、Docker 示例和 24h soak 仍未收口。

---

## 2. 处置总表

| ID | 级别 | 处置 | 责任侧 | 关闭条件 |
| --- | --- | --- | --- | --- |
| BUG-P4.5-PROXY-WS | High | 接受为“待复现冲突”；不能直接关闭 | Discord transport / Test / Ops | 同 commit/同配置复现通过 |
| BUG-P4.5-DT-TIMESTAMPS | Medium | 接受；DingTalk 补 lastConnected/lastEvent | DingTalk transport | health 字段与 Discord 对齐 |
| OBS-009 duration 退出延迟 | 中 | 接受；修取消/停止等待 | CLI / Host | `duration=60` 约 60s 退出 |
| OBS-010 失败原因不透出 | 中 | 接受；区分 direct/proxy/REST/WS/identify | Discord transport | 启动错误指向底层原因 |
| OBS-011 Docker template 缺失 | 低 | 接受；补 `live.env.example` | Deploy | README 命令可执行 |
| OBS-012 联系信息占位 | 低 | 接受；填 runbook | Ops | 无 `[OWNER]` 等占位 |
| OBS-013 sendFailures 不可观测 | 中 | 接受；health 增加发送失败字段 | Discord / Health | health 可见 |
| OBS-014 CLI health startedAt | 低 | 文档口径修正：loopback 才是主实例 | Docs/Test | 测试计划/验收不含 CLI startedAt |
| ENV-003/004 | 环境 | root/systemd、Docker、24h、secret 轮换 BLOCKED | Ops | 真实环境补测 |
| T3 P4 回归 | 阻塞 | 代理问题解决后重跑 | Test | slash/DM/guild 全绿 |

---

## 3. 代理 gateway 冲突：最高优先级

### 3.1 测试侧证据

测试环境：

- Discord 直连超时；
- `curl -x 127.0.0.1:7890 https://discord.com/api/v10/gateway` → 200；
- `openssl -proxy` TLS 1.3 成功；
- 原生 `ClientWebSocket` + `HttpClient.DefaultProxy` → `Open`；
- 但 `serve` 的 gateway **60s 未 ready**，报：

```text
Discord gateway did not become ready before the configured timeout.
```

### 3.2 运维侧证据

- 升级到 `c131e73` 后，相同代理：
  - `status=healthy`；
  - `connected=true`；
  - 启动到 READY 约 40s；
  - plugins 4/0。

### 3.3 处置

**不能把两边结果直接对冲。**
先做一次受控复现：

1. 固定 `HEAD=c131e73`，不改代码；
2. 固定 `readyTimeoutSeconds=90`；
3. 固定代理 `HTTP_PROXY/HTTPS_PROXY/ALL_PROXY=http://127.0.0.1:7890`；
4. 清空 `NO_PROXY` 中的 Discord 项；
5. 同一台机器、同一 bot token、同一 config；
6. 启动 `serve --duration 120`，每 5s 记录：
   - `curl /health`；
   - `journalctl`/日志中的 REST、WS、Hello、Identify、READY；
   - `lastConnectedAt`/`lastError`。

判定：

- 若测试侧复现失败、运维侧仍通过 → 检查测试 config 的 `readyTimeoutSeconds`、代理变量、插件 staging 是否一致；
- 若两边都能复现 → 产品仍有代理 gateway 竞态，必须修；
- 修好前 **BUG-P4.5-PROXY-WS 保持 Open**。

### 3.4 可能差异点

- `readyTimeoutSeconds` 60 vs 90；
- REST 已经走代理，但 gateway 仍用 `null` proxy；
- `HTTP_PROXY` vs `HTTPS_PROXY`/`ALL_PROXY` 的解析差异；
- 测试/运维使用不同插件 staging；
- 代理启动时 REST 已 200，但 WS 握手被 60s 切断；
- 多实例/旧进程占用同一 bot token。

---

## 4. BUG-P4.5-DT-TIMESTAMPS

### 4.1 现象

DingTalk upstream：

- `connected=true`；
- `lastEventAt=null`；
- `lastConnectedAt=null`；
- `reconnectCount=0`。

Discord 同场景字段有值。

### 4.2 影响

OBS-007 的可观测性对 Tier 2 不生效。
运维无法判断 DingTalk 最近连接/事件时间。

### 4.3 修复

- `DingTalkChatUpstream` 实现 `IChatUpstreamHealth`；
- `Ready` 时更新 `lastConnectedAt`；
- 收到消息 / interaction 时更新 `lastEventAt`；
- `Disconnected` 时更新 `lastDisconnectAt` 并递增 `reconnectCount`；
- `LastError` 脱敏；
- health 输出与 Discord 对齐；
- 测试：连接、收消息、断连、重连。

---

## 5. OBS-009：`--duration` 到期退出延迟

### 5.1 现象

坏代理：

```text
serve --duration 60
实际 151s 才退出
```

SIGTERM 场景 131ms 正常。

### 5.2 根因方向

`duration` 只控制 `ServeCommand` 的等待；host shutdown / upstream connect 的 90s 超时仍可能拖住退出。
需要区分：

- 正常 duration 到期；
- 取消时等待插件连接超时；
- `StopAsync` 的 10s 兜底是否生效。

### 5.3 修复

- `duration` 到期与取消走同一套 graceful stop；
- 插件 `ConnectAsync` 使用同一个 cancellation token；
- host stop 有硬上限（例如 10–15s），超时后记录并强制结束；
- 失败代理不应等满 `readyTimeoutSeconds` 才退出；
- 测试：`--duration 5`、`--duration 60`、bad proxy、SIGTERM 四种。

验收：

- `--duration 60` 约 60–70s 退出；
- SIGTERM 仍快速；
- 不丢日志。

---

## 6. OBS-010：失败原因不透出

当前坏代理/直连失败只报：

```text
Discord gateway did not become ready before the configured timeout.
```

无法区分：

- 无代理直连超时；
- 代理连接拒绝；
- REST 成功但 WS 失败；
- TLS 失败；
- Hello/Identify/READY 哪一步失败。

修复：

- transport 记录 phase：`rest_gateway` / `ws_connect` / `hello` / `identify` / `ready`；
- 错误消息包含 phase + 脱敏后的异常类型；
- `plugins list --detail` / health `lastError` 可见；
- 不打印代理凭据/HTTP 头。

---

## 7. 其他 OBS

### OBS-011：Docker template 缺失

`deploy/README.md` 要 `cp live.env.example live.env`，但 `deploy/docker/live.env.example` 不存在。

修复：

- 增加模板（只含变量名/占位符）；
- 或在 README 改为“创建 live.env”；
- 不提交真实 secret。

### OBS-012：runbook 联系信息占位

`[OWNER]` / `[ONCALL]` / `[ESCALATION]` 需要填真实值，或有明确“由部署方填写”。

### OBS-013：`sendFailures` 不可观测

`DiscordChatUpstream` 有 `_sendFailures` / `_lastSendError`，但 health 没暴露。

修复：

- `RuntimeHealthUpstream` 增加 `sendFailures`/`lastSendError`（脱敏）；
- `health` JSON 与 `/health` 一致；
- 测试发送失败后字段递增。

### OBS-014：CLI health 的 `startedAt`

CLI `health` 每次是新进程，`startedAt` 必然变；只有 loopback `/health` 对应主实例。

处理：

- 文档/验收计划明确 O-030 只看 loopback；
- CLI health 作为一次性检查，不用于 uptime 对账；
- 可选 `health --offline` 只读 state。

---

## 8. 测试侧 No-Go 的解除条件

测试当前因代理 gateway 阻塞，P4 回归未执行。解除条件：

- [ ] 代理 gateway 受控复现并修复/确认配置；
- [ ] 同一 config 下 `serve` READY；
- [ ] DM/guild 真实回复；
- [ ] slash/memory/reset 回归；
- [ ] Tier2 smoke；
- [ ] health 时间戳/Tier1-only 不回归。

---

## 9. 运维 Conditional Pass 的解除条件

- [ ] root 实机 systemd（`useradd`/unit/restart/on-failure）；
- [ ] Docker build/run/restart/persistence；
- [ ] 24h 被动 soak；
- [ ] secret 轮换；
- [ ] OBS-009/010 收敛；
- [ ] OBS-011/012/013/014 收敛或明确豁免。

---

## 10. 修复顺序

```text
1. 代理 gateway 冲突复现/修复
2. duration 退出延迟
3. 失败原因 phase 诊断
4. DingTalk health timestamps
5. sendFailures 可观测
6. Docker template / runbook 占位
7. 测试 P4 回归
8. 运维 systemd/Docker/24h/secret
9. P4.5 签收
```

---

## 11. 归档规则

本文是临时配对文档。

归档条件：

- BUG-P4.5-PROXY-WS 关闭；
- DT timestamps 修复；
- OBS-009/010/013 收敛；
- 测试 P4 回归通过；
- 运维 systemd/Docker/24h/secret 完成；
- 测试/运维双方 Go；
- 干净 HEAD 复跑。

归档动作：

1. 与测试/运维记录一起归档；
2. 更新索引；
3. 保留缺陷编号和修复 SHA；
4. 清理 state/凭据。

---

## 12. 一句话

**测试说代理 gateway 失败，运维说代理 gateway 成功——这是当前最重要的未决问题，不能用“有条件通过”盖过去。**

先同 commit/同 config 复现，再修 duration/诊断/DingTalk 字段；两侧都 Go 之前，P4.5 不签收。开发侧已完成 OBS-009/010/013 与 DT 时间戳、Docker/联系信息修复；代理 gateway 冲突仍待同 commit/同 config 复现。
