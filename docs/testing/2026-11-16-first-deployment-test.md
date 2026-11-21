# 2026-11-16 · 首次部署测试（Tier 1 · runbook v1）

> **开发侧处置**：[首次部署处置记录](2026-11-16-first-ops-deploy-response.md)（临时配对文档，代理修复/OBS 收敛后归档）。

> **被测文档**：[运维手册（Runbook v1）](../OPERATIONS_RUNBOOK.md) §1–§11
> **关联**：[测试资产清单与归档规则](ASSETS.md)、[Discord 插件设计 §5.5](../DISCORD_PLUGIN_DESIGN.md)、[P4 验收](2026-11-15-p4-acceptance.md)

## 1. 概要

- **测试时间**：2026-11-16 22:13–22:47（部署与验收）、2026-11-17 12:07–12:10（T+13h 巡检与恢复复测）；均为 `Asia/Shanghai`
- **被测对象**：`natter` CLI Release 发布产物；`natter.transport.discord`、`natter.feature.ai.deepseek`、内置示例插件；Tier 1 profile `tier1-default`；运维手册 §1–§11 的部署/观察/备份/恢复/停止流程
- **基线**：`git HEAD=5091fb9`；开工前工作区 3 项未提交（`README.md`、`deploy/README.md` 已改，`docs/OPERATIONS_RUNBOOK.md` 未跟踪）；本轮新增 4 个未提交文件（2 代码 + 1 测试 + 本记录），见 §6
- **环境**：Arch Linux（`bwrap` 沙箱，`sudo` 被 `no-new-privileges` 拒绝、PID 1 为 `bwrap`）；.NET SDK 10.0.114 / runtime 10.0.14；bot `1571154023493410836`、guild `1123031326354841661`、频道 `1123031326354841665`；**Discord 出网必须走 `127.0.0.1:7890` HTTP 代理**（直连 `discord.com`/`gateway.discord.gg` 超时）
- **证据目录**：`artifacts/ops/tier1-run1/`（`artifacts/` 已被 git 忽略），含 `opt/`、`etc/`、`var/lib/natter`、`var/log/natter`、`backup/`
- **结论**：**通过（有条件）**。18 项用例中 17 项通过，1 项运行期异常（T+13h 巡检发现 Discord upstream 断连未自愈，重启后恢复）；代理环境下发现 2 个阻塞缺陷（`BUG-004`/`BUG-005`），已按 §6 以未提交改动临时修复并复测通过；systemd 形态未验证（`ENV-003`）。

## 2. 范围

### 2.1 用例矩阵

| 用例 | 层 | 用例内容 | 判定依据 |
| --- | --- | --- | --- |
| TC-01 | 构建 | CLI Release 发布到部署目录 | §2.1 步骤 1；产物可执行 |
| TC-02 | 构建 | Discord/DeepSeek 插件 staging（`lib/` + `plugin.json` + schema） | §1.1 布局；`plugins list` 能识别 |
| TC-03 | 配置 | `profiles.json` + 2 份插件 config 仅用 `${secret:...}` | §3.1/§3.3；`plugins config validate` |
| TC-04 | 配置 | `live.env` 0600、state 0700、DB 0600 | §1.2/§3.2；`stat` |
| TC-05 | 启动 | `serve --health-port` 启动，Discord upstream connected | §2.3；启动摘要 + `/health` |
| TC-06 | 检查 | `health --json` 关键字段 | §5.1 |
| TC-07 | 检查 | `plugins list --detail` 无 Failed | §2.3/§9.1 |
| TC-08 | 检查 | `upstreams --json` 能力与连接态 | §2.3 |
| TC-09 | 检查 | loopback `/health` 仅本机可访问 | §5.2 |
| TC-10 | 并发 | 同 state 第二个 `serve` 被单实例锁拒绝 | §4 单实例锁；§9.2 |
| TC-11 | 数据 | `natter-backup.sh` 备份 + `integrity_check` | §7.1 |
| TC-12 | 恢复 | 备份恢复进临时 state 后启动成功且数据保留 | §7.3 |
| TC-13 | 生命周期 | SIGTERM 优雅退出（rc=0） | §4；§2.1 的 stop 语义 |
| TC-14 | 观察 | health 采样脚本落盘且权限 600 | §5.3 |
| TC-15 | 链路 | 真实 Discord 消息 → DeepSeek → 回复落库 | §2.3“最小消息 smoke” |
| TC-16 | 巡检 | T+13h 巡检验证上游仍连接 | §10 每日/§11 soak |
| TC-17 | 恢复 | 断连后重启恢复 | §9.3；§4 restart |
| TC-18 | 数据 | 恢复演练在含真实数据的备份上复验 | §7.3 |

