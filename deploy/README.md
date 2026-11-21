# Natter deployment (Tier 1)

> 完整运维流程见 [运维手册](../docs/OPERATIONS_RUNBOOK.md)。
> 本轮收口记录与已知风险见 [P4.5 运维收口](../docs/operations/2026-11-21-p4-5-ops-closeout-run.md)。

| 你要做的事 | 用这个 |
| --- | --- |
| 宿主机安装 systemd 服务 | `deploy/systemd/install.sh` + `deploy/systemd/verify.sh` |
| 构建/运行容器 | `deploy/docker/Dockerfile` + `deploy/docker/compose.yaml` |
| 验证镜像真的带 Tier 1 插件 | `deploy/docker/verify-image.sh` |
| 把 Discord/DeepSeek 插件装进发布目录 | `scripts/stage-tier1-plugins.sh` |
| 24h 被动 soak | `scripts/soak-sample.sh` + `scripts/soak-report.py` |
| 断连/发送失败告警（含 OBS-019） | `scripts/ops-health-alert.sh` |
| 出网 RTT 是否满足 3s 交互预算 | `scripts/discord-egress-probe.sh` |
| 轮换 secret | `scripts/rotate-secret.sh` + [轮换流程](../docs/operations/secret-rotation.md) |
| 备份 / 恢复 SQLite | `scripts/natter-backup.sh` |

---

## 1. 发布产物必须带业务插件

`dotnet publish` 只会带 CLI 和 **示例** 插件（greetings/metrics）。Discord/DeepSeek 需要显式 staging：

```bash
dotnet publish src/Natter.Cli/Natter.Cli.csproj -c Release -o /opt/natter
./scripts/stage-tier1-plugins.sh -o /opt/natter/plugins -c Release          # Discord + DeepSeek
./scripts/stage-tier1-plugins.sh -o /opt/natter/plugins -c Release --tier2  # 另加 DingTalk + Spark

# 不启 host 的离线校验（不需要 secret，不会抢 bot 会话）
/opt/natter/natter plugins inspect /opt/natter/plugins/discord
```

staging 布局（NPS 加载器要求）：

```text
/opt/natter/plugins/discord/{plugin.json,config.schema.json,lib/*}
/opt/natter/plugins/deepseek/{...}
/opt/natter/plugins/staging-manifest.json      # 审计用清单，记录 id/文件数/时间
```

脚本保证：缺 `lib/<assembly>.deps.json` 直接失败、不写 `config.json`、重跑时保留已有 `config.json`。

---

## 2. systemd

```bash
# 1) 发布 + staging（见 §1），产物放 /opt/natter
# 2) 安装（需要 root）
sudo deploy/systemd/install.sh --release /opt/natter --config-src config --start

# 3) 填 secret（install.sh 只生成 0600 空模板，不写值）
sudoedit /etc/natter/live.env

# 4) 采集证据（只读；没有 systemd/root 时输出 BLOCKED 而不是假装通过）
sudo deploy/systemd/verify.sh --evidence artifacts/ops/systemd-$(date +%F)
```

安装器做了什么：

- 建 `natter` 系统用户；`/opt/natter` 只读、`/etc/natter` root:natter、`/var/lib/natter` 与 `/var/log/natter` 0700；
- `live.env` 0600（只有空 key 模板）、`proxy.env` 0640（默认只有 `NO_PROXY`）；
- 装 `natter.service`（`StateDirectory`/`LogsDirectory`、`ProtectSystem=strict`、`NoNewPrivileges`、`SystemCallFilter=@system-service`）；
- 装 `natter-soak.service` + `natter-soak.timer`（每 5 分钟一次 `/health` 采样）并把 `soak-sample.sh` 复制到 `/opt/natter/scripts/`；
- `systemctl enable --now natter` 与 `natter-soak.timer`。

`natter serve` 自己处理 SIGINT/SIGTERM；unit 给 20s 优雅退出窗口（`KillSignal=SIGTERM` + `TimeoutStopSec=20`）。

---

## 3. Docker

