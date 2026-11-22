# Natter

> 六边形架构（Ports & Adapters）+ DDD 的 .NET 10 **框架/脚手架**：统一跨平台 CLI 入口、DI 装配可插拔端口适配器、Rx 进程内领域事件流、NLMP 聊天语义模型、可加载示例插件，并支持 Windows / Linux / macOS / Android 发布。

Natter 只交付**框架能力**：端口契约、组合根、插件加载、DI 装配、Rx 事件总线、CLI 骨架与最小示例。`Counter`、`Greeting` 是用于验证装配和事件流的示例，不是业务系统。

## 能力概览

- **DDD 基元**：`Entity`、`ValueObject`、`AggregateRoot`、领域事件、`Specification`、`Guard`。
- **六边形端口**：入站请求分发器，出站 `IRepository<,>`、`IUnitOfWork`、`IClock`、`IDomainEventPublisher`、`IGreetingPort`。
- **组合根**：`NatterApplicationBuilder` + `ICompositionModule`，宿主不引用具体适配器。
- **Rx 事件流**：`System.Reactive` 热流、类型过滤、故障订阅者隔离、事件序列号、内存回放日志。
- **NLMP 聊天模型**：`Source / Conversation / Message / Event / Command / Capabilities`，能力降级、事件去重、幂等发送、长文本分片、可选 JSON 投影。
- **Satori 适配映射**：`SatoriChatUpstream` + `ISatoriTransport`，message elements → 文本/附件/mentions、`login.features` → capabilities、`sn` 恢复与重放去重。
- **NPS v1 插件内核**：manifest/schema、Host API、类型化 contribution、依赖图与 host API 校验、配置 schema、秘密、权限、插件本地 DI、激活回滚、legacy 兼容。
- **Android 插件宿主**：平台无关的签名包校验/安装/回退 store；embedded 默认，`DynamicPlugins` 构建从 app 私有目录加载，AOT/Trim 关闭；Android Keystore 秘密提供者与 debug inbox。
- **Discord transport**：`natter.transport.discord` NPS 插件；Discord Gateway/REST 抽象、NLMP 双向映射、allowlist/bot/mention 过滤、分片、slash interaction、`AllowedMentions.None` 与 `discord status`。
- **DeepSeek AI responder**：`natter.feature.ai.deepseek`；Microsoft.Extensions.AI `IChatClient`、自定义 DeepSeek Chat Completions client、thinking/reasoning 隔离、host 会话历史、按会话串行、幂等、错误降级与 `ai status`。
- **第二上游/消费者**：`natter.transport.dingtalk`（Stream WebSocket + 本机/真实 gateway）与 `natter.feature.ai.spark`（MaaS OpenAI-compatible `IChatClient`）；复用共享 responder/context 与 host conversation store。
- **插件机制**：目录扫描、`plugin.json` 清单、可回收 `AssemblyLoadContext`、端口适配器覆盖、CLI 命令贡献、生命周期订阅。
- **CLI 内核**：平台无关的命令注册表、解析器、帮助、JSON 输出、`exec` 同宿主命令链；Console 与 Android 宿主共用。
- **多平台发布**：自包含单文件（win/linux/osx）+ Android APK；示例插件随发布目录部署或嵌入宿主。
- **Runtime/Profile**：`profiles.json` 显式 provider 路由、SQLite 会话、`serve`/`health`、`--config-dir`/`--state-dir`、Tier 1/2 启动策略。
- **Context/Memory**：host prompt/context/memory pipeline、SQLite `summaries`/`memory_items`、确定性摘要、显式 `remember`/`forget`、scope 隔离。
- **P4 Tier 1 UX**：Discord `/ask` `/remember` `/forget` `/memory` `/reset`，sample `memory.store` 插件，deploy/backup 示例。
- **P5 Task Runtime**：AgentTask / SQLite task store / TaskDispatcher / clock tool / `/task` `/status` `/tasks` `/cancel`，lease/cancel/timeout 与 health task metrics。
- **测试**：框架级测试覆盖聚合事件、Rx 总线、DI 管道、解析器、插件加载、CLI、NLMP 一致性与 NPS v1 插件内核（**用例数不在此写死，以 CI 实时输出为准**；本地 `dotnet test tests/Natter.Framework.Tests` 会打印总数）；另有可选的 live 套件对真实 Discord 与 DeepSeek API 做分层联调（见 [真实 API 联调测试方案](docs/LIVE_TEST_PLAN.md)）。