**不测**：

- systemd unit 实际安装与 `ProtectSystem/StateDirectory/Restart=on-failure` 行为（`ENV-003`，无 root）；
- Docker 形态（`deploy/docker/` 未执行）；
- 完整 24h soak（仅覆盖 13h 运行窗口）；
- Tier 2（DingTalk + Spark 未部署）；
- 多用户并发、多 guild 权限矩阵、限流风暴（runbook §12 列为未验证）。

## 3. 执行

### 3.1 部署命令

```bash
PREFIX=artifacts/ops/tier1-run1

# TC-01 发布
dotnet publish src/Natter.Cli/Natter.Cli.csproj -c Release -o $PREFIX/opt/natter

# TC-02 staging
dotnet build samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj -c Release
dotnet build src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj -c Release
# lib/* + plugin.json + config.schema.json → $PREFIX/opt/natter/plugins/{discord,deepseek}

# TC-03/TC-04 配置与权限
install -m 600 ~/.config/natter/live.env $PREFIX/etc/natter/live.env
chmod 700 $PREFIX/var/lib/natter
# profiles.json ← config/profiles.json；两份插件 config 只写 ${secret:...}

# TC-05 启动（包装脚本等价 unit 的 EnvironmentFile/Environment）
$PREFIX/bin/natter-env.sh serve --duration 0 --health-port 5099 --json

# TC-06–TC-09 检查
$PREFIX/bin/natter-env.sh health --json
$PREFIX/bin/natter-env.sh plugins list --detail
$PREFIX/bin/natter-env.sh upstreams --json
curl -s http://127.0.0.1:5099/health | jq

# TC-10 单实例
$PREFIX/bin/natter-env.sh serve --duration 30

# TC-11/TC-12 备份与恢复
scripts/natter-backup.sh $PREFIX/var/lib/natter/natter.db $PREFIX/backup/natter-2026-11-17-1209.db
sqlite3 $PREFIX/backup/natter-2026-11-17-1209.db "PRAGMA integrity_check;"
NATTER_STATE_DIR=<临时目录> opt/natter/natter --no-plugins serve --duration 5 --json

# TC-13 优雅停止（等价 systemctl stop）
touch $PREFIX/var/run/stop.flag

# TC-14 采样
$PREFIX/bin/natter-health-sample.sh
```

凭据来源：`~/.config/natter/live.env`（0600，仓库外；本轮复制到部署前缀并保持 0600）。代理：`$PREFIX/etc/natter/proxy.env`（0644，非 secret）。部署决定：`natter.transport.discord.json` 的 `readyTimeoutSeconds=90`（原 60，见 `BUG-005`）。

### 3.2 用例执行说明

- 所有检查命令都会启动完整 host：`health`/`plugins list`/`upstreams` 各自新建一次 Discord 网关会话，与主实例共用同一 bot token（`OBS-006`）。因此驻留期巡检统一走 loopback `/health`。
- 恢复演练第二次（TC-18）用 `--no-plugins` 启动：只验证 state/DB 与 health 结构，避免与主实例争抢 bot 会话；第一次演练（TC-12）带真实插件，覆盖了“恢复后仍能连网关”。

## 4. 结果

### 4.1 时间线

