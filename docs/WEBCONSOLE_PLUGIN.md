# Natter WebConsole 插件使用说明

> **状态**：Phase 1（只读诊断）+ Phase 2（配置编辑）+ Phase 3（受限命令）· 2026-11-21
> **定位**：面向运维的 loopback Web 控制台；不修改 core，不默认启用。
> **设计**：[WEBCONSOLE_PLUGIN_DESIGN.md](WEBCONSOLE_PLUGIN_DESIGN.md)
> **宿主契约缺口**：[WEBCONSOLE_HOST_API_GAP.md](WEBCONSOLE_HOST_API_GAP.md)
> **验收测试计划**：[WebConsole 插件验收测试计划](testing/2026-11-18-webconsole-acceptance.md)

---

## 1. 这是什么

`Natter.Plugin.WebConsole` 是一个 NPS v1 插件，在 `127.0.0.1:5090` 提供一个需要 token 登录的只读诊断页：

- health、upstreams、providers（profile 维度）、plugins 计数与失败原因；
- 插件自身配置的脱敏预览；
- 审计流水（登录、快照、拒绝、配置写入尝试）；
- 插件清单、host API、服务器状态。

Phase 2（配置编辑）与 Phase 3（受限命令）已接入宿主管理面（`IAdminConfigStore` / `IAdminCommandRunner` / `IAdminLogReader`，见 [缺口文档](WEBCONSOLE_HOST_API_GAP.md)）。宿主契约缺失（没有 `admin.*` 权限、宿主关闭管理面或旧版本宿主）时端点仍返回 `501 host.admin_config.unavailable` / `501 host.admin_command.unavailable`，UI 显示“未启用”，不会静默失败。

---

## 2. 构建

```bash
dotnet build plugins/Natter.Plugin.WebConsole/Natter.Plugin.WebConsole.csproj -c Release
```

产物在 `plugins/Natter.Plugin.WebConsole/bin/Release/net10.0/`：

```text
Natter.Plugin.WebConsole.dll
plugin.json
config.schema.json
config.sample.json
wwwroot/
```

插件只引用 `Natter.Plugin.Abstractions` 与 `Natter.Cli.Abstractions`；**不需要 ASP.NET Core**，可直接被宿主 ALC 加载。

---

## 3. staging 到宿主

宿主的插件根目录由 `NPS` 扫描（默认按宿主配置；示例部署用 `artifacts/live/plugins`）。目录布局必须是：

```text
plugins/natter.plugin.webconsole/
├── plugin.json
├── config.schema.json
├── config.sample.json
├── config.json          # 可选；也可放在宿主的 plugin-config 目录
├── lib/
│   └── Natter.Plugin.WebConsole.dll
└── wwwroot/
    ├── index.html
    ├── app.js
    └── app.css
```

本仓库提供脚本：

```bash
./scripts/stage-webconsole.sh <plugin-root>
# 例：./scripts/stage-webconsole.sh artifacts/live/plugins
```

脚本只做复制，不改宿主配置。

可复现 E2E（一次性 token、临时 state、loopback 端口）：

```bash
./scripts/webconsole-e2e.sh
```

---

## 4. 配置

```json
{
  "bind": "127.0.0.1",
  "port": 5090,
  "hostHealthUrl": "http://127.0.0.1:5099/health",
  "token": "${secret:WEBCONSOLE_TOKEN}",
  "readOnly": true,
  "allowConfigWrite": false,
  "allowCommandConsole": false,
  "commandAllowlist": [],
  "commandTimeoutSeconds": 10,
  "sessionMinutes": 60,
  "maxLogLines": 500,
  "trustedProxies": ["127.0.0.1"]
}
```

要点：

- `token` 只接受 `${secret:WEBCONSOLE_TOKEN}`；secret 由宿主 secret provider 提供（环境变量或密钥库）；
- 没有 `WEBCONSOLE_TOKEN` → 插件激活失败（实际可能是 `phase=config code=missing-secret`，与 `webconsole.token-missing` 语义等价），不会启动无认证控制台；
- `bind` 必须是 loopback（`127.0.0.1`、`::1`、`localhost`）；其他值 → `webconsole.bind-not-loopback`；
- `hostHealthUrl` 必须是 loopback URL；指向 `natter serve --health-port <port>` 的 `/health`；
- `publicBaseUrl` 仅在通过反向代理暴露时设置（用于 cookie `Secure`、Origin 校验）；
- `commandTimeoutSeconds`（1–10，默认 10）是插件侧命令预算，必须小于 HTTP 层 15s，保证超时能返回 `504 command:timeout` 而不是被传输层掐断。

### 4.1 宿主权限（manifest）

```json
"permissions": ["network", "secrets:WEBCONSOLE_TOKEN", "cli.commands",
                "admin.read", "admin.config.write", "admin.command.execute"]
```

- `admin.read` → 插件运行视图（plugins 明细）+ 宿主日志 tail；
- `admin.config.write` → `IAdminConfigStore`；
- `admin.command.execute` → `IAdminCommandRunner`；