## 架构

```text
                        ┌──────────────────────────────────────────┐
                        │  Hosts                                   │
                        │  Natter.Cli (Console)                    │
                        │  Natter.Hosting.Android (Activity)       │
                        │  tests (in-process)                      │
                        └──────────────────┬───────────────────────┘
                                           │  NatterCli.RunAsync(...)
                                           ▼
                        ┌──────────────────────────────────────────┐
                        │  Natter.Cli.Core                         │
                        │  command registry · parser · built-ins   │
                        └──────────────────┬───────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Infrastructure — Composition Root                            │
        │  NatterApplicationBuilder · NatterHost · NPS runtime · Rx bus        │
        │  Default adapters: clock · repository · UoW · journal · greeting     │
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Application — use cases, dispatcher, pipeline, chat orchestrator│
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Chat (NLMP) — model · keys · events · commands · capabilities│
        │  Natter.Chat.Satori — Satori event/element/feature/API mapping        │
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Domain — demo aggregate (Counter)                             │
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Abstractions — DDD primitives · ports · event/plugin contracts│
        └──────────────────────────────────────────────────────────────────────┘

  NPS v1 plugins ──implement──▶ Natter.Plugin.Abstractions（+ Natter.Chat/Cli 契约）
  Legacy plugins ──implement──▶ Natter.Abstractions + Natter.Cli.Abstractions
          ──loaded by──▶ collectible AssemblyLoadContext
```

依赖方向始终向**内核**（`Natter.Abstractions`）收敛：Domain 不引用 Infrastructure，Application 不引用宿主，适配器只在外围装配。

## 工程结构

> **权威清单以 `Natter.slnx` 为准**：`dotnet sln Natter.slnx list`。本表按「层 / 用途」解释每个工程做什么，**不记录工程总数**（增删工程时同步本表即可，数字不再写死）。