| 时间 | 用例 | 结果 | 关键数据 |
| --- | --- | --- | --- |
| 22:14 | TC-01/TC-02 | **通过** | `opt/natter` 49MB；`plugins/{discord,deepseek,greetings,metrics}` |
| 22:14 | TC-04 | **通过** | `var/lib/natter` 700、DB 600、`live.env` 600、备份 600、`health.jsonl` 600 |
| 22:15 | TC-03 | **通过** | 两份 config `{"valid":true,"issues":[]}` |
| 22:18 | TC-05（首次） | **失败** | `upstream-connect-failed: Discord gateway did not become ready before the configured timeout.` |
| 22:20 | TC-05（加 `proxy.env`） | **失败** | 同上 → 定位为 `BUG-004`（Discord.Net 两端都忽略代理） |
| 22:33–22:39 | 对照实验 | **完成** | `ws-probe`：默认=Open / 显式 `Proxy=null`=卡死 / 显式 `WebProxy`=Open |
| 22:39 | TC-05（修复后探针） | **通过** | `READY`，`latency=375ms`，`guilds=1`（`ConnectionTimeout=90s`） |
| 22:39–22:41 | TC-05（`serve`） | **通过** | `upstreams=[{discord, connected:true}]`，`activated=4 failed=0` |
| 22:39–22:41 | TC-06/TC-07/TC-08 | **通过** | `deepseek available=true`；4 插件 Activated；capabilities 正常 |
| 22:40 | TC-09 | **通过** | `curl 127.0.0.1:5099/health` → 200；无外网绑定 |
| 22:40 | TC-10 | **通过（被拒）** | `Another Natter serve instance is using state directory '...'`，exit=1 |
| 22:40 | TC-11 | **通过** | `backup/natter-2026-11-16-2240.db` 0600、`integrity_check=ok`、5 表 |
| 22:41–22:42 | TC-12 | **通过** | 临时 state 启动 30s，health `connected=true`，exit=0 |
| 22:43:16–18 | TC-13 | **通过** | `stop.flag` → SIGTERM → `serve exited rc=0`，2s 内释放 5099 |
| 22:43、22:47 | TC-14 | **通过** | `health.jsonl` 0600；`uptimeSeconds` 0 → 4 → 14 |
| 22:40:53 | TC-15（DM） | **通过** | 会话 DM：user 2 字 / assistant 17 字落库 |
| 23:32:41 | TC-15（guild 频道） | **通过** | 会话 `1F37AE7ED354EDC59A03E172`（频道 `1123031326354841665`），DB 累计 2 会话 / 16 消息 |
| 12:07:22（+1d） | TC-16 | **失败** | `status=degraded`、`upstreams[0].connected=false`、`plugins 4/0`；锁 mtime 仍为 22:47:07（进程未重启）、DB 最后写入 07:36 |
| 12:07:56–58 | TC-13（复测） | **通过** | 断连状态下 SIGTERM 仍 `rc=0`，2s 释放端口 |
| 12:08:04–56 | TC-17 | **通过** | 重启后 `connected=true`，恢复耗时 ≈ 40s（含代理握手） |
| 12:09 | TC-18 | **通过** | 备份 `sessions=2 messages=16`、`integrity_check=ok`；恢复后同数量，exit=0，主实例未受影响 |
| 12:09 | TC-14（补样） | **通过** | 采样 `connected=[true]`，`uptimeSeconds` 23 → 28 |

### 4.2 汇总

- **通过 17 / 失败 1（TC-16，运行期断连，已由 TC-17 恢复）+ 2 个阻塞缺陷（已临时修复、复测通过）**
- 框架回归：**244/244 通过**（含本轮新增 5 条代理解析测试）
- `health.status` 恒为 `degraded`：唯一不可用项是未部署的 `natter.feature.ai.spark#spark`（`OBS-005`）
- 凭据未泄漏：`grep -F` 反查 `var/log/**`、`etc/natter/config/**` 均无 token/key 明文；`live.env`、DB、备份、`health.jsonl` 均为 600

### 4.3 关键输出

```text
# TC-05 启动摘要
{"mode":"serve","profiles":2,"defaultProfile":"tier1-default","providers":1,
 "upstreams":[{"source":"discord:discord:1571154023493410836","connected":true}],
 "plugins":{"activated":4,"failed":0,"failures":[]}}

# TC-06 health（TC-17 恢复后）
{"status":"degraded","uptimeSeconds":23,"profiles":2,"conversations":2,
 "upstreams":[{"source":"discord:discord:1571154023493410836","connected":true}],
 "providers":[{"id":"natter.feature.ai.deepseek#deepseek","available":true,...},
              {"id":"natter.feature.ai.spark#spark","available":false,...}],
 "plugins":{"activated":4,"failed":0,"failures":[]}}

# TC-16 T+13h 巡检（断连证据）
{"status":"degraded","uptimeSeconds":18539,"conversations":2,
 "upstreams":[{"source":"discord:discord:1571154023493410836","connected":false}],
 "plugins":{"activated":4,"failed":0,"failures":[]}}

# TC-10 单实例锁
Another Natter serve instance is using state directory
'/home/ndm/Projects/csharp/Natter/artifacts/ops/tier1-run1/var/lib/natter'.
Use one instance per bot token/state directory.   (exit=1)

# TC-11 / TC-18 备份
0|2|2
backup -> backup/natter-2026-11-17-1209.db
ok
sessions=2
messages=16
```

