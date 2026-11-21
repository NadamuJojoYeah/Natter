# 2026-11-21 · P4.5 部署与长跑阻塞项收口（运维侧）

> **前置**：[P4.5 收尾复测（运维侧）](2026-11-17-p4-5-final-retest-run.md)、[P4.5 联合复测](2026-11-17-p4-5-retest-run.md)
> **任务**：task-4「收口 P4.5 部署与长跑阻塞项」
> **基线**：`af74820`（工作区干净，仅 `.gitignore` 本地改动）
> **状态**：Executed · **Conditional Pass**（Docker 通过；systemd / 真实 secret 轮换 / 24h 完整采样 / 真人回归仍为 BLOCKED，逐项给证据）

---

## 1. 概要

| 项 | 值 |
| --- | --- |
| 时间 | 2026-11-21 16:06–16:35（`Asia/Shanghai`） |
| 环境 | Arch Linux；dotnet 10.0.114；docker 29.9.3（**无 compose 插件、无 buildx**）；`sudo` 被 `no-new-privileges` 拒绝；PID 1 = `bwrap`；Discord 出网经本机 hosts 劫持 + 本地 CA |
| 被测对象 | `deploy/docker/*`（镜像/编排）、`deploy/systemd/*`、`scripts/*`（staging、soak、告警、出口探针、轮换） |
| 证据目录 | `artifacts/ops/p4.5/closeout/2026-11-21/`（gitignored） |
| 结论 | 镜像**首次真正构建成功**并跑通 Discord 链路（4 插件 / connected=true / healthy）；systemd 与真实 secret 轮换为环境 BLOCKED；24h 采样**未达成**，本轮交付采样+审计+告警工具与验收口径 |

### 1.1 上一轮遗留 → 本轮状态

| 编号 | 上轮状态 | 本轮 |
| --- | --- | --- |
| Docker 构建/运行 | BLOCKED（无 compose、构建未完成） | **通过**：镜像 399 MB，`13/13` 检查全绿 |
| Dockerfile 缺 Tier1 插件 | 未修 | **已修**：`scripts/stage-tier1-plugins.sh` + 构建期 manifest 校验 |
| systemd | BLOCKED（root/PID1） | **仍 BLOCKED**，但已给出可执行安装器 + 只读取证器 + BLOCKED 证据 |
| 24h soak | 进行中（14h 后被打断） | **未达成**（13.99h / 17.3% 覆盖）；工具与口径已交付 |
| secret 轮换 | BLOCKED（平台权限） | **流程+脚本就绪并实测**；真实平台轮换仍 BLOCKED |
| OBS-019 断连归因 | 未闭环 | **运维侧可观测性补齐**（告警规则 + 证据）；代码修复建议交回工程师 |
| Discord 3s 交互 | 已定位为出口 RTT | **文档化 + 探针工具**；新增容器场景的三层差异实测 |

---

## 2. Docker：镜像与可运行验证

### 2.1 改了什么

`deploy/docker/Dockerfile`（多阶段）

1. build 阶段只拷 `global.json` / `Directory.*.props` / `build/` / `src/` / `samples/` / staging 脚本，提高缓存命中；
2. `dotnet publish` 出 CLI 后，调用 `scripts/stage-tier1-plugins.sh -o /out/plugins` 把 **Discord + DeepSeek** 装进镜像（`--build-arg TIER2=true` 再加 DingTalk + Spark）；
3. 构建期 fail-fast：`natter.dll plugins inspect` 逐个校验 manifest，插件装不进去镜像就构建失败；
4. runtime 阶段 UID/GID **10001:10001**，`/state` 在镜像里就 chown 给 10001（Docker 创建命名卷时会继承属主，因此不需要 root 预处理）；
5. `HEALTHCHECK` 探 loopback `5099` 的 `/health`，`CMD` 带 `--health-port 5099`。

`deploy/docker/compose.yaml`

- 加 `args.TIER2`、`cap_drop: ALL`、`NO_PROXY`、`expose: 5099`（不映射到宿主）、`healthcheck`、日志轮转（`10m × 5`）；
- 注释里写清：healthcheck 是 **liveness**；`degraded`（Tier 2 缺失）不应让容器 flapping。

`deploy/docker/verify-image.sh`（新增）

