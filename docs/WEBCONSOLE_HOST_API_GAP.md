# Natter WebConsole 宿主契约缺口与适配方案

> **状态**：Living document · G4/G5/G6/G8 已由宿主实现并接入插件（2026-11-21）
> **日期**：2026-11-18（初版） · 2026-11-21（宿主契约落地与插件接入）
> **关联**：[WebConsole 插件设计](WEBCONSOLE_PLUGIN_DESIGN.md)、[NPS v1 规范](PLUGIN_SPEC.md)、[Harness 架构](HARNESS_ARCHITECTURE.md)、[运维手册](OPERATIONS_RUNBOOK.md)
> **约束**：本文件记录宿主（`src/`）**尚不存在**的接口与模型，以及插件侧的临时适配。按项目约束，WebConsole 实现**不修改主项目**来补齐这些契约；先文档化，再走独立提案。

---

## 0. 结论摘要

| # | 依赖 | 宿主现状 | 缺口 | 插件临时策略 |
| --- | --- | --- | --- | --- |
| G1 | manifest `kind: "admin"` | `PluginKind` 仅 `transport/feature/tool/memory`，manifest 校验拒绝其他值 | 无 admin kind | 用 `kind: "tool"` 发布，语义在 README/manifest description 中说明 |
| G2 | contribution `admin.ui` / `admin.api` / `admin.command` | `PluginContributionTypes` 与 manifest 校验只认 8 种 v1 类型 | 无管理面贡献点 | 不声明管理贡献；在 `ActivateAsync` 启动 loopback 服务，CLI 只贡献 `cli.command` |
| G3 | `IAdminDiagnostics` / `AdminSnapshot` / `AdminLogEntry` | 不存在 | 插件拿不到宿主聚合快照 | 插件内定义 `IWebConsoleHostPort`，数据源走已存在的 `serve --health-port` `/health` |
| G4 | `IPluginRuntimeDiagnostics` 桥接 | **已实现**：`BindAdminServices` 在 `admin.read` 授予时注入 | — | `/plugins` 自动升级为 plugin graph/contributions/permissions |
| G5 | `IAdminConfigStore` | **已实现**（`Natter.Application.Admin` + `HostAdminConfigStore`） | — | 插件适配为 `WebConsoleAdminGateway`：真实读/校验/保存、409 冲突、409 锁、备份路径、重启提示；`rollback` 走插件内存 undo |
| G6 | `IAdminCommandRunner` | **已实现**（`CliAdminCommandRunner`，`admin.command.execute`） | — | 插件转发到 runner，映射 400/403/409/504/502，输出二次脱敏与 64 KiB 预算 |
| G7 | ASP.NET Core 宿主管线 | host `runtimeconfig` 只有 `Microsoft.NETCore.App`；`PluginLoadContext` 不共享 `Microsoft.AspNetCore.*` | Kestrel/ASP.NET Core 无法从插件 ALC 加载 | Phase 1 用 BCL `HttpListener`（`WebConsoleServer`），不加 `FrameworkReference` |
| G8 | 宿主日志 tail API | **已实现**（`IAdminLogReader` + `AdminLogBuffer`） | — | `/api/logs` 返回真实宿主日志 tail（脱敏、有界），仍保留 `/api/audit` |
| G9 | 统一脱敏服务 | **已实现**（`IAdminRedactor`，`AdminRedactor.Default`） | — | 插件仍保留 `WebConsoleRedaction` 作为本地兜底，输出与配置回显双重脱敏 |
| G10 | CLI 子进程执行治理 | 无 `process.exec` 权限词、无审计桥 | 不能用子进程兜底命令终端 | 明确不做子进程兜底；等 `IAdminCommandRunner` |

**一句话**：Phase 1 只依赖**已经存在**的 `serve --health-port` `/health` 和插件自身上下文；其余缺口全部显式降级并在这里登记，不碰 `src/`。

---

## 1. 数据源：为什么 Phase 1 能先跑起来

`natter serve --health-port <port>` 已经提供 loopback `/health`（见 [P2_RUNTIME_PROFILE](P2_RUNTIME_PROFILE.md) §health、`src/Natter.Cli.Core/Commands/HealthHttpServer.cs`）。返回的 `RuntimeHealthSnapshot` 已包含：

