# Natter WebConsole Plugin Design

> **状态**：Accepted design · Phase 1 delivered；Phase 2/3 插件侧校验/策略已就绪（2026-11-18）  
> **日期**：2026-11-17（设计）/ 2026-11-18（实现决策）  
> **定位**：Natter 的独立运维/调试插件，提供 Web 诊断、配置编辑和受限命令终端；不进入 core，不默认启用。  
> **关联**：[NPS v1](PLUGIN_SPEC.md)、[运维手册](OPERATIONS_RUNBOOK.md)、[Harness 架构](HARNESS_ARCHITECTURE.md)、[P5 Agent Runtime](P5_AGENT_TASK_RUNTIME.md)、[宿主契约缺口与适配](WEBCONSOLE_HOST_API_GAP.md)、[插件使用说明](WEBCONSOLE_PLUGIN.md)

---

## 0. 决策摘要

1. WebConsole 是**独立插件**，不是 core 功能。
2. 分三阶段：
   - **Phase 1**：只读诊断面板；
   - **Phase 2**：配置编辑；
   - **Phase 3**：受限命令终端。
3. **OS shell 不放进插件进程**；需要交互式 shell 时用独立 sidecar（ttyd/Wetty/SSH）。
4. 默认只绑定 `127.0.0.1`，通过 SSH/Caddy/Nginx 访问；不默认暴露公网。
5. 必须有认证；不允许默认无密码。
6. 配置编辑必须 schema 校验、备份、diff、写锁、重启提示。
7. 不把 secret 值返回给浏览器；只显示 `${secret:...}` 引用和“已提供/未提供”。
8. 插件只能通过宿主公开的 admin API 读取状态，不拿根 `IServiceProvider`。
9. Android 不支持该插件；platforms 只列 windows/linux/macos。
10. WebConsole 不阻塞 P5；作为独立插件/side project 推进。

---

## 1. 目标与非目标

### 1.1 目标

- 本地/内网查看 Natter 运行状态；
- 查看插件、profile、upstream、provider、health；
- 查看脱敏日志；
- 安全地编辑 profiles/插件 config；
- 执行受限的 Natter CLI 命令；
- 为运维提供比 `journalctl + sqlite3` 更友好的界面。

### 1.2 非目标

- 公网无认证访问；
- 任意 OS shell；
- 多租户管理后台；
- 替代 Prometheus/Grafana；
- 修改核心代码；
- Android；
- 插件市场；
- 任意文件浏览器；
- 任意 SQL 执行器。

---

## 2. 形态

```text
Browser
   │ HTTPS / SSH tunnel
   ▼
Caddy / Nginx / SSH
   │ loopback
   ▼
Natter.Plugin.WebConsole
   ├── Auth
   ├── Read-only diagnostics
   ├── Config editor
   ├── Restricted command console
   └── Static UI
        │
        ▼
Host admin APIs
   ├── IAdminDiagnostics
   ├── IAdminConfigStore
   ├── IAdminCommandRunner
   └── IPluginCatalog
```

插件不直接读宿主根 DI，只消费宿主显式提供的 admin 契约。

---

## 3. 工程结构

```text
plugins/Natter.Plugin.WebConsole/
├── Natter.Plugin.WebConsole.csproj
├── plugin.json
├── config.schema.json
├── config.sample.json
├── WebConsolePlugin.cs
├── WebConsoleOptions.cs
├── WebConsoleServer.cs
├── WebConsoleRequestHandler.cs
├── WebConsoleAuth.cs
├── WebConsoleDiagnostics.cs
├── WebConsoleHostPort.cs
├── WebConsoleConfig.cs
├── WebConsoleCommands.cs
├── WebConsoleAudit.cs
└── wwwroot/
    ├── index.html
    ├── app.js
    └── app.css
```

csproj（**实际实现**）：

