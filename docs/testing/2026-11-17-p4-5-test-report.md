# 2026-11-17 · P4.5 修复验证（测试报告，复测后 superseded）

> **复测结论**：`BUG-P4.5-PROXY-WS` 关闭（代理 5s READY；此前失败为旧 staging 插件）；`BUG-P4.5-DT-TIMESTAMPS` 关闭。详见 [2026-11-17-p4-5-retest-report.md](2026-11-17-p4-5-retest-report.md)。

> **开发侧处置**：[P4.5 联合处置记录](2026-11-17-p4-5-joint-response.md)（代理 gateway 冲突、DingTalk 时间戳、OBS 收敛后归档）。

## 1. 概要

- **测试时间**：2026-11-17 12:31–12:45（Asia/Shanghai）
- **基线**：`HEAD=c131e73`（代理、健康、offline、upstream 诊断修复已合入）
- **被测对象**：`DiscordEgressProxy`、health offline/online、upstream 时间戳、P4 回归（slash/DM/guild）、Tier 2 smoke
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；凭据 `~/.config/natter/live.env`
- **网络关键事实**：DingTalk / DeepSeek / Spark 直连正常（200）；**Discord 直连超时（curl 000，8s×3）**，本地有 HTTP 代理 `127.0.0.1:7890`（REST 经代理 200，gateway WebSocket 未 ready）
- **证据目录**：`artifacts/p4.5/test/2026-11-17-1231/`
- **结论**：**No-Go（代理链路）**——health/offline/代理单测通过；Discord 直连被环境阻断，但代理下 REST/原生 WS 均验证正常，而 `serve` 的 gateway 60s 未 ready，定位为产品侧缺陷；另有 1 个 Medium（DingTalk 时间戳）。

## 2. 前置

| 项 | 结果 | 证据 |
| --- | --- | --- |
| 构建 | **通过** | `evidence/build.log` |
| 框架回归 | **245/245 通过** | `evidence/framework.log` |
| 无凭据 live | **40 skip / 0 failed** | `evidence/live-skip.log` |

## 3. Gate T1：代理解析

| ID | 用例 | 结果 |
| --- | --- | --- |
| T-001 | 代理单测 | **通过 5/5**（`evidence/proxy-tests.log`） |
| T-002 | 无代理 | 单测覆盖（`Resolve(null) → null`） |
| T-003 | NO_PROXY | 单测覆盖（`IsBypassed → null`） |
| T-004 | 命中代理 | 单测覆盖；实际 `curl -x 127.0.0.1:7890 https://discord.com/api/v10/gateway` → **200**（1.58s） |
| T-005 | 坏代理 | **部分通过**：`HTTP_PROXY=http://127.0.0.1:1` 时 DingTalk 立即 `Connection refused`；Discord 走 60s `ready` 超时后失败（不无限挂死，但慢） |

## 4. Gate T2：health

| ID | 用例 | 结果 |
| --- | --- | --- |
| T-010 | offline 不建 gateway | **通过**：631ms，`status=offline`、`upstreams=[]` |
| T-011 | offline 读 state | **通过**：`conversations` 可读（0） |
| T-012 | online timestamps | **通过**：`startedAt`/`sampledAt`/`uptimeSeconds>0` |
| T-013 | startedAt 稳定 | **通过（同进程）**：连续两次 `/health` → `startedAt` 不变、`sampledAt` 变化、uptime 441→443 |
| T-014 | Tier1-only | **未完成**：需要 Discord 连接成功才 healthy；当前 Discord 不可达 |
| T-015 | Tier1 缺失 | **通过**：exit 1，`requires provider 'natter.feature.ai.nope#x', but it is not loaded` |
| T-016 | 插件失败 | **通过**：`plugins.failures[]` 含 `id/phase/code/message`（Discord `upstream-connect-failed`） |
| T-017 | upstream 字段 | **部分**：Discord 有 `lastConnectedAt`/`lastEventAt`；**DingTalk 的 `lastConnectedAt`/`lastEventAt` 始终 null** → `BUG-P4.5-DT-TIMESTAMPS` |

### BUG-P4.5-DT-TIMESTAMPS（Medium）

- **现象**：DingTalk upstream `connected=true`，但 health 中 `lastEventAt` / `lastConnectedAt` 均为 `null`（`reconnectCount=0`）；Discord upstream 同场景字段有值。
- **证据**：`evidence/health-online-1.json`、`proxy2-serve.log` 对应 health（`lastConnectedAt:null`）。
- **影响**：OBS-007 的可观测性字段对 Tier 2 不生效，运维无法判断 DingTalk 最近连接/事件时间。
- **建议**：DingTalk transport/upstream 在 Ready/Message 时更新 `lastConnectedAt`/`lastEventAt`（与 Discord 对齐）。

