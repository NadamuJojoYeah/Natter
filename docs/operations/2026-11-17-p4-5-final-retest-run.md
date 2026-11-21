# 2026-11-17 · P4.5 收尾复测（运维侧 · 新修复 + 剩余项）

> **前置**：[P4.5 联合复测（运维侧）](2026-11-17-p4-5-retest-run.md)、[开发侧处置](../testing/2026-11-17-p4-5-retest-response.md)
> **修复基线**：`4885345`（phase/断连原因/取消语义）；运行 `HEAD=121bfc0`
> **状态**：Executed · **Conditional Pass**（可执行项收口；Docker/systemd/24h/secret/真人项见 §4）

## 1. 概要

- **时间**：2026-11-17 20:31–22:11（`Asia/Shanghai`）
- **环境**：Arch Linux；.NET SDK 10.0.114；docker daemon 29.9.2（**无 compose 插件**）；`sudo` 仍被 `no-new-privileges` 拒绝；Discord 走 `127.0.0.1:7890`
- **被测对象**：`4885345` 的三处修复（OBS-015/016/017）、Docker 部署形态、24h 被动 soak
- **证据目录**：`artifacts/ops/p4.5/retest/2026-11-17-2034/`（另沿用 `.../2026-11-17-1722/logs/` 的复测脚本输出）
- **结论**：**OBS-015、OBS-017 关闭**；OBS-016 代码已接入、运行中断连现场仍无法制造（部分关闭）；新版本 1.6h 稳定运行（`healthy`、`reconnectCount=0`、`sendFailures=0`）；24h soak 已按 5 分钟采样启动；**Docker 与 systemd 仍 BLOCKED**（本轮新增 `.dockerignore` 收敛构建上下文）。

## 2. 升级与稳定性

| 项 | 结果 |
| --- | --- |
| 发布/切换 | `dotnet publish -c Release` + staging discord/deepseek → 目录切换，旧产物保留 `opt/natter.prev-20261117-203411` |
| 启动 | 20:34:16 启动，20:34:17 `startedAt`，约 20s READY |
| 稳定性窗口（1.6h） | `healthy` 全程、`connected=true`、`plugins 4/0`、`sendFailures=0`；22:07 前 `reconnectCount=0`，22:09 出现一次断连并累计 `reconnectCount=12`，13s 后自动恢复（见 §3.3）；soak 采样 4 个点全部 healthy |
| uptime | 5792s（96 分钟）与墙钟一致（未休眠） |

## 3. 新修复复测

### 3.1 OBS-015 · phase 不再误报（关闭）

`NO_PROXY=discord.com`（bypass 直连），`readyTimeoutSeconds=20`：

```json
{"id":"natter.transport.discord","state":"Failed","phase":"activate",
 "code":"upstream-connect-failed",
 "message":"Discord gateway did not become ready during phase 'ws_connect' within 20s. lastDisconnectedError=none; connectionState=Connecting; proxy=False; restCompleted=True; gatewayStarted=True."}
```

- phase 从误报的 `identify_ready` 变为 `ws_connect`；同时给出 `proxy=False`、`connectionState`、`restCompleted/gatewayStarted`，可直接判断“代理被 bypass”。
- **OBS-015 关闭**；OBS-010（诊断弱）随此修复明显改善。

### 3.2 OBS-017 · 取消语义可区分（关闭）

坏代理 `127.0.0.1:1`，`--duration 60`：

```text
exit=0 elapsed=60161ms budget=60s
Serve duration reached during host startup; canceled.
```

- 不再把“duration 到点取消”伪装成插件失败（旧行为是 `A task was canceled.`）。
- **OBS-017 关闭**；`OBS-009` 退出延迟保持 60.2s（已关闭）。

### 3.3 OBS-016 · 断连原因（未闭环）

本轮意外抓到运行中断连现场（22:09:46Z）：

```json
{"connected":true,"lastEventAt":"...14:09:59.843Z","lastConnectedAt":"...14:09:59.843Z",
 "lastDisconnectAt":"...14:09:46.951Z","reconnectCount":12,
 "lastError":null,"sendFailures":0,"lastSendError":null}
```

- 断连 13s 后自动恢复（`lastDisconnectAt` 14:09:46 → `lastConnectedAt` 14:09:59）；
- **`lastError` 仍为 `null`**，`reconnectCount` 已累计 12；
- 代码根因：`DiscordChatUpstream.OnReady` 中 `LastError = null;` —— 重连成功即清空断连原因；transport 的 `_lastGatewayError` 只用于连接失败消息，未持久化到 health；
- 失败态（未连上）实测 `lastDisconnectedError=none`，语义正确；
- **判定：OBS-016 未闭环**。建议新增 `lastDisconnectError`/`lastDisconnectCode` 持久字段（不在 Ready 清空），否则运维事后只能看到 `reconnectCount`，看不到原因。

