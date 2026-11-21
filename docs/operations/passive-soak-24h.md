# 24h 被动 soak：采样、审计与验收口径

> **目的**：部署后无人值守跑满 24 小时，并留下可复核的证据
> **工具**：`scripts/soak-sample.sh`（采样）、`scripts/soak-report.py`（审计）、`scripts/ops-health-alert.sh`（告警）
> **背景**：P4.5 收口要求「24h 采样完整」，此前两轮都因机器休眠/实例重启没达成

---

## 1. 为什么必须是被动采样

`natter health --json`（不带 `--offline`）会**启动完整 host 并新建一次 Discord 网关会话**，与主实例抢同一个 bot 连接。所以巡检与长跑采样只能：

- 读主实例的 loopback `/health`（推荐）；
- 或 `natter health --offline --json`（不建 gateway，但拿不到 uptime/reconnect 等运行态）。

`soak-sample.sh` 默认走 loopback，失败时才可选用 offline 回退。

---

## 2. 启动采样（三种方式，选一个）

### 2.1 systemd timer（生产推荐）

```bash
# 安装时 deploy/systemd/install.sh 已经把 sampler 和 timer 装好
sudo systemctl list-timers natter-soak.timer
sudo systemctl status natter-soak.timer

# 输出：/var/log/natter/soak-YYYY-MM-DD.jsonl，每 5 分钟一行
sudo -u natter tail -f /var/log/natter/soak-$(date +%F).jsonl
```

- 依赖 `jq`（合并采样时间戳）；
- 服务重启/机器重启都不会丢采样：`OnBootSec=2min`、`OnUnitActiveSec=5min`；
- `Persistent=false`：停机期间不补采，缺口如实出现在审计里。

### 2.2 长驻进程（临时部署 / 无 systemd）

```bash
# 300s 一次，跑满 24h，写到指定文件
./scripts/soak-sample.sh -i 300 -d 86400 -o /var/log/natter/soak-$(date +%F).jsonl

# 或让脚本按天切文件
./scripts/soak-sample.sh -i 300 -d 86400 --out-dir /var/log/natter
```

### 2.3 验证采样器本身可用（60 秒）

```bash
./scripts/soak-sample.sh -i 15 -d 45 -o /tmp/soak-smoke.jsonl
python3 scripts/soak-report.py /tmp/soak-smoke.jsonl --interval 15 --max-gap 45
```

---

## 3. 审计（24h 后必须跑）

```bash
python3 scripts/soak-report.py /var/log/natter/soak-*.jsonl --interval 300
```

输出包含：

| 字段 | 含义 |
| --- | --- |
| `coveragePercent` | 实际样本 / 期望样本（按 interval 推算） |
| `gaps` | 相邻样本间隔 > 1.5×interval 的窗口，标记是否超过容差 |
| `restarts` | `startedAt` 变化次数（实例重启） |
| `upstreams.*.reconnects` | 累计重连次数（能正确处理重启后计数归零） |
| `upstreams.*.samplesWithDisconnectButNoReason` | **OBS-019 命中数**：有断连时间无断连原因 |
| `sendFailuresMax` | 发送失败峰值 |
| `result` | PASS / FAIL + 原因列表 |

### 验收口径（建议写进 P4.5 判定）

| 项 | 通过标准 |
| --- | --- |
| 采样覆盖 | ≥ 95%，且无单点缺口 > 900s |
| 状态 | 全程 `healthy`（Tier 1-only 部署允许 `degraded`，但必须在记录中说明原因） |
| 实例重启 | 0 次（计划内重启需在窗口外，或重开计时） |
| `sendFailures` | 全程 0 |
| Discord 断连 | 允许自动重连，但每次都要有归因（`lastError` 或日志行）；`silentDisconnects` 单独统计 |
| Tier 1 provider | `available=true` 全程 |

> `--interval` 必须与实际采样间隔一致，否则覆盖率会被算错。默认 300s，与 `natter-soak.timer` 一致。

---

## 4. 本轮对历史数据的审计结论（2026-11-21 复核）

历史两段采样都在 `artifacts/ops/tier1-run1/var/log/natter/`：

### 4.1 `soak-2026-11-17.jsonl`（首轮，20:38 起）

```text
samples=5  window=1.59h  coverage=25.0%
status: healthy 3 / degraded 2
gaps: 1（4938s，机器休眠）
discord: disconnected=2, reconnects=12, lastError="Server missed last heartbeat" / "Unable to connect to the remote server"
result: FAIL（覆盖不足 + 缺口 + 断连）
```

### 4.2 `soak-wake-2026-11-17.jsonl`（"重启 24h 计时"）