## 5. 具体问题

### BUG-004 · 代理出网环境下 Discord 全链路不可用

- **发现时间**：2026-11-16 22:18
- **严重度**：阻塞（代理环境 Tier 1 完全不可用）
- **影响**：进程环境已设 `HTTP_PROXY/HTTPS_PROXY/ALL_PROXY`，`serve` 仍报 `upstream-connect-failed`，网关永不 READY；REST `GET /gateway/bot` 同样挂住。
- **复现**：
  ```bash
  HTTP_PROXY=http://127.0.0.1:7890 HTTPS_PROXY=http://127.0.0.1:7890 ALL_PROXY=http://127.0.0.1:7890 \
    bin/natter-env.sh serve --duration 60 --json
  # plugins.failed=1 → natter.transport.discord / upstream-connect-failed
  ```
- **证据**：
  1. `ClientWebSocket` 三模式对照（同代理、同目标）：默认 → `Open`；`Options.Proxy = null` → 卡死；`Options.Proxy = new WebProxy(...)` → `Open`。
  2. 反编译 Discord.Net 3.20.2：`DefaultRestClientProvider.Instance = Create()`（`useProxy: false`）；`DefaultWebSocketClient.ConnectInternalAsync` 中 `_client.Options.Proxy = _proxy`（默认 null）。
  3. 死端口代理（`127.0.0.1:7999`）下探针依旧 `Connecting` 40s，证明两端均未走代理。
- **测试侧处置**：新增 `DiscordEgressProxy` 并在 `DiscordSocketTransport` 注入 REST/WebSocket 两个 provider（§6），复测通过；**不是正式修复**。
- **建议**：评审并入正式实现；补代理链路 live 用例；runbook §3.4 已补配置说明。

### BUG-005 · Discord.Net 默认 30s 连接超时短于代理握手耗时

- **发现时间**：2026-11-16 22:39
- **严重度**：高（代理可用也会反复断连）
- **影响**：REST `/gateway/bot` 实测 11.7s、WS 到 Hello ~23s，累计 >30s 触发 `TimeoutException: The operation has timed out.` → 断连重连，永不 READY。
- **复现**：探针保持默认 `ConnectionTimeout` 跑 40s：`Connecting → Received Hello → Sent Identify → TimeoutException → Disconnecting`。
- **证据**：`[Verbose] Rest: GET gateway/bot: 11765.79 ms`；改 90s 后 `Received Dispatch (READY)`、`Connected latency=375 guilds=1`。
- **测试侧处置**：`ConnectionTimeout = max(30s, readyTimeoutSeconds)`，部署 config `readyTimeoutSeconds=90`。
- **建议**：正式记录该约束（runbook §3.4 已写“代理链路建议 ≥ 90”）。

### OBS-004 · `uptimeSeconds` 从首次访问 health 起算

- **发现时间**：2026-11-16 22:39（12:08 复现）
- **现象**：进程已运行约 45s，第一次 `/health` 返回 `uptimeSeconds=0`，后续 4 → 14；每次重启后首样都为 0。
- **根因**：`RuntimeHealth` 用静态 `Stopwatch.StartNew()`，类型首次被访问时才初始化。
- **影响**：采样曲线每次首个样本为 0，易误判重启。
- **建议**：注入 host 启动时间，或改用进程启动时间戳。

### OBS-005 · 只部署 Tier 1 时 `status=degraded`

