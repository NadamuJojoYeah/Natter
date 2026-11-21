# WebConsole Phase 1 实现与验收记录（2026-11-18）

> **对象**：`plugins/Natter.Plugin.WebConsole`（NPS v1 独立插件）  
> **设计**：[WEBCONSOLE_PLUGIN_DESIGN.md](../WEBCONSOLE_PLUGIN_DESIGN.md)  
> **宿主缺口**：[WEBCONSOLE_HOST_API_GAP.md](../WEBCONSOLE_HOST_API_GAP.md)  
> **使用说明**：[WEBCONSOLE_PLUGIN.md](../WEBCONSOLE_PLUGIN.md)  
> **结论**：Phase 1 只读控制台在真实宿主中完成加载、认证、health 桥接、审计与停止；未修改 `src/`。配置写与命令终端按缺口文档返回 501。

---

## 0. 元数据

| 字段 | 值 |
| --- | --- |
| 测试时间 | 2026-11-18 10:09–10:38（Asia/Shanghai） |
| 基线 | 功能提交 `989062b`；记录提交随本文件归档 |
| 工作区 | 含其他会话的 P4.5/P5 未提交改动，本插件未触碰 |
| 环境 | Arch Linux `arch-x64`、.NET SDK 10.0.114、Microsoft.NETCore.App 10.0.14 |
| 被测 | `plugins/Natter.Plugin.WebConsole`（NPS v1，file plugin） |
| 不测 | 公网反向代理、真实 Discord/DingTalk 上游、24h soak、浏览器 dashboard 自动登录 |

---

## 1. 范围与约束

- 交付独立插件目录 `plugins/Natter.Plugin.WebConsole`，不进入 core；
- 宿主缺少的接口/模型（`admin` kind、`admin.*` contribution、`IAdminDiagnostics`、`IAdminConfigStore`、`IAdminCommandRunner`、日志读取、ASP.NET Core 共享）写入缺口文档，**不修改主项目**；
- 每个实现步骤单独提交，提交格式沿用 `feat:/fix:/test:/docs:` + 中文摘要 + 要点列表。

验证环境：

| 项 | 值 |
| --- | --- |
| OS | Arch Linux（`arch-x64`） |
| .NET SDK | 10.0.114 |
| Runtime | Microsoft.NETCore.App 10.0.14 |
| 宿主 | `src/Natter.Cli/bin/Debug/net10.0/natter.dll` |
| 浏览器 | Firefox headless（仅登录页截图） |

---

## 2. 交付物

```text
plugins/Natter.Plugin.WebConsole/
├── Natter.Plugin.WebConsole.csproj
├── plugin.json                     # kind=tool，cli.command/web.status
├── config.schema.json              # token 只允许 ${secret:WEBCONSOLE_TOKEN}
├── config.sample.json
├── WebConsolePlugin.cs             # INatterPlugin
├── WebConsoleOptions.cs            # 配置 + 校验 + CIDR
├── WebConsoleAuth.cs               # token/session/CSRF/Origin/限速
├── WebConsoleServer.cs             # HttpListener + 静态资源白名单
├── WebConsoleRequestHandler.cs     # 路由/认证/审计
├── WebConsoleDiagnostics.cs        # 快照与 checks
├── WebConsoleHostPort.cs           # /health + IPluginRuntimeDiagnostics 适配
├── WebConsoleConfig.cs             # 脱敏与配置预览
├── WebConsoleAudit.cs              # JSONL 审计
├── WebConsoleRuntime.cs            # 生命周期与 CLI 状态
├── WebConsoleCommands.cs           # natter web status [--json]
└── wwwroot/{index.html,app.js,app.css}

scripts/stage-webconsole.sh         # staging 到 NPS 插件根
tests/Natter.Plugin.WebConsole.Tests/  # 68 个 xUnit + UI smoke
```

`Natter.slnx` 仅登记上述两个新工程；`src/`、`samples/` 无改动（`git diff --stat -- src samples` 为空）。

---

## 3. 构建与单元测试

```text
$ dotnet build plugins/Natter.Plugin.WebConsole/Natter.Plugin.WebConsole.csproj -c Debug
Build succeeded. 0 Warning(s) 0 Error(s)

$ dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -c Debug
Passed! - Failed: 0, Passed: 68, Skipped: 0, Total: 68
```

覆盖点：