| 项目 | 层 | 内容 |
| --- | --- | --- |
| `src/Natter.Abstractions` | 内核 | DDD 基元、端口、事件/插件/组合契约 |
| `src/Natter.Domain` | 领域 | 示例 `Counter` 聚合与领域事件 |
| `src/Natter.Application` | 应用 | `IApplicationDispatcher` 实现、管道、示例用例 |
| `src/Natter.Infrastructure` | 适配器/组合根 | Rx 总线、内存适配器、插件加载器、DI 组合、NLMP 事件入口与命令调度 |
| `src/Natter.Chat` | 聊天模型 | NLMP 0.1 模型、复合键、事件/命令、能力降级、幂等/去重、JSON 投影 |
| `src/Natter.Chat.Satori` | 协议适配 | Satori 上游/传输边界、事件、message elements、features、API 与会话序号映射 |
| `src/Natter.Plugin.Abstractions` | 插件 Host API | NPS v1 manifest、INatterPlugin/IPluginContext、contribution、配置 schema、秘密、权限与诊断模型 |
| `src/Natter.Plugin.Store` | 插件包/存储 | `.natter-plugin.zip` 校验、哈希/ECDSA 签名、app 私有 store、安装/更新/回退/卸载 |
| `src/Natter.Chat.Discord` | Discord 适配 | Discord.Net transport、DTO 映射、NLMP upstream、能力与错误映射 |
| `src/Natter.Feature.Ai.DeepSeek` | AI responder | M.E.AI `IChatClient`、DeepSeek HTTP adapter、历史/并发/幂等与 `ai status` |
| `src/Natter.Feature.Ai` | 共享 AI 边界 | `AiChatResponder`、`IAiContextAssembler`、`IAiConversationStore`、会话 gate |
| `src/Natter.Chat.DingTalk` | 钉钉 transport | Stream WebSocket（gateway/ticket/ack/重连）+ callback parser + sessionWebhook 回复 |
| `src/Natter.Feature.Ai.Spark` | 星火 provider | OpenAI-compatible `IChatClient` + shared responder |
| `src/Natter.Cli.Abstractions` | CLI 契约 | `IConsole`、命令/选项描述符、处理器 |
| `src/Natter.Cli.Core` | CLI 内核 | 解析、注册表、内建命令、JSON/表格输出 |
| `src/Natter.Cli` | 宿主 | Console 入口，发布为 `natter` / `natter.exe` |
| `src/Natter.Hosting.Android` | 宿主 | `net10.0-android` Activity 宿主，嵌入示例插件 |
| `samples/Natter.Sample.Plugin.Greetings` | 插件 | 覆盖 `IGreetingPort`、贡献 `greet` 命令、订阅事件 |
| `samples/Natter.Sample.Plugin.Metrics` | 插件 | 独立消费 Rx 流、贡献 `metrics` 命令 |
| `samples/Natter.Sample.Plugin.Memory` | memory.store 示例 | 最小 NPS memory 插件 + profile 选择 |
| `samples/Natter.Sample.Plugin.Discord` | Discord NPS 插件 | `natter.transport.discord` manifest/config/status，真实网关的 live 入口 |
| `samples/Natter.Sample.Plugin.DingTalk` | 钉钉 NPS 插件 | `natter.transport.dingtalk` manifest/config/status |
| `plugins/Natter.Plugin.PromptLibrary` | Prompt 插件 | prompt provider 插件：manifest 声明 `prompt.provider` + `cli.command`，运行时注册 `PromptLibraryProvider`；权限含只读文件与 `chat.read` |
| `plugins/Natter.Plugin.WebConsole` | 管理面插件 | 只读诊断、配置编辑、受限命令台；独立 token、审计与诊断包导出 |
| `plugins/Natter.Plugin.ConversationConsole` | 管理面插件 | 会话只读回放（`IConversationQuery`），审计不记消息正文 |
| `plugins/Natter.Plugin.StateVitals` | 内容插件 | State & Vitals：断言态/vital/effects/trigger + pack 定义与 `states` CLI |
| `plugins/Natter.Plugin.AiEmbedding.OpenAi` | AI 端口插件 | OpenAI 兼容 `/v1/embeddings` 的 `embedding.provider` 实现（测试用 fake handler） |
| `tests/Natter.Framework.Tests` | 测试 | xUnit 框架测试（含插件契约、组合根、Admin 宿主契约等） |
| `tests/Natter.Live.Tests` | 测试 | 可选 live 集成测试：真实 DeepSeek/Spark/Discord/钉钉、端到端与独立 CLI 部署（见 [联调方案](docs/LIVE_TEST_PLAN.md)） |
| `tests/Natter.Plugin.PromptLibrary.Tests` | 测试 | Prompt Library 插件单测 |
| `tests/Natter.Plugin.WebConsole.Tests` | 测试 | WebConsole 插件单测 + Phase 2/3 验收 + UI 渲染 harness |
| `tests/Natter.Plugin.ConversationConsole.Tests` | 测试 | 会话控制台插件单测（认证、只读、审计、静态资源） |
| `tests/Natter.Plugin.StateVitals.Tests` | 测试 | State & Vitals 插件单测（内核、pack 校验、CLI） |
| `tests/Natter.Plugin.AiEmbedding.OpenAi.Tests` | 测试 | embedding 插件单测（请求体形状、认证、错误映射，不联网） |

