# Natter

> 六边形架构（Ports & Adapters）+ DDD 的 .NET 10 **框架/脚手架**：统一跨平台 CLI 入口、DI 装配可插拔端口适配器、Rx 进程内领域事件流、可加载示例插件，并支持 Windows / Linux / macOS / Android 发布。

Natter 只交付**框架能力**：端口契约、组合根、插件加载、DI 装配、Rx 事件总线、CLI 骨架与最小示例。`Counter`、`Greeting` 是用于验证装配和事件流的示例，不是业务系统。

## 能力概览

- **DDD 基元**：`Entity`、`ValueObject`、`AggregateRoot`、领域事件、`Specification`、`Guard`。
- **六边形端口**：入站请求分发器，出站 `IRepository<,>`、`IUnitOfWork`、`IClock`、`IDomainEventPublisher`、`IGreetingPort`。
- **组合根**：`NatterApplicationBuilder` + `ICompositionModule`，宿主不引用具体适配器。
- **Rx 事件流**：`System.Reactive` 热流、类型过滤、故障订阅者隔离、事件序列号、内存回放日志。
- **插件机制**：目录扫描、`plugin.json` 清单、可回收 `AssemblyLoadContext`、端口适配器覆盖、CLI 命令贡献、生命周期订阅。
- **CLI 内核**：平台无关的命令注册表、解析器、帮助、JSON 输出、`exec` 同宿主命令链；Console 与 Android 宿主共用。
- **多平台发布**：自包含单文件（win/linux/osx）+ Android APK；示例插件随发布目录部署或嵌入宿主。
- **测试**：28 个框架级测试覆盖聚合事件、Rx 总线、DI 管道、解析器、插件加载与 CLI。

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
        │  NatterApplicationBuilder · NatterHost · PluginLoader · Rx bus       │
        │  Default adapters: clock · repository · UoW · journal · greeting     │
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Application — use cases, dispatcher, pipeline behaviors      │
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Domain — demo aggregate (Counter)                             │
        └──────────────────────────────────┬───────────────────────────────────┘
                                           │
        ┌──────────────────────────────────▼───────────────────────────────────┐
        │  Natter.Abstractions — DDD primitives · ports · event/plugin contracts│
        └──────────────────────────────────────────────────────────────────────┘

  Plugins ──implement──▶ Natter.Abstractions + Natter.Cli.Abstractions
          ──loaded by──▶ collectible AssemblyLoadContext
```

依赖方向始终向**内核**（`Natter.Abstractions`）收敛：Domain 不引用 Infrastructure，Application 不引用宿主，适配器只在外围装配。

## 工程结构

| 项目 | 层 | 内容 |
| --- | --- | --- |
| `src/Natter.Abstractions` | 内核 | DDD 基元、端口、事件/插件/组合契约 |
| `src/Natter.Domain` | 领域 | 示例 `Counter` 聚合与领域事件 |
| `src/Natter.Application` | 应用 | `IApplicationDispatcher` 实现、管道、示例用例 |
| `src/Natter.Infrastructure` | 适配器/组合根 | Rx 总线、内存适配器、插件加载器、DI 组合 |
| `src/Natter.Cli.Abstractions` | CLI 契约 | `IConsole`、命令/选项描述符、处理器 |
| `src/Natter.Cli.Core` | CLI 内核 | 解析、注册表、内建命令、JSON/表格输出 |
| `src/Natter.Cli` | 宿主 | Console 入口，发布为 `natter` / `natter.exe` |
| `src/Natter.Hosting.Android` | 宿主 | `net10.0-android` Activity 宿主，嵌入示例插件 |
| `samples/Natter.Sample.Plugin.Greetings` | 插件 | 覆盖 `IGreetingPort`、贡献 `greet` 命令、订阅事件 |
| `samples/Natter.Sample.Plugin.Metrics` | 插件 | 独立消费 Rx 流、贡献 `metrics` 命令 |
| `tests/Natter.Framework.Tests` | 测试 | xUnit 框架测试 |

## 快速开始

要求：**.NET SDK 10.0.1xx**。Android 额外需要 `android` workload、Android SDK 与 JDK 17+。

```bash
dotnet build src/Natter.Cli/Natter.Cli.csproj

dotnet run --project src/Natter.Cli -- doctor
dotnet run --project src/Natter.Cli -- demo --name demo --times 3
dotnet run --project src/Natter.Cli -- ports
dotnet run --project src/Natter.Cli -- plugins list

# 同一个宿主里串行执行命令，插件状态与事件日志共享
dotnet run --project src/Natter.Cli -- exec "demo --times 3" "metrics" "greetings recent"

dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
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

| 命令 | 说明 |
| --- | --- |
| `version [--json]` | 框架、运行时、RID、OS 信息 |
| `doctor [--json]` | 组合结果：插件、端口适配器、事件总线、命令数 |
| `ports [--port <过滤>] [--json]` | 端口 → 适配器绑定（含插件覆盖） |
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

全局选项：`--plugin-dir <path>`（可重复）、`--no-plugins`、`--verbose/-v`、`--trace-events`、`-h/--help`。

> 注意：普通 CLI 每次调用都会新建宿主，因此 `metrics`、`events list` 的单次进程内状态是空的。用 `exec` 在**同一个宿主**里串联命令即可观察插件共享状态。

## 扩展点

| 想做的事 | 扩展方式 |
| --- | --- |
| 替换/新增出站适配器 | 实现端口接口，注册 `AddPortAdapter<TService, TImpl>()`；插件里用 `IPluginRegistrationContext.AddPortAdapter` |
| 新增用例 | 实现 `IRequestHandler<TRequest, TResponse>` 并注册到 DI |
| 横切逻辑 | 实现 `IPipelineBehavior<,>`，注册为开放泛型 |
| 贡献 CLI 命令 | 实现 `ICliCommandContributor`（内置 `ICliCommandHandler`） |
| 消费领域事件 | 实现 `IPluginLifecycle`，在 `StartAsync` 订阅 `IDomainEventStream` |
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

- [架构设计](docs/ARCHITECTURE.md)：六边形映射、组合根顺序、Rx 语义、插件加载器。
- [插件开发](docs/PLUGINS.md)：清单、适配器覆盖、CLI 命令、生命周期、嵌入模式。
- [发布指南](docs/PUBLISHING.md)：RID 矩阵、单文件、Android 签名与裁剪。

## 定位与边界

- 示例 `Counter` 只用于演示聚合、仓储、事件派发和 Rx 订阅；不包含任何业务实现。
- 默认适配器均为内存实现，用于脚手架自检；真实项目应替换为数据库/消息/HTTP 适配器。
- Rx 总线是**进程内热流**，没有 outbox、持久化或重试语义；跨进程事件请新增适配器。
- 插件卸载是 best-effort；DI 单例持有插件对象的期间，`AssemblyLoadContext` 不会真正回收。
- Android 无法扫描 APK 内的插件目录，因此示例以 embedded plugin 方式注册。

## 许可

MIT
