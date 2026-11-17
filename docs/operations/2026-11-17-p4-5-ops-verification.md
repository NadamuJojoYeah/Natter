# P4.5 修复验证（运维交接）

> **状态**：Ready for ops  
> **日期**：2026-11-17  
> **基线**：`HEAD=c131e73`（代理、健康、offline、upstream 诊断修复已合入）  
> **范围**：真实部署、代理、systemd/Docker、备份恢复、长跑  
> **配套**：[运维手册](../OPERATIONS_RUNBOOK.md)、[首次部署测试](../testing/2026-11-16-first-deployment-test.md)、[处置记录](../testing/2026-11-16-first-ops-deploy-response.md)
> **执行记录**：[P4.5 运维验收执行记录](2026-11-17-p4-5-ops-verification-run.md)（2026-11-17，可执行项完成、BLOCKED 项见该记录 §5）

---

## 0. 口径

- 测试负责产品行为；
- 运维负责真实部署、代理、systemd/Docker、24h 被动 soak；
- 真实代理 Discord 链路是联合验收；
- **OBS-007 只增加了观测字段，没有自动 watchdog/自动重启**；
- 不能把“health 能看见断连”当成“断连会自愈”。

---

## 1. 前置

```bash
git rev-parse --short HEAD   # 应为 c131e73 或后续修复提交
dotnet --info
natter --help
```

要求：

- 使用干净构建产物；
- 不改产品代码；
- 使用测试 bot / 测试 guild；
- secret 只放 `live.env` 0600；
- 开始前备份现有 state。

---

## 2. 代理验证

### 2.1 环境

以本机 HTTP 代理为例：

```bash
export HTTP_PROXY=http://127.0.0.1:7890
export HTTPS_PROXY=http://127.0.0.1:7890
export ALL_PROXY=http://127.0.0.1:7890
export NO_PROXY=localhost,127.0.0.1
```

systemd 用单独的 `proxy.env`，不要写进 `live.env` 以外的日志：

```text
/etc/natter/proxy.env
```

unit 中增加：

```ini
EnvironmentFile=-/etc/natter/proxy.env
```

### 2.2 用例

| ID | 场景 | 操作 | 预期 |
| --- | --- | --- | --- |
| O-001 | 无代理 | 清除代理变量，启动 | 直连可用则 connected；不可用则明确失败 |
| O-002 | HTTP 代理 | 设置 proxy.env，启动 | REST + gateway READY |
| O-003 | NO_PROXY | `NO_PROXY=discord.com` | 按 bypass 直连 |
| O-004 | 坏代理 | `HTTP_PROXY=http://127.0.0.1:1` | 明确失败，不无限卡死 |
| O-005 | 代理下 DM | 真实 DM | 回复成功 |
| O-006 | 代理下 guild | 频道 @bot | 回复成功 |

检查：

```bash
curl -s http://127.0.0.1:5099/health | jq
natter ... upstreams --json
journalctl -u natter -n 100 --no-pager
```

不要记录代理用户名/密码；如果代理 URL 带凭据，视为 secret。

---

## 3. systemd

需要 root 实机；没有 root 的环境标记为 **BLOCKED**，不能算通过。

```bash
# 发布
dotnet publish src/Natter.Cli/Natter.Cli.csproj -c Release -o /opt/natter
sudo chown -R natter:natter /opt/natter

# 配置/secret/state
sudo mkdir -p /etc/natter/config /var/lib/natter
sudo chmod 700 /var/lib/natter
sudo chmod 600 /etc/natter/live.env
sudo chown -R natter:natter /etc/natter/config /var/lib/natter

# unit
sudo cp deploy/systemd/natter.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable --now natter
```

验证：

```bash
systemctl status natter
journalctl -u natter -n 100 --no-pager
sudo systemctl restart natter
sudo systemctl stop natter
sudo systemctl start natter
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| O-010 | 首次启动 | active (running) |
| O-011 | restart | 5–10s 内恢复 connected |
| O-012 | stop | SIGTERM 优雅退出，10s 内 |
| O-013 | on-failure | 人为失败后自动重启（按 unit 配置） |
| O-014 | 权限 | 非 root 运行；config/secret/state 权限正确 |
| O-015 | 重启持久化 | memory/session 保留 |

---

## 4. Docker

```bash
cd deploy/docker
# live.env 0600
docker compose up -d --build
docker compose ps
docker compose logs -f natter
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| O-020 | 构建/启动 | 容器 healthy/running |
| O-021 | 非 root | UID 10001 |
| O-022 | config 只读 | 不可写 |
| O-023 | state 持久化 | restart 后 memory/session 保留 |
| O-024 | 代理 | proxy.env 传入后 READY |

