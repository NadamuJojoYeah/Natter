# 开发侧处置记录：2026-11-16 首次部署测试（P4.5 Conditional Pass）

> **状态**：Fixed · Ready for test-side retest（代理/超时、health 语义、offline、upstream 诊断已收敛；root systemd/24h soak 待运维复测）  
> **类型**：运维测试配对处置记录，不是长期设计文档  
> **对应测试**：[首次部署测试（Tier 1）](2026-11-16-first-deployment-test.md)  
> **对应 runbook**：[Natter 运维手册](../OPERATIONS_RUNBOOK.md)  
> **基线**：`HEAD=5091fb9`；测试期间新增 2 个代码文件、1 个测试文件、1 份记录  
> **归档**：`BUG-004`/`BUG-005` 正式合入、OBS 收敛、root/systemd 与被动 soak 完成后归档

---

## 1. 结论

本轮部署演练本身是成功的：

- publish + 插件 staging；
- config/secret/state 权限；
- `serve --health-port`；
- health / plugins / upstreams / loopback health；
- 单实例锁；
- WAL 备份 + 完整性校验 + 恢复演练；
- SIGTERM 优雅停止；
- health 采样；
- 真实 Discord 入站 → DeepSeek → 回复落库；
- 244/244 框架回归。

但结论应定为：

> **P4.5 Conditional Pass**：部署流程可执行，但代理修复仍是未提交代码，health 语义/CLI 巡检还有问题，systemd 与 24h soak 未完成。

当前不能把“Tier 1 已生产就绪”写死。

---

## 2. 处置总表

| ID | 级别 | 处置 | 责任侧 | 完成条件 |
| --- | --- | --- | --- | --- |
| BUG-004 Discord 代理不可用 | 阻塞 | 接受；正式评审并合入代理修复 | Discord transport | 代理/无代理/坏代理测试 |
| BUG-005 连接超时过短 | 高 | 接受；正式暴露/文档化超时 | Discord transport | 代理链路真实 READY |
| OBS-004 uptime 从首次访问起算 | 中 | 接受；改为 host 启动时间 | Host / Health | 首次采样不为 0 |
| OBS-005 Tier1-only degraded | 中 | 接受；裁剪 profile 或修 health 语义 | Profiles / Health | Tier1-only 为 healthy |
| OBS-006 CLI health 新建 gateway | 中 | 接受；增加 offline/纯快照模式 | CLI / Health | cron 不再抢 bot 会话 |
| OBS-007 长跑后 upstream 静默断连 | 高/运维阻断 | 接受；watchdog + 重连验证 + 告警 | Discord transport / Ops | 断连可发现/可恢复 |
| OBS-008 uptime 与墙钟不一致 | 低/中 | 接受；增加 startedAt/采样时间戳 | Health | soak 统计不误判 |
| ENV-003 无 root/systemd | 环境 | 不修产品；root 实机补测 | Ops | systemd 实际启动/停止 |
| ENV-004 NuGet 缓存只读 | 环境 | 不修产品；固定 NUGET_PACKAGES | CI/Ops | 诊断/CI 可写缓存 |

---

## 3. BUG-004：Discord 代理不可用

### 3.1 结论

接受为产品缺陷。

Discord.Net 3.20.2 的默认实现：

- REST：`DefaultRestClientProvider.Instance = Create()` → `useProxy: false`；
- Gateway：`DefaultWebSocketClient` 显式设置 `ClientWebSocketOptions.Proxy`，默认 `null`，绕过 `HTTP_PROXY/HTTPS_PROXY/ALL_PROXY`。

因此只设置环境变量时，`serve` 永远到不了 READY。

### 3.2 当前临时修复

工作区已有：

- `src/Natter.Chat.Discord/DiscordEgressProxy.cs`；
- `src/Natter.Chat.Discord/DiscordSocketTransport.cs`；
- `tests/Natter.Framework.Tests/DiscordEgressProxyTests.cs`（5 条）。