在没有 compose 插件的主机上，用等价 `docker run` 复现 compose 的全部设置并采集证据；支持 `--network`、`--mount-hosts`、`--ca-bundle` 以适配特殊网络环境。

### 2.2 构建结果

```text
Successfully tagged natter:tier1-20261121      # 399 MB
staged: /out/plugins/discord (33 files in lib/)
staged: /out/plugins/deepseek (24 files in lib/)
plugin probe: /out/plugins/discord   valid: True
plugin probe: /out/plugins/deepseek  valid: True
```

镜像内清单（`/app/plugins/staging-manifest.json`）：

```json
{"pluginRoot":"/out/plugins","configuration":"Release","tier2":false,
 "plugins":[{"name":"discord","id":"natter.transport.discord","libFiles":33},
            {"name":"deepseek","id":"natter.feature.ai.deepseek","libFiles":24}]}
```

> 三个构造期踩到的坑，都已修在代码里（见 §2.4）。

### 2.3 运行验证（13 项全绿）

```bash
deploy/docker/verify-image.sh --image natter:tier1-20261121 \
  --env-file <tier1 脱敏 env> --config-dir <容器 config> \
  --ca-bundle <合并 CA> --network host --mount-hosts \
  --evidence artifacts/ops/p4.5/closeout/2026-11-21/docker-verify/script-run
```

```text
checks:
  - image exists: natter:tier1-20261121
  - image stages plugin: discord
  - image stages plugin: deepseek
  - discord manifest valid in image
  - image user is 10001:10001
  - container starts
  - container healthcheck healthy
  - root filesystem read-only
  - container runs as 10001:10001
  - container /health reachable
  - container status healthy
  - at least one upstream connected
  - no secret value in evidence
failures: 0
```

容器内 `/health`（真实 Discord 会话）：

```json
{"status":"healthy","uptimeSeconds":51,
 "upstreams":[{"source":"discord:discord:1571154023493410836",
               "connected":true,"reconnectCount":0,"lastError":null,"sendFailures":0}],
 "providers":[{"id":"natter.feature.ai.deepseek#deepseek","available":true,"supportTier":1},
              {"id":"natter.feature.ai.spark#spark","available":false,"supportTier":2}],
 "plugins":{"activated":4,"failed":0,"failures":[]}}
```

要点：

- **Tier 1-only 仍是 `healthy`**（OBS-005 的语义修复在容器形态同样成立），spark 不可用只作为 Tier 2 降级；
- `docker inspect` 显示 `user=10001:10001 readOnly=true health=healthy restarts=0`；
- 容器启动日志：`serve: profiles=2 providers=1 upstreams=1 plugins=4 failed=0` / `upstream discord:... connected=True`。

### 2.4 构建/验证期间发现并修复的 4 个真问题

| # | 问题 | 影响 | 处置 |
| --- | --- | --- | --- |
| 1 | staging 脚本用 `python3` 生成 manifest，而 `sdk:10.0` 镜像**没有 python3** | 镜像内 `staging-manifest.json` 所有字段为空 | 改为纯 bash JSON 转义，去掉 python 依赖 |
| 2 | 基础镜像自带 `ENV APP_UID=1654`，覆盖同名 `ARG APP_UID=10001` | 容器实际以 `app(1654)` 运行，**不是**承诺的 10001 | 形参改名 `NATTER_UID/NATTER_GID`，并在 Dockerfile 注释里说明原因 |
| 3 | `docker inspect` 会把 `.Config.Env` 全部打印出来 | 证据文件（`container-inspect.json`）**明文包含 bot token / API key** | 保存前 `jq 'del(.Config.Env)'`，并保留 secret 泄漏扫描作为最后防线 |
| 4 | 无 compose 插件时 `docker build --progress=plain` 直接失败 | 首次构建命令 exit 125 | 文档/脚本改用 legacy builder 兼容参数 |
| 5 | `--tmpfs /state` / bind mount 的目录属主是 root | host 启动即崩：`UnauthorizedAccessException: /state/.lock`，`status=exited` | 命名卷（继承镜像 10001 属主）或用 `--tmpfs /state:uid=10001,gid=10001,mode=0700`；已实测两种写法 |

> 第 3 条是运维侧最容易忽略的泄漏点：任何"留证据"的脚本都不要原样保存 `docker inspect`。

### 2.5 compose 可运行性

