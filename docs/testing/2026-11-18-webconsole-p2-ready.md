# WebConsole Phase 2/3 插件侧就绪与运维硬化（2026-11-18）

> **对象**：`plugins/Natter.Plugin.WebConsole`  
> **上一轮**：[Phase 1 实现与验收](2026-11-18-webconsole-p1.md)  
> **设计**：[WEBCONSOLE_PLUGIN_DESIGN.md](../WEBCONSOLE_PLUGIN_DESIGN.md) · **缺口**：[WEBCONSOLE_HOST_API_GAP.md](../WEBCONSOLE_HOST_API_GAP.md)  
> **结论**：在不修改 `src/`、不绕过宿主契约的前提下，补齐配置校验/ETag/diff、命令 allowlist/截断/脱敏、审计权限、HEAD/转发协议与 `external` 模式；写入与执行仍按 G5/G6 返回 501。

## 0. 元数据

| 字段 | 值 |
| --- | --- |
| 测试时间 | 2026-11-18 10:37–10:52（Asia/Shanghai） |
| 基线 | 上一轮 `2c7953d`；本轮功能提交 `349f21f`；测试提交 `c96a452` |
| 工作区 | 仅本插件/测试/文档改动；`src/`、`samples/` 无 diff；其他会话的 P4.5/P5 改动未触碰 |
| 环境 | Arch Linux `arch-x64`、.NET SDK 10.0.114、Microsoft.NETCore.App 10.0.14 |
| 被测 | WebConsole 插件（file plugin）、Natter CLI 宿主、loopback HTTP |
| 不测 | 宿主 `IAdminConfigStore` / `IAdminCommandRunner`（尚不存在）、公网代理、24h soak |

---

## 1. 本轮变更

### 1.1 运维硬化

| 项 | 实现 |
| --- | --- |
| 审计权限 | 目录 0700、文件 0600（Unix，best-effort）；health 新增 `audit` 检查 |
| 请求体上限 | login 4 KiB、config 260 KiB、command 8 KiB；超限 413 |
| HEAD | 静态资源支持 HEAD（服务端不写 body，保留 Content-Length） |
| 反向代理协议 | 仅可信代理的 `X-Forwarded-Proto: https` 生效；会话 cookie 加 `Secure` |
| 启动失败清理 | 端口/静态资源/secret 失败时释放 health 端口与审计句柄 |
| 命令 allowlist 校验 | 空项/超长/控制字符在配置校验阶段拒绝 |
| `external` 模式 | 端口被既有控制台占用时探测 `/api/session`，不重复绑定，`web status` 仍可用 |

### 1.2 Phase 2 插件侧（配置）

- `WebConsoleConfigEditPolicy`：id 白名单、256 KiB、JSON/root、NUL、明文 secret、ETag、有界 LCS diff；
- `POST /api/config/{id}/validate`：无写入校验，返回 issues/ETag/size/secret 标记；
- `POST /api/config/{id}`：先校验（无效 400 `config.invalid`），有效后仍 501 `host.admin_config.unavailable`；
- 明文 secret 检测覆盖 `sk-*`、`ak-*`、`Bearer`、PEM、`"token":"明文"`；`${secret:NAME}` 放行。

### 1.3 Phase 3 插件侧（命令）

- `WebConsoleCommandPolicy`：命令归一化 + 精确 allowlist、参数 8×128、控制字符拒绝、输出 64 KiB 共享预算、token 脱敏；
- `POST /api/commands/validate`：只判定不执行；
- `POST /api/commands`：关闭 403；非 allowlist 403 `command.not_allowed`；通过后 501 `host.admin_command.unavailable`；
- 不启动子进程、不拼 shell。

---

## 2. 单元测试

```text
$ dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -c Debug
Passed! - Failed: 0, Passed: 123, Skipped: 0, Total: 123
```

新增覆盖：

| 文件 | 覆盖 |
| --- | --- |
| `WebConsoleConfigEditTests` | valid/invalid JSON、root 类型、secret literal/ref、大小、NUL、id、ETag、diff 增删改/截断/大输入回退 |
| `WebConsoleCommandPolicyTests` | 归一化 allowlist、未知命令、空 allowlist、参数上限、控制字符、请求解析、输出脱敏/截断 |
| `WebConsoleAuditTests` | 0600 权限、CanWrite、只读文件、损坏行、limit 取最新 |
| `WebConsoleDiagnosticsTests` | 6 项 checks、audit ok、token 失败 → error |
| `WebConsoleRuntimeTests` | external 模式、非控制台占用端口失败、URL 格式化 |
| `WebConsoleRequestHandlerTests` | validate 端点、400/501 顺序、命令 403/501、413、Secure cookie（可信/不可信 XFF） |
| `WebConsoleServerTests` | HEAD、端口冲突、静态资源缺失、CSP |

UI smoke：

```text
$ node tests/Natter.Plugin.WebConsole.Tests/ui-smoke.mjs
{ "result": "ui smoke ok", "checks": 6, "gaps": 7, "configRedacted": true }
```

---

## 3. 真实宿主验证

Staging：`artifacts/webconsole-e2e/plugins/natter.plugin.webconsole`（`readOnly=false`、`allowConfigWrite=true`、`allowCommandConsole=true`、allowlist=`health/plugins list/upstreams`）。  
宿主：`natter serve --duration 60 --health-port 5199`。

### 3.1 配置校验

