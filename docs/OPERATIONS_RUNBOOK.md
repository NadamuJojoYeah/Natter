# Natter 运维手册（Tier 1）

> **状态**：Runbook v1  
> **适用版本**：P4 Go 基线（.NET 10 / Natter 0.1.0-preview）  
> **适用对象**：负责 Natter Discord/DeepSeek 主链路、DingTalk/Spark 辅助链路的运维人员  
> **目标**：让服务可部署、可观察、可备份、可恢复、可升级、可排障  
> **关联**：[部署示例](../deploy/README.md)、[P4 产品化](P4_TIER1_PRODUCTIZATION.md)、[依赖演进策略](DEPENDENCY_EVOLUTION_POLICY.md)、[首次部署测试记录](testing/2026-11-16-first-deployment-test.md)、[P4.5 修复验证（运维）](operations/2026-11-17-p4-5-ops-verification.md)、[P4.5 实机验收交接](operations/P4.5_REAL_MACHINE_HANDOFF.md)、[P4.5 运维验收执行记录](operations/2026-11-17-p4-5-ops-verification-run.md)、[P4.5 联合复测（运维侧）](operations/2026-11-17-p4-5-retest-run.md)、[P4.5 收尾复测](operations/2026-11-17-p4-5-final-retest-run.md)

---

## 0. 先看结论

- **Tier 1**：Discord + DeepSeek，必须保障。
- **Tier 2**：DingTalk + Spark，只保证核心文本链路，best-effort。
- **一个 bot token 只能跑一个 `serve` 实例**；同一 state 目录有单实例锁。
- 配置在 `config/`，状态在 `state/`，secret 只在环境/secret provider。
- 备份用 `scripts/natter-backup.sh`，恢复必须停服务。
- 健康检查优先 loopback `/health`；无服务时用 `natter health --offline --json`（不启动 upstream/gateway）。
- 多用户并发/多 guild 权限矩阵尚未完整验证，按“小范围部署”对待。
- P5 task runtime 使用 ProfileAware AI worker；task 使用独立 conversation 上下文；无 provider 时降级 deterministic clock worker；Pending 任务在 host 重启时重新入队，过期 Running lease 标记 Expired。
- `http-fetch` 需要 `NATTER_HTTP_FETCH_HOSTS`（逗号分隔 allowlist）且 profile `policy.allowNetworkTools=true`。

---

## 1. 目录与账号

### 1.1 systemd 推荐布局

```text
/opt/natter/                    # 发布产物，只读
├── natter
├── natter.dll
└── ...

/etc/natter/
├── config/                     # profiles.json + 插件 config（只读）
│   ├── profiles.json
│   ├── natter.transport.discord.json
│   ├── natter.feature.ai.deepseek.json
│   └── ...
├── live.env                    # 0600，secret
└── proxy.env                   # 0640，出网代理（可选，见 §3.4）
/var/lib/natter/                # state，0700，natter 用户
├── natter.db
├── natter.db-wal
└── .lock
```

### 1.2 账号

```bash
sudo useradd --system --home /opt/natter --shell /usr/sbin/nologin natter
sudo chown -R natter:natter /opt/natter /etc/natter/config /var/lib/natter
sudo chmod 700 /var/lib/natter
sudo chmod 600 /etc/natter/live.env
```

不要用 root 跑 `serve`。

### 1.3 Docker 布局

`deploy/docker/compose.yaml`：

- 容器用户 UID 10001；
- `/config` 只读；
- `/state` 命名卷；
- `read_only: true`；
- `no-new-privileges`。

`env_file`、`config` 的路径相对于 `deploy/docker/`，不要按仓库根目录理解。

---

## 2. 首次部署

### 2.1 systemd

```bash
# 1. 发布
dotnet publish src/Natter.Cli/Natter.Cli.csproj -c Release -o /opt/natter
sudo chown -R natter:natter /opt/natter

# 2. 配置
sudo mkdir -p /etc/natter/config
sudo cp config/profiles.json /etc/natter/config/
# 放入 Discord/DeepSeek/DingTalk/Spark 插件 config（只允许 ${secret:...}）
sudo chmod 600 /etc/natter/live.env

# 3. state
sudo mkdir -p /var/lib/natter
sudo chown natter:natter /var/lib/natter
sudo chmod 700 /var/lib/natter

# 4. unit
sudo cp deploy/systemd/natter.service /etc/systemd/system/natter.service
sudo systemctl daemon-reload
sudo systemctl enable --now natter
sudo systemctl status natter
```