- `status`、`uptimeSeconds`、`startedAt`、`sampledAt`；
- `profiles` 数量、`conversations` 数量、`tasks` 指标；
- `upstreams[]`：`connected/lastConnectedAt/lastDisconnectAt/reconnectCount/lastError/sendFailures`；
- `providers[]`：`id/available/profile/supportTier`；
- `plugins`：`activated/failed` 与失败项的 `phase/code/message`。

这满足 WebConsole Phase 1 的 health、upstreams、profiles（provider 维度）、plugins（计数与失败）四块只读数据，且**不需要新增宿主接口**。

`/health` 没有认证，因此：

- 只允许 loopback URL（`127.0.0.0/8`、`::1`、`localhost`）；
- 插件 2s 超时 + 3s 缓存，避免把控制台变成放大器；
- 失败时区块标记 `available: false`，控制台整体仍可登录和查看自身状态。

---

## 2. G3/G4：诊断快照与插件运行视图

### 2.1 早先提议的聚合契约（`IAdminDiagnostics`，仍未实现，已被满足需求取代）

```csharp
namespace Natter.Application.Admin; // 提议命名空间，当前不存在

public interface IAdminDiagnostics
{
    ValueTask<AdminSnapshot> GetSnapshotAsync(CancellationToken ct = default);
    ValueTask<IReadOnlyList<AdminLogEntry>> GetLogsAsync(int limit, CancellationToken ct = default);
}

public sealed record AdminSnapshot(
    AdminHealth Health,
    IReadOnlyList<AdminPlugin> Plugins,
    IReadOnlyList<AdminUpstream> Upstreams,
    IReadOnlyList<AdminProfile> Profiles,
    AdminCounts Counts,
    DateTimeOffset GeneratedAt);

public sealed record AdminLogEntry(
    DateTimeOffset Timestamp,
    string Level,
    string Source,
    string Message); // 必须已脱敏，且不含 prompt/memory 正文

public sealed record AdminPlugin(
    string Id,
    string Version,
    string Kind,
    string State,
    IReadOnlyList<AdminContribution> Contributions,
    AdminPluginError? Error);

public sealed record AdminPluginError(string Phase, string Code, string Message);
```

要求（写进未来提案）：

- 只返回脱敏后的数据；
- `GetLogsAsync` 有硬上限，不返回 prompt/memory 正文；
- 快照构建必须无副作用，不能触发上游重连。

### 2.2 插件侧适配（已实现）

`plugins/Natter.Plugin.WebConsole/WebConsoleHostPort.cs` 定义：

```csharp
public interface IWebConsoleHostPort
{
    ValueTask<WebConsoleHostSnapshot> ReadAsync(CancellationToken ct = default);
}
```

实现按优先级组合：

1. `HealthEndpointHostPort`：拉取 `hostHealthUrl`，映射 health/upstreams/providers/plugins 计数；
2. `PluginDiagnosticsHostPort`：`provider.GetService<IPluginRuntimeDiagnostics>()`，拿到则补充 plugin graph/contributions/permissions；
3. 都不可用 → 各区块 `available: false` + `gapCode`。

未来宿主实现 `IAdminDiagnostics` 后，只需在插件 provider 注册该服务，再增加一个 `AdminDiagnosticsHostPort` 适配器；**服务器、UI、测试都不用改**。

### 2.3 G4 的宿主桥接（已实现）

`NpsPluginRuntime.BuildPluginProvider` 增加：

```csharp
services.AddSingleton(_hostProvider.GetRequiredService<IPluginRuntimeDiagnostics>());
```

2026-11-21 落地形态（`NpsPluginRuntime.BindAdminServices`）：授予 `admin.read` 时注册 `IPluginRuntimeDiagnostics`（+ `IAdminLogReader`），不暴露根 `IServiceProvider`；未授予则该服务不存在，插件自动回落 health 计数与日志缺口。

---

## 3. G1/G2：manifest 与管理面贡献点

当前 `PluginManifestReader` 校验：

- `kind` ∈ `transport/feature/tool/memory`；
- `contributions[].type` ∈ `PluginContributionTypes.All`（8 种，无 `admin.*`）。

因此本插件的实际 manifest 使用：

```json
"kind": "tool",
"contributions": [{ "type": "cli.command", "id": "web.status" }]
```

提议（未实现）：

