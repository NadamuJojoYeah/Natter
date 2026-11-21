# 2026-11-21 · WebConsole Phase 2/3 独立验收（配置写入 + 受限命令）

> **执行**：qa（task-5）
> **基线**：`HEAD=0f3d61c`（task-1 宿主管理面 + task-2 Phase 2/3 接入已完成）
> **被测对象**：WebConsole Phase 2（`/api/config/*` 保存/校验/回滚/undo）与 Phase 3（`/api/commands`、`/api/commands/validate`），宿主侧 `IAdminConfigStore` / `IAdminCommandRunner` / `IAdminAuditSink` 的真实接入
> **新增测试**：`tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/`（4 个文件，**44 个用例**）
> **端到端现场**：`artifacts/qa-p5-wc/`（隔离 config/state/plugins，仓库 `artifacts/` 已 gitignore）
> **关联**：[P5 + WebConsole 复测](2026-11-18-p5-webconsole-retest.md)、[WebConsole 宿主 API 差距](../WEBCONSOLE_HOST_API_GAP.md)

---

## 1. 结论

| 项 | 结果 |
| --- | --- |
| Phase 1 回归（WebConsole 插件全量） | **225/225 通过**（181 基线 + 44 本任务新增） |
| 框架回归 | **361/361 通过** |
| Phase 2 真实落盘 | **通过**：文件写入隔离 config 目录，内容与请求一致，权限 `0600` |
| Phase 2 备份 / 回滚 | **通过**：`.backup/<ts>/profiles.json` 为旧值、`0600`；rollback 后文件回到旧内容 |
| Phase 2 stale ETag | **通过**：`409 config.conflict`，**文件未被改写、未产生备份** |
| Phase 2 并发写 | **通过**：同一 ETag 两个并发请求 → 恰好 `1×200 + 1×409`，落盘只有一个赢家且仍是合法 JSON |
| Phase 2 路径 / 符号链接 / 越权 id | **通过**：符号链接读与写都 `409 config.symlink` 且目标文件未被改写；`../`、`plugin:../x`、`secrets`、`plugin:` → `400 config.id_invalid`；未知插件 → `404 config.plugin_unknown` |
| Phase 3 allowlist 真执行 | **通过**：`health --offline --json` 由宿主 in-process runner 执行，`exitCode=0`、530 字节输出、3ms |
| Phase 3 未授权 / 参数 | **通过**：非 allowlist → `403 command.not_allowed`；控制字符、9 个参数 → `403 command.invalid`（均未触达 runner） |
| Phase 3 超时 / 截断 / 脱敏 | **通过（进程内）**：`504 command.timeout`；>64 KiB 输出截断到预算并带 `…`；`sk-`/`Bearer` 形状脱敏 |
| 审计 | **通过**：宿主 `state/admin-audit.jsonl` 与插件 `audit/webconsole-audit.jsonl` 双落盘，`config.write`/`config.rollback`/`command.run` 的 ok 与 denied 都在 |
| 501 | **不再出现**：契约齐备时 Phase 2/3 全路径无 `501`；只有显式缺契约时才回退 501（契约仍在，见 §4） |
| 隔离 | **通过**：独立 `--config-dir/--state-dir/--plugin-dir`，token 为临时随机值，全程未读写生产 config/secret |

**判定**：Phase 2/3 **可签（Go）**。遗留 1 个 Low 观察项（命令输出中 `key=value` 形式的 secret 未脱敏，见 §5）与 2 个端到端未构造项（超时/截断，已由进程内覆盖）。

---

## 2. 端到端验收（真实 `natter serve` + curl，隔离目录）

### 2.1 起实例

```bash
NATTER_SECRET_WEBCONSOLE_TOKEN=<临时随机 token，≥24 字符> \
./src/Natter.Cli/bin/Debug/net10.0/natter \
  --plugin-dir artifacts/qa-p5-wc/plugins \
  --config-dir artifacts/qa-p5-wc/config \
  --state-dir  artifacts/qa-p5-wc/state \
  serve --duration 280 --health-port 5198
# serve: profiles=2 providers=0 upstreams=0 plugins=3 failed=0
# WebConsole 监听 127.0.0.1:5197；health 在 5198
```

插件配置（隔离副本，非生产）：`readOnly=false`、`allowConfigWrite=true`、`allowCommandConsole=true`、
`commandAllowlist=["health","plugins list","plugins graph","memory list"]`、`commandTimeoutSeconds=5`。

登录：`POST /api/session` → `200`、`role=admin`、`Set-Cookie: natter_webconsole=…; HttpOnly; SameSite=Strict`；
写请求统一携带 `X-WebConsole-CSRF`。**缺 CSRF → `403 csrf.invalid`；无会话 → `401 auth.required`**。

### 2.2 Phase 2 证据