## 快速开始

要求：**.NET SDK 10.0.1xx**。Android 额外需要 `android` workload、Android SDK 与 JDK 17+。

```bash
dotnet build src/Natter.Cli/Natter.Cli.csproj

dotnet run --project src/Natter.Cli -- doctor
dotnet run --project src/Natter.Cli -- demo --name demo --times 3
dotnet run --project src/Natter.Cli -- ports
dotnet run --project src/Natter.Cli -- plugins list
dotnet run --project src/Natter.Cli -- upstreams
dotnet run --project src/Natter.Cli -- satori status --features message.update,message.delete

# 同一个宿主里串行执行命令，插件状态与事件日志共享
dotnet run --project src/Natter.Cli -- exec "demo --times 3" "metrics" "greetings recent"

dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj

# 可选：对真实 Discord / DeepSeek API 的联调测试（需要 artifacts/live/.env）
cp tests/Natter.Live.Tests/.env.example artifacts/live/.env && chmod 600 artifacts/live/.env
scripts/live-test.sh all
```

构建时两个示例插件会自动复制到 `src/Natter.Cli/bin/<Configuration>/net10.0/plugins/<id>/`，CLI 启动即加载。

典型输出：

```text
$ natter exec "demo --times 2" "metrics"
Hey demo, this greeting came from the greetings plugin adapter (07:31:23 UTC).
counter 'demo' (...) = 2 after 2 increment(s) of 1.
3 domain event(s) dispatched into the Rx stream:
...
$ natter metrics
Domain events observed by the metrics plugin: 3
  CounterCreated           1
  CounterIncremented       2
```

## CLI 命令

```text
natter <command> [arguments] [options]
```

| 命令 | 说明 | 示例 |
| --- | --- | --- |
| `version [--json]` | 框架、运行时、RID、OS 信息 |
| `doctor [--json]` | 组合结果：插件、端口适配器、事件总线、命令数 |
| `ports [--port <过滤>] [--json]` | 端口 → 适配器绑定（含插件覆盖） |
| `upstreams [--source <过滤>] [--json]` | NLMP 上游、连接状态与协商能力 |
| `satori status [--features ...] [--json]` | Satori feature → capability 与 API 路由诊断 |
| `plugins list --detail [--json]` | 插件状态、NPS kind/hostApi/trust/contributions |
| `plugins graph [--json]` | NPS 依赖/顺序图（节点与边、循环/冲突） |
| `plugins doctor <id> [--json]` | manifest/config/secret/权限/激活诊断 |
| `plugins contributions [--json]` | 声明/暂存/激活的 contribution |
| `plugins permissions [--json]` | 请求/授予/拒绝的权限 |
| `plugins config validate <id> [--json]` | 配置 schema 校验（JSON Pointer 错误） |
| `plugins store list [--root <dir>] [--json]` | app 私有插件 store 记录 |
| `plugins store install <package> [--root ...] [--trusted-key ...]` | 校验并安装/更新签名插件包 |
| `plugins store enable\|disable\|rollback\|uninstall <id>` | 启用/禁用/回退/卸载（重启生效） |
| `plugins store cleanup` | 清理 staging/trash |
| `discord status [--json]` | Discord 连接、Source、延迟、allowlist 与 capabilities |
| `ai status [--json]` | DeepSeek model/thinking、请求/错误、延迟、token 与缓存命中 |
| `dingtalk status [--json]` | 钉钉 Stream source/allowlist/连接状态 |
| `spark status [--json]` | 星火 model/请求/错误/延迟/token |
| `serve [--duration <s>] [--json]` | 长驻 host；加载 profile/upstream，等待取消或到时退出 |
| `health [--json] [--offline]` | upstream/profile/provider/plugin/会话健康快照；`--offline` 不启动 gateway |
| `remember <text> [--scope]` | 显式、用户确认的长期记忆 | `remember "likes tea" --scope user --source ...` |
| `forget <id\|text>` | 删除显式记忆并失效 summary | `forget "likes tea"` |
| `memory list [--limit]` | 查看当前 source/conversation/actor 可见记忆 | `memory list --json` |
| `memory export [--output]` | 导出当前 scope memory（文件 0600） | `memory export --output memory.json` |
| `memory import --file` | 导入并校验 scope/schema | `memory import --file memory.json` |
| `/task prompt:<text>` | 创建 AgentTask（Discord slash） | `task` |
| `/status task:<id>` | 查询任务状态 | `status` |
| `/tasks` | 当前 session active task | `tasks` |
| `/cancel task:<id>` | 取消创建者任务 | `cancel` |
| `plugins list [--failed] [--json]` | 已加载/失败的插件 |
| `plugins inspect <directory> [--json]` | 只探测插件目录，不注册 |
| `demo [--name] [--amount] [--times] [--json]` | 聚合 → 仓储 → 领域事件 → Rx 全链路 |
| `events list [--limit] [--json]` | 读取内存事件日志 |
| `events watch [--seconds]` | 实时订阅 Rx 流 |
| `events clear` | 清空事件日志 |
| `exec <command> [more...]` | 同一宿主内执行多条命令 |
| `greet <name>` | 示例插件命令（插件贡献） |
| `greetings recent [--json]` | 插件订阅到的事件（插件贡献） |
| `metrics [--json]` | 示例插件计数器（插件贡献） |
| `help [command]` | 帮助 |