行为：

- 从 `HttpClient.DefaultProxy` 解析；
- 命中则同时注入：
  - `DefaultRestClientProvider.Create(useProxy: true, webProxy: proxy)`；
  - `DefaultWebSocketProvider.Create(proxy)`；
- `NO_PROXY` 命中则保持直连；
- 代理不进入插件 config/secret。

### 3.3 正式合入要求

- 代码 review；
- 保留 `NO_PROXY`/无代理/坏代理测试；
- 增加一条**真实代理链路 smoke**（gateway + REST 各一）；
- 代理地址/凭据不写日志；
- 文档：runbook §3.4、Discord 设计 §5.5；
- 提交后跑 CI + live；
- 不允许把临时文件长期留在工作区。

### 3.4 验收

- 直连：正常 READY；
- 环境代理：REST + gateway 都走代理；
- `NO_PROXY` 命中：直连；
- 坏代理：明确失败，不无限卡死；
- 无 secret/代理凭据泄漏。

---

## 4. BUG-005：连接超时短于代理握手

### 4.1 结论

接受为高优先级产品约束。

代理链路实测：

- REST `GET gateway/bot`：~11.7s；
- WS 握手到 Hello：~23s；
- 默认 30s `ConnectionTimeout` 会被切断，触发 `TimeoutException` 后反复重连。

临时修复：

```text
ConnectionTimeout = max(30s, readyTimeoutSeconds)
```

部署配置使用 `readyTimeoutSeconds=90`。

### 4.2 正式要求

- 保留该计算，避免默认 30s 低于 READY 窗口；
- 上限受控，不能无限大；
- 文档化“代理链路建议 ≥ 90s”；
- 增加测试：`ReadyTimeoutSeconds=90` 时 `ConnectionTimeout>=90s`；
- 错误信息区分“连接超时”和“鉴权失败”。

### 4.3 验收

- 代理链路真实 READY；
- 无重复重连风暴；
- 无代理时仍能在合理时间失败/成功。

---

## 5. OBS-004：uptimeSeconds 首次采样为 0

### 5.1 结论

接受为监控语义缺陷。

`RuntimeHealth` 使用静态 `Stopwatch.StartNew()`，第一次构造 health 快照才初始化，导致每次重启后的首个样本为 0。

### 5.2 修复

- 在 host 启动时记录 `StartedAt`；
- health 使用 `now - StartedAt`；
- 支持注入 `TimeProvider`/clock 便于测试；
- 增加首次 health 采样断言 `uptimeSeconds > 0`。

### 5.3 验收

- 启动后第一次 `/health` 不为 0；
- 重启后不误判；
- 采样曲线连续。

---

## 6. OBS-005：Tier1-only 仍 degraded

### 6.1 结论

接受为 health 策略问题，不是 Tier2 故障。

部署只装 Discord/DeepSeek，但 `profiles.json` 仍包含 Tier2 Spark profile；health 统计全部 profile provider，导致 `degraded`。

### 6.2 修复选择

优先：

- 部署时裁剪未使用 profile（Tier1-only config 只保留 tier1 profile）；
- health 只把“已安装/启用”的 profile provider 计入 required availability；
- Tier2 缺失显示 warning，不让整体 status 变 degraded。

两项都做更稳。

### 6.3 验收

- Tier1-only：`status=healthy`；
- 真正 Tier1 provider 缺失：`degraded`/失败；
- Tier2 缺失：不影响 Tier1 status；
- 文档说明 Tier1-only 预期。

---

## 7. OBS-006：CLI health 新建 gateway 会话

### 7.1 结论

接受为运维可用性问题。

`health`/`plugins list`/`upstreams` 每次启动完整 host，会连接 Discord，和主实例抢 bot 会话；cron 5 分钟一次会持续顶号。

### 7.2 修复