| 步骤 | 请求 | 结果 |
| --- | --- | --- |
| 契约就绪 | `GET /api/config` | `writeEnabled=true`，`admin={configStoreAvailable:true, commandRunnerAvailable:true, logReaderAvailable:true}`，documents 4 条 |
| 创建文档 | `POST /api/config/profiles {content, etag:null}` | `200`、`code=config.saved`、`reloadHint=restart-host`、`restartRequired=true` |
| 落盘检查 | — | `config/profiles.json` 250 B、**mode 0600**、内容与请求一致 |
| stale ETag | 合法内容 + 旧 ETag | `409 config.conflict`，`detail.conflictETag` 为**当前** ETag；**文件内容不变**、未生成备份 |
| 正确 ETag 保存 | `POST /api/config/profiles`（ETag=v1） | `200`、`backupPath=.backup/20261121T085229196Z/profiles.json`、`undoId=undo-1` |
| 备份检查 | — | 备份为**旧值**、mode `0600`；`.backup/` 目录 `0700` |
| 回滚 | `POST /api/config/profiles/rollback {etag, undoId}` | `200`，文件回到旧内容；本次回滚自身又产生一条备份与 `undo-2` |
| 无快照回滚 | `POST /api/config/plugin:natter.sample.greetings/rollback` | `409 config.undo_empty`，不写入 |
| 符号链接 | `profiles.json` → 指向目录外文件 | `GET` 与 `POST` 均 `409 config.symlink`；**目标文件内容未变** |
| 并发写 | 同一 ETag 两个并发 `POST` | `qa-x → 200`、`qa-y → 409 config.conflict`；落盘只剩赢家且为合法 JSON |
| 越权 id | 见下表 | 全部拒绝，未创建任何 `*.json` |

越权 id 实测：

| 请求路径 | 状态 | 错误码 |
| --- | --- | --- |
| `/api/config/../etc/passwd`（`--path-as-is`） | 404 | `route.not_found`（不会触达文件） |
| `/api/config/%2e%2e%2fetc%2fpasswd` | 400 | `config.id_invalid` |
| `/api/config/plugin:../x` | 400 | `config.id_invalid` |
| `/api/config/secrets` | 400 | `config.id_invalid` |
| `/api/config/plugin:nope.nope` | 404 | `config.plugin_unknown` |

### 2.3 Phase 3 证据

| 步骤 | 请求 | 结果 |
| --- | --- | --- |
| 状态 | `GET /api/commands` | `{enabled:true, readOnly:false, runnerAvailable:true, allowlist:[…4 条], timeoutSeconds:5}` |
| 真执行 | `POST /api/commands {"command":"health","args":["--offline","--json"]}` | `200`、`executed=true`、`exitCode=0`、`outputBytes=530`、`durationMs=3`、`timedOut=false`，stdout 为真实 health JSON |
| 非 allowlist | `plugins disable` | `403 command.not_allowed`（runner 未调用） |
| 参数边界 | 含 `\n` 的 arg / 9 个 args | `403 command.invalid` |
| 校验端点 | `POST /api/commands/validate` | `200`、`decision.allowed=true`、`command.allowed` |

### 2.4 只读实例（第二套隔离目录，端口 5196）

| 请求 | 结果 |
| --- | --- |
| `GET /api/config` | `writeEnabled=false`、`writeGapCode=webconsole.write_disabled` |
| `POST /api/config/profiles` | `403 config.write_disabled` |
| `POST /api/commands` / `/api/commands/validate` | `403 command.disabled` |
| `GET /api/commands` | `enabled=false`、`gapCode=webconsole.command_disabled`、`runnerAvailable=true` |

### 2.5 审计与脱敏

```text
/api/audit（29 条）动作覆盖：auth.login / config.view / config.read / config.validate / config.write /
config.rollback / config.undo.list / command.status / command.run / command.validate / audit.view /
csrf.rejected / plugin.start
结果分布：config.write denied×7 ok×3；config.read denied×3（符号链接）；command.run denied×3 ok×1
落盘：state/admin-audit.jsonl（宿主，2375 B）+ plugins/<id>/audit/webconsole-audit.jsonl（插件，4127 B）
```

- `GET /api/logs`、`GET /api/config` 响应中**不含明文 token**；
- `GET /api/config.values.token` 显示 `${secret:WEBCONSOLE_TOKEN}`（未展开 secret）。

---

## 3. 进程内验收（`Phase23Acceptance/`，44 用例）

> 端到端无法稳定构造的边界（超时、64 KiB 截断）以及精确的状态码矩阵放在这里；真实宿主实现由 §2 的端到端证据覆盖。