全局选项：`--plugin-dir <path>`（可重复）、`--no-plugins`（禁用所有插件加载，包括显式 `--plugin-dir`）、`--config-dir <path>`、`--state-dir <path>`、`--profile <id>`、`--verbose/-v`、`--trace-events`、`-h/--help`。

> 注意：普通 CLI 每次调用都会新建宿主，因此 `metrics`、`events list` 的单次进程内状态是空的。用 `exec` 在**同一个宿主**里串联命令即可观察插件共享状态。

## 扩展点

| 想做的事 | 扩展方式 |
| --- | --- |
| 替换/新增出站适配器 | 实现端口接口，注册 `AddPortAdapter<TService, TImpl>()`；插件里用 `IPluginRegistrationContext.AddPortAdapter` |
| 新增用例 | 实现 `IRequestHandler<TRequest, TResponse>` 并注册到 DI |
| 横切逻辑 | 实现 `IPipelineBehavior<,>`，注册为开放泛型 |
| 贡献 CLI 命令 | 实现 `ICliCommandContributor`（内置 `ICliCommandHandler`） |
| 消费领域事件 | 实现 `IPluginLifecycle`，在 `StartAsync` 订阅 `IDomainEventStream` |
| 编写新插件 | 按 NPS v1：`plugin.json` + `INatterPlugin` + `IContributionRegistry`，见 [插件规范](docs/PLUGIN_SPEC.md) |
| 接入聊天平台 | 实现 `IChatUpstream`，注册到 `IChatUpstreamRegistry`；命令统一走 `IChatCommandDispatcher` |
| 接入 AI/回复逻辑 | 实现 `IChatResponder`，用 `AddChatOrchestrator<TResponder>()` 接到 NLMP 事件流 |
| 新增宿主 | 调用 `NatterCli.RunAsync(args, IConsole, options, configure)` |
| 替换仓储/事务 | 覆盖 `IRepository<,>` / `IUnitOfWork` 的注册 |
| 新增组合模块 | 实现 `ICompositionModule`，`builder.AddModule<T>()` |

## 发布