- 运维采样优先 loopback `/health`（runbook 已改）；
- CLI 增加 `--offline` / 纯快照模式：
  - 读取 state/config/plugin catalog；
  - 不启动 upstream；
  - 不连接 Discord；
- `health` 默认是否 offline 需要产品决策；至少提供显式选项；
- 文档标注“CLI health 会建会话”的旧行为。

### 7.3 验收

- `/health` 采样不影响 gateway；
- `health --offline` 不产生 gateway 会话；
- 故障排查时仍能在线 health。

---

## 8. ENV-003 / ENV-004

### ENV-003

本轮无 root、无 systemd 控制面，unit 未实际安装。

- 不是产品缺陷；
- 需要在 root VM/实机补：
  - `useradd`；
  - `systemctl enable --now`；
  - `Restart=on-failure`；
  - `ProtectHome`/`ProtectSystem`；
  - SIGTERM 优雅停止；
  - secret 轮换。

### ENV-004

NuGet 全局缓存只读影响诊断工程。

- 不修产品；
- CI/沙箱固定 `NUGET_PACKAGES` 到工作区；
- 文档记录。

---

## 9. 临时改动处置

| 文件 | 处置 |
| --- | --- |
| `src/Natter.Chat.Discord/DiscordEgressProxy.cs` | 正式 review 后合入 |
| `src/Natter.Chat.Discord/DiscordSocketTransport.cs` | 正式 review 后合入 |
| `tests/Natter.Framework.Tests/DiscordEgressProxyTests.cs` | 保留并补代理链路测试 |
| `docs/OPERATIONS_RUNBOOK.md` | 保留 |
| `docs/testing/2026-11-16-first-ops-deploy.md` | 保留 |
| `docs/DISCORD_PLUGIN_DESIGN.md` §5.5 | 保留 |

原则：

- 临时修复不能长期留在工作区；
- 合入前必须 code review；
- 不允许只靠“部署机手动改文件”维持；
- 代理相关变更属于产品代码，不是运维 workaround。

---

## 10. 复测闸门

P4.5 完成前：

- [x] 代理修复正式合入并跑 CI；
- [x] 代理/无代理/NO_PROXY/坏代理单元测试通过；
- [ ] 真实代理链路 READY；
- [x] 245/245 回归；
- [x] OBS-004 首次 uptime > 0；
- [x] OBS-005 Tier1-only healthy；
- [x] OBS-006 CLI health 不抢 gateway；
- [x] OBS-007 upstream lastEvent/reconnect 诊断；
- [x] OBS-008 startedAt/采样时间戳；
- [ ] root systemd 实机启动/停止/重启；
- [ ] Docker Compose 至少跑一次；
- [ ] 备份/恢复；
- [ ] 24h 被动 soak（health 采样、WAL/内存/重连）；
- [ ] 文档/索引归档。

---

## 11. 归档规则

本文是临时配对文档。

归档条件：

- `BUG-004`/`BUG-005` 正式合入；
- OBS-004/005/006/007/008 收敛；
- systemd/Docker/24h soak 完成；
- 测试负责人 Go；
- 干净 HEAD 复跑。

归档动作：

1. 与 [首次运维部署记录](2026-11-16-first-deployment-test.md) 一起归档；
2. 标记 `Archived`；
3. 更新 [测试索引](README.md)；
4. 保留修复提交 SHA/复测证据；
5. 不保留 state/凭据/临时代理诊断工程。

---

## 12. 一句话结论

**首次部署流程跑通了，但代理修复还是未提交产品代码，health 语义和 CLI 巡检还不适合长期运维。**

先正式合入代理/超时修复，再收 OBS-004/005/006，补 systemd 和被动 soak；这些完成前，开发侧已完成 BUG-004/005 与 OBS-004/005/006/007/008 的代码收敛；P4.5 仍只算 Conditional Pass，等待真实代理 READY、systemd 与被动 soak。
