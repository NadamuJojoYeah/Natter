# 2026-11-17 · P4.5 运维验收执行记录

> **被测文档**：[P4.5 修复验证（运维交接）](2026-11-17-p4-5-ops-verification.md)
> **配套**：[运维手册](../OPERATIONS_RUNBOOK.md)、[首次部署测试](../testing/2026-11-16-first-deployment-test.md)
> **状态**：Executed（可执行项完成，BLOCKED 项见 §5）  
> **开发侧处置**：[P4.5 联合处置记录](../testing/2026-11-17-p4-5-joint-response.md)

## 1. 概要

- **测试时间**：2026-11-17 12:31–12:45（`Asia/Shanghai`）
- **基线**：`HEAD=c131e73`（代理/health/offline/upstream 诊断修复已合入）；接手时线上实例仍是 11-16 的旧产物（`/health` 无 `startedAt`），本轮先做升级部署再验收
- **被测对象**：`natter` CLI + Discord/DeepSeek 插件 + `deploy/` 示例；运维交接文档 O-001–O-053
- **环境**：Arch Linux（`bwrap` 沙箱；`sudo` 被 `no-new-privileges` 拒绝；PID 1 为 `bwrap`；docker socket 无权限）；.NET SDK 10.0.114；Discord 出网必须走 `127.0.0.1:7890`；凭据 `~/.config/natter/live.env`（0600）
- **证据目录**：`artifacts/ops/p4.5/2026-11-17-1230/`（`logs/`、`backup/`、`health.json`、`natter-prev/`、`faults/`、`deploy.log`）
- **结论**：**有条件通过**。可执行的产品侧用例全部通过（代理链路、health/offline/Tier1-only、failed plugin 诊断、备份恢复、升级/回滚）；3 项代理失败态用例部分通过（诊断信息与退出时间，见 `OBS-009`/`OBS-010`）；17 项依赖 root / 长跑 / 平台权限，按文档口径标 **BLOCKED**，不计入通过。

## 2. 前置

| 项 | 结果 |
| --- | --- |
| `git rev-parse --short HEAD` | `c131e73` ✔ |
| `dotnet --info` | SDK `10.0.114` / runtime `10.0.14` ✔ |
| state 备份 | `backup/natter-prev.db`，0600，`integrity_check=ok`，2 会话 / 16 消息 ✔ |
| 二进制备份 | `natter-prev/`（49MB，旧产物） ✔ |
| 新产物 | `dotnet publish -c Release` → `opt/natter.new`，重建并 staging `discord`/`deepseek` 插件 ✔ |
| 升级方式 | 停实例 → 目录切换（`opt/natter` ↔ `opt/natter.prev-*`）→ 启动；未改产品代码 ✔ |
| secret 处理 | 仅 `etc/natter/live.env` 0600；证据目录未复制 `live.env` ✔ |

> 交接文档口径“运维负责真实部署 + 代理 + systemd/Docker + 长跑”；本轮把“升级部署”作为验收前置一并做了，因此也顺带覆盖了 runbook §8 的升级/回滚（§3.9）。

## 3. 用例结果

### 3.1 §2 代理（O-001–O-006）

| ID | 场景 | 结果 | 证据 / 数据 |
| --- | --- | --- | --- |
| O-001 | 无代理，直连不可用 | **部分通过** | 明确失败：`upstream-connect-failed`，50.6s 退出（`--duration 30` + 关闭等待）；诊断只报 “gateway did not become ready”，未指向直连失败（`OBS-010`） |
| O-002 | HTTP 代理启动 | **通过** | 升级后 `status=healthy`、`connected=true`、`plugins 4/0`；启动 0→READY 约 40s（代理握手） |
| O-003 | `NO_PROXY=discord.com` | **部分通过** | bypass 生效（走直连 → 失败），51.0s；同 O-001 的诊断/退出问题 |
| O-004 | 坏代理 `127.0.0.1:1` | **部分通过** | 有明确 `failed plugin`（`code=upstream-connect-failed`），但 `--duration 60` 实际 **151s** 才退出（`OBS-009`）；SIGTERM 场景 131ms `rc=0` |
| O-005 | 代理下 DM | **待真人复验** | 历史证据：2026-11-16 22:40 DM 往返落库（旧产物，同代理代码路径） |
| O-006 | 代理下 guild @bot | **待真人复验** | 历史证据：2026-11-16 23:32 频道会话（2 会话 / 16 消息） |

### 3.2 §3 systemd（O-010–O-015）

**BLOCKED**。证据：

```text
$ systemctl is-system-running
running                      # 无可用控制面
$ sudo -n true
sudo: The "no new privileges" flag is set, which prevents sudo from running as root.
```