- `PluginKind.Admin`；
- `admin.ui`（静态 UI 入口）、`admin.api`（管理 API 前缀）、`admin.command`（命令终端）三个 contribution 类型；
- 权限词 `admin.read`、`admin.config.write`、`admin.command.execute`。

在提案落地前，控制台由 `ActivateAsync` 直接启动；这不违反 NPS 生命周期（`ActivateAsync` 允许启动插件资源），但宿主无法在贡献图里看到 UI 入口。

---

## 4. G5：配置编辑（已接入）

保存链路（宿主实现，插件只做 HTTP 适配）：

```text
read → validate(content+schema) → diff → lock → backup → atomic replace → reload/restart hint
```

宿主契约（`src/Natter.Application/Admin/AdminContracts.cs`，程序集 `Natter.Application` 属于 `PluginContractAssemblies` 共享集，插件侧类型标识一致）：

```csharp
ValueTask<IReadOnlyList<AdminConfigDocumentInfo>> ListAsync(ct);   // 附加能力，插件不依赖
ValueTask<AdminConfigReadResult> ReadAsync(string id, ct);          // Available=false ⇒ store 不可用/拒绝
ValueTask<AdminConfigValidation> ValidateAsync(string id, string content, ct);
ValueTask<AdminConfigSaveResult> SaveAsync(string id, string content, string? etag, ct);
```

关键语义（插件依赖这些，改动需同步本文）：

| 语义 | 宿主行为 | 插件映射 |
| --- | --- | --- |
| ETag 口径 | `AdminETag.Compute` = `sha256:` + 小写 hex(SHA256(UTF-8 原文)) | 与 `WebConsoleConfigEditPolicy.ComputeETag` **逐字节一致**（对拍测试 `AdminContractParityTests`） |
| 陈旧 ETag | 不抛异常，返回 `Saved=false` + `Code=config.etag_mismatch` + `ConflictETag` | `409 config.conflict`，`detail.conflictETag` 供 UI 刷新 |
| 创建文档 | `etag=null` 且文档不存在才允许；否则 `config.etag_mismatch` | 同上（409） |
| 写锁 | 每文档 `SemaphoreSlim`，超时 `config.locked` | `409 config.locked` |
| 备份 | `config/.backup/<UTC时间戳>/<file>`，0600/0700；失败则 `config.backup_failed` 且不写 | `502 host.admin_config.failed`，成功时把 `backupPath` 透出 |
| 原子替换 | 临时文件 + `File.Move(overwrite:true)`，保留原权限 | 无（宿主保证） |
| 符号链接 | 拒绝读写（`config.symlink_not_allowed`） | `409 config.symlink` |
| 文档 id | 仅 `profiles`、`plugin:<pluginId>`；拒绝 `..`/分隔符/secret 文件名 | 插件先跑同一白名单（`400 config.id_invalid`），双保险 |
| 重载提示 | `profiles → RestartHost`；`plugin:<id> → RestartPlugin` | 响应 `reloadHint`/`restartRequired`，UI 明确“需重启宿主” |

**回滚能力边界**：宿主未提供 backup 列表/回读 API（`SaveAsync` 只返回本次备份路径），因此插件的“回滚”是**内存 undo**：保存前读到的原文 + ETag 留在会话内存（每文档 ≤8 条、全局 ≤32 条、30 分钟 TTL），`POST /api/config/{id}/rollback` 用它重新走一次 `SaveAsync`；宿主备份路径在 UI 展示，供运维手动恢复。重启插件后 undo 失效属预期。

写入路径仍只有一条：插件**从不直接读写配置文件**，全部经 `IAdminConfigStore`。

## 5. G6：受限命令终端（已接入）

宿主契约：

```csharp
ValueTask<AdminCommandResult> RunAsync(string command, IReadOnlyList<string> args, ct);
```

宿主实现要点（`src/Natter.Cli.Core/Cli/CliAdminCommandRunner.cs`）：`CliCommandRegistry` + `CliParser` 解析、宿主 allowlist 复核、`SemaphoreSlim` 串行（`ConsoleAccessor` 是进程级状态）、10s 默认超时、`BoundedConsole` 64 KiB 共享输出预算、`IAdminRedactor` 脱敏、`IAdminAuditSink` 审计。**不启子进程、不拼 shell**。

插件侧映射（`WebConsoleAdminGateway.RunAsync`）：