- **发现时间**：2026-11-16 22:41
- **现象**：`upstreams.connected=true`、`plugins.failed=0`、DeepSeek `available=true`，但 `status=degraded`。原因是 `profiles.json` 的 `tier2-dingtalk-spark` 引用了未安装的 `spark` provider。
- **影响**：runbook 原写“预期 healthy”，Tier 1-only 部署会恒 degraded，报警阈值失效。
- **处置**：runbook §2.3 已补“Tier 1-only 的 degraded 属预期”。
- **建议**：部署时裁剪未使用 profile，或让 status 只统计已安装 provider 的 profile。

### OBS-006 · CLI 检查命令会新建 Discord 网关会话

- **发现时间**：2026-11-16 22:39
- **现象**：`health`/`plugins list`/`upstreams` 每次启动完整 host 并连接 Discord（各约 40s），与主实例共用同一 bot token。
- **影响**：把 CLI `health` 做成 5 分钟 cron 会持续抢占主实例会话。
- **处置**：采样脚本改为 loopback `/health` 优先、CLI 回退；runbook §5.3 已同步。
- **建议**：提供 `--offline`/纯快照模式给运维巡检。

### OBS-007 · 长时间运行后 upstream 断连且未自愈

- **发现时间**：2026-11-17 12:07（T+13h）
- **现象**：`connected=false`，`plugins 4/0` 无 Failed；锁文件 mtime 仍为 22:47:07、DB 最后写入 07:36，说明**进程未重启**；同时本机代理正常（`curl -x http://127.0.0.1:7890 https://discord.com/api/v10/gateway` → 200），直连依旧超时。
- **影响**：bot 在无 Failed 日志的情况下静默失联，只能靠 health 发现；`plugins list` 看不出问题。
- **测试侧处置**：`touch var/run/stop.flag` 优雅停止（rc=0）后重启，40s 内恢复 `connected=true`（TC-17）。
- **建议**：排查 Discord.Net 自动重连在代理链路下的失效点（断流/重连退避是否终止），补 upstream 断连告警与可选自动重启策略。

### OBS-008 · `uptimeSeconds` 与墙钟不一致（休眠不计时）

- **发现时间**：2026-11-17 12:07
- **现象**：实例自 2026-11-16 22:47 起存活约 13h20m，`uptimeSeconds=18539`（≈5h09m），差值与该机夜间休眠时长吻合。
- **根因**：`Stopwatch` 基于 `CLOCK_MONOTONIC`，系统挂起期间不前进；用 `uptimeSeconds` 做时长对账会偏差。
- **建议**：同时展示“单调 uptime”和“墙钟启动时间”；soak 统计以采样时间戳为准。

### ENV-003 · 无 root / 无 systemd 控制面，unit 无法实际安装

- **现象**：`sudo` 被 `no-new-privileges` 拒绝；PID 1 为 `bwrap`，`/etc/systemd/system` 不可写，`useradd natter` 不可执行。
- **影响**：runbook §2.1 的 `useradd`/`systemctl enable --now`、unit 的 `ProtectSystem/StateDirectory/Restart` 未验证；本轮以等价目录前缀 + 直接进程覆盖了配置、权限、锁、健康、备份、恢复、停止。
- **建议**：在有 root 的实机补一次 systemd 形态验收。

### ENV-004 · NuGet 全局缓存只读

- **现象**：诊断工程 restore 无法写 `~/.nuget/packages` 与 `~/.local/share/NuGet/http-cache`。
- **处置**：探针使用 `NUGET_PACKAGES=<workspace>/bin/nuget`；不影响部署产物。
- **建议**：沙箱/CI 固定 `NUGET_PACKAGES` 到工作区目录。

## 6. 临时改动

| 文件 | 改动 | 原因 | 回退 |
| --- | --- | --- | --- |
| `src/Natter.Chat.Discord/DiscordEgressProxy.cs`（新增） | 从 `HttpClient.DefaultProxy` 解析 `IWebProxy`（含 `NO_PROXY` 判断） | `BUG-004` | 删除文件 |
| `src/Natter.Chat.Discord/DiscordSocketTransport.cs` | 命中代理时注入 `RestClientProvider`/`WebSocketProvider`；`ConnectionTimeout=max(30s, readyTimeoutSeconds)` | `BUG-004`/`BUG-005` | `git checkout -- src/Natter.Chat.Discord/DiscordSocketTransport.cs` |
| `tests/Natter.Framework.Tests/DiscordEgressProxyTests.cs`（新增） | 5 条纯逻辑单测（null/bypassed/命中/环境来源/探针豁免） | 覆盖 `BUG-004` | 删除文件 |
| `docs/OPERATIONS_RUNBOOK.md` | §1.1/§2.3/§3.4/§5.3/§14 补代理、degraded 语义、loopback 采样 | 部署反馈 | `git checkout -- docs/OPERATIONS_RUNBOOK.md`（若已提交则 revert） |
| `docs/DISCORD_PLUGIN_DESIGN.md` | §5.5 出网代理 | 同上 | `git checkout -- docs/DISCORD_PLUGIN_DESIGN.md` |