### 2.2 Docker

```bash
cd deploy/docker
# 创建 live.env（0600），不要提交
mkdir -p config
# 放 profiles.json / 插件 config
docker compose up -d --build
docker compose ps
docker compose logs -f natter
```

### 2.3 启动后检查

```bash
systemctl status natter
journalctl -u natter -n 100 --no-pager

natter --config-dir /etc/natter/config --state-dir /var/lib/natter health --offline --json
natter --config-dir /etc/natter/config --state-dir /var/lib/natter plugins list --detail
natter --config-dir /etc/natter/config --state-dir /var/lib/natter upstreams --json
```

预期：

- `health.status=healthy`（Tier 1 profile 的 provider 可用且 upstream 都连接；Tier 2 缺失只作为 warning，不单独把状态降级）；
- `uptimeSeconds` / `startedAt` 的权威值来自主实例 loopback `/health`；CLI `health` 是新进程，不能用于 uptime 对账。
- Discord 连接失败时错误包含 `phase`、`lastDisconnectedError`、`proxy`、`connectionState`；代理凭据会脱敏。
- 只部署 Tier 1（未装 Spark）时 `status=degraded` 属预期：`profiles.json` 里 `tier2-dingtalk-spark` 的 provider 不可用；
- Discord/DeepSeek `available=true`；
- 没有 `Failed` 插件；
- state 目录 700，DB 600。

> `plugins list --detail` / `upstreams --json` / 普通 `health` 都会启动完整 host，即新建一次 Discord 网关会话；主实例运行期间连续跑会互相抢占会话（见 §5.3）。运维巡检使用 loopback `/health` 或 `health --offline --json`。

---

## 3. 配置与 Secret

### 3.1 配置目录

NPS 从 `NATTER_CONFIG_DIR` 读取：

```text
<config>/profiles.json
<config>/<pluginId>.json
```

插件包目录只读；不要把配置写回插件目录。

### 3.2 Secret

`live.env` 只放环境变量：

```text
DISCORD_BOT_TOKEN
DEEPSEEK_API_KEY
DINGTALK_CLIENT_ID
DINGTALK_CLIENT_SECRET
DINGTALK_BOT_ID
SPARK_API_KEY
```

规则：

- 0600；
- 不提交 git；
- 不贴 issue/聊天/截图；
- 轮换时先改文件，再 `systemctl restart natter`；
- 轮换后跑一次 `health` 和最小消息 smoke。

### 3.3 Profile

`profiles.json` 只引用组件，不放 secret：

```json
"ai": { "provider": "natter.feature.ai.deepseek#deepseek" },
"prompt": { "provider": "host#static" },
"context": { "assembler": "host#recent-window" },
"memory": { "store": "host#sqlite" }
```

Tier 2 profile 可以选 `natter.feature.ai.spark#spark` / `natter.sample.memory#file`，但不要让 Tier 2 失败影响 Tier 1。

### 3.4 出网代理

Discord.Net 的默认 REST provider 以 `useProxy: false` 创建，网关 socket 又显式赋 `ClientWebSocketOptions.Proxy = null`，两者都**不会**读代理环境变量。natter 启动时会把 `HttpClient.DefaultProxy` 解析出来，同时注入 REST 与 gateway，所以代理只需要放在进程环境里：

```text
HTTP_PROXY=http://127.0.0.1:7890
HTTPS_PROXY=http://127.0.0.1:7890
ALL_PROXY=http://127.0.0.1:7890
NO_PROXY=127.0.0.1,localhost,::1
```

- systemd：加 `Environment=HTTPS_PROXY=...`，或 `EnvironmentFile=/etc/natter/proxy.env`（0640，不放 secret）；
- Docker：`environment:` 或 `env_file:` 同样生效；
- `NO_PROXY` 必须放行 loopback，否则本机 `/health` 也会被代理劫持；
- 代理链路握手慢：实测 REST `/gateway/bot` ~12s、网关 WS 握手 ~23s，`readyTimeoutSeconds` 建议 ≥ 90（默认 30）；
- 验证：`upstreams --json` 里 `connected=true`；带 `--verbose` 启动时日志出现 `Received Dispatch (READY)`，而不是 `upstream-connect-failed`。

---

