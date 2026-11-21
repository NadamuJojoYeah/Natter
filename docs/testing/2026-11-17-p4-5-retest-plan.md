# P4.5 联合复测计划（测试 + 运维）

> **状态**：Ready for joint retest  
> **日期**：2026-11-17  
> **修复基线**：`0778c1b`（duration/phase/DingTalk health/sendFailures/Docker template）  
> **当前 HEAD**：`24922c7`（测试/运维记录归档）  
> **前置结论**：[P4.5 联合处置](2026-11-17-p4-5-joint-response.md)  
> **目标**：确认工程修复后，测试与运维在**同一 commit、同一配置**下得到一致结论
> **运维执行记录**：[P4.5 联合复测（运维侧）](../operations/2026-11-17-p4-5-retest-run.md)（2026-11-17 17:22–17:33，Conditional Pass）

---

## 0. 这次要解决的三个问题

1. **代理 gateway 结果冲突**：测试说 60s 未 ready，运维说 40s READY；
2. **修复后的行为回归**：duration、phase、DingTalk health、sendFailures；
3. **部署/长跑收口**：systemd、Docker、24h soak、secret 轮换。

只有 1、2 通过，3 才有意义；3 未完成，P4.5 仍然不能算生产完成。

---

## 1. 当前修复范围

`0778c1b` 已包含：

- `serve --duration` 的取消预算同时覆盖 host start 与 command runner；
- Discord connect 超时/异常带 phase：
  - `rest_gateway`
  - `ws_connect`
  - `identify_ready`
- DingTalk upstream：
  - `lastConnectedAt`
  - `lastEventAt`
  - `lastDisconnectAt`
  - `reconnectCount`
  - `LastError`
- `sendFailures` / `lastSendError` 进入 health；
- `deploy/docker/live.env.example`。

仍未包含：

- OBS-007 自动 watchdog / 自动恢复；
- 代理 gateway 冲突的最终定论（需要本次复测）。

---

## 2. 测试执行

### 2.1 基线

```bash
git rev-parse --short HEAD   # 24922c7 或后续修复
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

预期 **245/245**。

### 2.2 代理解析单测

```bash
dotnet test tests/Natter.Framework.Tests \
  --filter "FullyQualifiedName~DiscordEgressProxyTests"
```

预期 **5/5**。

### 2.3 代理 gateway 受控复现（重点）

固定：

```text
HEAD=24922c7
readyTimeoutSeconds=90
HTTP_PROXY=http://127.0.0.1:7890
HTTPS_PROXY=http://127.0.0.1:7890
ALL_PROXY=http://127.0.0.1:7890
NO_PROXY 中不要包含 discord.com
只运行一个 serve
```

执行：

```bash
natter --plugin-dir <plugins> \
  --config-dir <config> \
  --state-dir <state> \
  serve --duration 120 --health-port 5099
```

每 5 秒记录：

```bash
curl -s http://127.0.0.1:5099/health | jq
```

同时保留日志中的 phase：

```text
rest_gateway
ws_connect
identify_ready
```

判定：

| 结果 | 结论 |
| --- | --- |
| 90s 内 READY，DM/guild 正常 | 代理冲突可能由 60s 配置导致；记录最小可用超时 |
| 90s 仍未 READY | `BUG-P4.5-PROXY-WS` 仍为 High，不能签收 |
| 测试通过、运维仍失败 | 对齐插件 staging/代理/配置后再测 |

补充：

- 用 `--duration 60` 再跑一次，确认是否只是 60s 不够；
- 测试 **bad proxy** `127.0.0.1:1`，确认 phase 与退出时间；
- 测试 `NO_PROXY=discord.com`，确认 bypass 行为。

### 2.4 duration 退出延迟

```bash
# 坏代理，duration=60
time natter ... serve --duration 60
```

预期：

- 约 60–75s 退出；
- 不再 151s；
- 日志有取消/停止记录。

### 2.5 DingTalk health

同时加载 DingTalk：

```bash
curl -s http://127.0.0.1:5099/health | jq
```

预期：

- DingTalk `lastConnectedAt` 非 null；
- 收到一条消息后 `lastEventAt` 非 null；
- `reconnectCount` 可见；
- `lastDisconnectAt` 在断连后可见。

### 2.6 sendFailures

制造一次发送失败（权限/频道删除/无效 channel），然后：

```bash
curl -s http://127.0.0.1:5099/health | jq
```

预期：

- `sendFailures > 0`；
- `lastSendError` 有脱敏错误；
- 不影响 health 其他字段。

### 2.7 health / offline / Tier1-only

- `health --offline --json`：`status=offline`、不建 gateway、可读 state；
- loopback health：`startedAt` 稳定、`sampledAt` 变化、`uptimeSeconds>0`；
- Tier1-only：`status=healthy`；
- Tier1 provider 缺失：启动失败/明确 degraded。

### 2.8 P4 回归

代理 READY 后重跑：

- `/ask`；
- `/remember`；
- `/memory`；
- `/forget`；
- `/reset`；
- DM；
- guild @；
- Tier2 DingTalk+Spark smoke；
- 重启后 memory/session。

---

## 3. 运维执行

### 3.1 代理

- 无代理：直连不可用则明确失败；
- HTTP 代理：REST + gateway READY；
- `NO_PROXY`：按 bypass；
- 坏代理：明确失败 + duration 退出；
- 代理下 DM/guild 各一条。

### 3.2 systemd

root 实机：

```bash
sudo cp deploy/systemd/natter.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now natter
sudo systemctl restart natter
sudo systemctl stop natter
sudo systemctl start natter
```

验证：

- `Restart=on-failure`；
- `ProtectSystem/ProtectHome`；
- SIGTERM 优雅；
- secret 轮换；
- state 持久化。

### 3.3 Docker

```bash
cd deploy/docker
cp live.env.example live.env
chmod 600 live.env
docker compose up -d --build
```

验证：

- UID 10001；
- `/config` 只读；
- `/state` 持久化；
- restart 后 memory/session；
- 代理环境变量。

### 3.4 24h 被动 soak

- loopback `/health` 每 5 分钟采样；
- 观察：
  - `connected`；
  - `lastDisconnectAt`；
  - `reconnectCount`；
  - `sendFailures`；
  - 重复回复；
  - DB/WAL；
  - 内存；
- OBS-007 不宣称自愈，只记录证据。

### 3.5 secret 轮换

- 平台重置；
- 更新 `live.env` 0600；
- restart；
- health；
- 日志无密钥。

---

## 4. 结论口径

测试和运维必须使用**同一 commit、同一 config、同一代理**。

| 结果 | P4.5 |
| --- | --- |
| 测试代理 READY + P4 回归通过；运维 systemd/Docker/24h 通过 | **Go** |
| 测试 READY 但运维未完成 systemd/Docker/soak | **Conditional Pass** |
| 测试 90s 仍不 READY | **No-Go** |
| 测试与运维仍不一致 | **No-Go，继续复现** |

---

## 5. 证据

```text
artifacts/p4.5/retest/<run>/
├── build.log
├── framework.trx
├── proxy-tests.trx
├── serve-phase.log
├── health.jsonl
├── health-offline.json
├── dingtalk-health.json
├── duration-bad-proxy.log
├── systemd-status.txt
├── docker-ps.txt
├── backup/
└── screenshots/
```

不提交 state、secret、代理凭据。

---

## 6. 一句话

**先证明代理 gateway 在 90s 配置下可 READY，再谈 duration/DingTalk/sendFailures 和 systemd/24h；测试与运维不一致时，P4.5 不能签收。**