## 4. 剩余项状态

| 项 | 结果 | 说明 |
| --- | --- | --- |
| Docker 构建/运行 | **BLOCKED（环境）** | 无 `docker compose`（`unknown command`）、无 docker-compose/podman；`docker pull mcr.microsoft.com/dotnet/{runtime,sdk}:10.0` 均成功；`docker build`（legacy builder，无 buildx）两次均未完成：第 1 次 10 分钟停留拉取层且无构建输出（已 kill），第 2 次按用户要求中止。镜像 `natter:p45-retest` 未产出 |
| systemd | **BLOCKED** | `sudo` 被 `no-new-privileges` 拒绝、PID 1 为 `bwrap` |
| 24h 被动 soak | **进行中** | 20:38:03 启动，`bin/soak-sample.sh 300 86400`，写入 `var/log/natter/soak-2026-11-17.jsonl`；当前 5 行（起始 + 4 次采样），末条 22:10:21 `healthy`/`connected=true` |
| secret 轮换 | **BLOCKED** | 需 Discord/DeepSeek 控制台权限 |
| 真人回归（P4） | **待人工** | `/ask` `/remember` `/memory` `/forget` `/reset`、DM、guild @bot |
| DingTalk `lastEventAt` | **待人工** | 需一条真实钉钉消息触发 |

### 4.1 Docker 附带的工程问题

- 仓库无 `.dockerignore`，构建上下文 **1.6G**（含 `artifacts/`、`.git`）；本轮新增 `.dockerignore`（排除 `artifacts/`、`tests/`、`bin/`、`obj/`、`.git` 等），未提交，仅用于本轮尝试。
- 镜像构建未包含 Tier1 业务插件（Dockerfile 只 `dotnet publish` CLI，Discord/DeepSeek 需要额外 staging），即使构建成功，容器内也无法跑真实 Discord 链路——建议 Dockerfile 增加插件 staging 步骤。

## 5. 结论

- 新修复 **OBS-015/017 关闭**；OBS-016 **未闭环**（运行中断连有现场，但 `LastError` 在重连成功后清空 → 新增 `OBS-019`）；
- 修复版本已在真实代理下稳定运行 1.6h，24h soak 已开始采样；
- **P4.5 仍为 Conditional Pass**：Docker（环境缺 compose/构建未完成）、systemd（root）、secret 轮换（平台）、真人回归（人工）四项未完成；
- 转 Go 的最小剩余路径：① 有 compose/buildx 的环境跑 `docker compose up -d --build`；② root 实机装 systemd；③ 真人跑 5 条命令 + DM/guild + 钉钉消息；④ soak 满 24h 后核对 `soak-*.jsonl`。

### 3.4 新观测 OBS-019 · 断连原因被 Ready 清空

- **现象**：`reconnectCount=12`、`lastDisconnectAt` 有值，但 `lastError=null`；代码 `OnReady` 里 `LastError = null`。
- **影响**：运维/告警只能看到“断过”，看不到“为什么断”，24h soak 也无法归因。
- **建议**：health 增加 `lastDisconnectError`（与 `lastError` 分离，仅在下一次断连时覆盖），或在 Ready 时不清空 `lastError`。

## 5.1 复测现场

```json
// /health（22:10Z，重连已恢复）
{"connected":true,"reconnectCount":12,
 "lastDisconnectAt":"2026-11-17T14:09:46.951Z",
 "lastConnectedAt":"2026-11-17T14:09:59.843Z","lastError":null}
```

## 6. 证据清单

| 路径 | 内容 |
| --- | --- |
| `artifacts/ops/p4.5/retest/2026-11-17-2034/logs/docker-build.log` | 第 1 次构建（拉取层完成后无进展） |
| `artifacts/ops/p4.5/retest/2026-11-17-2034/logs/docker-build-2.log` | 第 2 次构建（用户中止） |
| `artifacts/ops/p4.5/retest/2026-11-17-2034/docker-config/` | 容器用 config（仅 `${secret:...}` 引用） |
| `artifacts/ops/p4.5/retest/2026-11-17-1722/logs/no-proxy-bypass.log` | OBS-015 复测（`phase 'ws_connect'`、`proxy=False`） |
| `artifacts/ops/p4.5/retest/2026-11-17-1722/logs/duration-bad-proxy-60.log` | OBS-017 复测（`Serve duration reached ... canceled.`） |
| `artifacts/ops/tier1-run1/var/log/natter/soak-2026-11-17.jsonl` | 24h soak 采样 |
| `.dockerignore`（新增，未提交） | 构建上下文收敛 |