```text
$ docker compose version
docker: unknown command: docker compose        # 本机无 compose 插件
```

因此本轮**没有**执行 `docker compose up -d --build`，改为：

1. 用 legacy builder `docker build` 真实构建镜像（成功）；
2. 用 `verify-image.sh` 以 compose 等价参数 `docker run`（成功，13/13）；
3. compose 文件本身由 CI 的 `docker build` + 检查项覆盖，另在 `.github/workflows/deploy-verify.yml` 里保留 compose 校验步骤（runner 自带 compose，会走 `docker compose config -q`）。

**判定**：镜像与编排语义已验证；`compose up` 这一条命令本身**未在本机执行**（环境缺插件），属环境限制而非配置缺陷。

---

## 3. systemd：可执行安装 + 只读取证 + 诚实的 BLOCKED

### 3.1 交付

| 文件 | 作用 |
| --- | --- |
| `deploy/systemd/natter.service` | 加固后的 unit：`StateDirectory`/`LogsDirectory`、`ProtectSystem=strict`、`ProtectHome`、`ProtectProc=invisible`、`RestrictAddressFamilies`、`SystemCallFilter=@system-service`、`CapabilityBoundingSet=`、`TimeoutStopSec=20`、`StartLimitBurst=5`；`EnvironmentFile=-/etc/natter/proxy.env`（可选） |
| `deploy/systemd/natter-soak.service` | 一次性采样单元（`soak-sample.sh --once --out-dir /var/log/natter`） |
| `deploy/systemd/natter-soak.timer` | 每 5 分钟采样，`OnBootSec=2min`，`Persistent=false`（停机不补采，缺口如实保留） |
| `deploy/systemd/install.sh` | root 安装器，**幂等**、不写 secret 值（只生成 0600 空模板）、`--dry-run` 可无 root 演练 |
| `deploy/systemd/verify.sh` | 只读取证（unit 状态、权限、loopback `/health`、`health --offline`、DB integrity），**无法 systemd 时输出 BLOCKED 而不是 FAIL/PASS** |

`--dry-run` 实跑输出（节选）：

```text
1/7 system user            [dry-run] useradd --system ... natter
2/7 release layout         [dry-run] chmod -R go-w <release>
3/7 config layout          [dry-run] chmod 640 /etc/natter/config/*.json
4/7 secret/proxy env       [dry-run] create /etc/natter/live.env template (0600)
5/7 state and log dirs     [dry-run] chmod 700 /var/lib/natter /var/log/natter
6/7 unit files & sampler   [dry-run] install -m 0644 ... natter-soak.timer
7/7 start                  skipped (pass --start to start now)
```

### 3.2 BLOCKED 证据（不伪造通过）

```text
$ deploy/systemd/verify.sh --evidence artifacts/.../systemd --json
{"result":"BLOCKED",
 "reason":"unit natter.service is not installed and installing it needs root (uid=1000, pid1=bwrap)",
 "pid1":"bwrap","unitInstalled":0}
exit=4
```

`artifacts/ops/p4.5/closeout/2026-11-21/systemd/BLOCKED.txt` 里写明：没有安装、没有启动、没有测量，本机不得据此签 systemd 通过。

**判定**：systemd **BLOCKED（环境）**。在目标机执行：

```bash
sudo deploy/systemd/install.sh --release /opt/natter --config-src config --start
sudo deploy/systemd/verify.sh --evidence artifacts/ops/systemd-$(date +%F)
sudo systemctl list-timers natter-soak.timer
```

---

## 4. 24h 被动 soak：工具交付 + 历史数据审计

### 4.1 交付

| 文件 | 作用 |
| --- | --- |
| `scripts/soak-sample.sh` | 只读 loopback `/health`；`--once`（timer 模式）、`--out-dir`（按天切文件）、`--offline-fallback`、断连/不可达都记成结构化行 |
| `scripts/soak-report.py` | 审计：覆盖率、缺口、实例重启、重连次数（能处理重启后计数归零）、`sendFailures` 峰值、**OBS-019 命中数**、PASS/FAIL 判定 |
| `deploy/systemd/natter-soak.{service,timer}` | 生产化排程，替代"后台长驻进程" |
| [24h 被动 soak 文档](passive-soak-24h.md) | 采样/审计/验收口径/补齐操作单 |

自检（真机 + 真实 host）：