```bash
cd deploy/docker
cp live.env.example live.env && chmod 600 live.env    # 只放 secret，不提交
mkdir -p config                                       # profiles.json + <pluginId>.json
docker compose up -d --build
docker compose ps
docker compose logs -f natter
```

镜像里带了什么：

- `/app`：CLI 发布产物（`dotnet natter.dll`）+ 示例插件；
- `/app/plugins`：`discord`、`deepseek`（`--build-arg TIER2=true` 时再加 `dingtalk`、`spark`）；
- 运行用户 **10001:10001**，rootfs `read_only`，`cap_drop: ALL`，`no-new-privileges`；
- `/state` 命名卷（首次创建时继承镜像里 10001 的属主，因此不需要 root 预建）；
- `HEALTHCHECK` 探 loopback `5099` 的 `/health`（**liveness**，不代表 Tier 1 profile 健康）。

没有 compose 插件的主机（本机就是这种），用等价 `docker run` 验证：

```bash
deploy/docker/verify-image.sh \
  --image natter:tier1 \
  --env-file deploy/docker/live.env \
  --config-dir deploy/docker/config \
  --evidence artifacts/ops/docker-verify-$(date +%F-%H%M)
```

它会检查：镜像存在 → 插件 staged → manifest 合法 → 用户 10001:10001 → 容器起来 → healthcheck healthy → `/health` 可读 → 至少一个 upstream connected → 证据里没有 secret 值。

> **`/state` 权限坑**：镜像里 `/state` 已 chown 给 10001，命名卷首次创建会继承属主 → 无需 root。
> 但如果改用 `--tmpfs /state` 或 bind mount，目录由 root 拥有，host 会因 `/state/.lock` 权限被拒直接崩溃
> （`UnauthorizedAccessException`）。这类挂载必须写 `--tmpfs /state:uid=10001,gid=10001,mode=0700`
> 或预先 `chown 10001:10001`。已实测：裸 `--tmpfs /state` → `status=exited`；带 uid/gid → `status=running`。

> **网络前提**：容器必须能直达 Discord（或配 `HTTP(S)_PROXY`）。若宿主机用 hosts 劫持 + 私有 CA 把 Discord 指到本地加速器，需要显式给容器 `--network host`、挂 `/etc/hosts` 与合并后的 CA；判定与实测见 [3s 交互预算](../docs/operations/discord-3s-interaction-egress.md) §3.3。

---

## 4. SQLite backup

```bash
scripts/natter-backup.sh /var/lib/natter/natter.db /backup/natter-$(date +%F).db
```

脚本会 `wal_checkpoint(FULL)` + `sqlite3 .backup` + 0600。
恢复必须停服务：

```bash
systemctl stop natter
cp /backup/natter-YYYY-MM-DD.db /var/lib/natter/natter.db
chown natter:natter /var/lib/natter/natter.db && chmod 600 /var/lib/natter/natter.db
systemctl start natter
natter --config-dir /etc/natter/config --state-dir /var/lib/natter health --offline --json
```

Never copy `-wal`/`-shm` separately; use the backup API while stopped or checkpointed.

---

## 5. 24h soak

```bash
# systemd 部署：timer 已经装好，确认即可
systemctl list-timers natter-soak.timer

# 手动/无 systemd
./scripts/soak-sample.sh -i 300 -d 86400 --out-dir /var/log/natter

# 24h 后审计（覆盖率、缺口、重启、重连、OBS-019 命中数）
python3 scripts/soak-report.py /var/log/natter/soak-*.jsonl --interval 300
```

采样只读 loopback `/health`，不会新建 Discord 网关会话。完整口径见 [24h 被动 soak](../docs/operations/passive-soak-24h.md)。

---

## 6. 告警与出口检查

```bash
# 每 5 分钟：状态 / 断连 / 发送失败 / interaction 超时 / OBS-019
*/5 * * * * /opt/natter/scripts/ops-health-alert.sh --state /var/lib/natter/log/alert-state.json

# slash 命令「未响应」时，先量 RTT
./scripts/discord-egress-probe.sh -n 6 -i 2
```
