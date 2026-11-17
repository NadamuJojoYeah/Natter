# 2026-11-17 · P4.5 联合复测报告（测试侧）

> **开发侧处置**：[P4.5 复测处置记录](2026-11-17-p4-5-retest-response.md)（Conditional Pass；P4 回归/OBS 收敛后归档）。

> **状态**：代理 gateway 已通过；P4 回归待真实消息  
> **日期**：2026-11-17  
> **基线**：`HEAD=24922c7`（修复 `0778c1b`）  
> **依据**：[P4.5 联合复测计划](2026-11-17-p4-5-retest-plan.md)  
> **证据**：`artifacts/p4.5/retest/2026-11-17-1723/`

## 1. 前置

| 项 | 结果 |
| --- | --- |
| 构建 | 成功 |
| 框架回归 | **245/245** |
| 代理单测 | **5/5** |

## 2. 代理 gateway 受控复现（2.3 重点）

固定条件：`HEAD=24922c7`、`readyTimeoutSeconds=90`、`HTTP_PROXY/HTTPS_PROXY/ALL_PROXY=http://127.0.0.1:7890`、`NO_PROXY` 不含 discord.com、单实例、`serve --duration 120 --health-port 5100`、每 5s 采样。

| 时间 | 结果 |
| --- | --- |
| 启动后 ~5s | **Discord READY**（`lastConnectedAt=09:26:57`，启动 `09:26:52`） |
| t=10s…230s（24 次采样） | **全部 `healthy`**，DingTalk + Discord 双 `connected=true`，`failures=[]` |
| duration 120 | 约 120s 后进程退出（无 151s 延迟） |

**结论：`BUG-P4.5-PROXY-WS` 关闭。**

- **根因修正**：12:31 那次“60s 未 ready”是**测试装置使用旧 staging 插件**（P4 `plugins/all` 未随 36e0c41/0778c1b 重建）导致；重新 staging 最新构建后，代理下 **5s READY**。
- 代理链路本身正常（此前已证：REST 200、TLS 1.3、WS 101、原生 ClientWebSocket Open）。
- 运维 40s READY 与测试结论现已一致（同一 commit/同一配置）。

## 3. duration 退出（2.4）

- 代理正常：`duration 120` → 约 120s 退出，无 151s。
- **坏代理 `duration 60` → exit 0，60.1s 退出**（`evidence/duration-bad-proxy.log`）；DingTalk 立即 `Connection refused`，Discord 被取消（`A task was canceled`），无 151s 延迟。

## 4. DingTalk health（2.5）

`/health` 采样显示：

```json
{"source":"dingtalk:dingtalk:dingeojh87nyyna3lqpg","connected":true,
 "lastConnectedAt":"2026-11-17T09:29:14.0408817+00:00",
 "lastEventAt":null,"lastDisconnectAt":null,"reconnectCount":0,
 "sendFailures":0,"lastSendError":null}
```

- `lastConnectedAt` **非 null**（`BUG-P4.5-DT-TIMESTAMPS` 关闭）；
- `lastEventAt` 待一条钉钉消息后验证；
- `reconnectCount` / `lastDisconnectAt` / `sendFailures` / `lastSendError` 字段已出现。

## 5. sendFailures（2.6）

- 字段已进入 health（`sendFailures=0`、`lastSendError=null`），`DiscordChatUpstream` 有计数实现；
- 真实失败构造未完成：临时 live 探针（`SendFailureProbeTests`）在本环境无法让 embedded host 注册 Discord upstream（`registry.Upstreams` 为空），已移除该探针，避免污染 live 测试；
- 替代路径：等代理实例收到一条真实消息后，向不存在的频道/已删除频道触发一次回复（或运维在真实部署中制造 403），再读 health。

## 5.1 health / offline / Tier1-only（2.7）

| 用例 | 结果 |
| --- | --- |
| `health --offline` | 通过（631ms、`status=offline`、`upstreams=[]`） |
| loopback 采样 | 通过（`startedAt` 稳定、`sampledAt` 变化、uptime 增长） |
| **Tier1-only** | **通过**：只装 discord+deepseek（profiles memory 改 `host#sqlite`）→ `status=healthy`、deepseek available、spark unavailable、Discord connected、`failures=[]`（Tier2 缺失不 degraded） |
| Tier1 缺失 | 通过（exit 1，`requires provider ... but it is not loaded`） |

## 6. 待补/待人工

1. **P4 回归（2.8）**：`/ask` `/remember` `/memory` `/forget` `/reset`、DM、guild @、钉钉 smoke、重启后 memory/session；
2. 钉钉 `lastEventAt`（发一条消息）；
3. `sendFailures` 构造（无效频道/权限）——实现已在 `DiscordChatUpstream`，尚未触发真实失败；
4. 运维项：systemd、Docker、24h soak、secret 轮换（不在测试侧）。

## 6.1 实例与回归等待

- 修复后实例以代理启动（`--duration 3600 --health-port 5100`），**18:29 正常到期退出**（exit 0，无 error）。
- 实例在线期间（17:29–18:29）**未收到任何真实 Discord/钉钉消息**：`sessions=0`、`messages=0`、`memory_items=0`，DingTalk `lastEventAt=null`。
- 因此 P4 回归（2.8）与钉钉 `lastEventAt` 仍未执行，等待测试侧人工发送。

## 7. 结论

- 测试侧：代理 gateway 与 DingTalk health 修复**通过**；`BUG-P4.5-PROXY-WS`、`BUG-P4.5-DT-TIMESTAMPS` 关闭。
- P4 回归与 sendFailures 未完成前，测试侧**不单独签 Go**；按复测计划口径，当前为 **Conditional Pass**（等待 P4 回归 + 运维项）。
- 实例已于 18:29 正常结束；需要复测时用同一配置重启（代理 + `--health-port 5100`）。
