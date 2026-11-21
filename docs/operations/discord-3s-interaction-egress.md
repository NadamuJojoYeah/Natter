# Discord 3 秒交互预算与出网出口选择

> **适用**：Natter 的 Discord slash 命令（`/ask` `/remember` `/memory` `/forget` `/reset`）
> **现象**：命令偶尔/持续显示「该应用程序未响应」，普通 @bot 消息却正常
> **关联**：`docs/operations/2026-11-17-p4-5-final-retest-run.md` §9、`docs/OPERATIONS_RUNBOOK.md` §3.4
> **工具**：`scripts/discord-egress-probe.sh`、`scripts/ops-health-alert.sh`

---

## 1. 结论先说

- Discord 要求 slash interaction 在 **3 秒** 内被 ack（`DeferAsync` 或 `Respond`）。
- Natter 收到事件后第一件事就是 `DeferAsync()`，但 **defer 本身是一次 HTTPS REST POST**，所以「链路 RTT」直接吃掉 3 秒预算。
- 判定线（`scripts/discord-egress-probe.sh` 的判定口径）：

| p95 RTT | 判断 | 现象 |
| --- | --- | --- |
| < 1000 ms | 充裕 | slash 稳定可用 |
| 1000–2000 ms | 勉强 | 偶发超时，daemon 抖动就翻车 |
| > 2000 ms | 不合格 | `/ask` 大面积「未响应」 |

- 超时后 `/health` 的 Discord upstream 会留下明确证据：

```json
{"lastError":"interaction_defer_failed: Cannot defer an interaction after 3 seconds!"}
```

---

## 2. 实测数据（2026-11-17 / 2026-11-21）

| 时间 | 出口 | 结果 |
| --- | --- | --- |
| 11-17 22:23 | 代理 `127.0.0.1:7890` | REST 2/6 成功，成功也 5.1–5.4 s；失败 8.0 s 超时 → slash 大面积失败 |
| 11-17 22:42 | 改直连 | 5/5 成功，293–613 ms → slash 恢复 |
| 11-17 22:52 | 直连 | `lastError=null`，`reconnectCount=0` |
| 11-21 16:15 | 直连（宿主） | `discord.com/api/v10/gateway` 245–303 ms；`gateway.discord.gg` 460–931 ms；6/6 成功 → PASS |

复现命令：

```bash
./scripts/discord-egress-probe.sh -n 6 -i 2          # 当前环境
./scripts/discord-egress-probe.sh --proxy http://127.0.0.1:7890 -n 6
./scripts/discord-egress-probe.sh --direct -n 6
```

探针不需要 bot token：它只量 DNS + TLS + HTTP 往返，反映 defer 能拿到的时间预算。

---

## 3. 出口方案（按推荐顺序）

### 3.1 直连（首选）

```text
# /etc/natter/proxy.env 只保留这一行，不要导出 HTTP(S)_PROXY
NO_PROXY=127.0.0.1,localhost,::1
```

- 实测 245–931 ms，满足预算；
- 前提：机器能直达 Discord（不经高延迟代理）。

### 3.2 低延迟代理 / 分流（次选）

- 代理节点 RTT 必须 < 1 s，否则 3 s 预算必破；
- 只把 Discord 域名分流到该节点，其它流量直连；
- 代理变量只放进程环境（`/etc/natter/proxy.env` 0640），不要进 `live.env`；
- 代理凭据会被日志脱敏，但仍不要贴进 issue。

### 3.3 容器部署的特殊性（**新增，本轮实测**）

本机 `/etc/hosts` 被本地加速器（Steamcommunity302 / Watt Toolkit 类工具）接管：
**166 条 discord 相关域名指向 `127.0.0.1:443`**，宿主用该工具的自签名 CA 信任本地证书。容器里会出现三层差异：

| 层 | 宿主 | 容器（bridge，默认） | 表现 |
| --- | --- | --- | --- |
| DNS | `/etc/hosts` 覆盖 → 127.0.0.1 | Docker 自带 `/etc/hosts`，DNS 走 `192.168.8.1` | 解析到伪造 IPv6 `2001::ade7:c6b` |
| IPv6 | 可达（2 ms） | bridge 无 IPv6 路由 | TCP 443 **失败 42–47 ms** |
| CA | 系统信任 `Steamcommunity302` | 只信公共 CA | 连上本地 443 后 TLS 失败 |

实测三种容器形态：

| 形态 | 结果 |
| --- | --- |
| bridge（默认） | `ws_connect` 90 s 超时，`lastDisconnectedError=none` |
| bridge + `DOTNET_SYSTEM_NET_DISABLEIPV6=1` | `lastDisconnectedError=Connection refused (discord.com:443)`（hosts 里 discord.com 也指向 127.0.0.1） |
| `--network host` + 挂宿主 `/etc/hosts` + 合并 CA | **`plugins=4/0`、`upstream discord connected=True`、`status=healthy`** |

生产建议：

1. 容器部署 **不要**依赖宿主 hosts 劫持：让容器内 DNS 解析真实 Discord（或给 `HTTP(S)_PROXY` 指到可用代理）；
2. 若必须复用宿主的分流器（本机这种场景），compose 需要显式：

```yaml
    network_mode: host                 # 复用宿主的 127.0.0.1:443 分流
    volumes:
      - /etc/hosts:/etc/hosts:ro       # 复用宿主域名映射
      - ./ca/ca-certificates.crt:/etc/ssl/certs/ca-certificates.crt:ro   # 信任本地自签名 CA
```

CA 合并（公共 CA + 本地 302 CA），幂等：

```bash
mkdir -p ca
cat /etc/ssl/certs/ca-certificates.crt /etc/ca-certificates/trust-source/anchors/steamcommunityCA.crt \
  > ca/ca-certificates.crt
```

> 这是**环境适配**，不是镜像要求。默认 compose 保持 bridge + 公共 CA，避免把私有 CA 打进镜像。

---

## 4. 告警（比用户截图更早发现）

```bash
# 推荐 cron：每 5 分钟，退出码 1 表示有告警
*/5 * * * * /opt/natter/scripts/ops-health-alert.sh --state /var/lib/natter/log/alert-state.json
```

会命中的关键规则：

- `interaction-ack-timeout`：`lastError` 含 `interaction_defer_failed` → **critical**
- `upstream-disconnected` / `disconnect`（带 `lastDisconnectCode`/`Error`） / `disconnect-without-reason`（新 host 无消息） / `disconnect-without-reason-legacy`（旧载荷） / `reconnect-counter-reset`
- `send-failures`：`sendFailures` 增长 → critical

---

## 5. 一句话

**slash 是否可用，本质是「这台机器到 Discord 的 RTT 是否远小于 1 秒」；代理不是问题，慢代理才是问题；容器部署要么直连 Discord，要么显式复用宿主的分流器（hosts + CA）。**