| 平台 | 已验证产物 | 命令 |
| --- | --- | --- |
| Linux x64 | 自包含单文件 `natter` | `./scripts/publish.sh linux-x64` |
| Windows x64 | 自包含单文件 `natter.exe` | `./scripts/publish.sh win-x64` |
| macOS arm64 | 自包含单文件 `natter`（Mach-O） | `./scripts/publish.sh osx-arm64` |
| Android | `dev.natter.hosting.android-Signed.apk` | `./scripts/publish-android.sh` |

脚本默认发布完整 RID 集合（linux-x64/arm64、osx-x64/arm64、win-x64/arm64）。发布目录布局：

```text
artifacts/cli/linux-x64/
├── natter
└── plugins/
    ├── greetings/{plugin.json, Natter.Sample.Plugin.Greetings.dll}
    └── metrics/{plugin.json, Natter.Sample.Plugin.Metrics.dll}
```

Android 产物位于 `artifacts/android/`。示例构建使用默认调试签名；生产发布请配置 `AndroidKeyStore` 等签名属性。详见 [发布指南](docs/PUBLISHING.md)。

## 文档

- [开发指南](docs/DEVELOPMENT.md)：环境、日常循环、扩展 recipe、DI 约定、测试与调试。
- [真实 API 联调测试方案](docs/LIVE_TEST_PLAN.md)：分层测试矩阵、真实执行结果、部署级验证与问题登记。
- [测试记录](docs/testing/README.md)：每轮测试的时间、内容、具体问题与临时改动登记（测试不负责修复）。
- [轻量消息协议模型 (NLMP)](docs/NLMP.md)：源/会话/消息/事件/命令/能力模型，以及 Satori 映射。
- [架构设计](docs/ARCHITECTURE.md)：六边形映射、组合根顺序、Rx 语义、插件加载器。
- [Harness 架构](docs/HARNESS_ARCHITECTURE.md)：控制面/执行面、Session/Task、上下文缓存、记忆持久化与多平台演进。
- [Context & Memory 插件](docs/CONTEXT_MEMORY_PLUGIN.md)：Prompt/Context/Memory 契约、Profile、NPS contribution、数据所有权与测试。
- [第二上游/消费者验证](docs/SECOND_ADAPTER_VALIDATION.md)：钉钉 + 星火的接入测试矩阵、边界与验收。
- [P2 Runtime & Profile 开发文档](docs/P2_RUNTIME_PROFILE.md)：serve、profiles、provider 路由、SQLite 会话、health 与一等/二等公民策略。
- [P3 Context & Memory 开发文档](docs/P3_CONTEXT_MEMORY.md)：Prompt/Context/Memory 契约、profile 扩展、SQLite 摘要/记忆、NPS contribution 与验收。
- [P4 Tier 1 产品化](docs/P4_TIER1_PRODUCTIZATION.md)：Discord 命令 UX、memory 用户入口、DeepSeek 稳定性、serve 部署/备份/24h soak 与发布闸门。
- [P5 Agent Task Runtime](docs/P5_AGENT_TASK_RUNTIME.md)：AgentTask、TaskStore、Worker、Tools、取消/超时/重启恢复与安全边界。
- [Prompt Compiler 设计](docs/PROMPT_COMPILER.md)：片段、预设、来源、排序、关闭/启用、版本与安全边界。
- [Harness 责任边界](docs/HARNESS_RESPONSIBILITY.md)：机制归 host、内容归插件；默认值≠强制，安全条款可关闭。
- [P5 验收测试计划](docs/testing/2026-11-18-p5-acceptance.md)：/task、lease、重启幂等、工具 allowlist 与健康指标。
- [WebConsole 插件设计](docs/WEBCONSOLE_PLUGIN_DESIGN.md)：只读诊断、配置编辑、受限命令终端、认证/反向代理与 ASP.NET Core 加载风险。
- [WebConsole 插件使用](docs/WEBCONSOLE_PLUGIN.md)：staging、配置、反向代理、排障与已知限制。
- [WebConsole 宿主契约缺口](docs/WEBCONSOLE_HOST_API_GAP.md)：G1–G10（admin kind/contribution、IAdmin*、日志、脱敏、ALC）与临时适配。
- [依赖与类型演进策略](docs/DEPENDENCY_EVOLUTION_POLICY.md)：依赖检查/升级周期、net10→net12 LTS、插件 ABI、C# union/closed hierarchy 与 Result 边界。
- [插件开发](docs/PLUGINS.md)：清单、适配器覆盖、CLI 命令、生命周期、嵌入模式。
- [插件规范（NPS v1）](docs/PLUGIN_SPEC.md)：manifest、contribution、依赖图、Host API、配置/秘密、权限与激活回滚。
- [Android 插件宿主](docs/ANDROID_PLUGIN_HOST.md)：embedded/dynamic 双发行、AOT off 动态加载、签名安装与回滚。
- [Discord 上游设计](docs/DISCORD_PLUGIN_DESIGN.md)：manifest、config、transport 边界、NLMP 映射、slash 前置与测试。
- [AI Provider 设计](docs/AI_PROVIDER_DESIGN.md)：Microsoft.Extensions.AI、IChatClient、DeepSeek 适配、thinking/reasoning 与 responder 设计。
- [插件 Manifest Schema](docs/plugin.schema.json)：NPS v1 `plugin.json` 的机器可读 JSON Schema。
- [发布指南](docs/PUBLISHING.md)：RID 矩阵、单文件、Android 签名与裁剪。
- [运维手册](docs/OPERATIONS_RUNBOOK.md)：部署、secret/state、health、备份恢复、升级回滚与故障处理。
- [P4.5 测试验证](docs/testing/2026-11-17-p4-5-test-verification.md) / [运维验证](docs/operations/2026-11-17-p4-5-ops-verification.md)：代理、health/offline、systemd/Docker、OBS-007 与 24h soak 交接。
- [P4.5 实机验收交接](docs/operations/P4.5_REAL_MACHINE_HANDOFF.md)：root/systemd、真实 P5 命令、24h soak、secret 轮换与 Go 条件。

