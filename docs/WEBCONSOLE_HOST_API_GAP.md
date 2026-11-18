# Natter WebConsole 宿主契约缺口与适配方案

> **状态**：Living document · 随实现更新  
> **日期**：2026-11-18  
> **关联**：[WebConsole 插件设计](WEBCONSOLE_PLUGIN_DESIGN.md)、[NPS v1 规范](PLUGIN_SPEC.md)、[Harness 架构](HARNESS_ARCHITECTURE.md)、[运维手册](OPERATIONS_RUNBOOK.md)  
> **约束**：本文件记录宿主（`src/`）**尚不存在**的接口与模型，以及插件侧的临时适配。按项目约束，WebConsole 实现**不修改主项目**来补齐这些契约；先文档化，再走独立提案。

---

## 0. 结论摘要

| # | 依赖 | 宿主现状 | 缺口 | 插件临时策略 |
| --- | --- | --- | --- | --- |
| G1 | manifest `kind: "admin"` | `PluginKind` 仅 `transport/feature/tool/memory`，manifest 校验拒绝其他值 | 无 admin kind | 用 `kind: "tool"` 发布，语义在 README/manifest description 中说明 |
| G2 | contribution `admin.ui` / `admin.api` / `admin.command` | `PluginContributionTypes` 与 manifest 校验只认 8 种 v1 类型 | 无管理面贡献点 | 不声明管理贡献；在 `ActivateAsync` 启动 loopback 服务，CLI 只贡献 `cli.command` |
| G3 | `IAdminDiagnostics` / `AdminSnapshot` / `AdminLogEntry` | 不存在 | 插件拿不到宿主聚合快照 | 插件内定义 `IWebConsoleHostPort`，数据源走已存在的 `serve --health-port` `/health` |
| G4 | `IPluginRuntimeDiagnostics` 桥接 | 接口存在，宿主 DI 已注册；**未注入插件 provider** | 插件无法读取 plugin graph/contributions/permissions | 插件 provider 里 `GetService<IPluginRuntimeDiagnostics>()`，为空则 `/plugins` 降级为 health 里的计数/失败项 |
| G5 | `IAdminConfigStore` | 不存在 | 配置编辑无路径白名单、schema/diff/backup/lock/ETag | Phase 2 端点在未注入端口时返回 `501 admin_api_unavailable`，UI 只读 |
| G6 | `IAdminCommandRunner` | 不存在；CLI 无执行治理权限词 | 受限命令终端无法安全执行 | Phase 3 端点返回 `501`，`commandAllowlist` 仅做配置校验 |
| G7 | ASP.NET Core 宿主管线 | host `runtimeconfig` 只有 `Microsoft.NETCore.App`；`PluginLoadContext` 不共享 `Microsoft.AspNetCore.*` | Kestrel/ASP.NET Core 无法从插件 ALC 加载 | Phase 1 用 BCL `HttpListener`（`WebConsoleServer`），不加 `FrameworkReference` |
| G8 | 宿主日志 tail API | `IPluginLog` 只写；无读取端口 | `/logs` 无法取宿主日志 | `/logs` 返回 `unavailable`；`/api/audit` 返回插件审计记录 |
| G9 | 统一脱敏服务 | `RuntimeHealth.Sanitize` 是 CLI 内部静态方法，非公开契约 | 插件不能复用宿主脱敏 | 插件内 `WebConsoleRedaction`，规则与宿主保持一致并单测 |
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

### 2.1 提议的宿主契约（未实现）

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

### 2.3 G4 的最小宿主改动建议（一行桥接）

`NpsPluginRuntime.BuildPluginProvider` 增加：

```csharp
services.AddSingleton(_hostProvider.GetRequiredService<IPluginRuntimeDiagnostics>());
```

这不会扩大插件能力面：`IPluginRuntimeDiagnostics` 本身就是只读诊断契约，且已在宿主 DI 注册。当前实现不修改宿主，仅在插件侧做空值降级。

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

## 4. G5：配置编辑

设计要求的保存链路是：

```text
read → edit → validate schema → diff → backup → lock → write → reload/restart hint
```

宿主目前没有暴露：