本地/受信任插件默认自动授予；宿主用 `ForceDeniedPermissions` 可单项回收。缺哪个，对应面板就显示 gap 码，不会半死不活。

生成 token：

```bash
openssl rand -base64 48
```

把值放进宿主的 secret 来源，不要写进 `config.json`、Caddy/Nginx 配置或 shell history。

---

## 5. 运行

1. 启动宿主并开启 health endpoint：

   ```bash
   natter serve --health-port 5099
   ```

2. 确认 health 可读：

   ```bash
   curl -s http://127.0.0.1:5099/health | head -c 400
   ```

3. 启动/重启宿主加载插件，日志应出现：

   ```text
   Natter.Plugin.natter.plugin.webconsole: webconsole listening on http://127.0.0.1:5090/
   ```

4. 浏览器打开 `http://127.0.0.1:5090/`，输入 token。

CLI 自检（不需要 HTTP）：

```bash
natter web status
natter web status --json
```

> 若控制台端口已被正在运行的宿主占用，插件进入 `external` 模式：
> 不重复绑定端口，只探测 `GET /api/session` 确认对端是本插件，然后保持 `web status` 可用。
> 日志会输出 `running in external mode`，`server.state=external`。

---

## 6. 反向代理

推荐拓扑：

```text
Internet/内网
  → Caddy/Nginx（TLS、访问控制）
  → 127.0.0.1:5090
```

Caddy 示例：

```caddyfile
natter-admin.example.com {
    reverse_proxy 127.0.0.1:5090
}
```

要求：

- 不要把 5090 直接暴露到公网；
- 只信任 `trustedProxies` 里的 `X-Forwarded-For`；
- 设置 `publicBaseUrl` 为外部 URL，cookie 会加 `Secure`；
- 代理层再加一层认证（mTLS / SSO / basic auth）属于纵深防御，不替代插件 token。

---

## 7. HTTP 端点

| 方法 | 路径 | 说明 |
| --- | --- | --- |
| GET | `/` `/app.js` `/app.css` | 静态白名单；支持 HEAD |
| GET | `/api/session` | 登录状态 + CSRF |
| POST | `/api/session` | 登录（token） |
| DELETE | `/api/session` | 登出（CSRF） |
| GET | `/api/snapshot` | 聚合快照（health/plugins/upstreams/profiles/config/audit） |
| GET | `/api/health` `/api/plugins` `/api/upstreams` `/api/profiles` | 单区块 |
| GET | `/api/logs` | 宿主日志（缺口）+ 审计 |
| GET | `/api/audit` | 审计 tail |
| GET | `/api/logs` | 宿主日志 tail（`IAdminLogReader`）+ 插件审计 |
| GET | `/api/config` | 插件配置脱敏预览 + 可编辑文档列表 + 宿主管理面可用性 |
| GET | `/api/admin` | 管理面状态（config store / runner / log reader / allowlist / 超时） |
| GET | `/api/admin/documents` | 可编辑文档列表（宿主 `ListAsync`，缺失则 501） |
| GET | `/api/config/{id}` | 读取文档：`{id,exists,eTag,lastModified,content,source,reloadHint}` |
| POST | `/api/config/{id}/validate` | 校验：宿主可用时含 schema 校验与 diff；否则插件策略 |
| POST | `/api/config/{id}` | 保存 `{content,etag}` → 200 / 400 / 403 / 409 / 413 / 501 / 502 |
| GET | `/api/config/{id}/undo` | 会话内可恢复快照列表 |
| POST | `/api/config/{id}/rollback` | 回滚到快照 `{etag,undoId}`（走宿主 `SaveAsync`） |
| GET | `/api/commands` | 命令台状态（enabled/allowlist/超时/runner 可用性） |
| POST | `/api/commands/validate` | 命令 allowlist 判定（无执行） |
| POST | `/api/commands` | 执行：403 拒绝；501 无 runner；504 超时；502 宿主异常 |

`id` 只接受 `profiles` 或 `plugin:<plugin-id>`；所有 POST/DELETE 需要 `X-WebConsole-CSRF`。

### 7.1 Phase 2/3 错误码

| 状态 | 码 | 含义 / 处理 |
| --- | --- | --- |
| 409 | `config.conflict` | ETag 过期；`detail.conflictETag` 是当前值，重新加载后再存 |
| 409 | `config.locked` | 宿主同一文档正在被写，稍后重试 |
| 409 | `config.symlink` | 目标是符号链接，宿主拒绝覆盖 |
| 409 | `config.undo_empty` | 没有会话内快照；用展示的宿主备份路径手动恢复 |
| 400 | `config.invalid` | 插件策略或宿主 schema 拒绝；`detail.issues[]` 带 JSON Pointer |
| 502 | `host.admin_config.failed` | 宿主备份/写入失败（`config.backup_failed` / `config.write_failed`） |
| 501 | `host.admin_config.unavailable` | 没有 store（权限/宿主版本/管理面关闭） |
| 403 | `command.not_allowed` | 插件或宿主 allowlist 拒绝 |
| 409 | `command.busy` | 宿主正在执行另一条管理命令 |
| 504 | `command.timeout` | 超时；`result` 仍带部分 stdout/stderr |
| 502 | `host.admin_command.failed` | runner 抛异常（消息已脱敏） |