- `net10.0`，**不引用** `Microsoft.AspNetCore.App`（原因见 §10 与缺口 G7）；
- `ProjectReference Natter.Plugin.Abstractions` / `Natter.Cli.Abstractions`；
- `plugin.json` 同时作为 `EmbeddedResource`（测试可校验）和输出文件；
- `config.schema.json`、`config.sample.json`、`wwwroot/**` 复制到输出目录，便于 staging。

> **实现偏差**：Kestrel 改为 BCL `HttpListener`。这不是“简化设计”，而是宿主 ALC/runtimeconfig 当前无法提供 ASP.NET Core 共享框架的必然结果；迁移路径已写入 [缺口文档 G7](WEBCONSOLE_HOST_API_GAP.md#6-g7aspnet-core--alc-实测结论)。

---

## 4. Manifest

```json
{
  "schemaVersion": 1,
  "id": "natter.plugin.webconsole",
  "name": "Natter WebConsole",
  "version": "0.1.0",
  "kind": "admin",
  "hostApi": ">=0.1.0 <1.0.0",

  "entry": {
    "assembly": "lib/Natter.Plugin.WebConsole.dll",
    "type": "Natter.Plugin.WebConsole.WebConsolePlugin"
  },

  "permissions": [
    "network",
    "secrets:WEBCONSOLE_TOKEN",
    "storage",
    "cli.commands"
  ],

  "secrets": [
    { "name": "WEBCONSOLE_TOKEN", "required": true }
  ],

  "configSchema": "config.schema.json",

  "contributions": [
    { "type": "cli.command", "id": "web.status" }
  ],

  "platforms": ["windows", "linux", "macos"]
}
```

未来可新增：

```text
admin.ui
admin.api
admin.command
```

第一版可以只启动 loopback server + `web status` CLI，不要求 core 先支持 `admin.ui`。

> **实际 manifest 偏差**：宿主 `PluginKind` 没有 `admin`，`admin.*` contribution 也未进入 v1 词表，因此 Phase 1 使用 `"kind": "tool"` 且只声明 `cli.command/web.status`。登记见 [缺口 G1/G2](WEBCONSOLE_HOST_API_GAP.md#3-g1g2manifest-与管理面贡献点)。

---

## 5. 配置

```json
{
  "bind": "127.0.0.1",
  "port": 5090,
  "publicBaseUrl": "https://natter-admin.example.com",
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

字段：

| 字段 | 说明 |
| --- | --- |
| `bind` | 默认 `127.0.0.1`；非 loopback 直接激活失败（v1） |
| `port` | 默认 5090 |
| `publicBaseUrl` | 反向代理外部 URL，用于 cookie/CSRF/Origin |
| `hostHealthUrl` | **Phase 1 数据源**：`natter serve --health-port` 的 `/health`，必须是 loopback URL |
| `token` | 只允许 `${secret:WEBCONSOLE_TOKEN}`（schema enum + 运行时校验） |
| `readOnly` | 强制只读 |
| `allowConfigWrite` | 允许 Phase 2 配置写入（宿主端口缺失时仍 501） |
| `allowCommandConsole` | 允许 Phase 3 受限命令（宿主端口缺失时仍 501） |
| `commandAllowlist` | 只允许白名单命令 |
| `trustedProxies` | 反向代理来源，支持 CIDR |
| `sessionMinutes` | 登录会话 |
| `maxLogLines` | 日志窗口 |

> `hostHealthUrl` 是**实现新增字段**，不属于原设计。它是 G3/G4 缺口下唯一不改宿主的真实数据源；`/health` 无认证，因此只允许 loopback 并强制超时/缓存。

---

## 6. 认证与安全

### 6.1 默认规则

- 只绑定 loopback；
- 公网必须经 Caddy/Nginx/SSH 隧道；
- 必须配置 token；
- 无 token 不允许启动；
- 登录失败限速；
- CSRF/Origin 校验；
- `SameSite=Strict` cookie；
- 不允许把 token 放 URL query；
- 不在日志/HTML/JSON 返回 secret；
- 审计所有写操作和命令。

### 6.2 角色

P5 第一版可以只有：

```text
viewer  -> read-only
admin   -> config write / command
```

不要把角色系统做成复杂 RBAC；先两个角色够用。

### 6.3 反向代理

推荐：

```text
Internet/内网
  → Caddy/Nginx TLS
  → 127.0.0.1:5090
```

要求：

- 只信任 `trustedProxies` 的 `X-Forwarded-*`；
- 不直接暴露 5090；
- 不把 `WEBCONSOLE_TOKEN` 写进 Caddy 配置。

---

## 7. Phase 1：只读诊断

### 7.1 页面

```text
/                 Dashboard
/health           health JSON / 图表
/plugins          plugin 状态、failed 原因、contributions
/upstreams        connected/lastConnectedAt/lastDisconnectAt/reconnectCount/lastError
/profiles         profile 匹配、provider、prompt/context/memory
/logs             脱敏 tail
/config           只读预览（secret 显示引用，不显示值）
```

### 7.2 Host API

需要新增：

```csharp
public interface IAdminDiagnostics
{
    ValueTask<AdminSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<AdminLogEntry>> GetLogsAsync(int limit, CancellationToken ct = default);
}
```

`AdminSnapshot` 包含：

- health；
- plugins；
- upstreams；
- profiles；
- providers；
- counts；
- task/memory 统计（P5/P3）。

插件只能拿到脱敏后的 snapshot。

> **Phase 1 实际数据源**：`IAdminDiagnostics` 尚不存在，插件用 `IWebConsoleHostPort` 组合两个现有来源：
>
> 1. `natter serve --health-port` 的 `/health`（health/upstreams/providers/plugins 计数）；
> 2. 插件 provider 中的 `IPluginRuntimeDiagnostics`（若宿主桥接，见缺口 G4）。
>
> 都不可用时区块返回 `available:false` + `gapCode`，不伪造数据。契约与降级表见 [WEBCONSOLE_HOST_API_GAP.md](WEBCONSOLE_HOST_API_GAP.md)。

### 7.3 日志

- 只返回最近 N 行；
- 先脱敏；
- 不返回 prompt/memory 正文；
- 不返回 secret/代理凭据；
- 支持按 level/plugin 过滤。

---

## 8. Phase 2：配置编辑

### 8.1 可编辑文件

- `profiles.json`；
- 插件 `config.json`；
- 不允许编辑 secret 文件内容；
- 不允许任意路径。

### 8.2 保存流程

```text
read → edit → validate schema → diff → backup → lock → write → reload/restart hint
```

要求：

- schema 校验失败不写；
- 备份到 `config/.backup/<timestamp>/`；
- 0600/0700；
- 写锁防止并发；
- 版本/ETag 防止覆盖；
- 保存后提示“需要重启/热重载”；
- 不自动重启整个 host，除非显式确认。

### 8.3 Host API

```csharp
public interface IAdminConfigStore
{
    ValueTask<AdminConfigDocument> ReadAsync(string id, CancellationToken ct = default);
    ValueTask<AdminConfigValidation> ValidateAsync(string id, string content, CancellationToken ct = default);
    ValueTask<AdminConfigSaveResult> SaveAsync(string id, string content, string? etag, CancellationToken ct = default);
}
```

`id` 只能是：

```text
profiles
plugin:<pluginId>
```

不允许 `../`、绝对路径、secret 文件。

### 8.4 Phase 2 插件侧就绪（宿主端口缺失时的行为）

宿主 `IAdminConfigStore` 未交付前，插件提供**无写入**的校验链路：

```http
POST /api/config/{id}/validate   # auth + CSRF
{"content": "<JSON 文本>"}
→ 200 {"id":"profiles","validation":{"valid":true,"eTag":"sha256:…","sizeBytes":13,
        "containsSecretLiteral":false,"issues":[]}}
```

`POST /api/config/{id}` 先跑同一校验：

- 无效 → `400 config.invalid`（附 issues/ETag）；
- 有效但宿主端口缺失 → `501 host.admin_config.unavailable`；
- 写入关闭 → `403 config.write_disabled`。

校验规则（`WebConsoleConfigEditPolicy`，纯函数）：

- id 白名单 `profiles` / `plugin:<id>`；
- ≤256 KiB；root 必须 object/array；NUL/控制字符拒绝；
- 明文 secret（`sk-*`、`Bearer`、PEM、`"token":"明文"`）拒绝，`${secret:NAME}` 允许；
- ETag = `sha256(content)`；
- 行级 diff（LCS 有界），预览 ≤200 行 / 32 KiB。

宿主接入后，`ReadAsync/ValidateAsync/SaveAsync` 可直接复用该策略；备份、写锁、原子替换仍由宿主负责。

---

## 9. Phase 3：受限命令终端

### 9.1 允许的命令

第一版只允许 Natter CLI：

```text
health --json
plugins list --detail
plugins contributions
plugins permissions
upstreams --json
memory list
task status
```

通过 `IAdminCommandRunner`：

```csharp
public interface IAdminCommandRunner
{
    ValueTask<AdminCommandResult> RunAsync(
        string command,
        IReadOnlyList<string> args,
        CancellationToken ct = default);
}
```

要求：

- 命令 allowlist；
- 参数白名单/长度限制；
- 超时；
- 输出截断/脱敏；
- 审计；
- 不执行任意 shell；
- 不拼接用户输入到 shell string；
- 不返回 secret。

### 9.2 OS shell

如果确实要 bash/powershell：

```text
ttyd/Wetty/SSH sidecar
  → loopback/内网
  → 单独账号
  → 单独审计
```

WebConsole 只提供链接和提示，不代理 shell。

### 9.3 Phase 3 插件侧就绪（宿主 runner 缺失时的行为）

```http
POST /api/commands/validate       # auth + CSRF
{"command":"plugins list","args":["--detail"]}
→ 200 {"decision":{"allowed":true,"code":"command.allowed","command":"plugins list","args":["--detail"]}}
```

`POST /api/commands` 的执行顺序：

1. 控制台关闭 → `403 command.disabled`；
2. 非 allowlist / 参数非法 → `403 command.not_allowed` / `command.invalid`；
3. 通过策略但无 `IAdminCommandRunner` → `501 host.admin_command.unavailable`。

策略（`WebConsoleCommandPolicy`，纯函数）：

- 命令归一化后与 `commandAllowlist` 精确匹配；≤128 字符；控制字符拒绝；
- 参数 ≤8 个、每个 ≤128 字符，argv 数组传递，不做 shell 拼接；
- 输出 ≤64 KiB（stdout/stderr 共享预算），截断标记 + token 脱敏；
- 超时由宿主 runner 负责（插件只传 CancellationToken）。

---

## 10. ASP.NET Core 与 NPS/ALC 风险（已实测，结论：Phase 1 不用 ASP.NET Core）

实测 `src/Natter.Cli/bin/Debug/net10.0/natter.runtimeconfig.json` 只声明 `Microsoft.NETCore.App`，`PluginLoadContext` 的共享名单与框架前缀都不含 `Microsoft.AspNetCore.*`。结论：

- 插件即使 `FrameworkReference Microsoft.AspNetCore.App` 构建成功，运行时也解析不到 ASP.NET Core 共享框架；
- 把 ASP.NET Core 打进插件包违反包布局与体积约束，不可取；
- **Phase 1 使用 BCL `HttpListener`**，不新增框架依赖，宿主 ALC 直接可用。

Phase 1 已落实：

- 只绑定 loopback；非 loopback 配置在激活前失败并给出 `webconsole.bind-not-loopback`；
- 静态资源白名单加载（`/`、`/app.js`、`/app.css`），不做任意文件读取；
- `web status` CLI；
- 端口冲突、静态资源缺失、缺 secret 都映射为结构化启动错误（写日志，不泄漏 secret）。

迁移到 Kestrel 的前置条件（宿主改动，未实施）：

1. 宿主 head 增加 `FrameworkReference Microsoft.AspNetCore.App`；
2. `PluginLoadContext` 增加 `Microsoft.AspNetCore.*` 显式共享策略；
3. 插件新增 `KestrelWebConsoleServer` 实现同一服务器抽象，用配置切换；
4. 两套实现跑同一组请求处理测试。

详细证据与迁移步骤见 [WEBCONSOLE_HOST_API_GAP.md §6](WEBCONSOLE_HOST_API_GAP.md#6-g7aspnet-core--alc-实测结论)。

---

## 11. 生命周期

```text
ConfigureAsync
  → 读 config/secret
  → 注册服务/贡献
  → 不启动 Kestrel

ActivateAsync
  → 启动 HTTP 服务器（Phase 1 为 HttpListener）
  → 绑定 loopback
  → 初始化 auth/session
  → 失败则插件 Failed

DeactivateAsync
  → 停止接收请求
  → 取消 session
  → 优雅停服务器
```

要求：

- 不在 `ConfigureAsync` 绑定端口；
- 不访问宿主根 DI；
- 端口冲突给出明确错误；
- `serve` 停止时不挂起；
- 不阻塞 NPS 激活。

---

## 12. 审计与日志

审计事件：

- 登录成功/失败；
- 查看 snapshot；
- 配置读取/保存；
- 命令执行；
- 配置回滚；
- 终端拒绝。

日志字段：

```text
time, user, ip, action, target, result, duration
```

不记录：

- token；
- secret；
- prompt/memory 正文；
- 完整命令输出（只记录摘要/长度）。

---

## 13. 测试

### 13.1 单元

- token 校验；
- CSRF/Origin；
- 路径白名单；
- schema 校验；
- 配置 ETag/锁；
- 命令 allowlist；
- 输出脱敏；
- 端口冲突。

### 13.2 集成

- file plugin 启动；
- loopback 服务器启动/停止；
- `web.status`；
- 反向代理 header；
- 配置保存/备份/回滚；
- 命令执行；
- 插件停止。

### 13.3 安全

- 无 token 拒绝启动；
- 错误 token 限速；
- 目录穿越拒绝；
- shell 注入拒绝；
- secret 不出现在 HTML/JSON/日志；
- 只读模式不能写；
- 公网未代理时 warning。

---

## 14. 实施阶段

### Phase 1：只读 Dashboard（**实现中，2026-11-18 起步**）

- [x] plugin skeleton（`plugins/Natter.Plugin.WebConsole`）；
- [x] loopback 服务器（HttpListener；Kestrel 需宿主改动）；
- [x] token auth + session + CSRF/Origin + 登录限速；
- [x] `/api/health` `/api/plugins` `/api/upstreams` `/api/profiles`；
- [ ] `/api/logs`（依赖宿主日志端口，当前 501/降级）；
- [x] `web status` CLI；
- [x] 静态 UI（登录 + dashboard）；
- [x] 审计 JSONL；
- [ ] `IAdminDiagnostics` host API（缺口，见 G3/G4）。

### Phase 2：配置编辑

- [x] 插件侧 id/大小/JSON/secret literal 校验 + ETag + 有界 diff（`/api/config/{id}/validate`）；
- [x] 无效内容在写入前 `400`，有效内容在宿主缺失时 `501`；
- [ ] `IAdminConfigStore` 读取/保存/备份/写锁/原子替换（宿主）；
- [x] 只允许 profiles/plugin config；
- [ ] 重启提示（宿主返回 reload hint 后接入）。

### Phase 3：受限命令

- [x] 插件侧 allowlist/参数/控制字符/输出截断与脱敏（`/api/commands/validate`）；
- [x] 拒绝路径审计（`command.run` denied）；
- [ ] `IAdminCommandRunner` 执行、超时与结果回传（宿主）；
- [x] 不拼 shell / 不启动子进程。

### Phase 4：可选 sidecar

- ttyd/Wetty/SSH 集成；
- 不在插件内执行 shell。

---

## 15. 验收标准

Phase 1 状态见 §18；未达成项都对应 [缺口文档](WEBCONSOLE_HOST_API_GAP.md) 中的 Gx 编号。

- [x] 默认只绑定 loopback；
- [x] 无 token 不启动；
- [ ] 只读 dashboard 可看 health/plugins/upstreams/profiles/logs（logs 缺口 G8；plugins 明细缺口 G4）
- [x] secret 不返回；
- [ ] 配置编辑 schema/diff/backup/lock（缺口 G5）
- [x] 路径白名单（只读侧）
- [ ] 命令 allowlist（缺口 G6）
- [x] 审计；
- [ ] file plugin 可加载（staging 脚本已提供，待真实宿主集成验证）
- [x] ASP.NET Core ALC：Phase 1 不使用，改用 HttpListener（G7）
- [x] Android 不加载（manifest platforms 不含 android）
- [x] 不阻塞 P5（无 src 改动）

---

## 16. 不做

- 公网无认证；
- 任意 OS shell；
- 文件浏览器；
- SQL 执行器；
- 多租户后台；
- 插件市场；
- 自动改 secret；
- 自动重启 host（除非显式确认）。

---

## 17. 一句话

**WebConsole 是运维控制面，不是给插件一把 root shell。**

先做只读诊断，再做配置编辑，最后做受限命令；OS shell 用独立 sidecar，不进入 NPS 插件进程。

---

## 18. Phase 1 实现映射（2026-11-18）

| 设计要求 | 实现位置 | 说明 |
| --- | --- | --- |
| loopback 绑定 | `WebConsoleServer.cs` | 非 loopback 在 `WebConsoleOptionsValidator` 拒绝 |
| token 认证 | `WebConsoleAuth.cs` | SHA-256 + `FixedTimeEquals`；缺 secret 拒绝激活 |
| session / CSRF / Origin | `WebConsoleAuth.cs`、`WebConsoleRequestHandler.cs` | `SameSite=Strict` cookie；写请求必须带 `X-WebConsole-CSRF` |
| 登录限速 | `WebConsoleAuth.cs` | 5 次失败 / 5 分钟 / 客户端，IP 取自可信代理链 |
| 只读快照 | `WebConsoleDiagnostics.cs` | `IWebConsoleHostPort` 组合 health endpoint + 可选 `IPluginRuntimeDiagnostics` |
| 脱敏 | `WebConsoleConfig.cs` | 键名规则 + `${secret:...}` 引用保留 + 截断 |
| 审计 | `WebConsoleAudit.cs` | JSONL，字段 `time/user/ip/action/target/result/durationMs` |
| 静态 UI | `wwwroot/` | 无外部依赖，CSP `default-src 'none'` |
| CLI | `WebConsoleCommands.cs` | `web status [--json]`，与 HTTP 快照同源 |
| 配置编辑 | `WebConsoleConfigEdit.cs`、`WebConsoleRequestHandler.cs` | 校验/ETag/diff 已就绪；写入在 G5 缺失时 501 |
| 命令终端 | `WebConsoleCommandPolicy.cs`、`WebConsoleRequestHandler.cs` | allowlist/截断/脱敏已就绪；执行在 G6 缺失时 501 |

测试与运行记录：[2026-11-18 WebConsole Phase 1](testing/2026-11-18-webconsole-p1.md)。
