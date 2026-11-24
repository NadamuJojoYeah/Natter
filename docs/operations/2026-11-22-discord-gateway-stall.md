# 2026-11-22 Discord 网关静默失联 3h19m

> **级别**：P0 可用性事故
> **影响**：Discord bot 无法收发消息 3 小时 19 分 44 秒
> **状态**：已人工恢复（14:08:17 重启），根因未修
> **关联**：[运维手册](../OPERATIONS_RUNBOOK.md)、[代码审查 S2 §6.2](../CODE_REVIEW_2026-11-22.md)

---

## 1. 结论

不是网络故障，不是平台封禁，是 **gateway 客户端进入终态后没有任何外部接管**。

三层缺陷叠加：

1. **丢诊断**：Discord.Net 把所有可重连关闭码泛化成一个固定异常的固定文案，原始 close code 没被保留；
2. **无自愈**：transport 层只发布断连事件，不重连；完全依赖库内部 `_reconnect` 循环，一旦退出即永久停止；
3. **无日志**：整条连接路径零日志，journal 全程沉默，只能靠轮询 `/health` 发现。

---

## 2. 时间线（CST）

| 时刻 | 事件 | 证据 |
| --- | --- | --- |
| 11-21 20:19:18 | host 启动，Discord connected | `journalctl` 启动行 |
| 11-21 23:01:11 | 机器进入 suspend（s2idle） | `PM: suspend entry` |
| 11-22 09:14:16 | 唤醒 | `PM: suspend exit` |
| 11-22 09:15:21 | 重连成功，`lastConnectedAt` 更新 | `/health` |
| 09:15 – 10:48 | 稳定运行 1h33m | — |
| **10:48:33** | **断开**，`lastDisconnectAt == lastEventAt`（同毫秒） | `/health` |
| 10:48 – 14:08 | **零重连尝试，3h19m44s** | `reconnectCount` 停在 7 |
| 14:08:17 | 人工 `systemctl restart natter` | `journalctl` |
| 14:08:22 | 恢复，`connected=True`、`reconnectCount=0` | `/health` |

---

## 3. 现场快照

断连期间 `/health`：

```json
{
  "status": "degraded",
  "uptimeSeconds": 64054,
  "upstreams": [{
    "source": "discord:discord:1571154023493410836",
    "connected": false,
    "lastEventAt": "2026-11-22T02:48:33.6932155+00:00",
    "lastConnectedAt": "2026-11-22T01:15:21.158784+00:00",
    "lastDisconnectAt": "2026-11-22T02:48:33.6932155+00:00",
    "reconnectCount": 7,
    "lastError": "Server requested a reconnect",
    "sendFailures": 0,
    "lastDisconnectError": "Server requested a reconnect",
    "lastDisconnectCode": "gateway_disconnected"
  }]
}
```

关键点：

- `lastEventAt` 与 `lastDisconnectAt` 精确到毫秒完全相同 → 断开时就停在最后一个事件上；
- `reconnectCount=7` 是历史累计，第 7 次断开后**没有第 8 次**；
- `sendFailures=0` 说明不是发送失败，是根本没连上；
- 两个原因字段都是**固定文案**，无诊断价值。

---

## 4. 根因分析

### 4.1 第一层：close code 被丢弃

`src/Natter.Chat.Discord/DiscordSocketTransport.cs:548`：

```csharp
private Task OnDisconnectedAsync(Exception exception)
{
    _lastGatewayError = Sanitize(exception?.Message ?? "gateway disconnected without exception detail");
    ...
}
```

Discord 网关主动关闭时会给 close code：

```text
4000 未知错误        4001 未知 opcode      4002 解码失败
4003 未认证          4005 重复认证         4007 序列号无效
4009 会话超时
```

Discord.Net 把这些统一包成 `GatewayReconnectException`，其 `Message` **固定为 `"Server requested a reconnect"`**。

结果：`lastDisconnectError` 只能告诉我们「断了」，无法区分是会话超时、平台 4000、还是心跳 ACK 超时（僵尸连接）。

**本次属于哪一类，已不可考。**

### 4.2 第二层：没有外部重连兜底（真正的原因）

`OnDisconnectedAsync` 只做三件事：存原因、发事件、返回。**没有任何重连代码。**

全仓搜索确认：