Compose 的 `env_file`/`config` 路径相对于 `deploy/docker/`。

---

## 5. health / offline

```bash
# 主实例
curl -s http://127.0.0.1:5099/health | jq

# 离线快照
natter --config-dir /etc/natter/config --state-dir /var/lib/natter \
  health --offline --json
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| O-030 | loopback health | `startedAt`/`sampledAt`/`uptimeSeconds` |
| O-031 | 首次采样 | `uptimeSeconds>0` |
| O-032 | Tier1-only | `status=healthy`（Tier2 缺失不降级） |
| O-033 | offline | 不建 gateway，快速返回 |
| O-034 | failed plugin | health 显示 id/phase/code |
| O-035 | upstream 断连字段 | `lastDisconnectAt` / `reconnectCount` 可见 |

采样用 loopback，不要用 CLI `health` cron：

```bash
*/5 * * * * curl -s http://127.0.0.1:5099/health >> /var/log/natter/health.jsonl
```

否则会和主实例抢同一个 bot 会话。

---

## 6. 备份 / 恢复

```bash
scripts/natter-backup.sh \
  /var/lib/natter/natter.db \
  /backup/natter-$(date +%F-%H%M).db
sqlite3 /backup/natter-*.db "PRAGMA integrity_check;"
```

恢复：

```bash
sudo systemctl stop natter
sudo cp /backup/natter-YYYY-MM-DD.db /var/lib/natter/natter.db
sudo chown natter:natter /var/lib/natter/natter.db
sudo chmod 600 /var/lib/natter/natter.db
sudo systemctl start natter
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| O-040 | 备份 | 0600，integrity ok |
| O-041 | 恢复 | 启动正常，memory/session 保留 |
| O-042 | 恢复后消息 | DM/guild 正常 |

---

## 7. OBS-007：静默断连

这是本轮最重要的运维观察项。

步骤：

1. 服务正常运行；
2. 断开代理/网络，保持 1–2 分钟；
3. 恢复代理/网络；
4. 观察：

```bash
curl -s http://127.0.0.1:5099/health | jq
natter ... health --offline --json
journalctl -u natter --since "10 min ago"
```

记录：

- `connected`；
- `lastDisconnectAt`；
- `reconnectCount`；
- 是否自动恢复；
- 是否需要人工 restart。

当前预期：

- health 能看到断连；
- 不保证自动恢复；
- 如果没有自动恢复，由 systemd/运维重启；
- **不能把 OBS-007 标成“已修复自愈”**。

建议：

- 设置 `connected=false` 告警；
- 可选 systemd watchdog / 外部健康检查重启；
- 在 runbook 中记录“静默断连需人工/外部重启”。

---

## 8. 被动 24h soak

不用专人盯：

1. 部署；
2. 每 5 分钟 loopback health 采样；
3. 第二天看：
   - 进程是否重启；
   - connected 是否长期 false；
   - reconnectCount；
   - 重复回复；
   - DB/WAL 增长；
   - 内存；
   - sendFailures。

| ID | 用例 | 预期 |
| --- | --- | --- |
| O-050 | 24h 采样 | 无异常断连/无重复回复 |
| O-051 | 断连记录 | OBS-007 有证据 |
| O-052 | 重启恢复 | 重启后 connected |
| O-053 | 数据 | memory/session 完整 |

---

## 9. Secret 轮换

```bash
# 平台重置 token/key
sudoedit /etc/natter/live.env   # 0600
sudo systemctl restart natter
curl -s http://127.0.0.1:5099/health | jq
```

验证：

- 新 secret 生效；
- 旧 secret 失效；
- 日志无 secret；
- 回滚路径明确。

---

## 10. 证据与签字

```text
artifacts/ops/p4.5/<run>/
├── deploy.log
├── health.json
├── health.jsonl
├── systemd-status.txt
├── journal-tail.txt
├── docker-ps.txt
├── backup/
└── screenshots/
```

Go 条件：

- [ ] 代理无/有/NO_PROXY/坏代理；
- [ ] systemd 启动/停止/重启/on-failure；
- [ ] Docker；
- [ ] health/offline/Tier1-only；
- [ ] 备份/恢复；
- [ ] OBS-007 有观测证据，不宣称自愈；
- [ ] 24h 被动 soak；
- [ ] secret 轮换；
- [ ] 无密钥泄漏。

---

## 11. 一句话

**运维验的是“真实机器、真实代理、真实 systemd/Docker、真实长跑”；测试验的是“代码行为”。OBS-007 目前只是可观测，不是自愈。**