| 测试文件 | 覆盖 |
| --- | --- |
| `WebConsoleOptionsTests` | loopback/端口/token 引用/health URL/publicBaseUrl/CIDR/trustedProxies |
| `WebConsoleAuthTests` | token 常量时间校验、session 过期、限速 5 次/5 分钟、Origin、Cookie 属性 |
| `WebConsoleRedactionTests` | 键名脱敏、`${secret:...}` 保留、超长截断、sk-/Bearer 文本脱敏 |
| `WebConsoleRequestHandlerTests` | 401/403/405/429、CSRF、Origin、静态 CSP、配置/命令 501、路径穿越、XFF 信任、审计 |
| `WebConsoleHostPortTests` | /health 映射与缓存、失败降级、`IPluginRuntimeDiagnostics` 映射、Composite 合并 |
| `WebConsoleManifestTests` | manifest 校验、包内/嵌入一致、schema 默认值、非法 token/bind/port |
| `WebConsoleServerTests` | 真实 loopback 监听 + 登录 + 快照、端口冲突、缺失静态资源、CSP |

UI 逻辑 smoke（fake DOM + 固定 snapshot，覆盖渲染路径，不依赖浏览器）：

```text
$ node tests/Natter.Plugin.WebConsole.Tests/ui-smoke.mjs
{
  "result": "ui smoke ok",
  "checks": 5,
  "gaps": 7,
  "statusLine": "health=degraded · server=listening http://127.0.0.1:5090 · host=health-endpoint",
  "configRedacted": true,
  "auditTable": 1
}
```

> 该 smoke 抓到并修复了一个真实缺陷：`renderChecks` 被传入数据数组而非 `<ul>` 元素，且多处混用 `ui.*` 与浏览器 id 全局变量，导致 dashboard 渲染中断。修复后重跑通过。

---

## 4. 真实宿主加载（NPS ALC）

Staging：

```text
$ CONFIGURATION=Debug ./scripts/stage-webconsole.sh artifacts/webconsole-e2e/plugins
artifacts/webconsole-e2e/plugins/natter.plugin.webconsole/
├── plugin.json
├── config.schema.json
├── config.sample.json
├── config.json            # 测试用，port=5187，hostHealthUrl=http://127.0.0.1:5199/health
├── lib/Natter.Plugin.WebConsole.dll
└── wwwroot/...
```

运行（真实 CLI/ALC，未加 `--no-plugins`）：

```bash
WEBCONSOLE_TOKEN=... NATTER_CONFIG_DIR=.../config NATTER_STATE_DIR=.../state \
  dotnet natter.dll --plugin-dir .../plugins web status --json
```

输出（节选）：

```json
{
  "plugin": "natter.plugin.webconsole",
  "version": "0.1.0",
  "hostApi": ">=0.1.0 <1.0.0",
  "kind": "tool",
  "pluginDirectory": ".../plugins/natter.plugin.webconsole",
  "server": { "state": "listening", "url": "http://127.0.0.1:5187", "runtime": "httplistener" },
  "tokenConfigured": true,
  "auditPath": ".../audit/webconsole-audit.jsonl",
  "gaps": [ "host.admin_diagnostics.unavailable", "... 共 7 项" ]
}
```

说明：

- 插件 DLL 只引用共享契约程序集，`PluginLoadContext` 正常解析；
- 未出现 ASP.NET Core 解析失败（未引用）；
- 宿主启动日志：`NatterHost: Natter host started with 3 plugin(s) and 4 lifecycle hook(s)`。

---

## 5. HTTP 端到端矩阵

宿主：`natter serve --duration 90 --health-port 5199`；控制台：`http://127.0.0.1:5187`。

| 请求 | 期望 | 实测 |
| --- | --- | --- |
| `GET /` | 200 + CSP | `200`，`Content-Security-Policy: default-src 'none'...`，`X-Content-Type-Options: nosniff` |
| `GET /api/snapshot`（无 cookie） | 401 | `401 auth.required` |
| `POST /api/session` 错误 token | 401 | `401 auth.invalid_token` |
| 连续 5 次错误后正确 token | 429 | `429` + `Retry-After`（单测覆盖） |
| `POST /api/session` 正确 token | 200 + Strict cookie | `200`，`Set-Cookie: ...; HttpOnly; SameSite=Strict; Max-Age=3600` |
| `GET /api/snapshot`（带 cookie） | 200，health 可用 | `200`，`hostDataSource=health-endpoint` |
| `POST /api/config/profiles` 无 CSRF | 403 | `403 csrf.invalid` |
| 同上带 CSRF、写入关闭 | 403 | `403 config.write_disabled` |
| 同上带 CSRF、写入开启 | 501 | `501 host.admin_config.unavailable`（单测） |
| `POST /api/commands` 开启 | 501 | `501 host.admin_command.unavailable`（单测） |
| `DELETE /api/session` 带 CSRF | 200 + 过期 cookie | `200`，后续 snapshot `401` |
| `GET /api/logs` | host logs 不可用 + 审计 | `host.logs.unavailable` + audit 数组 |