```text
samples=4  interval=30s  coverage=100.0%
status counts: {'healthy': 4}
upstream discord: reconnects=0 disconnected=0 sendFailuresMax=0 silentDisconnects=0
result: PASS
```

### 4.2 历史数据审计（本轮复核，结论：**24h 未达成**）

| 文件 | 样本 | 窗口 | 覆盖 | 缺口 | 重启 | 关键观测 |
| --- | --- | --- | --- | --- | --- | --- |
| `soak-2026-11-17.jsonl` | 5 | 1.59 h | 25.0% | 1（4938 s，休眠） | 0 | `degraded`×2、discord `disconnected=2`、`lastError="Server missed last heartbeat"` |
| `soak-wake-2026-11-17.jsonl` | 29 | **13.99 h** | **17.3%**（期望 168） | 2（39800 s 休眠、2767 s） | **2**（14:20、14:43 两次部署重启） | reconnects=3、`sendFailures=0`、**silentDisconnects=21**、`lastError` 曾出现 `interaction_defer_failed: Cannot defer an interaction after 3 seconds!` |

结论与归因：

1. 缺口主因是**机器休眠（11 小时）**与**部署动作在窗口内重启实例**，不是服务异常；
2. 采样器是前台后台进程，随会话/睡眠一起停 → 本轮改为 systemd timer；
3. 因此**不宣称 24h 通过**；补齐 24h 的操作单见 [24h 被动 soak §5](passive-soak-24h.md)。

---

## 5. OBS-019（断连原因不可见）：运维侧证据 + 告警（不改 src）

### 5.1 证据

历史 soak 里 29 个样本中有 **21 个**是「`lastDisconnectAt` 有值、`lastError=null`」——即断连发生过、原因被 `OnReady` 清空。同一文件里也出现过 `interaction_defer_failed`（说明**有些**断连/错误是能看到的），两者并存正好说明：**能看到的是"没连上"的原因，看不到的是"连上了又断"的原因**。

### 5.2 本轮交付（纯运维侧）

`scripts/ops-health-alert.sh`：

- 读 loopback `/health` 或快照文件，与上次基线比较，输出人类可读或 `--json`；
- 规则：`status-not-healthy`、`plugin-failed`、`upstream-disconnected`、`reconnect`（增量）、**`disconnect-without-reason`（OBS-019）**、`interaction-ack-timeout`、`send-failures`、`tier1-provider-unavailable`、`task-failed`；
- 首次运行视为基线（不误报），状态存 0600；
- 退出码：`0` 无告警 / `1` 有告警 / `3` 无快照。

实测：

```text
# 基线（首跑，静默）
checked at ... status=healthy alerts=0                       exit=0

# 合成一次"断连但无原因"
[warning] reconnect: upstream discord:discord:1 reconnected 1 time(s) since the last check;
          lastDisconnectAt=...; recorded reason=null
[warning] disconnect-without-reason: OBS-019: discord:discord:1 dropped and reconnected but no
          reason is exposed (lastDisconnectAt=..., lastError=null, reconnectCount=3).
          Correlate with journalctl/docker logs around that timestamp.
                                                              exit=1
```

### 5.3 交回工程师的代码建议（本任务不碰 src）

1. health 增加持久字段 `lastDisconnectError` / `lastDisconnectCode`，**只在下一次断连时覆盖**，不要在 `OnReady` 清空；
2. 或把 `DiscordChatUpstream.OnReady` 里的 `LastError = null;` 改为只在"从未失败"时清空，并把 transport 的 `_lastGatewayError` 提升进 health；
3. 顺带把 `lastDisconnectAt` 与 `lastDisconnectError` 配对输出，便于告警直接引用（现在只能靠时间戳去翻日志）。

> 运维侧不阻断：`ops-health-alert.sh` 已经能把"断过但没原因"变成显式告警，并给出对应时间窗的日志检索命令。

---

## 6. Discord 3s 交互：低延迟出口说明

完整内容见 [Discord 3 秒交互预算与出网出口选择](discord-3s-interaction-egress.md)。要点：