| 宿主结果 | HTTP |
| --- | --- |
| `Executed=true, TimedOut=false` | `200`（`exitCode/stdout/stderr/truncated/outputBytes/durationMs`） |
| `TimedOut=true` | `504 command.timeout`，仍带回部分 stdout/stderr |
| `command.not_allowed` | `403 command.not_allowed` |
| `command.not_registered` / `command.invalid` | `400` |
| `command.busy` | `409 command.busy` |
| `command.cancelled` | `408 command.cancelled` |
| 宿主抛异常 | `502 host.admin_command.failed`（异常消息已脱敏） |

双重门禁：插件先按 `commandAllowlist` 判定（不含即 `403 command.not_allowed`），宿主再按自己的 `AdminOptions.CommandAllowlist` 判定；输出在宿主脱敏后再过一遍 `WebConsoleCommandPolicy.NormalizeOutput`（幂等 + 64 KiB 预算）。

插件侧预算 `commandTimeoutSeconds`（默认 10、上限 10）刻意小于传输层 15s，保证控制台能先返回 504 而不是被 `HttpListener` 层掐断。

## 6. G7：ASP.NET Core / ALC 实测结论

实测宿主产物 `src/Natter.Cli/bin/Debug/net10.0/natter.runtimeconfig.json`：

```json
"framework": { "name": "Microsoft.NETCore.App", "version": "10.0.0" }
```

`PluginLoadContext` 的共享名单（`PluginContractAssemblies`）不含 `Microsoft.AspNetCore.*`，`FrameworkPrefixes` 也不含。插件若 `FrameworkReference Microsoft.AspNetCore.App`：

1. 构建可以过（SDK 有 targeting pack）；
2. 运行时 `Microsoft.AspNetCore.*` 在插件 ALC 里解析不到；
3. 默认 ALC 也没有 ASP.NET Core 共享框架，最终 `FileNotFoundException`。

所以 Phase 1 使用 `System.Net.HttpListener`：

- 属于 `Microsoft.NETCore.App`，宿主 ALC 可直接解析；
- 足够支撑 loopback 只读 UI + JSON API；
- 不支持 HTTP/2、WebSocket、TLS — 与“只绑定 loopback + 反向代理终结 TLS”的部署模型一致；
- 无新增 NuGet/框架依赖，插件包更小。

迁移路径（宿主改动，未实施）：

1. 宿主 head 增加 `FrameworkReference Microsoft.AspNetCore.App`，`runtimeconfig` 出现 `Microsoft.AspNetCore.App`；
2. `PluginContractAssemblies` 增加 `Microsoft.AspNetCore.*` 的显式前缀策略（不能按 `Natter.*` 前缀推断）；
3. 插件提供 `KestrelWebConsoleServer` 实现同一 `IWebConsoleServer` 抽象，配置开关 `server: "kestrel" | "httplistener"`；
4. 一致性测试覆盖两种实现。

---

## 7. G8/G9：日志与脱敏（已接入）

- `/api/logs`：宿主 `IAdminLogReader.ReadAsync(limit)` → `AdminLogBuffer`（有界环形缓冲，`level/category/message/timestamp/sequence`）。插件按 `maxLogLines` 夹取 limit，逐条 `SanitizeText` 后返回；**过滤/分页/导出全部在插件侧完成**（`WebConsoleLogsPolicy`：level/source/q/since/until + limit/offset/order + `format=text|jsonl`，5000 行 / 1 MiB 上限），宿主契约无需扩展；读取失败 → `available:false` + `host.logs.failed`，不会 500。
- `/api/audit`：插件自己的 JSONL 审计（`config.read/config.save/config.rollback/command.run/...`），字段 `time/user/ip/action/target/result/durationMs`。
- 脱敏：宿主 `IAdminRedactor` 负责命令行输出；插件 `WebConsoleRedaction` 负责配置回显、日志行、审计 target 与错误消息（`sk-*`、`ak-*`、`Bearer`、敏感键名）。两者规则一致且有对拍测试。

**双审计**：宿主 `JsonlAdminAuditSink` 记 `admin.config.save`/`admin.command.run`，插件记 `config.write`/`command.run`/`auth.role_denied`/`console.read_only`/`logs.export`。两边都只记命令名/文档 id/字节数/行数，不记内容。

