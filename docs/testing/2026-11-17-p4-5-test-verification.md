# P4.5 修复验证（测试交接）

> **状态**：Ready for test  
> **日期**：2026-11-17  
> **基线**：`HEAD=c131e73`（代理、健康、offline、upstream 诊断修复已合入）  
> **范围**：验证 P4.5 运维缺陷修复，不重做 P4 全量功能验收  
> **对应记录**：[首次部署测试](2026-11-16-first-deployment-test.md)、[首次部署处置](2026-11-16-first-ops-deploy-response.md)

---

## 0. 结论口径

测试只回答三件事：

1. 修复后的产品行为是否符合预期；
2. 是否破坏既有 P4/P3 功能；
3. 是否留下新的可观测性/回归风险。

运维环境、systemd、真实代理、24h soak 由运维验证，不在本文默认范围；真实代理 Discord 链路是测试+运维联合项。

---

## 1. 当前修复内容

| 项 | 修复 |
| --- | --- |
| BUG-004 | `DiscordEgressProxy` + REST/WebSocket provider 注入 |
| BUG-005 | `ConnectionTimeout = max(30s, readyTimeoutSeconds)` |
| OBS-004 | `HostRuntimeClock.StartedAt` / `uptimeSeconds` |
| OBS-005 | health 只把 Tier1 provider 缺失视为 degraded |
| OBS-006 | `natter health --offline` 不启动 upstream |
| OBS-007 | 仅增加 `lastConnectedAt` / `lastDisconnectAt` / `reconnectCount`；**未实现自动 watchdog** |
| OBS-008 | health 增加 `startedAt` / `sampledAt`；wall-clock uptime |

---

## 2. 测试前置

```bash
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

预期：**245/245**。

无凭据 live：

```bash
env -u NATTER_LIVE \
    -u DEEPSEEK_API_KEY \
    -u DISCORD_BOT_TOKEN \
    -u SPARK_API_KEY \
    -u DINGTALK_CLIENT_SECRET \
  dotnet test tests/Natter.Live.Tests/Natter.Live.Tests.csproj
```

预期：全部 skip，0 failed。

---

## 3. 测试用例

### Gate T1：代理解析

| ID | 用例 | 命令 | 预期 |
| --- | --- | --- | --- |
| T-001 | 代理单测 | `dotnet test ... --filter FullyQualifiedName~DiscordEgressProxyTests` | 5/5 |
| T-002 | 无代理 | `HttpClient.DefaultProxy=null` | 返回 null |
| T-003 | NO_PROXY | `NO_PROXY=discord.com` | bypass，返回 null |
| T-004 | 命中代理 | `HTTP_PROXY=http://127.0.0.1:7890` | 返回代理 |
| T-005 | 坏代理 | `HTTP_PROXY=http://127.0.0.1:1` | 明确失败/超时，不无限卡死 |

### Gate T2：health 离线/在线

| ID | 用例 | 命令 | 预期 |
| --- | --- | --- | --- |
| T-010 | offline 不建 gateway | `natter ... health --offline --json` | `status=offline`，快速返回 |
| T-011 | offline 读 state | 同上 | `conversations` 可读 |
| T-012 | online timestamps | `natter ... health --json` | `startedAt`、`sampledAt`、`uptimeSeconds>0` |
| T-013 | startedAt 稳定 | 连续两次 health | `startedAt` 不变，`sampledAt` 变化 |
| T-014 | Tier1-only | 只装 Discord/DeepSeek | `status=healthy`，Spark 缺失不 degraded |
| T-015 | Tier1 缺失 | profile 引用不存在 Tier1 provider | 启动失败/明确 degraded，不 healthy |
| T-016 | 插件失败 | 故意坏插件 | `plugins.failures[]` 带 id/phase/code/message |
| T-017 | upstream 字段 | 在线 health | 有 `lastConnectedAt`/`lastDisconnectAt`/`reconnectCount` |

### Gate T3：P4 回归

| ID | 用例 | 预期 |
| --- | --- | --- |
| T-020 | slash `/ask` | AI 回复 |
| T-021 | `/remember` → `/memory` → `/forget` | 写入/可见/删除 |
| T-022 | `/reset` | 会话/摘要清理 |
| T-023 | DM/guild | 各一条回复，不串 |
| T-024 | DingTalk+Spark smoke | 文本回复 |
| T-025 | 重启 | memory/session 保持 |

### Gate T4：真实代理链路（联合）

| ID | 用例 | 负责 | 预期 |
| --- | --- | --- | --- |
| T-030 | 无代理直连 | 测试/运维 | Discord READY |
| T-031 | HTTP 代理 | 测试/运维 | REST + gateway READY |
| T-032 | NO_PROXY | 测试/运维 | 按规则直连 |
| T-033 | 坏代理 | 测试 | 明确失败，不挂死 |
| T-034 | 代理下 DM/guild | 联合 | 回复成功，无重复 |

---

## 4. OBS-007 的测试边界

OBS-007 当前只补了观测字段：

- `lastDisconnectAt`；
- `reconnectCount`；
- `lastConnectedAt`；
- `LastError`。

**没有自动 watchdog/自动重启。**

测试应验证：

- health 能看到断连；
- 断连后字段更新；
- 进程不崩溃；
- 但不要断言“自动恢复”——这是运维/后续开发项。

真实断流演练由运维做，测试只验字段/回归。

---

## 5. 证据

```text
artifacts/p4.5/test/<run>/
├── build.log
├── framework.trx
├── live-skip.trx
├── proxy-tests.trx
├── health-offline.json
├── health-online.json
├── logs/
└── screenshots/
```

不提交 state、凭据、代理凭据。

---

## 6. Go/No-Go

- [ ] 245/245；
- [ ] 代理单测 5/5；
- [ ] offline 不建 gateway；
- [ ] online startedAt/sampledAt/uptime 正常；
- [ ] Tier1-only healthy；
- [ ] Tier1 缺失不 healthy；
- [ ] OBS-007 字段可见但不宣称自愈；
- [ ] P4 命令/DM/guild 回归；
- [ ] Tier2 smoke；
- [ ] 无密钥泄漏。

**测试只对产品行为签字；systemd/24h soak 由运维签字。**

---

## 7. 一句话

**测试验“修复后的产品行为”，运维验“真实部署和长跑”；OBS-007 仍是可观测性修复，不是自动恢复修复。**