- 3 s 是硬预算，defer 本身是 REST POST，所以判定看 **RTT**：p95 < 1000 ms 舒适、1000–2000 ms 勉强、> 2000 ms 必破；
- 本轮直连实测：`discord.com` 245–303 ms、`gateway.discord.gg` 460–931 ms，6/6 成功 → **PASS**；
- 历史代理实测：成功 5.1–5.4 s、失败 8 s 超时 → 必然 `interaction_defer_failed`；
- 新增容器场景实测（本机 hosts 被本地加速器接管）：bridge 默认失败 → `DOTNET_SYSTEM_NET_DISABLEIPV6=1` 仍失败（`Connection refused`）→ `--network host` + 挂 `/etc/hosts` + 合并 CA 后 **connected=true**；
- 工具：`scripts/discord-egress-probe.sh`（无需 token，输出 p50/p95/成功率与 verdict）。

---

## 7. Secret 轮换：流程 + 脚本 + 实测

交付：`scripts/rotate-secret.sh` + [轮换流程](secret-rotation.md)。

脚本约束（都已实测）：只改目标 key、其它行逐字节保留；同目录临时文件 + `rename()` 原子替换；备份 0600；写后回读指纹校验、不一致自动回滚；**不打印明文**，只给 `sha256` 前 8 位；拒绝空值/含引号空格 `$` 反斜杠的值；拒绝从 TTY 读（避免进 history）。

实测（临时 env，不涉真实凭据）：

```text
--check           DISCORD_BOT_TOKEN len=13 sha256=941ebbdd
rotate            old fingerprint: 941ebbdd -> new fingerprint: 41275c0a
written: .../live.env (600)   backup: live.env.bak.20261121-161500 (0600)
其它行（注释、DEEPSEEK_API_KEY、NO_PROXY）保持不变
```

**真实轮换 BLOCKED**：需要 Discord Developer Portal / DeepSeek 控制台权限，本机无该权限，未执行，也未伪造"已轮换"。

---

## 8. 变更文件清单

| 文件 | 类型 | 说明 |
| --- | --- | --- |
| `deploy/docker/Dockerfile` | 改 | 插件 staging + 构建期校验 + UID 修正 + healthcheck |
| `deploy/docker/compose.yaml` | 改 | 加固、healthcheck、日志轮转、无端口映射 |
| `deploy/docker/verify-image.sh` | 新增 | 无 compose 环境的镜像运行验证 + 证据 |
| `deploy/systemd/natter.service` | 改 | 安全加固、`EnvironmentFile`、优雅退出 |
| `deploy/systemd/natter-soak.service` / `.timer` | 新增 | 被动采样排程 |
| `deploy/systemd/install.sh` | 新增 | root 安装器（幂等、dry-run、不写 secret） |
| `deploy/systemd/verify.sh` | 新增 | 只读取证 + BLOCKED 判定 |
| `deploy/README.md` | 改 | 部署入口重写 |
| `scripts/stage-tier1-plugins.sh` | 新增 | Tier1/Tier2 staging（无 python 依赖、保留 config.json、manifest） |
| `scripts/soak-sample.sh` | 新增 | 被动采样（loop / once / out-dir） |
| `scripts/soak-report.py` | 新增 | soak 审计与 PASS/FAIL |
| `scripts/ops-health-alert.sh` | 新增 | 状态/断连/OBS-019/发送失败告警 |
| `scripts/discord-egress-probe.sh` | 新增 | 出口 RTT 探针与 3s 预算判定 |
| `scripts/rotate-secret.sh` | 新增 | secret 原子轮换 |
| `.github/workflows/deploy-verify.yml` | 新增 | 脚本 lint + 镜像构建 + 插件/manifest/uid/healthcheck 校验 + soak 自检 |
| `docs/operations/2026-11-21-p4-5-ops-closeout-run.md` | 新增 | 本文 |
| `docs/operations/discord-3s-interaction-egress.md` | 新增 | 3s 预算与出口选择 |
| `docs/operations/passive-soak-24h.md` | 新增 | 采样/审计/验收口径 |
| `docs/operations/secret-rotation.md` | 新增 | 轮换 runbook |

**未改动**：`src/**`（OBS-019 代码建议已交回）、`config/**`（生产配置）、`plugins/**`（WebConsole）、`scripts/webconsole-e2e.sh`（与 task-2 协调后不碰）。

---

## 9. 剩余风险与未完成项