Snapshot 关键字段（实测）：

```text
self:    ok / listening / health-endpoint / uptime 8s
health:  available=true, status=unknown, activatedPlugins=3, providers=2
upstreams: available=true, 0 条（当前宿主未配置 upstream）
profiles: available=true, [tier1-default/deepseek/False, tier2-dingtalk-spark/spark/False]
plugins: available=true, activated=3, source=health-endpoint
logs:    available=false, host.logs.unavailable
gaps:    7 项（admin diagnostics / plugin diagnostics / config / command / logs / kind / ASP.NET Core）
```

---

## 6. 审计证据

`audit/webconsole-audit.jsonl`（节选）：

```json
{"timestamp":"2026-11-18T02:26:43.6199737+00:00","action":"snapshot.view","ip":"127.0.0.1","result":"ok","durationMs":65}
{"timestamp":"2026-11-18T02:26:43.7283558+00:00","action":"csrf.rejected","ip":"127.0.0.1","result":"denied","durationMs":0}
{"timestamp":"2026-11-18T02:26:43.7367588+00:00","action":"config.write","ip":"127.0.0.1","target":"profiles","result":"denied","durationMs":0}
{"timestamp":"2026-11-18T02:26:43.7440307+00:00","action":"config.view","ip":"127.0.0.1","result":"ok","durationMs":0}
{"timestamp":"2026-11-18T02:26:43.7525678+00:00","action":"logs.view","ip":"127.0.0.1","target":"limit=5","result":"ok","durationMs":1}
{"timestamp":"2026-11-18T02:28:04.3293173+00:00","action":"plugin.stop","user":"system","result":"ok","durationMs":0}
```

审计中无 token、无 secret、无 prompt/memory 正文。

---

## 7. UI 证据

登录页（headless Firefox，1200×900）：深色登录卡片、token 输入框、`Natter WebConsole` 标题，说明静态资源与 CSP 正常加载。

本地截图（未提交，git-ignored）：`artifacts/webconsole-e2e/login.png`，1200×900，25 KB。

> 仓库内不提交二进制；需要复核时按 §11 启动后自行截图。

Dashboard 渲染由 `ui-smoke.mjs` 覆盖（见 §3），断言 checks=5、upstreams/profiles/plugins 表格、7 个 gap、配置脱敏、审计表格与日志降级文案。

---

## 8. 安全验证清单

| 项 | 结果 |
| --- | --- |
| 非 loopback bind | 配置校验/激活前失败 `webconsole.bind-not-loopback` |
| 缺 secret | 激活失败 `webconsole.token-missing`（未启动服务器） |
| token 太短 | `webconsole.token-too-short`（<24 字符） |
| 常量时间比较 | SHA-256 + `FixedTimeEquals` |
| 登录限速 | 5 次 / 5 分钟 / 客户端 + 全局 50 次 |
| Origin / Sec-Fetch-Site | 非白名单 Origin、`cross-site` 拒绝 |
| CSRF | 写请求必须带 `X-WebConsole-CSRF`，与会话绑定 |
| Cookie | `HttpOnly; SameSite=Strict`，HTTPS publicBaseUrl 时 `Secure` |
| 路径白名单 | 仅 `/`、`/app.js`、`/app.css`；无任意文件读取 |
| 配置 ID 白名单 | 仅 `profiles`、`plugin:<id>`；`../` 拒绝 |
| secret 泄漏 | 配置预览保留 `${secret:...}`、敏感键 `***`、文本 sk-/Bearer 脱敏 |
| 写/命令 | 关闭时 403，开启但宿主无端口时 501，不提供替代执行路径 |
| 端口冲突 | `webconsole.port-in-use` + 插件 Failed |
| 停止 | `deactivate`/lifetime stop 幂等，审计 `plugin.stop` |

---

## 9. 缺口与降级（与缺口文档对应）

| 缺口 | 现象 | 处理 |
| --- | --- | --- |
| G1/G2 admin kind/contribution | manifest 用 `kind=tool` | 文档登记，UI 仍由 `ActivateAsync` 启动 |
| G3 `IAdminDiagnostics` | 无宿主聚合快照 | `/health` 桥接 + 缺失区块 `available=false` |
| G4 `IPluginRuntimeDiagnostics` | 未注入插件 provider | `/plugins` 仅计数/失败；缺口码 `host.plugin_diagnostics.unavailable` |
| G5 `IAdminConfigStore` | 配置编辑不可用 | 端点 501，UI 只读 |
| G6 `IAdminCommandRunner` | 命令终端不可用 | 端点 501，无子进程兜底 |
| G7 ASP.NET Core | 宿主 runtimeconfig/ALC 不共享 | HttpListener 实现，无新框架依赖 |
| G8 日志读取 | 无 tail API | `/api/logs` 返回缺口 + `/api/audit` 审计 |
| G9 脱敏契约 | 宿主无公开 redactor | 插件内实现 + 单测 |