## 7. 当前运行状态

- 实例：`121bfc0` 构建，20:34:16 启动，`healthy`、`connected=true`、`plugins 4/0`
- soak：20:38:03 起每 300s 采样，持续 86400s
- 停止：`touch artifacts/ops/tier1-run1/var/run/stop.flag`（实例）；soak 采样可直接结束对应后台任务

## 8. 睡眠事件与 soak 重启（22:13 追加）

验收期间机器休眠，soak 采样出现 82 分钟缺口；实例未重启，唤醒后自动恢复。

| 时间（Asia/Shanghai） | 事件 | 证据 |
| --- | --- | --- |
| 20:38:03 | soak 启动（300s 间隔） | `soak-2026-11-17.jsonl` 首行 |
| 20:48:03 | 采样：`connected=false`、`reconnectCount=9` | 旧 soak 第 3 行 |
| 20:48–22:10 | **机器休眠**，采样缺口 **82m18s** | `20:48:03 -> 22:10:21` |
| 22:09:46–22:09:59 | 唤醒后断连 13s，随后自动重连 | `/health`：`lastDisconnectAt=14:09:46Z`、`lastConnectedAt=14:09:59Z` |
| 22:10:21 | 采样恢复：`healthy`、`reconnectCount=12` | 旧 soak 末行 |
| 22:13:19 | 人工补采样：`healthy`、`connected=true` | 旧 soak 追加行 |
| 22:13:29 | **重启连续 24h 计时**（新文件） | `soak-wake-2026-11-17.jsonl` 首行 |

结论与处置：

- 实例进程未重启（`startedAt=12:34:17Z` 不变），唤醒后 13s 内自动恢复 → 不构成人工重启需求；
- `uptimeSeconds=5934` ≈ 墙钟 99 分钟（修复后按墙钟计），**包含**休眠时段，与旧版“单调时钟不含休眠”不同；
- 睡眠窗口不计入 24h 验收：旧文件保留为缺口证据，自 22:13:29 重新计时 24h（`artifacts/ops/tier1-run1/var/log/natter/soak-wake-2026-11-17.jsonl`）；
- 睡眠期间的断连仍只有 `reconnectCount`、没有 `lastError`，再次印证 `OBS-019`（`OnReady` 清空 `LastError`）。

## 9. Slash 交互与代理链路（22:45 追加）

### 9.1 现象

- `/ask`、`/remember` 间歇显示“该应用程序未响应”，`/memory` 偶发；普通 @bot 消息与钉钉回复正常；
- 同一命令 22:22 失败、22:23 成功，呈现随机性。

### 9.2 证据

`/health` 的 Discord upstream：

```json
{"lastError":"interaction_defer_failed: Cannot defer an interaction after 3 seconds!"}
```

代理链路（`127.0.0.1:7890` → `discord.com`）6 次 REST：

| 次数 | 结果 |
| --- | --- |
| 1–2 | HTTP 200，5180ms / 5431ms |
| 3–6 | 超时（8009–8012ms） |
| 直连对照 | 初始 2/2 超时（当时不可用） |

Discord 要求 interaction 在 **3s** 内 ack；实现已在收到事件后立即 `DeferAsync()`，但 defer 本身是 REST POST，代理链路“成功也 5s+”，因此大概率超时。

### 9.3 处置与验证

- 22:42 复测直连：DNS 解析到 IPv6，REST **4/4 成功 268–590ms**；
- 22:43 将 `etc/natter/proxy.env` 改为直连（只保留 `NO_PROXY`，不导出代理变量）并重启；
- 22:44–22:45 复核：直连 REST **5/5 成功 293–613ms**；`/health` 双 upstream `connected=true`、`lastError=null`、`reconnectCount=0`、`plugins 6/0`；
- 数据：DB 24 条消息（新增 user/assistant 各 1 对）、3 会话；钉钉 `lastEventAt=14:45:04Z`（真人消息已触发）；
- guild 命令仍为 `ask remember forget memory reset`。

### 9.4 风险与建议

- 直连当前走 **IPv6**；若 IPv6 中断，表现与“应用未响应”相同。恢复代理只需在 `etc/natter/proxy.env` 写回三行 `HTTP(S)_PROXY/ALL_PROXY` 并重启；
- 建议对 `lastError` 含 `interaction_defer_failed` 加告警，比用户截图更早暴露；
- 长期方案：给 Discord 域名单独分流到低延迟节点，或在可直连 Discord 的机器上部署，避免 3s 边界问题。