## 定位与边界

- 示例 `Counter` 只用于演示聚合、仓储、事件派发和 Rx 订阅；不包含任何业务实现。
- 默认适配器均为内存实现，用于脚手架自检；真实项目应替换为数据库/消息/HTTP 适配器。
- Rx 总线是**进程内热流**，没有 outbox、持久化或重试语义；跨进程事件请新增适配器。
- 插件卸载是 best-effort；DI 单例持有插件对象的期间，`AssemblyLoadContext` 不会真正回收。
- Android 无法扫描 APK 内的插件目录，因此示例以 embedded plugin 方式注册。
- NLMP 默认是进程内模型：不包含远程 transport、握手或服务发现；`Natter.Chat.Satori` 只做映射，不包含 WebSocket/HTTP 客户端。
- Satori message elements 是有损映射（v0.1 只保留文本、附件和 mentions）；富文本/按钮/复杂布局需要后续能力扩展。
- NPS v1 Phase 1 已交付内核；Android 动态 store/签名包/host 骨架已交付；Discord transport Phase 1/2 已交付；DeepSeek AI responder 核心已交付；第二上游/消费者（钉钉 Stream + 星火 MaaS）已完成代码、本机 WebSocket 与 fake HTTP 交叉验证，真实平台凭据未接入。management UI、真机矩阵、真实钉钉/星火 live、插件市场/热禁用与 out-of-process 宿主仍待补，见 [插件规范 §16](docs/PLUGIN_SPEC.md)、[Android 插件宿主](docs/ANDROID_PLUGIN_HOST.md)、[Discord 设计](docs/DISCORD_PLUGIN_DESIGN.md)、[AI Provider 设计](docs/AI_PROVIDER_DESIGN.md) 与 [第二上游验证](docs/SECOND_ADAPTER_VALIDATION.md)。

## 许可

MIT