| 项 | 状态 | 说明 / 下一步 |
| --- | --- | --- |
| systemd 实机安装 | **BLOCKED（环境）** | 需 root + 真 systemd PID 1；`install.sh` / `verify.sh` 已就绪 |
| `docker compose up` 原命令 | **未执行（环境）** | 本机无 compose 插件；镜像与等价参数已验证，CI 覆盖 compose 校验 |
| 24h 完整采样 | **未达成** | 历史最长 13.99 h / 覆盖 17.3%；timer 已交付，需生产机跑满 24h 并用 `soak-report.py` 判定 |
| 真实 secret 轮换 | **BLOCKED（平台权限）** | 流程与脚本已实测；控制台操作需账号权限 |
| OBS-019 代码修复 | **交回工程师** | 需新增 `lastDisconnectError` 持久字段；运维侧告警已可先兜住 |
| 容器内 Discord 出网 | **环境相关** | 本机依赖 hosts 劫持 + 私有 CA；生产需容器可直达 Discord 或显式代理，否则 `ws_connect` 超时 |
| 真人回归（P4） | **待人工** | `/ask` `/remember` `/memory` `/forget` `/reset`、DM、guild @bot、钉钉消息 |
| DingTalk/Spark 容器化 | 未验证 | `--build-arg TIER2=true` 已支持 staging，未做运行验证（本机 Tier 1 优先） |
| 多用户并发 / 多 guild | 仍未覆盖 | 与 runbook §12 一致，按"小范围部署"对待 |

---

## 10. 交接命令

```bash
# 1) 构建并验证镜像（任一有 docker 的机器）
docker build -f deploy/docker/Dockerfile -t natter:tier1 .
deploy/docker/verify-image.sh --image natter:tier1 --build \
  --env-file <live.env> --config-dir <config> --evidence artifacts/ops/docker-verify-$(date +%F-%H%M)

# 2) 目标机 systemd 安装 + 取证
sudo deploy/systemd/install.sh --release /opt/natter --config-src config --start
sudo deploy/systemd/verify.sh --evidence artifacts/ops/systemd-$(date +%F)
systemctl list-timers natter-soak.timer

# 3) 24h 后审计
python3 /opt/natter/scripts/soak-report.py /var/log/natter/soak-*.jsonl --interval 300 | tee soak-report.txt

# 4) 日常：告警 + 出口
*/5 * * * * /opt/natter/scripts/ops-health-alert.sh --state /var/lib/natter/log/alert-state.json
./scripts/discord-egress-probe.sh -n 6 -i 2

# 5) 轮换 secret
printf '%s' "$NEW" | ./scripts/rotate-secret.sh --file /etc/natter/live.env --key DISCORD_BOT_TOKEN
sudo systemctl restart natter && scripts/ops-health-alert.sh
```

---

## 11. 证据清单

| 路径（相对 `artifacts/ops/p4.5/closeout/2026-11-21/`） | 内容 |
| --- | --- |
| `logs/publish.log`、`logs/stage.log`、`logs/stage.json` | CLI 发布 + 插件 staging |
| `logs/docker-build.log` / `-2.log` / `-3.log` | 三次镜像构建（含两次失败原因） |
| `docker-verify/script-run/summary.txt` | 13 项检查、0 失败 |
| `docker-verify/script-run/health-inside.json` | 容器内 `/health`（`healthy`、`connected=true`） |
| `docker-verify/script-run/container-summary.txt` | `user=10001:10001 readOnly=true health=healthy` |
| `docker-verify/script-run/egress-diagnosis.txt` | 容器内 DNS/TCP 诊断 |
| `docker-verify/ipv6-diagnosis/` | bridge 形态下 IPv6/TCP 失败证据 |
| `logs/docker-build-3.log` | uid 修正后的成功构建 |
| （实测记录）裸 `--tmpfs /state` → `status=exited`；`--tmpfs /state:uid=10001...` → `status=running` | `/state` 权限坑的最小复现 |
| `docker-verify/container-run/` | 首次成功连上 Discord 的容器证据 |
| `systemd/BLOCKED.txt` | systemd 环境受限记录 |
| `soak/soak-toolcheck.jsonl` | 真实 host 下的采样器自检 |
| `logs/obs019-*.json` + `logs/obs019-state.json` | OBS-019 告警复现（基线静默 → 断连报警） |
| `rotate-test/` | secret 轮换演练（临时值） |
| `ca/ca-certificates.crt` | 公共 CA + 本机 302 CA 合并包（仅供本机容器验证） |