| 文件 | 用例数 | 覆盖 |
| --- | --- | --- |
| `ConfigPhase2AcceptanceTests.cs` | 18 | 创建落盘 + `0600`、stale 409 不写、`etag=null` 仅允许创建、非法内容 400 不写、备份为旧值、回滚、`undo_empty` 409、符号链接读/写拒绝、6 组非法 id、未知插件 404、并发写恰一个赢家、只读 403、validate 不写盘 |
| `CommandPhase3AcceptanceTests.cs` | 14 | 状态/allowlist、真执行参数透传、非 allowlist 403、控制字符与参数上限 403、**超时 504**、**>64 KiB 截断**、`sk-`/`Bearer` 脱敏、`key=value` 未脱敏（观察项）、validate、命令台关闭 403、审计落盘并含 ok/denied |
| `HostContractAcceptanceTests.cs` | 12 | admin 契约标志、契约齐备时无 501、缺 store/缺 runner 时回退 501、4 条写路径未认证 401、3 条写路径缺 CSRF 403、config 快照脱敏 |
| `AcceptanceHarness.cs` | — | 夹具：真实文件 `IAdminConfigStore` 参照实现（真实 ETag/备份/写锁/符号链接）、脚本化 runner、真实审计 store |

```bash
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj \
  --filter "FullyQualifiedName~Phase23Acceptance"
# Passed! - Failed: 0, Passed: 44, Skipped: 0, Total: 44
```

说明：该测试项目只引用插件程序集，**宿主实现（`Natter.Infrastructure.Admin`）不在引用链内**，所以进程内用"契约等价、真实文件 IO"的参照 store；宿主实现本身的落盘/备份/ETag/并发由 §2 的端到端证据负责，两者不重复计分。

---

## 4. 501 语义（不允许把 501 当通过）

| 条件 | 结果 |
| --- | --- |
| 契约齐备（本任务验收路径） | Phase 2/3 **全部非 501**：保存 200、冲突 409、非法 400、未知插件 404、禁用 403、超时 504、执行 200 |
| 宿主未提供 `IAdminConfigStore` | `501 host.admin_config.unavailable`（保留的降级契约，**不计通过**） |
| 宿主未提供 `IAdminCommandRunner` | `501 host.admin_command.unavailable`（同上） |

`writeEnabled` 语义提醒：它只反映插件配置（`readOnly`/`allowConfigWrite`），真实可用性看
`GET /api/config.admin.configStoreAvailable` 与 `GET /api/commands.runnerAvailable`；UI 不应仅凭 `writeEnabled` 判断可写。

---

## 5. 观察项

| ID | 级别 | 内容 | 处置建议 |
| --- | --- | --- | --- |
| OBS-WC-P3-REDACT-KV | Low | 命令输出脱敏只覆盖 `sk-`/`ak-`/`Bearer` 形状；`token=…`、`API_KEY=…` 这类 `KEY=value` 文本会原样返回浏览器（已固化为用例 `Plain_key_value_secret_in_command_output_is_a_documented_redaction_gap`） | 扩一条 `(?i)(token|secret|password|api[_-]?key)\s*=\s*[^\s]+` 规则，或让 runner 侧统一按行脱敏 |
| OBS-WC-CONFIG-MODE | Low | 宿主只在**新建** config 目录时设 `0700`；目录已存在时沿用既有权限（本次预建目录为 `0755`）。文件与备份文件本身仍是 `0600` | 安装文档写明 config 目录权限，或每次保存时收紧 |
| OBS-WC-E2E-TIMEOUT | Low | 端到端未构造超时与 64 KiB 截断（现有 allowlist 命令都在毫秒级、输出远小于上限），改由进程内用例覆盖 | 保留进程内覆盖；若需端到端，可临时加一个慢命令到隔离 allowlist |
| OBS-WC-WRITEGAP | Info | `writeGapCode` 在 `writeEnabled=true` 时仍返回 `host.admin_config.unavailable` 作为静态提示；端到端响应里该字段为 `null`，两处语义不完全一致 | 统一由 `admin.*Available` 驱动，或让 gap 码随实际可用性置空 |

---

## 6. 未测 / 待人工

- **真实浏览器 UI 操作**（点击保存/回滚/执行按钮、错误提示渲染）：本轮只验 HTTP 契约；`ui smoke` 属 Phase 1 基线（`ui-smoke.mjs`），未在浏览器里手点。
- **真实生产配置写入**：按任务要求全程使用隔离目录与临时 token，未触碰仓库 `config/` 与任何生产 secret。
- **长时间并发压测**：只构造了两路并发写；未做 N 路持续压测。
- **Windows 权限语义**：`0600` 断言仅 POSIX 生效（Windows 分支跳过）。

---

## 7. 复现清单

```bash
# 1) 进程内验收
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj \
  --filter "FullyQualifiedName~Phase23Acceptance"      # 44/44

# 2) 插件全量（Phase 1 + Phase 2/3 回归）
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj   # 225/225

# 3) 框架回归
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj                   # 361/361

# 4) 端到端（见 §2.1 起实例；隔离目录 artifacts/qa-p5-wc，已 gitignore）
```

证据文件保留在 `artifacts/qa-p5-wc/evidence/`（login / save-v1 / save-stale2 / save-v2 / rollback /
undo-list / symlink-save / command-health / command-denied / command-ctrl / audit / logs / config-* 等）。
不提交 state、审计原文、token 或任何 secret。
