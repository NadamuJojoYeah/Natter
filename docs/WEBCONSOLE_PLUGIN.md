# Natter WebConsole 插件使用说明

> **状态**：Phase 1（只读诊断） · 2026-11-18  
> **定位**：面向运维的 loopback Web 控制台；不修改 core，不默认启用。  
> **设计**：[WEBCONSOLE_PLUGIN_DESIGN.md](WEBCONSOLE_PLUGIN_DESIGN.md)  
> **宿主契约缺口**：[WEBCONSOLE_HOST_API_GAP.md](WEBCONSOLE_HOST_API_GAP.md)

---

## 1. 这是什么

`Natter.Plugin.WebConsole` 是一个 NPS v1 插件，在 `127.0.0.1:5090` 提供一个需要 token 登录的只读诊断页：

- health、upstreams、providers（profile 维度）、plugins 计数与失败原因；
- 插件自身配置的脱敏预览；
- 审计流水（登录、快照、拒绝、配置写入尝试）；
- 插件清单、host API、服务器状态。

Phase 2（配置编辑）与 Phase 3（受限命令）的 HTTP 端点在宿主管理 API 缺失时返回 `501 admin_api_unavailable`，UI 会显示“未启用”，不会静默失败。

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
  "sessionMinutes": 60,
  "maxLogLines": 500,
  "trustedProxies": ["127.0.0.1"]
}
```

要点：

- `token` 只接受 `${secret:WEBCONSOLE_TOKEN}`；secret 由宿主 secret provider 提供（环境变量或密钥库）；
- 没有 `WEBCONSOLE_TOKEN` → 插件激活失败（`webconsole.token-missing`），不会启动无认证控制台；
- `bind` 必须是 loopback（`127.0.0.1`、`::1`、`localhost`）；其他值 → `webconsole.bind-not-loopback`；
- `hostHealthUrl` 必须是 loopback URL；指向 `natter serve --health-port <port>` 的 `/health`；
- `publicBaseUrl` 仅在通过反向代理暴露时设置（用于 cookie `Secure`、Origin 校验）。

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
| GET | `/api/config` | 插件配置脱敏预览 |
| POST | `/api/config/{id}/validate` | 配置文本校验（无写入） |
| POST | `/api/config/{id}` | 保存：无效 400；有效但宿主 store 缺失 501 |
| POST | `/api/commands/validate` | 命令 allowlist 判定（无执行） |
| POST | `/api/commands` | 执行：拒绝 403；宿主 runner 缺失 501 |

`id` 只接受 `profiles` 或 `plugin:<plugin-id>`；所有 POST/DELETE 需要 `X-WebConsole-CSRF`。

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
| 插件 Failed，缺 secret | `webconsole.token-missing` | 配置 `WEBCONSOLE_TOKEN` |
| 插件 Failed，端口占用 | `webconsole.port-in-use` | 改 `port` 或停掉占用进程 |
| 插件 Failed，绑定非法 | `webconsole.bind-not-loopback` | 改回 `127.0.0.1` |
| 插件 Failed，静态资源缺失 | `webconsole.static-assets-missing` | 重新 staging，确认 `wwwroot/` 完整 |
| 页面 health 灰显 | `host.health.unavailable` | 启动 `serve --health-port` 并核对 `hostHealthUrl` |
| plugins 明细缺失 | `host.plugin_diagnostics.unavailable` | 宿主播接 `IPluginRuntimeDiagnostics`（缺口 G4） |
| `/api/logs` 501 | `host.logs.unavailable` | 缺口 G8，暂用 `/api/audit` |
| 配置保存 501 | `host.admin_config.unavailable` | 缺口 G5 |
| 命令执行 501 | `host.admin_command.unavailable` | 缺口 G6 |

---

## 10. 已知限制

- 无 ASP.NET Core：Phase 1 用 `HttpListener`，不支持 HTTP/2/WebSocket；
- 无宿主日志读取端口：`/logs` 只展示审计；
- 无插件明细：`IPluginRuntimeDiagnostics` 未注入插件 provider；
- 配置编辑只有校验/ETag/diff（`validate` 端点）；保存与备份等待 `IAdminConfigStore`；
- 命令终端只有 allowlist/策略判定（`validate` 端点）；执行与超时等待 `IAdminCommandRunner`；
- `external` 模式只做探测，不代理另一个实例的请求；
- 单 token / 单角色（admin）；viewer 角色与多用户未实现；
- 会话只存内存，插件重启后需要重新登录。