## 4. 启动、停止、重启

### systemd

```bash
sudo systemctl start natter
sudo systemctl stop natter       # SIGTERM，优雅退出
sudo systemctl restart natter
sudo systemctl status natter
journalctl -u natter -f
```

### Docker

```bash
docker compose -f deploy/docker/compose.yaml up -d
docker compose -f deploy/docker/compose.yaml stop
docker compose -f deploy/docker/compose.yaml restart
docker compose -f deploy/docker/compose.yaml logs -f natter
```

### 单实例锁

`serve` 会对 `<state-dir>/.lock` 获取独占锁。  
同一 state 启动第二个实例会失败：

```text
Another Natter serve instance is using state directory ...
```

不要用不同 state 目录绕锁来跑同一个 bot token；同一 bot 多实例会双回复。

---

## 5. 健康检查

### 5.1 CLI

```bash
natter --config-dir /etc/natter/config --state-dir /var/lib/natter health --json
```

关键字段：

- `status`：`healthy` / `degraded` / `unknown`；
- `upstreams[].connected`；
- `providers[].available`；
- `plugins.activated/failed`；
- `conversations`。

### 5.2 HTTP loopback

启动时加：

```bash
natter serve --health-port 5099
```

只能从本机访问：

```bash
curl -s http://127.0.0.1:5099/health | jq
```

不要把 health port 暴露到公网。

### 5.3 建议采样

cron 每 5 分钟：

```bash
*/5 * * * * /usr/local/bin/natter-health-sample.sh
```

脚本示例：

```bash
#!/usr/bin/env bash
set -euo pipefail
out=/var/log/natter/health.jsonl
mkdir -p "$(dirname "$out")"
# 优先 loopback /health：不新建 bot 会话；端口不可用时回退 CLI。
curl -fsS --max-time 5 http://127.0.0.1:5099/health >> "$out" || \
  natter --config-dir /etc/natter/config --state-dir /var/lib/natter health --offline --json >> "$out"
chmod 600 "$out"
```

日志权限 600；只记录 health JSON，不记录正文。

> `natter health --json`（无 `--offline`）会启动完整 host 并新建 Discord 网关会话，和主实例抢同一个 bot 连接；cron 必须使用 loopback 或 `--offline`。
> 主实例在跑时采样一律走 loopback `/health`，CLI 版本只用于人工排障。

### 5.4 CLI 命令与 host 启动语义（S1-11）

**默认不启动 host。** 只有显式声明"需要运行态"的命令才会 `StartAsync`，其余命令只读配置、state 库与插件元数据（`dotnet build` 级别的图构建不连接任何网关）。

| 命令 | 启动 host | 需要它的原因 / 离线降级 |
| --- | --- | --- |
| `serve` | **是** | 长驻服务本体 |
| `health`（不带 `--offline`） | **是** | 读运行态 uptime / upstream / provider；`health --offline` 走静态视图 |
| `upstreams` | **是** | 读 upstream 的**实时**连接状态；加 `--no-host` 退化为静态列表 |
| `events list` / `events watch` / `events clear` | **是** | 操作运行中的内存事件日志 |
| `exec` | **是** | 在同一个 host 内跑多条命令 |
| `demo` | **是** | 端到端演练运行中的 host |
| `discord status`（插件命令） | **是** | 读 Discord 网关状态 |
| `plugins graph` / `plugins contributions` / `plugins permissions` / `plugins doctor` | **是** | 读**已激活**插件的图/贡献/授权/诊断 |
| `memory list` / `memory export` / `memory import` / `remember` / `forget` | **是** | 读写 host 打开的 memory store |
| 其余全部：`plugins list` / `plugins inspect` / `plugins config validate`、`plugins store *`（list/install/enable/disable/rollback/uninstall/cleanup）、`doctor`、`ports`、`version`、`help`、`satori status`、`ai status`、**插件贡献命令的默认值** | **否** | 只读配置/state/插件元数据（如 `plugins list` 显示的是扫描结果 `Discovered`，激活态需启动 host）；插件若要运行态可用 `ICliHostRequirementProvider` 声明 |

> 未解析的命令（不在内置表、也没声明规则）按**插件命令**处理 → 会启动 host 让插件有机会注册；若最终无人提供，命令报 `Unknown command`。
> `ai status` 属于插件命令的默认档：主实例在跑时它不会启动第二个 host；若它确实需要运行态，由插件自带规则或改用 loopback `/health`。