**轮转归属**：宿主缓冲按 `AdminOptions.MaxLogEntries` 自动淘汰；插件审计超过 1 MiB 轮转为 `.1`。长期留存/采集/轮转策略属于运维文档范围（本插件不实现文件轮转）。

## 8. 对验收的影响

| 验收项（设计 §15） | 状态 | 说明 |
| --- | --- | --- |
| 默认只绑定 loopback | 达成 | 非 loopback 直接激活失败 |
| 无 token 不启动 | 达成 | 缺 secret → `webconsole.token-missing` |
| 只读 dashboard 可看 health/plugins/upstreams/profiles/logs | 达成 | health/upstreams/profiles 走 `/health`；plugins 走 `IPluginRuntimeDiagnostics`；logs 走 `IAdminLogReader` |
| secret 不返回 | 达成 | 配置回显/命令输出/日志/审计四路脱敏，单测覆盖 |
| 配置编辑 schema/diff/backup/lock | 达成 | 宿主 schema 校验 + 有界 diff + `.backup` + 写锁 + 原子替换；插件 400/409/502 映射 |
| 配置回滚 | 部分达成 | 无宿主 backup 回读契约 → 插件内存 undo（重启失效），备份路径展示供手动恢复 |
| 路径白名单 | 达成 | 文档 id 白名单（插件+宿主双份）、静态资源白名单、符号链接拒绝 |
| 命令 allowlist | 达成 | 插件 + 宿主双重 allowlist，argv 语义，超时/输出上限 |
| 审计 | 达成 | 插件 JSONL + 宿主 `admin-audit.jsonl` |
| file plugin 可加载 | 达成 | `./scripts/stage-webconsole.sh` + E2E |
| ASP.NET Core ALC 正常 | 不适用 | 仍用 `HttpListener`（G7 未变） |
| Android 不加载 | 达成 | manifest `platforms` 不含 android |
| 不阻塞 P5 | 达成 | 插件目录 + 文档；本任务未改 `src/` |

### 8.1 接入后新增/变化的插件配置

| 键 | 默认 | 作用 |
| --- | --- | --- |
| `commandTimeoutSeconds` | `10` | 插件侧命令预算（1–10，小于传输层 15s） |
| `viewerToken` | `""` | 可选只读凭据；`${secret:WEBCONSOLE_VIEWER_TOKEN}` 时启用 viewer 登录 |
| `permissions` | — | manifest 新增 `admin.read`、`admin.config.write`、`admin.command.execute`（权限缺失即回落 501） |

### 8.2 已修复问题（宿主侧）

**OBS-003 · `admin.command.execute` 会让插件 CLI 命令从 CLI 表层消失（已修复 `0f3d61c`）**

- 触发：插件声明 `admin.command.execute` → 宿主 `BindAdminServices` 在**插件激活中**解析 `IAdminCommandRunner` → `CliAdminCommandRunner` 构造参数 `CliCommandRegistry` 被提前实例化 → 其构造时枚举 `ICliCommandContributor`，而 `NpsPluginRuntime.GetCliCommands()` 过滤 `State == Activated`，此刻插件尚未 Activated → 得到空快照并被单例固化。
- 影响：所有插件贡献的 CLI 命令（`web status`、`greet`、`metrics` …）在任何后续解析中都不存在；`natter doctor` 显示 `commands: 32 registered`（缺 4 个插件命令），但 `natter plugins contributions` 仍显示 active。
- 规避（临时）：E2E 断言保留，等待宿主修好后回归；不要为了绕过而从 manifest 去掉该权限（否则 Phase 3 回到 501）。
- 修复（`0f3d61c`）：`CliAdminCommandRunner` 不再在构造期解析 `CliCommandRegistry`，改为 `RunAsync` 内首次取用；命令表在插件全部激活后才成形。
- 回归证据：`natter web status --json` 恢复（`state=external`，`admin` 三项均 `available`）；`natter greet alice` 恢复；`natter doctor` 显示 `commands: 35`（含插件命令）；`./scripts/webconsole-e2e.sh` 全绿。

## 9. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-18 | 初版：登记 G1–G10，确定 health endpoint 为 Phase 1 数据源，确定 HttpListener 过渡实现 |
| 2026-11-18 | Phase 2/3 插件侧：配置校验/ETag/diff、命令 allowlist/截断与 validate 端点；写入/执行仍待宿主 |