---

## 10. 已知限制

- 仅 phase 1；配置编辑、命令终端、viewer 角色、多用户、会话持久化未实现；
- `IPluginRuntimeDiagnostics` 明细依赖宿主播接（一行改动，未实施）；
- 宿主日志、prompt/memory 正文不可见（设计约束）；
- `HttpListener` 不支持 HTTP/2、WebSocket、TLS；
- Firefox 截图只覆盖登录页，dashboard 由 fake-DOM smoke 覆盖；
- CI 尚未纳入新增测试工程（需更新 `.github/workflows/ci.yml`，属独立改动）。

---

## 11. 复现命令

```bash
# 1) 构建与单测
dotnet build plugins/Natter.Plugin.WebConsole/Natter.Plugin.WebConsole.csproj -c Debug
dotnet test  tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -c Debug
node tests/Natter.Plugin.WebConsole.Tests/ui-smoke.mjs

# 2) staging
CONFIGURATION=Debug ./scripts/stage-webconsole.sh artifacts/webconsole-e2e/plugins

# 3) 准备 config.json（token 用 secret 引用）并导出 secret
export WEBCONSOLE_TOKEN="$(openssl rand -base64 48)"

# 4) 启动宿主 + health
dotnet src/Natter.Cli/bin/Debug/net10.0/natter.dll \
  --plugin-dir "$PWD/artifacts/webconsole-e2e/plugins" \
  serve --health-port 5199

# 5) 另一个终端
curl -s http://127.0.0.1:5099/health | head -c 300
curl -s -i http://127.0.0.1:5090/ | head -20
```

---

## 12. 下一步（Phase 2/3）

1. 宿主侧：`IAdminConfigStore` + `IAdminCommandRunner` + `IPluginRuntimeDiagnostics` 桥接；
2. 插件侧：config diff/backup/lock/ETag 与命令 allowlist/输出截断；
3. viewer 角色与 session 持久化（可选）；
4. Kestrel 迁移（宿主 ASP.NET Core 共享就绪后）；
5. CI 纳入 `tests/Natter.Plugin.WebConsole.Tests`。


---

## 13. 问题登记

### BUG-001 · dashboard 渲染中断（开发侧已修复）

- **发现时间**：2026-11-18 10:31
- **严重度**：高（登录后白屏）
- **影响**：dashboard 无法显示 checks/upstreams/plugins/config/audit
- **复现**：修复前运行 `node tests/Natter.Plugin.WebConsole.Tests/ui-smoke.mjs`，抛出 `checks.replaceChildren is not a function` / `statusLine is not defined`
- **证据**：smoke 首次执行堆栈；修复后输出 `ui smoke ok`
- **处置**：统一 `ui.*` 引用；`renderChecks` 改为操作 `<ul>` 元素；config 改用 `textContent`
- **回归**：68/68 单测 + smoke 通过

### OBS-001 · 无 upstream 时宿主 health.status=unknown

- **发现时间**：2026-11-18 10:26
- **影响**：控制台 health 卡片显示 `unknown`，checks 仍为 ok
- **说明**：沿用 `RuntimeHealth` 既有语义，非插件缺陷
- **建议**：配置 upstream 或接受 unknown

### OBS-002 · plugins 明细缺失（G4）

- **发现时间**：2026-11-18 10:26
- **影响**：`/api/plugins` 只有计数与失败项，无 graph/contributions/permissions
- **说明**：`IPluginRuntimeDiagnostics` 未注入插件 provider，缺口文档 G4 已登记
- **建议**：宿主播接后插件无需改 UI

### ENV-001 · 工作区并行改动

- **发现时间**：2026-11-18 10:09
- **影响**：提交需按路径挑选，避免带入其他会话的 P4.5/P5 改动
- **处置**：每步只 `git add` 本插件/文档路径

---

## 14. 变更记录

| 时间 | 变更 |
| --- | --- |
| 10:09–10:20 | 读设计/NPS/CLI health 文档，锁定缺口与数据源 |
| 10:20–10:28 | 插件骨架、认证、服务器、UI、单测 68/68 |
| 10:28–10:33 | 真实宿主加载、serve + curl 矩阵、审计、登录页截图、UI smoke |
| 10:33–10:40 | 文档、slnx、staging 脚本、分步提交 |
