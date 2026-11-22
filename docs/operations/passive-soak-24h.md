# 24h 被动 soak：采样、审计与验收口径

> **目的**：部署后无人值守跑满 24 小时有效运行，并留下可复核的证据
> **两种口径**：`wallclock`（连续墙钟，适合稳定供电）与 `cumulative`（累计有效运行，适合每晚断电的部署机）——见 [§3.1](#31-验收口径连续墙钟-vs-累计有效运行)
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

### 2.3 采样行里新增的两个 best-effort 字段

`soak-sample.sh` 现在每行额外尝试采集两项，**采不到就省略该字段，从不影响采样本身与退出码**：

| 字段 | 来源 | 用途 |
| --- | --- | --- |
| `bootId` | `/proc/sys/kernel/random/boot_id` | cumulative 模式用它区分「断电重启」与「服务重启」 |
| `rssBytes` | `systemctl show $NATTER_UNIT -p MemoryCurrent --value`，回落 `/proc/<MainPID>/status` 的 `VmRSS*1024` | cumulative 模式的内存漂移判定 |

```bash
# 单位名可通过环境变量覆盖（默认 natter.service）
NATTER_UNIT=natter.service ./scripts/soak-sample.sh --once --out-dir /var/log/natter

# 一行样例（成功时）
# {"sampledAt":"...","source":"loopback","status":"healthy",...,
#  "bootId":"e54cc627-...","rssBytes":102830080}
#
# 不可达时仍然写一行，只带 bootId：
# {"sampledAt":"...","source":"loopback","error":"unreachable","httpCode":"000","bootId":"e54cc627-..."}
```

> `rssBytes` 的语义是「采集时刻进程 RSS」。它是判趋势用的，不是精确计量；缺失只会让漂移分项变成 `insufficient-data`。

### 2.4 验证采样器本身可用（60 秒）

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

## 3.1 验收口径：连续墙钟 vs 累计有效运行

### 3.1.1 环境约束与决策

部署机（本机）**每晚断电**，能维持的运行窗口只有白天清醒期。原口径要求「连续 24h 墙钟、覆盖率 ≥95%、零缺口」，在这个环境里**永远不可能通过**——不是服务不稳，是电源断了。

因此把验收口径改为 **累计有效运行 24h**，可跨天、跨重启、跨断电累加。这是**有记录的口径调整**（task-24），并同时新增三项防放水的护栏（单段最短时长、缺口归因、RSS 漂移），不是把标准降低。

### 3.1.2 新旧标准并列

| 维度 | `wallclock`（旧口径，保留） | `cumulative`（新口径，现行验收） |
| --- | --- | --- |
| 时长 | 连续 24h 墙钟窗口 | **Σ 有效运行时长 ≥ 24h**，可跨天/重启/断电 |
| 采样覆盖 | ≥ 95%，无单点缺口 > 900s | 不再要求覆盖率；每段内累积 `min(delta, 1.5×interval)` |
| 缺口 | 任何缺口即 FAIL | **允许缺口，但必须归因**：`host-down` / `host-restart` / `suspend-or-stall`；`unknown` 才判负 |
| 单段 | —（只有一个窗口） | **最长单段 ≥ 6h**（防止用碎片凑数） |
| 内存 | 人工看 RSS 趋势 | **最差段 RSS 漂移 ≤ 20%**（`insufficient-data` 单列，不判负） |
| 重启 | 0 次 | 允许；每次重启要有归因（`host-restart`/`host-down`），重启落在两次采样之间会提示核对 coredump |
| 崩溃 | `startedAt` 变化即判负 | `startedAt` 变化视为重启；**core dump 不在样本数据里**，需 `coredumpctl list natter` 人工核对 |
| 发送失败 | `sendFailures = 0` | 同左 |
| 断连归因 | 新载荷无原因即 FAIL | 同左（沿用 OBS-019 逻辑；legacy 静默只统计） |
| 适用环境 | 稳定供电、可连续跑 | **每晚断电 / 频繁重启 / 打断是常态** |

### 3.1.3 为什么断电不影响要抓的风险

断电打断的是**墙钟连续性**，而这些风险都发生在**清醒运行期间**、并且靠累计样本就能暴露：

| 要抓的风险 | 断电是否掩盖它 | 依据 |
| --- | --- | --- |
| 内存泄漏 | 不会 | 漂移按**每段首末** `rssBytes` 算，取最差段；短窗口也能看到斜率 |
| 重连风暴 | 不会 | `reconnectCount` 增量按事件统计，跨重启也不会漏 |
| `sendFailures` | 不会 | 每行样本都带，累计统计 |
| 进程崩溃 | 部分不会 | 崩溃导致 `startedAt` 变化 → 变成段断点并需归因；core dump 仍需人工核对 |
| 重复投递 / 数据损坏 | 不会 | 需要 DB integrity + 消息计数，与采样窗口连续与否无关 |
| 长时间低速泄漏（如每 8h +5%） | **会削弱** | 正是新增 RSS 漂移分项的原因：单段太短时容易看不见，所以要求单段 ≥ 6h |

所以护栏是成对的：**累计时长**负责「够久」，**单段最短 + RSS 漂移**负责「够实」。

### 3.1.4 标准命令

```bash
# 现行验收（多天多个文件一起算）
python3 scripts/soak-report.py /var/log/natter/soak-*.jsonl \
  --mode cumulative --target-hours 24 --min-segment-hours 6 \
  --interval 300
```

可选参数（默认值即验收线）：

| 参数 | 默认 | 含义 |
| --- | --- | --- |
| `--target-hours` | 24 | 有效运行目标 |
| `--min-segment-hours` | 6 | 最长单段下限 |
| `--max-unexplained-percent` | 2 | 未归因缺口时长占比上限 |
| `--max-rss-drift-percent` | 20 | 最差段 RSS 增长上限 |

### 3.1.5 示例输出（真实历史数据，缺 `bootId` 的老样本）

```text
$ python3 scripts/soak-report.py artifacts/ops/tier1-run1/var/log/natter/soak-wake-2026-11-17.jsonl --interval 300 --mode cumulative
mode            : cumulative (effective runtime; gaps attributed, not fatal by themselves)
effective       : 2.25h / target 24.0h   [SHORT]  (8100s)
window          : 29 samples, 13.99h wall clock, interval 300s, tolerance 450s
segments        : 3   longest 1.92h (required 6.0h)   restarts 2
gaps            : 2   host-down=0 host-restart=0 suspend-or-stall=2 unknown=0
   2026-11-17T14:58:29Z -> 2026-11-18T02:01:50Z  39800s  suspend-or-stall  (bootId missing; startedAt unchanged, ...)
   2026-11-18T03:06:50Z -> 2026-11-18T03:52:58Z  2767s   suspend-or-stall  (bootId missing; startedAt unchanged, ...)
unexplained     : 0.0% (allowed 2.0%), 0s of excess gap time
worst RSS drift : insufficient-data (allowed 20%)
disconnects     : attributed=0 silent-new=0 silent-legacy=3 rate=0.0%
sendFailures    : max 0
host crashes    : 0  (core dumps are outside the sample data; verify with `coredumpctl list natter`)
result          : FAIL
  - effective runtime 2.25h < target 24.0h
  - longest continuous segment 1.92h < required minimum 6.0h
  note: 3 legacy disconnect(s) have no reason field (pre-OBS-019 payload) — counted for information, not as a failure
  note: 2 restart(s) happened between samples (no gap to attribute); a host crash would look like this, so check `coredumpctl list natter` for core dumps
```

读法：

- 这份老数据没有 `bootId`/`rssBytes` → 缺口退回 `startedAt` 判定（`suspend-or-stall`，并标注 `bootId missing`），RSS 分项标 `insufficient-data`，**不抛异常**；
- 时长自然不够（2.25h），所以 FAIL——新口径不会把老数据"洗白"；
- `--json` 输出含 `mode / effectiveSeconds / effectiveHours / segments[] / gapAttribution / unexplainedPercent / worstRssDriftPercent / verdictReasons[]`，可直接进验收报告。

### 3.1.6 缺口归因规则（只看样本自身，不依赖 journal）

| 两端的证据 | 归因 | 含义 |
| --- | --- | --- |
| `bootId` 不同 | `host-down` | 机器重启/断电 |
| `bootId` 相同、`startedAt` 不同 | `host-restart` | 服务重启（部署、崩溃拉起） |
| `bootId`、`startedAt` 都相同，`delta > 1.5×interval` | `suspend-or-stall` | 机器休眠或采样器卡住 |
| 缺 `bootId`（老样本） | 退回 `startedAt` 判定，标 `bootIdMissing` | 无法区分断电与服务重启 |
| 两端都缺 `bootId` 且无 `startedAt` | `unknown` | 计入未解释占比 |

### 3.1.7 何时仍用 wallclock

- 目标机有稳定供电（服务器/机房/带 UPS），能连续跑满 24h；
- 想验证「一次不间断的长跑」而不是「累计够久」；
- CI 与日常巡检（`deploy-verify.yml` 的自检）继续用默认 `--mode wallclock`，行为与退出码不变。

```bash
# 稳定供电环境的严格口径
python3 scripts/soak-report.py /var/log/natter/soak-*.jsonl --interval 300   # 默认 wallclock
```

---

### 3.2 验收口径 · `--mode wallclock`（连续墙钟；适用于稳定供电环境）

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

按累计口径复核同一份数据（`--mode cumulative`）：

```text
effective 2.25h / target 24h | segments 3, longest 1.92h | gaps 2 → suspend-or-stall ×2
worst RSS drift: insufficient-data | verdict: FAIL (effective runtime + longest segment)
```

即：**在两种口径下这份数据都不通过**，且新口径能自动指出"有效 2.25h、最长段 1.92h、两个缺口都是休眠/卡顿（缺 bootId 无法进一步区分）"。老样本缺 `bootId`/`rssBytes`，工具按设计降级而不是报错。

**结论：24h 被动 soak 至今没有完整达成。** 原因清楚且可修复：

1. 机器休眠直接制造 11 小时缺口（不是服务问题）；
2. 部署动作（staging DingTalk/Spark）在计时窗口内重启了实例；
3. 采样器是前台后台进程，随会话/睡眠一起停 → 应由 systemd timer 接管（本轮已提供）。

因此本轮**不宣称 24h 通过**，改为交付可执行的采样/审计链路 + 明确的验收口径。补齐 24h 的做法见 §5。

---

## 5. 补齐 24h 的操作单（连续墙钟做法；稳定供电时使用）

> 如果部署机每晚断电（本机就是），先看 [§3.1](#31-验收口径连续墙钟-vs-累计有效运行)：那里是**现行验收口径**（累计有效运行），不需要 mask 睡眠也不需要连续跑满。下面的操作单只适用于能连续供电的机器。

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

### 6.1 断连归因统计口径（已适配 lastDisconnectError/Code）

`soak-report.py` 现在按**载荷版本**判定静默断连，不再只看 `lastError`：

| 载荷 | 判定 | 计入字段 |
| --- | --- | --- |
| 有 `lastDisconnectError` 且有值 | 断连**有原因** | `disconnectsWithReason.newPayload` |
| 有该字段但为空 | 断连**真静默**（修复后仍无原因 → 判定 FAIL） | `silentDisconnects.newPayload` |
| 无该字段（旧 host / 旧抓取） | **legacy**：`lastError` 有值=有原因，否则静默 | `disconnectsWithReason.legacyPayload` / `silentDisconnects.legacyPayload` |

两个关键修正：

1. **按断连事件去重**：`lastDisconnectAt` 是粘性字段，之后每个样本都会重复同一个时间戳。按样本计数会把一次断连放大成 N 次；现在按 `lastDisconnectAt` 归并成事件，新增 `distinctDisconnects`。
2. **旧口径会把修复后的 host 误判**：只看 `lastError == null` 会把"有 `lastDisconnectError` 的正常重连"算成静默；现在新载荷优先，legacy 仅在字段缺失时启用。

真实历史数据的前后对比（同一文件）：

```text
# 旧口径（只看 lastError）: samplesWithDisconnectButNoReason = 21（样本数，重复放大）
# 新口径: legacy silent = 3 个事件、attributed = 0；payload versions: new=0 legacy=29
python3 scripts/soak-report.py artifacts/ops/tier1-run1/var/log/natter/soak-wake-2026-11-17.jsonl --interval 300
```

自检（无需真实数据）：

```bash
python3 scripts/soak-report.py --self-test
# 5 个场景 / 13 条断言：新载荷有原因、新载荷静默→FAIL、旧载荷静默、旧载荷 lastError 归因、无断连
```

JSON 里新增字段：

```json
{"distinctDisconnects": 3,
 "silentDisconnects": {"newPayload": 0, "legacyPayload": 3},
 "disconnectsWithReason": {"newPayload": 0, "legacyPayload": 0},
 "payloadVersions": {"new": 0, "legacy": 29},
 "disconnectReasons": [{"code": "ws_closed", "error": "gateway closed (1006)", "samples": 1}]}
```