### 7.2 配置编辑语义

- 宿主 schema 校验权威（`profiles` 校验结构与 provider 引用，`plugin:<id>` 校验插件 `config.schema.json`）；插件策略先跑，保证无宿主时仍是可解释的 400；
- `etag` 只有**文档不存在**时才可为 `null`（创建）；否则 409；
- 保存响应带 `backupPath`（宿主备份，`config/.backup/<时间戳>/<file>`）与 `reloadHint`/`restartRequired`；
- **回滚**是插件会话内存 undo（每文档 ≤8 条、全局 ≤32 条、30 分钟 TTL），不是宿主备份回读；重启插件后失效，宿主备份需手动恢复；
- 插件任何情况下都不直接读写配置文件。

配置校验会拒绝明文 secret（`sk-*`、`Bearer`、PEM、`"token":"明文"`），只允许 `${secret:NAME}` 引用；命令策略要求命令精确匹配 `commandAllowlist`，参数 ≤8 个、每个 ≤128 字符。

---

## 8. 安全边界

- 默认只绑定 loopback；
- token 只从 secret provider 读取，日志/HTML/JSON/审计都不出现明文；
- 登录失败限速：5 次 / 5 分钟 / 客户端，超过返回 429；
- 所有写请求要求 `X-WebConsole-CSRF` 且 Origin 合法；
- 静态资源白名单 + CSP `default-src 'none'`，无任意文件读取；
- 不执行 shell、不拼命令行、不启动子进程；
- 审计只记录摘要，不记录 token、secret、prompt/memory 正文或完整命令输出；
- 审计目录 0700、文件 0600（Unix，best-effort）；只读挂载时 health 的 `audit` 检查变为 `error`；
- 反向代理下只信任 `trustedProxies` 的 `X-Forwarded-For` / `X-Forwarded-Proto`；后者为 `https` 时会话 cookie 加 `Secure`；
- Phase 2/3 的校验端点不写文件、不执行命令；真正写入/执行在宿主 API 缺失时 501，不提供“临时替代路径”。

---

## 9. 故障排查

| 现象 | 错误码 | 处理 |
| --- | --- | --- |
| 插件 Failed，缺 secret | `missing-secret`（等价 `webconsole.token-missing`） | 配置 `WEBCONSOLE_TOKEN` |
| 端口占用 | 可能自动进入 external 模式并记录 warning；以启动日志/`web status` 为准 | 改 `port` 或停掉占用进程 |
| 插件 Failed，绑定非法 | `webconsole.bind-not-loopback` | 改回 `127.0.0.1` |
| 插件 Failed，静态资源缺失 | `webconsole.static-assets-missing` | 重新 staging，确认 `wwwroot/` 完整 |
| 页面 health 灰显 | `host.health.unavailable` | 启动 `serve --health-port` 并核对 `hostHealthUrl` |
| plugins 明细缺失 | `host.plugin_diagnostics.unavailable` | 宿主播接 `IPluginRuntimeDiagnostics`（缺口 G4） |
| `/api/logs` 501 | `host.logs.unavailable` | 缺口 G8，暂用 `/api/audit` |
| 配置保存 501 | `host.admin_config.unavailable` | 检查 manifest 是否声明 `admin.config.write`、宿主是否关闭管理面 |
| 命令执行 501 | `host.admin_command.unavailable` | 检查 `admin.command.execute` 权限 |
| 配置保存 409 | `config.conflict` | 重新加载文档（新 ETag）再保存 |
| 命令 504 | `command.timeout` | 调小 `commandTimeoutSeconds` 或换命令；部分输出仍会返回 |

---

## 10. 已知限制

- `readOnly=true` 时即使 `allowCommandConsole=true`，命令控制台仍保持禁用；需要同时 `readOnly=false`。
- 方法不允许通常返回 403（配置/策略层），不是 405。

- 无 ASP.NET Core：Phase 1 用 `HttpListener`，不支持 HTTP/2/WebSocket；
- 无宿主日志读取端口：`/logs` 只展示审计；
- 无插件明细：`IPluginRuntimeDiagnostics` 未注入插件 provider；
- 配置回滚依赖插件内存 undo（宿主无 backup 回读契约）；插件重启后只能按备份路径手动恢复；
- `profiles` 保存后宿主返回 `restart-host`（当前不会热重载 profile store）；
- 命令执行依赖宿主在进程内解析 CLI 命令（`admin.command.execute` 触发的命令表提前固化缺陷已由 `0f3d61c` 修复，见缺口文档 OBS-003）；
- `external` 模式只做探测，不代理另一个实例的请求；
- 单 token / 单角色（admin）；viewer 角色与多用户未实现；
- 会话只存内存，插件重启后需要重新登录。