重建：`dotnet build samples/Natter.Sample.Plugin.Discord -c Release` → 按 §3.1 staging 覆盖 `opt/natter/plugins/discord`。部署决定（非代码改动）：`readyTimeoutSeconds=90`、`proxy.env` 0644、采样走 loopback。

## 7. 结论与后续

- **结论**：runbook v1 的 Tier 1 首次部署流程可执行；发布、配置、secret/state 权限、启动、四项检查、单实例锁、备份、恢复、优雅停止、采样均通过；真实 Discord 消息完成两轮“入站 → DeepSeek → 回复落库”（DM 与 guild 频道）；代理环境必须依赖 `BUG-004`/`BUG-005` 的修复才能上线。
- **待开发确认**：`BUG-004`/`BUG-005` 是否并入正式实现；`OBS-004`/`005`/`006`/`007`/`008` 的处置优先级（其中 `OBS-007` 建议按运维阻断项跟踪）。
- **下一轮建议**：
  1. root 实机补 systemd 形态（`ENV-003`），含 `Restart=on-failure` 与 secret 轮换演练；
  2. 完整 24h soak（runbook §11），以采样时间戳统计，覆盖 `OBS-007` 的断连复现与重连退避；
  3. 补 Tier 2 部署与 `OBS-005` 的 profile 裁剪策略；
  4. CI 增加代理链路冒烟（本地 http/socks 代理 + 假网关）；
  5. upstream 断连告警（loopback 采样 + 阈值）纳入日常巡检。

---

## 附录 A · 运行现状与交接

当前实例：2026-11-17 12:08 重启，`serve --duration 0 --health-port 5099`，`upstreams.connected=true`、`plugins 4/0`，仅 Tier 2 provider 不可用（`OBS-005`）。

```bash
cd artifacts/ops/tier1-run1
bin/natter-env.sh serve --duration 0 --health-port 5099 --json   # 启动
touch var/run/stop.flag && sleep 5                               # 优雅停止（SIGTERM，rc=0）
curl -s http://127.0.0.1:5099/health | jq                        # 巡检（推荐，不新建会话）
bin/natter-env.sh health --json                                  # 人工排障（会新建 bot 会话）
bin/natter-health-sample.sh                                      # 采样 → var/log/natter/health.jsonl
scripts/natter-backup.sh var/lib/natter/natter.db backup/$(date +%F-%H%M).db
```

## 附录 B · 证据清单

| 路径（相对 `artifacts/ops/tier1-run1/`） | 内容 |
| --- | --- |
| `var/log/natter/serve.log` | 各次启动摘要（22:39–22:47、12:08 恢复） |
| `var/log/natter/serve-verbose.log` | 22:24 无代理失败的 verbose 现场 |
| `var/log/natter/second-instance.log` | TC-10 单实例锁拒绝 |
| `var/log/natter/restore-drill.log` / `restore-drill-2.log` | TC-12 / TC-18 恢复演练 |
| `var/log/natter/cli/*.log` | TC-06/07/08 原始输出 |
| `var/log/natter/health.jsonl` | TC-14 采样（含 12:09 补样） |
| `backup/natter-2026-11-16-2240.db` / `natter-2026-11-17-1209.db` | TC-11 / TC-18 备份（0600） |
| `var/lib/natter/natter.db` | 2 会话 / 16 消息（含 22:40 DM 与 23:32 guild 频道） |
| `bin/ws-probe`、`bin/dnet-probe` | `BUG-004`/`BUG-005` 最小诊断工程（源码保留，构建产物已清理） |