## 5. Gate T3：P4 回归（环境阻塞）

| ID | 用例 | 结果 |
| --- | --- | --- |
| T-020~T-025 | slash、memory、reset、DM/guild、Tier2、重启 | **无法执行**：Discord 直连被阻断；代理下 gateway 60s 未 ready（见 T-031）。Tier2（钉钉）单侧可连，但 P4 回归以 Discord 为主 |

## 6. Gate T4：真实代理链路（联合）

| ID | 用例 | 结果 |
| --- | --- | --- |
| T-030 | 无代理直连 | **阻塞**：`curl https://discord.com/api/v10/gateway` 连续 3 次 8s 超时（000） |
| T-031 | HTTP 代理 | **失败（产品缺陷）**：REST 经代理 200；`openssl -proxy` 到 `gateway.discord.gg:443` TLS 1.3 成功；原生 `ClientWebSocket + HttpClient.DefaultProxy(HttpEnvironmentProxy)` → `state=Open`；但 `serve` 代理下 Discord gateway **60s 未 ready**（`upstream-connect-failed`）→ `BUG-P4.5-PROXY-WS` |
| T-032 | NO_PROXY | 未测（依赖直连可用） |
| T-033 | 坏代理 | **部分通过**（同 T-005） |
| T-034 | 代理下 DM/guild | **阻塞**（依赖 T-031） |

### BUG-P4.5-PROXY-WS（High，产品侧）

- **现象**：代理下 REST 正常、原生 WebSocket 正常，但 `serve` 的 Discord gateway 60s 未 ready。
- **证据链**：
  1. `curl -x 127.0.0.1:7890 https://discord.com/api/v10/gateway` → **200**（1.66s）；
  2. `openssl s_client -proxy 127.0.0.1:7890 -connect gateway.discord.gg:443` → **TLS 1.3 / 证书 OK**；
  3. 手动 WS upgrade（经 openssl 隧道）→ **`HTTP/1.1 101 Switching Protocols`**；
  4. 原生 `ClientWebSocket` + `HttpClient.DefaultProxy`（`HttpEnvironmentProxy`，`bypassDiscord=False`）→ **`connected state=Open`**（`evidence/wsprobe-proxy.txt`）；
  5. 同环境 `serve` → `plugin failed natter.transport.discord phase=activate code=upstream-connect-failed: Discord gateway did not become ready before the configured timeout.`（`evidence/proxy2-serve.log`）；
  6. 无代理对照：`HttpNoProxy`、`bypassDiscord=True`、WS 超时（`evidence/wsprobe.txt`）。
- **判断**：代理解析与 .NET WS 代理都正常，问题在 `Discord.Net 3.20.2` 的 `DefaultWebSocketProvider.Create(egressProxy)` 注入或 `DiscordSocketConfig` 使用路径（REST provider 生效、WS provider 未生效）。BUG-004 的 WS 修复不完整。
- **建议**：为 gateway 显式构造 `ClientWebSocket` 并设置 `Options.Proxy = egressProxy`（或升级/绕过 Discord.Net 的 provider）；修复后用 `serve + HTTP_PROXY` 回归 T-031/T-034。

## 7. 观察

- `T-013` 用 CLI `health` 连续两次时 `startedAt` 会变化（每次新建 host 进程），属预期；同进程（HTTP `/health`）才稳定——建议文档补充该边界。
- `health --offline` 时 `plugins.activated=0`、providers `available=false`，符合「不启动 upstream」语义。
- 当前环境 Discord 不可达，但 12:33 有一个 host 曾成功 `lastConnectedAt=04:33:39`（网络短暂可用/或旧进程），说明阻断是环境侧、不是 token 失效。

## 8. 结论

- **通过**：245/245、代理单测 5/5、offline health、online timestamps/同进程稳定性、Tier1 缺失失败、插件失败诊断、坏代理不挂死（DingTalk 立即失败）。
- **未完成（环境阻塞）**：T-014、T-020~T-025、T-030~T-034 —— Discord 网络不可达，代理下 gateway 未 ready。
- **缺陷**：`BUG-P4.5-DT-TIMESTAMPS`（Medium）；`OBS-P4.5-PROXY-WS`（待联合排查）。
- **Go/No-Go**：**No-Go**（`BUG-P4.5-PROXY-WS` High；P4 真实链路未完成）。health/offline/诊断修复项通过；DingTalk 时间戳为 Medium。修好 WS 代理注入后重跑 T-031/T-034 与 T-020~025。