为什么这样设计：`plugins list`、`upstreams`、`ai status` 这类巡检命令以前也会拉起完整 host，**主实例运行时执行它们会建立第二条网关会话，导致其中一条被 Discord 断开**。现在读命令不再产生写副作用。

#### 主实例在跑时执行"需要 host"的命令

需要 host 的命令与 `serve` 共用 `<state-dir>/.lock`，因此**第二个进程会被拒绝**而不是抢会话：

```bash
$ natter --state-dir /var/lib/natter upstreams --json
error: HL-CLI-HOST-BUSY: another Natter instance is using state directory '/var/lib/natter'.
       'upstreams' needs a running host (...), and starting a second host would open a second
       gateway session (one of them would be disconnected by Discord).
       options:
         - inspect the running instance: curl -s http://127.0.0.1:5099/health | jq
         - run this command without a host: natter upstreams --no-host   (output is degraded)
         - or stop the running instance first: sudo systemctl stop natter
$ echo $?
3
```

- **退出码 `3` = `HL-CLI-HOST-BUSY`**（与 `1` 一般失败、`2` 用法错误区分，脚本可直接判断）；
- 不会静默返回空数据：拒绝时给错误码 + 提示，降级时给 note。

#### 逃生口

| 方式 | 作用 |
| --- | --- |
| `--no-host`（全局） | 任何命令都不启动 host；需要运行态的命令打印降级 note |
| `--offline`（`health` 专有） | 等价于对 `health` 不启动 host，输出 `status=offline` 的静态视图 |

```bash
natter --state-dir /var/lib/natter plugins list --json      # 安全：不建网关会话
natter --state-dir /var/lib/natter upstreams --no-host      # 显式降级
natter --state-dir /var/lib/natter health --offline --json  # 静态健康视图
```

#### 巡检推荐顺序

1. 服务在跑：`curl -s http://127.0.0.1:5099/health | jq`（零副作用）；
2. 服务在跑且想看插件状态：`natter plugins list --detail`（离线，安全）；
3. 服务停了想排障：`natter health --offline --json`、`natter doctor --json`、`natter plugins inspect <dir>`；
4. 确实要看运行态：先停实例，或直接读 loopback `/health`。

---

## 6. 日志

### systemd

```bash
journalctl -u natter --since "1 hour ago"
journalctl -u natter -p warning --since today
```

### Docker

```bash
docker compose -f deploy/docker/compose.yaml logs --since 1h natter
```

关注：

- `upstream-connect-failed`；
- `Routed event ... profile ... provider ...`；
- `ai replied`；
- `sendFailures` / `not_found`；
- `Another Natter serve instance`；
- `clipped` / fallback；
- 429/401/5xx。

禁止：

- 日志出现 bot token/API key；
- 日志出现 prompt/memory 正文；
- 截图包含 secret。

---

## 7. 备份与恢复

### 7.1 备份

```bash
scripts/natter-backup.sh \
  /var/lib/natter/natter.db \
  /backup/natter-$(date +%F-%H%M).db
```

脚本会：

- `PRAGMA wal_checkpoint(FULL)`；
- `sqlite3 .backup`；
- 设置 600。

要求：

- 机器安装 `sqlite3`；
- 备份目录 700；
- 不要只复制 `-wal`/`-shm`；
- 建议每日备份 + 保留 14 天。

### 7.2 恢复

```bash
sudo systemctl stop natter
sudo cp /backup/natter-YYYY-MM-DD.db /var/lib/natter/natter.db
sudo chown natter:natter /var/lib/natter/natter.db
sudo chmod 600 /var/lib/natter/natter.db
sudo systemctl start natter

natter --config-dir /etc/natter/config --state-dir /var/lib/natter health --json
```

恢复后检查：

- conversations/messages/memory 是否合理；
- `/memory` 是否可见；
- 一条 DMs/guild 消息是否正常；
- 没有 `database is locked`。

### 7.3 恢复演练

至少每季度做一次：

1. 备份当前 DB；
2. 在临时 state 目录恢复；
3. 用临时 config 启动 `serve --duration 5`；
4. `health`；
5. 删除临时目录。

不要在生产 state 上直接练习恢复。

---

## 8. 升级与回滚

### 8.1 升级前

- CI 绿；
- 备份 DB；
- 备份当前二进制/插件目录；
- 记录当前 commit/tag；
- 确认 migration 方向。