- 可编辑文档 ID（`profiles` / `plugin:<id>`）到真实路径的映射；
- schema 校验入口（`NpsPluginConfigLoader` 是 Infrastructure internal）；
- 备份目录、写锁、ETag/版本；
- 热重载/重启提示。

插件**不会**自行猜测 `config/`、`profiles.json` 路径，也不做任意文件读写。Phase 2 在 `IAdminConfigStore` 注入前：

- `POST /api/config/{id}` → `501 admin_api_unavailable`；
- `/api/config` 仅返回本插件配置的脱敏预览；
- `allowConfigWrite=true` 只是“允许未来写入”，不会绕过端口探测。

提议契约为设计文档 §8.3 的 `IAdminConfigStore`，补充一条：返回的 `AdminConfigDocument` 必须带 `Etag` 与 `LastModified`，保存必须原子替换（`File.Move(overwrite: true)`）。

---

## 5. G6：受限命令终端

宿主 CLI 命令在进程内注册（`ICliCommandContributor`），插件 provider 只能拿到自己被注入的服务；`IChatCommandDispatcher` 是聊天命令，不是管理命令。

因此插件不做以下兜底：

- 不 `Process.Start("natter ...")`；
- 不执行 shell；
- 不解析用户输入拼命令行。

`allowCommandConsole` 与 `commandAllowlist` 只做配置校验和 UI 展示。等 `IAdminCommandRunner`（设计 §9.1）落地后，命令白名单映射到宿主注册的 CLI 命令，插件只做 HTTP → runner 的转发和审计。

提议补充权限词：`admin.command.execute`，并在宿主侧对每条命令做 allowlist + 参数 schema，而不是在插件里做字符串匹配。

---

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

## 7. G8/G9：日志与脱敏

- `/logs`：无宿主日志读取端口 → `available: false`，`gapCode: host.logs.unavailable`；
- `/api/audit`：返回插件自己的审计 JSONL（登录、快照、配置读取、命令拒绝、配置写入尝试），字段 `time/user/ip/action/target/result/durationMs`；
- 脱敏：插件内 `WebConsoleRedaction`，规则：
  - 键名匹配 `token|secret|password|passwd|api[-_]?key|authorization|cookie|credential|private[-_]?key|connection[-_]?string` → `***`；
  - 值形如 `${secret:NAME}` → 保留引用，不展开；
  - 超长字符串截断到 2000 字符并标记；
  - 审计/日志只记录摘要与长度，不记录完整输出。

等宿主提供公共 `IAdminRedactor` 后，插件改为注入该服务，保留本地实现作为 fallback。

---

## 8. 对验收的影响

| 验收项（设计 §15） | Phase 1 状态 | 说明 |
| --- | --- | --- |
| 默认只绑定 loopback | 达成 | 非 loopback 直接激活失败 |
| 无 token 不启动 | 达成 | 缺 secret → `webconsole.token-missing` |
| 只读 dashboard 可看 health/plugins/upstreams/profiles/logs | 部分达成 | health/upstreams/providers/plugins 计数可用；logs 缺口 G8 |
| secret 不返回 | 达成 | 配置预览只保留 `${secret:...}` 引用 |
| 配置编辑 schema/diff/backup/lock | 未达成 | 缺口 G5，端点 501 |
| 路径白名单 | 达成（只读侧） | 静态资源白名单，无任意文件读取 |
| 命令 allowlist | 未达成 | 缺口 G6，端点 501 |
| 审计 | 达成 | JSONL 审计 + 保留期由运维负责 |
| file plugin 可加载 | 待集成验证 | 插件目录 staging 见 `docs/WEBCONSOLE_PLUGIN.md` |
| ASP.NET Core ALC 正常 | 不适用 | 用 HttpListener 规避，见 G7 |
| Android 不加载 | 达成 | manifest `platforms` 不含 android；宿主侧仍需按平台过滤 |
| 不阻塞 P5 | 达成 | 独立插件目录，无 `src/` 改动 |

---

## 9. 变更记录

| 日期 | 变更 |
| --- | --- |
| 2026-11-18 | 初版：登记 G1–G10，确定 health endpoint 为 Phase 1 数据源，确定 HttpListener 过渡实现 |