无 root 无法 `useradd`/写 `/etc/systemd/system`/`systemctl enable --now`；unit 的 `ProtectSystem`/`StateDirectory`/`Restart=on-failure` 行为未验证。**按文档口径不计通过。**

### 3.3 §4 Docker（O-020–O-024）

**BLOCKED**。证据：

```text
$ docker info
Server:
permission denied while trying to connect to the docker API at unix:///var/run/docker.sock
$ docker ps
permission denied ...
```

`deploy/docker/` 静态检查通过（UID 10001、`/config:ro`、`/state` 命名卷、`read_only: true`、`no-new-privileges`），但镜像构建/运行/持久化未实测。附带发现：`deploy/README.md` 要求 `cp live.env.example live.env`，而 `deploy/docker/live.env.example` 不存在（`OBS-011`）。

### 3.4 §5 health / offline（O-030–O-035）

| ID | 场景 | 结果 | 证据 / 数据 |
| --- | --- | --- | --- |
| O-030 | loopback health 时间戳 | **通过** | `startedAt=2026-11-17T04:33:32.527Z` 恒定，`sampledAt` 每次变化，`uptimeSeconds` 递增 |
| O-031 | 首次采样 `uptimeSeconds>0` | **通过** | 新产物首次采样即 `48`（旧产物是 `0`，`OBS-004` 已修） |
| O-032 | Tier1-only `status=healthy` | **通过** | 旧产物 `degraded` → 新产物 `healthy`（Spark 缺失不再降级，`OBS-005` 已修） |
| O-033 | offline 不建 gateway | **通过** | `health --offline --json`：**530ms**，`status=offline`，`upstreams=[]`，`plugins activated=0`，`conversations=2`（读 state） |
| O-034 | failed plugin 诊断 | **通过** | 非法 config（明文 token）：`validate` exit=1 + `/token` pattern；`serve` → `failed=1`，`id/phase=config/code=invalid-config` |
| O-035 | 断连字段 | **部分通过** | 字段存在：`lastEventAt`/`lastConnectedAt`/`lastDisconnectAt=null`/`reconnectCount=0`/`lastError=null`；真实断连场景 **BLOCKED**（无 root 无法断网、代理进程不可控） |

关键输出（升级后首次 loopback）：

```json
{"status":"healthy","uptimeSeconds":50,
 "startedAt":"2026-11-17T04:33:32.5276281+00:00",
 "sampledAt":"2026-11-17T04:34:22.2761447+00:00",
 "upstreams":[{"source":"discord:discord:1571154023493410836","connected":true,
               "lastEventAt":"...04:33:39.400Z","lastConnectedAt":"...04:33:39.400Z",
               "lastDisconnectAt":null,"reconnectCount":0,"lastError":null}],
 "plugins":{"activated":4,"failed":0,"failures":[]}}
```

> 补充观测：CLI 在线 `health --json` 每次是新进程，`startedAt` 随之变化（5.4s / 11.1s 两次实测）；只有 loopback `/health` 的 `startedAt` 对应主实例。O-030 的判据应限定为 loopback（`OBS-014`）。

### 3.5 §6 备份 / 恢复（O-040–O-042）

| ID | 场景 | 结果 | 证据 / 数据 |
| --- | --- | --- | --- |
| O-040 | 备份 | **通过** | `scripts/natter-backup.sh` → `backup/natter-2026-11-17-1242.db`，0600，`integrity_check=ok`，2 会话 / 16 消息 |
| O-041 | 恢复 | **通过** | 备份复制到临时 state，`--no-plugins serve --duration 3` exit=0；恢复后 `sessions=2`、`messages=16` |
| O-042 | 恢复后真实消息 | **待真人复验** | 需频道内真人 @bot；数据面已由 O-041 覆盖 |

### 3.6 §7 OBS-007 静默断连

| 检查项 | 结果 |
| --- | --- |
| health 能看到断连 | **通过（可观测）**：旧产物 11-17 12:07 实测 `connected=false`，进程未重启（锁 mtime 22:47、DB 最后写入 07:36） |
| 新增字段可见 | **通过**：`lastConnectedAt`/`lastDisconnectAt`/`reconnectCount`/`lastError` 已在 `/health` 暴露 |
| 断连后自动恢复 | **未宣称**：修复只增加观测，无 watchdog；需人工/外部重启（交接文档口径一致） |
| 人工重启恢复 | **通过**：多轮重启均 40s 内 `connected=true` |

### 3.7 §8 被动 24h soak（O-050–O-053）

**BLOCKED（时间）**。仅有 11-16 22:47 → 11-17 12:07 的旧产物 13h 窗口证据（含一次静默断连），不满足“24h 被动 soak”判定；采样脚本与 loopback 口径已就绪。

### 3.8 §9 Secret 轮换