```text
POST /api/config/profiles/validate {"content":"{\"port\":5090}"}
→ 200 {"validation":{"valid":true,"eTag":"sha256:89c3f54e…","sizeBytes":13,"issues":[]}}

POST /api/config/profiles/validate {"content":"{\"token\":\"plain-value\"}"}
→ 200 {"validation":{"valid":false,"containsSecretLiteral":true,"issues":["config.secret_literal"]}}

POST /api/config/profiles {"content":"{oops}"}
→ 400 config.invalid（附 issues/ETag）

POST /api/config/profiles {"content":"{}"}
→ 501 host.admin_config.unavailable
```

### 3.2 命令策略

```text
POST /api/commands/validate {"command":"plugins   list","args":["--detail"]}
→ 200 {"decision":{"allowed":true,"code":"command.allowed","command":"plugins list"}}

POST /api/commands {"command":"rm","args":["-rf","/"]}
→ 403 command.not_allowed

POST /api/commands {"command":"health","args":["--json"]}
→ 501 host.admin_command.unavailable
```

### 3.3 协议与运维

```text
HEAD / → 200，Content-Type: text/html，无 body
POST /api/session（可信代理 + X-Forwarded-Proto: https）
→ Set-Cookie: …; HttpOnly; SameSite=Strict; Max-Age=3600; Secure
GET /api/snapshot → healthStatus=ok，checks: server/token/static-assets/audit/host.health/host.plugins 全 ok
```

审计（节选）：

```json
{"action":"config.validate","target":"profiles","result":"ok","durationMs":11}
{"action":"config.validate","target":"profiles","result":"invalid","durationMs":9}
{"action":"config.write","target":"profiles","result":"invalid","durationMs":2}
{"action":"config.write","target":"profiles","result":"unavailable","durationMs":0}
{"action":"command.validate","target":"plugins list","result":"ok","durationMs":3}
{"action":"command.run","target":"rm","result":"denied","durationMs":0}
{"action":"command.run","target":"health","result":"unavailable","durationMs":0}
```

### 3.4 `external` 模式

控制台已由 `serve` 占用 5187 时，另起一个宿主执行：

```text
$ natter ... web status --json
[warning] webconsole: port is already served by another instance; running in external mode
{"server":{"state":"external","url":"http://127.0.0.1:5187"},"tokenConfigured":true,…}
```

退出码 0。非 WebConsole 进程占用端口时仍按 `webconsole.port-in-use` 失败（单测覆盖）。

---

## 4. 安全与边界

| 项 | 结果 |
| --- | --- |
| 明文 secret 写入 | 拒绝（`config.secret_literal`） |
| 写入关闭 | 403；无效内容优先 400；有效但无宿主 501 |
| 命令未授权 | 403 `command.not_allowed`，审计记录命令名 |
| 命令参数 | 8×128、控制字符拒绝、argv 语义 |
| 输出 | 64 KiB 上限、截断标记、token 脱敏 |
| 审计权限 | 0600/0700；不可写时 health error |
| 代理头 | 仅 trustedProxies 生效；Secure cookie |
| 文件写入/进程执行 | 本轮未新增（仍等待宿主契约） |

---

## 5. 问题登记

### OBS-001 · validate 端点对非法内容返回 200

- **发现时间**：2026-11-18 10:40
- **说明**：`/validate` 是“判定”接口，`valid=false` + issues 属于正常结果；HTTP 200 便于 UI 展示。
- **影响**：调用方需读取 `validation.valid`，不能只看状态码。
- **建议**：保持现状；写入端点仍用 400。

### OBS-002 · external 模式不代理另一实例

- **发现时间**：2026-11-18 10:41
- **说明**：本进程只提供 `web status`，HTTP 请求仍由原实例处理；两进程共享同一审计文件（append）。
- **影响**：审计顺序可能交错，但 JSONL 行完整。
- **建议**：生产中只保留一个常驻控制台；external 仅用于诊断命令。

### ENV-001 · dashboard 未做浏览器自动登录

- **发现时间**：2026-11-18 10:42
- **说明**：headless Firefox 只验证登录页；dashboard 由 fake-DOM smoke 覆盖。
- **建议**：后续引入 Playwright/Marionette 时补真实登录渲染。

---

## 6. 结论与下一步

- Phase 2/3 的“纯逻辑 + HTTP 接线”已经完成，且不违反“宿主契约缺失只写文档”的约束；
- 真正落盘与执行仍必须等 `IAdminConfigStore` / `IAdminCommandRunner`（G5/G6）；
- 下一步：宿主播接后增加适配器与端到端写/执行测试；再补 viewer 角色、审计轮转与 CI 纳管。

---

## 7. CI 与可复现 E2E

`.github/workflows/ci.yml` 新增：

```text
dotnet restore tests/Natter.Plugin.WebConsole.Tests/...
dotnet build   tests/Natter.Plugin.WebConsole.Tests/... -c Release --no-restore
dotnet test    tests/Natter.Plugin.WebConsole.Tests/... -c Release --no-build
```

本地 Release 复核：

```text
$ dotnet test tests/Natter.Plugin.WebConsole.Tests/... -c Release
Passed! - Failed: 0, Passed: 123, Skipped: 0, Total: 123
```

可复现脚本：

```text
$ ./scripts/webconsole-e2e.sh
== auth ==
== config validation ==
== command policy ==
== snapshot / audit / HEAD ==
== external mode ==
PASS: WebConsole E2E matrix
  console=http://127.0.0.1:5187 health=http://127.0.0.1:5199/health
```

脚本使用一次性 token、`artifacts/webconsole-e2e` 工作目录与 loopback 端口，退出时清理宿主进程；断言覆盖 401/200、config 400/501、command 403/501、audit check 与 external 模式。