### 8.2 升级

```bash
sudo systemctl stop natter
# 替换 /opt/natter 发布产物
sudo chown -R natter:natter /opt/natter
sudo systemctl start natter
natter ... health --json
```

### 8.3 回滚

- 二进制回滚；
- 插件目录回滚；
- 如果 DB migration 不兼容，恢复升级前备份；
- 恢复后 `health` + 最小消息。

不要在没有备份的情况下跑不可逆 migration。

---

## 9. 常见故障

### 9.1 服务起不来

```bash
systemctl status natter
journalctl -u natter -n 200 --no-pager
natter ... plugins list --detail
natter ... plugins config validate <pluginId>
```

常见原因：

- secret 缺失；
- profile/组件 ref 不存在；
- state 目录权限错误；
- 单实例锁被占用；
- 插件 config schema 失败。

### 9.2 双回复

检查：

```bash
ps -ef | grep natter
systemctl status natter
ls -l /var/lib/natter/.lock
```

根因通常是同一 bot token 有两个 host。  
停掉多余实例；确保一个 bot token 一个 `serve`。

### 9.3 upstream disconnected

- 检查网络；
- 检查 token；
- Discord/钉钉后台权限/Intent；
- Tier 2 断开不影响 Tier 1；
- 必要时重启。

### 9.4 429/5xx

- 看 provider 日志；
- 检查重试/退避；
- 不要在故障时重启风暴；
- 用户侧应有降级文案。

### 9.5 database is locked

- 确认只有一个 serve；
- 停止服务；
- 备份；
- 检查 `-wal`/`-shm`；
- 恢复或修复后再启动。

### 9.6 磁盘满

- `du -sh /var/lib/natter /var/log/natter`；
- 清理旧备份/日志；
- 不要把 state 放 tmpfs；
- 备份保留策略。

### 9.7 密钥疑似泄漏

1. 立即在平台轮换；
2. 更新 `live.env`；
3. `systemctl restart natter`；
4. 检查日志/截图/issue；
5. 记录事件。

---

## 10. 日常检查

### 每日

- `health` healthy；
- 无 Failed 插件；
- 无重复回复；
- 备份成功；
- 磁盘空间。

### 每周

- 日志 warning/error；
- upstream 重连次数；
- `sendFailures`；
- DB/WAL 大小；
- plugin 版本。

### 每月

- 依赖安全扫描；
- 备份恢复抽样；
- secret 轮换计划；
- 容量趋势。

### 每季度

- 完整恢复演练；
- profile/组件兼容检查；
- Tier 2 smoke；
- 权限/账号 review。

---

## 11. 24h 被动 soak

没有时间盯 24 小时时，部署后让服务自己跑：

1. 启动 systemd/Docker；
2. 配置 health 采样；
3. 第二天检查：
   - 服务是否重启；
   - `health` 是否稳定；
   - 日志是否有重复回复/重连风暴；
   - DB/WAL 增长；
   - 内存增长；
   - `sendFailures`。
4. 记录到测试/运维记录。

不要求人工守夜，但要有证据。

---

## 12. 支持边界与已知风险

- Tier 1：Discord + DeepSeek，优先支持。
- Tier 2：DingTalk + Spark，核心文本链路 best-effort。
- 未完整验证：
  - 多用户并发；
  - 多 guild 权限矩阵；
  - 大规模限流；
  - 长时间高负载；
  - Android 动态插件。
- 当前 memory 是显式记忆，不是自动记忆提取。
- 不要承诺未实现的能力（streaming/attachments/tools）。

---

## 13. 联系与升级

```text
Owner:        __由部署方填写__
On-call:      __由部署方填写__
Escalation:   __由部署方填写__
Repo:         https://github.com/NadamuJojoYeah/Natter
CI:           GitHub Actions
```

严重级别建议：

- SEV1：Tier 1 完全不可用/数据损坏/secret 泄漏；
- SEV2：DM 或 guild 主链路部分不可用；
- SEV3：Tier 2 故障、诊断/日志问题；
- SEV4：文档/低风险问题。

---

## 14. 一句话

**一个 bot token 一个 serve；secret 只进 live.env；代理只进进程环境（§3.4）；state 700/DB 600；备份再恢复；health 先看 upstream/provider/plugin；Tier 2 故障不阻塞 Tier 1。**