**BLOCKED（平台权限）**。Discord/DeepSeek 控制台重置需要账号持有人；本轮只验证了“日志无 secret”（证据目录与日志反查无 token/key）。

### 3.9 runbook §8 升级 / 回滚（附加）

| 时间 | 动作 | 结果 |
| --- | --- | --- |
| 12:33:27 | 停旧实例（SIGTERM）→ 切换新产物 | `rc=0`，端口释放 |
| 12:33:32 | 新产物启动 | `healthy` + `startedAt`/`sampledAt`/断连字段生效 |
| 12:43 | 回滚：停新实例 → 恢复旧产物 | 旧产物 `status=degraded`、`startedAt=null` → 证明版本切换生效 |
| 12:44:17 | 复归：停旧实例 → 恢复新产物 | `healthy`、`connected=true`、`plugins 4/0` |

切换耗时：启动约 40s（含代理握手）；停止 2s 内。状态 DB 在 3 次切换中保持一致（2 会话 / 16 消息）。

## 4. 具体问题

### OBS-009 · 坏代理下 `--duration` 到期退出延迟 90s

- **发现时间**：2026-11-17 12:40
- **现象**：`serve --duration 60` 在坏代理（`127.0.0.1:1`）下实际 **151s** 才退出，多出约 90s ≈ `ConnectionTimeout`；同样配置下 SIGTERM 退出仅 **131ms**。
- **影响**：演练/一次性任务用 `--duration` 时会被拖住；运维 `systemctl stop`（SIGTERM）不受影响，但“用 duration 做健康探测”的做法会误判卡死。
- **证据**：`logs/o-004-bad-proxy.log`（151s）、`logs/obs-009-sigterm-bad-proxy.log`（131ms, rc=0）。
- **建议**：duration 到期与 SIGTERM 走同一条取消路径，或在关闭时跳过 `ConnectionTimeout` 等待。

### OBS-010 · 激活失败信息不体现底层原因

- **发现时间**：2026-11-17 12:38
- **现象**：无代理 / NO_PROXY bypass / 坏代理三种失败都只报 `Discord gateway did not become ready before the configured timeout.`；SIGTERM 中断时则变成 `A task was canceled.`。
- **影响**：排障时无法区分“代理拒绝、DNS 失败、直连不可用、token 错误”。
- **建议**：在 `upstream-connect-failed` 的 message 中带上内层异常类型/最后一次连接错误（脱敏后）。

### OBS-011 · `deploy/README.md` 引用的 `live.env.example` 不存在

- **发现时间**：2026-11-17 12:34
- **现象**：`deploy/README.md` 与运维交接 §4 都要求 `cp live.env.example live.env`，`deploy/docker/` 下只有 `compose.yaml`、`Dockerfile`。
- **影响**：Docker 部署第一步即失败，运维需自行猜变量名。
- **建议**：补 `deploy/docker/live.env.example`（仅变量名与占位值，不含 secret）。

### OBS-012 · §13 联系信息仍是占位符

- **现象**：`Owner/On-call/Escalation` 仍为 `[OWNER]`/`[ONCALL]`/`[ESCALATION]`。
- **影响**：SEV1/SEV2 时无法按文档升级；文档承诺的应急路径不可用。

### OBS-013 · `sendFailures` 无法观测

- **现象**：交接文档 §8 与 runbook §6 都要求关注 `sendFailures`，但 `/health`、`health --json`、CLI 输出均无该字段（Discord upstream 内部有统计）。
- **影响**：soak 检查项无法执行。
- **建议**：把 upstream 的 `sendFailures`/发送错误计数暴露进 health，或在文档中改为可观测指标。

### OBS-014 · O-030 判据未区分 loopback 与 CLI

- **现象**：CLI `health --json` 每次是新进程，`startedAt` 随之变化（实测两次不同）；只有 loopback `/health` 的 `startedAt` 稳定对应主实例。
- **影响**：按文档字面执行会误判“startedAt 不稳定”为缺陷。
- **建议**：O-030 明确“loopback 场景”；CLI 场景改判 `sampledAt` 单调、`uptimeSeconds` 与本进程一致。

## 5. BLOCKED 清单（不计通过）

| 范围 | 用例 | 阻塞原因 | 解除条件 |
| --- | --- | --- | --- |
| systemd | O-010–O-015 | 无 root（`no-new-privileges`）、无可用 systemd 控制面 | root 实机 |
| Docker | O-020–O-024 | docker socket 无权限 | 加入 docker 组或 root |
| 静默断连 | O-035 断连场景 | 无法断网/控制代理进程 | root（iptables）或可控代理 |
| 24h soak | O-050–O-053 | 时间窗口不足（仅 13h 旧产物） | 连续 24h 运行 |
| secret 轮换 | §9 | 平台凭据需账号持有人 | Discord/DeepSeek 控制台权限 |
| 真人消息 | O-005/O-006/O-042 | 需真人在频道/DM 触发 | 人工配合 |