```text
samples=29  window=13.99h  coverage=17.3%（期望 168）
gaps: 39800s（11h03m，休眠）+ 2767s（46m）
restarts: 2 次（14:20、14:43，对应部署 DingTalk/Spark 的两次重启）
discord: reconnects=3, sendFailures=0,
         silentDisconnects=21  ← OBS-019 直接证据
         lastError 曾出现 "interaction_defer_failed: Cannot defer an interaction after 3 seconds!"
dingtalk: lastError="The remote party closed the WebSocket connection without completing the close handshake."
result: FAIL（覆盖 17.3% + 两个超容差缺口 + 2 次重启）
```

**结论：24h 被动 soak 至今没有完整达成。** 原因清楚且可修复：

1. 机器休眠直接制造 11 小时缺口（不是服务问题）；
2. 部署动作（staging DingTalk/Spark）在计时窗口内重启了实例；
3. 采样器是前台后台进程，随会话/睡眠一起停 → 应由 systemd timer 接管（本轮已提供）。

因此本轮**不宣称 24h 通过**，改为交付可执行的采样/审计链路 + 明确的验收口径。补齐 24h 的做法见 §5。

---

## 5. 补齐 24h 的操作单（在目标机器上执行）

```bash
# 0) 前置：服务以 systemd 运行，jq 已安装
command -v jq || sudo pacman -S --needed jq

# 1) 记录起点
date -Is | tee /tmp/soak-start.txt
systemctl is-active natter

# 2) 确认采样已排程
systemctl list-timers natter-soak.timer --no-pager

# 3) 期间不要部署、不要重启、不要让机器睡眠
sudo systemctl mask sleep.target suspend.target hibernate.target hybrid-sleep.target

# 4) 24h 后审计
python3 /opt/natter/scripts/soak-report.py /var/log/natter/soak-*.jsonl --interval 300 | tee /tmp/soak-report.txt
```

> 若机器必须睡眠，先在窗口内把 sleep 目标 mask 掉，或直接接受缺口并在记录里声明（不要伪造覆盖率）。

---

## 6. 与告警配合（OBS-019 已修复，2026-11-21 适配）

```bash
# 每 5 分钟：状态/断连/发送失败/interaction 超时
*/5 * * * * /opt/natter/scripts/ops-health-alert.sh --state /var/lib/natter/log/alert-state.json

# 改规则后先自检（无需 host / 网络 / secret）
./scripts/ops-health-alert.sh --self-test
```

断连原因的读取顺序（P4.5 follow-up 之后）：

| 采样载荷 | 告警 id | 含义 |
| --- | --- | --- |
| 有 `lastDisconnectError` 且有值 | `disconnect` | **断连有原因**：消息里带 `code=` + `error=` |
| 有该字段但为 `null` | `disconnect-without-reason` | 新版本 host 但该次断连没给消息（少见），仍需翻日志 |
| **没有**该字段（旧版本 / 旧样本） | `disconnect-without-reason-legacy` | 旧载荷，原因只能靠日志 |

要点：

- **不再用 `lastError` 判断断连归因**。`lastError` 仍是"当前错误"语义，`OnReady` 会清空；它只用于 `interaction-ack-timeout` / `upstream-error` 这类实时错误告警；
- 每次 `reconnectCount` 增量只出 **一条**告警（同一快照重复轮询不会再报），避免 OBS-019 时代的重复提示；
- `reconnectCount` 变小 → `reconnect-counter-reset`（host 重启）。soak 里出现它，说明采样窗口内有重启，需在审计结论里扣除。

只有落到"无原因"两种分支时才需要翻日志：

```bash
journalctl -u natter --since "<lastDisconnectAt 前 2 分钟>" --until "<lastDisconnectAt 后 1 分钟>" --no-pager
# 或 Docker
docker compose -f deploy/docker/compose.yaml logs --since 10m natter
```

### 6.1 仍未适配的地方（需要扩大写作用域）

`scripts/soak-report.py` 的 `samplesWithDisconnectButNoReason` **仍在用旧口径**（`lastDisconnectAt` 有值且 `lastError == null`）。对修复后的 host，这个口径会把"有 `lastDisconnectError` 的正常断连"也算成"无原因"，从而**高估**静默断连数。

判定方法（在适配前临时使用）：

```bash
# 直接看新字段有没有值，而不是看 lastError
jq -r 'select(.upstreams) | .sampledAt as $t
       | .upstreams[] | select((.source|startswith("discord")) and .lastDisconnectAt != null)
       | [$t, (.lastDisconnectCode // "null"), (.lastDisconnectError // "null")] | @tsv' soak-*.jsonl
```

适配 soak-report.py 属后续任务（本任务的写作用域只有 `scripts/ops-health-alert.sh` 与 `docs/operations/`）。
