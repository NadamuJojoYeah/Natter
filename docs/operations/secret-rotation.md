# Secret 轮换流程（Tier 1 / Tier 2）

> **适用**：`DISCORD_BOT_TOKEN`、`DEEPSEEK_API_KEY`（Tier 1）、`DINGTALK_CLIENT_ID/_SECRET/_BOT_ID`、`SPARK_API_KEY`（Tier 2）
> **工具**：`scripts/rotate-secret.sh`
> **原则**：值只进 `live.env`（0600）；终端里不出现明文；重启后再吊销旧凭据；每一步留证据

---

## 1. 什么时候轮换

| 触发 | 时限 |
| --- | --- |
| 疑似泄漏（日志/截图/issue/聊天/提交历史） | 立即，按 SEV1 |
| 定期轮换 | 每 90 天 |
| 人员变动 / 交接 | 交接前完成 |
| 平台侧策略要求（token 重置、key 到期） | 按平台提示 |

---

## 2. 平台入口（新值从哪来）

| Secret | 位置 |
| --- | --- |
| `DISCORD_BOT_TOKEN` | Discord Developer Portal → 应用 → Bot → **Reset Token**（旧 token 立即失效） |
| `DEEPSEEK_API_KEY` | DeepSeek 开放平台 → API Keys → 新建 → 删除旧 key |
| `DINGTALK_CLIENT_ID/_SECRET` | 钉钉开放平台 → 应用 → 凭证与基础信息 |
| `DINGTALK_BOT_ID` | 钉钉开放平台 → 机器人配置 |
| `SPARK_API_KEY` | 讯飞星火控制台 → 服务管理 → API Key |

> 控制台操作需要**平台账号权限**。没有权限时如实记录为 BLOCKED，不要伪造"已轮换"。

---

## 3. 标准流程（以 `DISCORD_BOT_TOKEN` 为例）

### 3.1 准备（可提前做）

```bash
# 1) 备份数据库与当前配置
sudo scripts/natter-backup.sh /var/lib/natter/natter.db /backup/natter-$(date +%F-%H%M).db

# 2) 记录当前指纹，便于轮换后核对（不显示明文）
scripts/rotate-secret.sh --file /etc/natter/live.env --check

# 3) 确认当前健康基线
scripts/ops-health-alert.sh            # 期望 alerts=0
```

### 3.2 写入新值（原子、可回滚）

```bash
# 从控制台复制新 token 后，直接管道给脚本（不进 shell 历史、不进 ps）
printf '%s' "$NEW_DISCORD_TOKEN" | ./scripts/rotate-secret.sh \
  --file /etc/natter/live.env --key DISCORD_BOT_TOKEN

# 或者从文件读（更稳，读后立即删除该文件）
printf '%s' '<粘贴到这里>' > /root/new-token.txt && chmod 600 /root/new-token.txt
./scripts/rotate-secret.sh --file /etc/natter/live.env --key DISCORD_BOT_TOKEN --from-file /root/new-token.txt
shred -u /root/new-token.txt
```

脚本做了什么：

- 只改目标 key，其它行与注释逐字节保留；
- 同目录临时文件 + `rename()` 原子替换；
- 备份到 `live.env.bak.YYYYmmdd-HHMMSS`（0600）；
- 写后回读校验指纹，不一致自动回滚；
- **不打印值**，只打印 `sha256` 前 8 位。

### 3.3 生效

```bash
# systemd
sudo systemctl restart natter
journalctl -u natter -n 50 --no-pager

# Docker（必须重建容器，restart 不会重读 env_file）
cd deploy/docker && docker compose up -d --force-recreate
docker compose logs --since 2m natter
```

### 3.4 验证

```bash
# 1) 健康与 upstream
curl -sS http://127.0.0.1:5099/health | jq '.status, (.upstreams[] | {source, connected, lastError, sendFailures})'

# 2) 告警检查（应为 0 条）
scripts/ops-health-alert.sh

# 3) 最小 smoke：Discord 里发一条 @bot 消息 + 一条 /ask；钉钉（若部署）发一条消息
# 4) 插件与 provider 状态
sudo -u natter /opt/natter/natter --config-dir /etc/natter/config --state-dir /var/lib/natter plugins list --detail
```

通过口径：`status=healthy`、目标 upstream `connected=true`、`lastError=null`、`sendFailures=0`、smoke 有回复。

### 3.5 吊销旧值 + 记录

- 控制台里删除/重置旧凭据（Discord 的 token reset 本身就是吊销）；
- 记录到运维记录：时间、操作人、影响的 secret 名、指纹前后值、验证结果；
- **不要**在记录里写明文，只写指纹。

---

## 4. 一图流

```text
备份 DB ──→ 记录旧指纹 ──→ 控制台生成新值 ──→ rotate-secret.sh 写入
                                                      │
                          ┌───────────────────────────┘
                          ▼
                  重启（systemd/docker --force-recreate）
                          │
                          ▼
              health + alert-check + smoke ──失败──→ 用 .bak 回滚 → 重启 → 重试
                          │成功
                          ▼
                  控制台吊销旧值 ──→ 记录（只写指纹）
```

---

## 5. 回滚

```bash
# 选最近的备份
ls -lt /etc/natter/live.env.bak.* | head -3

sudo cp -p /etc/natter/live.env.bak.20261121-161500 /etc/natter/live.env
sudo chmod 600 /etc/natter/live.env
sudo systemctl restart natter
scripts/ops-health-alert.sh
```

---

## 6. 常见坑

| 坑 | 后果 | 处理 |
| --- | --- | --- |
| 值里带引号/空格/`$` | env 文件语法错误，插件拿到错值 | `rotate-secret.sh` 直接拒收 |
| 把值贴进 shell 命令行 | 进 `history`、`ps aux` | 只用 stdin / `--from-file` |
| `live.env` 权限 644 | 同机其它用户可读 | 脚本强制 600，`--check` 会提示 |
| Docker `restart` 而非 `up -d --force-recreate` | 容器仍在用旧 env | 必须重建容器 |
| 代理变量写进 `live.env` | secret 与出网配置混杂、轮换时误改 | 代理只放 `/etc/natter/proxy.env`（0640） |
| 多实例 | 新旧 token 同时在线 → 双回复 | 一个 bot token 一个 `serve`；轮换前先确认单实例 |
| 忘记吊销旧值 | 旧 token 仍有效 | 平台侧删除，别只改本地文件 |

---

## 7. 演练（不碰生产 key）

```bash
# 造一个临时 env 文件，跑一遍全流程，确认脚本行为
mkdir -p /tmp/rotate-drill && printf 'DISCORD_BOT_TOKEN=placeholder\nDEEPSEEK_API_KEY=placeholder2\n' > /tmp/rotate-drill/live.env
chmod 600 /tmp/rotate-drill/live.env

scripts/rotate-secret.sh --file /tmp/rotate-drill/live.env --check
printf 'rotated-value' | scripts/rotate-secret.sh --file /tmp/rotate-drill/live.env --key DISCORD_BOT_TOKEN
scripts/rotate-secret.sh --file /tmp/rotate-drill/live.env --check
rm -rf /tmp/rotate-drill
```

本轮实测：`--check` → 轮换 → 备份 0600 → 其它行保持 → 回读指纹一致，全部通过（证据见收口记录）。