## 6. Go 条件勾选（按交接文档 §10）

- [x] 代理：有/无/NO_PROXY/坏代理均已执行（失败态判据部分通过，见 `OBS-009`/`OBS-010`）
- [ ] systemd 启动/停止/重启/on-failure —— **BLOCKED**
- [ ] Docker —— **BLOCKED**
- [x] health/offline/Tier1-only
- [x] 备份/恢复
- [x] OBS-007 有观测证据，未宣称自愈
- [ ] 24h 被动 soak —— **BLOCKED**
- [ ] secret 轮换 —— **BLOCKED**
- [x] 无密钥泄漏（日志/证据目录反查无 token/key）

## 7. 结论与后续

- **结论**：P4.5 的三项核心修复（代理、`startedAt/uptime`、Tier1-only 健康语义、offline 快照、failed plugin 诊断）在真实代理环境下**全部验证通过**；升级与回滚可执行；失败态诊断与 `--duration` 退出延迟是新发现，不影响上线但影响排障效率。
- **不能宣称**：OBS-007 自愈（仅可观测）、systemd/Docker/24h soak 通过（BLOCKED）。
- **下一轮建议**：
  1. root 实机补 systemd（含 `Restart=on-failure`）与 Docker 构建；
  2. 修 `OBS-009`（duration 取消路径）与 `OBS-010`（失败原因透出）；
  3. 补 `deploy/docker/live.env.example`、填 §13 联系人、暴露 `sendFailures`；
  4. 24h soak 用 loopback 采样（`curl → health.jsonl`），以采样时间戳统计；
  5. 真实断连演练需要 root 或可控代理，完成后回填 O-035。

### 7.1 与测试侧报告的交叉验证

同一时间窗内测试侧产出 [P4.5 测试报告](../testing/2026-11-17-p4-5-test-report.md)，其中 Gate T2 的 `T-014`（Tier1-only healthy）因“Discord gateway 未 ready”未完成，Discord 真实链路 T-020 类被网络阻断。

运维侧结论可互补：

- 同一代理（`127.0.0.1:7890`）、同一时段，部署实例（`proxy.env` 注入进程环境）`/health` 为 `healthy`、`upstreams[0].connected=true`，并完成升级/回滚；说明“代理 + 修复”在本机可用，测试侧未 ready 更可能是其 live 进程未继承代理变量，而非链路本身不可用；
- 建议测试侧跑 `T-020`–`T-034` 前显式注入 `HTTP_PROXY/HTTPS_PROXY/ALL_PROXY/NO_PROXY`（与 §2.1 一致），再复测 `T-014`；
- 两侧共同确认 `OBS-009`/`OBS-010`（失败慢、诊断弱）：测试侧 `T-005` 同样记录“60s ready 超时后失败、不无限挂死但慢”。

## 8. 证据清单

| 路径（相对 `artifacts/ops/p4.5/2026-11-17-1230/`） | 内容 |
| --- | --- |
| `git-head.txt` / `dotnet-info.txt` | 基线 |
| `health.json` | 升级后 loopback health（healthy + 全字段） |
| `logs/health-online-1.json` / `-2.json` | CLI 在线快照（startedAt 随进程变化） |
| `logs/health-offline.json` | offline 快照（530ms、status=offline） |
| `logs/o-001-no-proxy.log` / `o-003-no-proxy-bypass.log` / `o-004-bad-proxy.log` | 代理失败态对照 |
| `logs/o-034-validate.log` / `o-034-serve.log` | failed plugin 诊断 |
| `logs/obs-009-sigterm-bad-proxy.log` | SIGTERM 131ms、rc=0 |
| `logs/o-041-restore.log` | 恢复演练（2 会话 / 16 消息） |
| `logs/serve-before-upgrade.log` / `logs/deploy.log` | 升级/回滚时间线 |
| `logs/systemd-status.txt` / `sudo.txt` / `docker-info.txt` / `docker-ps.txt` | BLOCKED 证据 |
| `backup/natter-prev.db` / `natter-2026-11-17-1242.db` | 升级前 / 验收后备份（0600） |
| `natter-prev/` | 旧产物（回滚用） |
| `faults/` | 故障注入配置与脚本（不含 secret） |

## 9. 当前运行状态

- 实例：新产物，`serve --duration 0 --health-port 5099`，2026-11-17 12:44:22 启动
- `/health`：`healthy`、`connected=true`、`plugins 4/0`、`startedAt=2026-11-17T04:44:22Z`
- 备份：`backup/natter-2026-11-17-1242.db`（2 会话 / 16 消息）