```bash
grep -rn "Restart\|StopAsync\|StartAsync" src/Natter.Chat.Discord/DiscordChatUpstream.cs
# 无结果
```

`DiscordSocketTransport` 的构造也没有任何重连相关配置：

```csharp
var config = new DiscordSocketConfig
{
    GatewayIntents = ParseIntents(options.Intents),
    MessageCacheSize = 100,
    AlwaysDownloadUsers = false,
    ConnectionTimeout = (int)TimeSpan.FromSeconds(...).TotalMilliseconds,
};
_client = new DiscordSocketClient(config);
```

所以完全依赖库内部 `_reconnect` 循环。该循环一旦退出（重连耗尽 / 进入 fatal / 状态机卡住），**host 永远不会自己恢复**。

这解释了 3h19m 一次重连都没有——不是连不上，是没人再去连。

### 4.3 第三层：零日志

```bash
journalctl -u natter.service --since "2026-11-22 00:00"
# -- No entries --
```

`src/Natter.Chat.Discord/` 下没有任何 connect / disconnect / reconnect 的 `LogInformation`/`LogWarning`。

7 次断连、7 次重连、3h19m 卡死，journal 全程沉默。

唯一可见的是 `/health` 的两个无信息量字段，而没有人会 7×24 轮询 health。

这属于审查报告 §6.2「降级策略淹没了真实失败」的同款问题：状态字段有了，但没人能看见。

---

## 5. 为什么不是休眠导致

休眠影响的是昨天 23:01 → 今天 09:14 那一段，醒来后 09:15:21 已成功重连。

10:48:33 的断开是**独立事件**，当时机器清醒、网络正常、`sendFailures=0`。

1h33m 的稳定运行也说明休眠后的恢复是有效的。

---

## 6. 影响

1. **用户侧**：3h19m 内所有 Discord 消息无响应，且无任何告警；
2. **soak 验收**：`status=degraded` 会让累计口径直接 FAIL（`--allow-status` 默认 `healthy`）；重启切断当前段，记 `host-restart`（已归因缺口，不阻塞累计）；
3. **可信度**：如果没有 `/health` 轮询，这类静默失联可以持续到下一次人工巡检——24h soak 可能跑出一个「主机活着但服务早死了」的绿灯。

---

## 7. 修复方向

| 优先级 | 问题 | 修法 |
| --- | --- | --- |
| P0 | 无重连兜底 | upstream 层加 watchdog：`connected=false` 超过 N 秒 → 重建 transport（Stop + Login + Start） |
| P1 | close code 丢失 | 从 Discord.Net 的 `LogMessage` / `GatewayCloseCode` 抓原始码，落到 `lastDisconnectCode`（如 `gateway_4009`） |
| P1 | 零日志 | connect / disconnect(code) / reconnect 尝试与结果 / 重连耗尽 / watchdog 触发，全部落 journal，不含 token |
| P2 | 无主动告警 | ops-health-alert 已能识别 `disconnect-without-reason`；应扩展识别 `connected=false` 超时 |

### 验收要点

- 人为 kill 网关连接后，watchdog 在阈值内自动重建，`connected` 回到 true，无需人工重启；
- `lastDisconnectCode` 能反映真实 close code，不再是固定字符串；
- journal 能看到完整断连→重连链路；
- 默认配置下不影响既有行为，不引入新的常驻线程泄漏。

---

## 8. 教训

1. **状态字段 ≠ 可观测**：`lastDisconnectError` 存在了（OBS-019 修过），但内容是常量，等于没修到根上；
2. **依赖库的重连等于没有重连**：任何外部连接都必须有 host 自己的看门狗，因为库的失败模式对我们是不透明的；
3. **重启是恢复手段，不是修复**：本次重启让服务恢复，但代码里的三个缺陷一个都没消失，下次还会发生。

---

## 9. 附录：复核命令

```bash
# 当前状态
curl -s http://127.0.0.1:5099/health | python3 -m json.tool

# 重启记录
journalctl -u natter.service --no-pager --since "2026-11-22 10:00" -o short-iso

# 断连期间是否真的没有日志（预期为空）
journalctl -u natter.service --no-pager --since "2026-11-22 10:48" --until "2026-11-22 14:08"

# 确认没有外部重连代码
grep -rn "Restart\|StopAsync\|StartAsync" src/Natter.Chat.Discord/DiscordChatUpstream.cs
```
