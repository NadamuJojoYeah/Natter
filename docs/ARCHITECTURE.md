# 架构设计

## 1. 六边形映射

| 六边形概念 | 本项目落地 |
| --- | --- |
| 领域内核 | `Natter.Domain` + `Natter.Abstractions/Ddd` |
| 入站端口（driving） | `IApplicationDispatcher`、`ICliCommandHandler` |
| 出站端口（driven） | `IRepository<,>`、`IUnitOfWork`、`IClock`、`IGreetingPort`、`IDomainEventPublisher`、`IEventJournal` |
| 聊天模型 / 端口 | `Natter.Chat` 的 `Source`、`Message`、`ChatEvent`、`ChatCommand`、`Capabilities`、`IChatUpstream`、`IChatCommandDispatcher` |
| 入站适配器 | CLI 命令处理器、Android Activity 宿主、测试 |
| 出站适配器 | `RxDomainEventBus`、`InMemoryRepository<,>`、`InMemoryUnitOfWork`、`SystemClock`、`InMemoryEventJournal`、`DefaultGreetingAdapter` |
| 组合根 | `NatterApplicationBuilder`、`NatterHost`、`ICompositionModule` |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` |
| 事件流 | `System.Reactive` 的 `Subject<T>` + 安全观察者包装 |
| 聊天运行时 | `RxChatEventHub`、`ChatUpstreamRegistry`、`ChatIngressProcessor`、`InMemoryChatUpstream` |
| 聊天适配映射 | `Natter.Chat.Satori` 的 `SatoriChatUpstream`、`ISatoriTransport`、event / element / feature / API mapper |
| 插件 Host API | `Natter.Plugin.Abstractions` 的 manifest、`INatterPlugin`、`IPluginContext`、contribution、配置/秘密/权限/诊断模型 |
| 插件运行时 | `Natter.Infrastructure/Plugins/Nps` 的 manifest reader、依赖图、插件本地容器、contribution staging、激活回滚、端口路由与诊断 |

依赖规则：

```text
Natter.Abstractions
   ▲        ▲          ▲          ▲
   │        │          │          │
Domain   Application  Chat    Cli.Abstractions
            ▲          ▲          ▲
            │          │          │
            └──── Natter.Plugin.Abstractions ────┘
                       ▲
                       │
                  Infrastructure ◀── Cli.Core ◀── Cli / Android
                       ▲
                       │
             Natter.Chat.Satori（协议适配）
```

- `Natter.Abstractions` 不引用任何产品程序集，只依赖 `Microsoft.Extensions.DependencyInjection.Abstractions`（用于契约中的服务注册）。
- `Natter.Domain` 只依赖 Abstractions。
- `Natter.Application` 依赖 Domain + Abstractions。
- `Natter.Infrastructure` 依赖 Application/Domain/Abstractions/Chat，**不依赖宿主**。
- `Natter.Chat` 只依赖 Abstractions（`IClock`），不引用 Infrastructure、宿主或 Satori 类型。
- `Natter.Chat.Satori` 依赖 Chat + Abstractions，是协议适配层；NLMP 核心不知道 Satori 的存在。
- `Natter.Plugin.Abstractions` 是 Host API/契约聚合层，依赖 Abstractions + Chat + Application + Cli.Abstractions；插件只引用它，不引用 Infrastructure/宿主。
- `Natter.Infrastructure` 额外依赖 Plugin.Abstractions，负责 NPS manifest、依赖图、插件本地 provider、contribution 与激活。
- `Natter.Cli.Core` 依赖 Infrastructure，向上提供命令内核；宿主只负责提供 `IConsole` 和额外模块。

## 2. DDD 基元

- `Entity<TId>`：身份相等，类型 + Id 决定 `Equals/GetHashCode`。
- `ValueObject`：由 `GetEqualityComponents()` 决定结构相等。
- `AggregateRoot<TId>`：内部缓冲 `IDomainEvent`，通过 `Raise(...)` 记录；`ClearDomainEvents()` 由派发器调用。
- `DomainEvent`：抽象 record，带 `EventId`、`OccurredAt`、`EventName`；派生事件保持不可变。
- `Specification<T>`：可组合的 `Expression<Func<T,bool>>`，可交给 ORM 适配器翻译。
- `Guard` / `DomainException`：领域不变量失败统一为领域异常。

示例聚合 `Natter.Domain/Demo/Counter.cs`：

```csharp
var counter = Counter.Create("demo");
counter.Increment(2);
await repository.AddAsync(counter);
var count = await dispatcher.DispatchAsync(counter); // 清空聚合事件并发布到 Rx 流
```

## 3. 组合根

`NatterApplicationBuilder` 是唯一的对象图装配入口：

```csharp
var builder = NatterApplicationBuilder.Create()
    .AddNatterFramework()                       // Core + Application 模块
    .AddModule<CliCoreCompositionModule>()      // 宿主模块
    .AddPluginDirectory("plugins")              // 文件插件
    .AddEmbeddedPlugin<GreetingsPlugin>()       // 嵌入插件（Android）
    .ConfigureOptions(options => options.EnvironmentName = "Development")
    .ConfigureServices(services => services.AddSingleton<IMyPort, MyAdapter>());

await using var host = builder.Build();
await host.StartAsync();
var service = host.Services.GetRequiredService<IMyPort>();
await host.StopAsync();
```

`Build()` 的顺序：

1. 注册日志、选项、适配器目录（`IPortAdapterRegistry`/`IPortAdapterCatalog`）、插件目录（`IPluginCatalog`）、探测端口。
2. 扫描插件目录：每个插件目录一个可回收 `PluginLoadContext`，扫描 `IPluginModule` 实现。
3. 依次执行**框架/宿主组合模块**的 `Configure(services, context)`。
4. 依次执行**插件模块**的 `Configure(registrationContext)`。
5. 执行调用方通过 `ConfigureServices` 追加的注册。
6. 构建 `ServiceProvider`（`ValidateOnBuild = true`、`ValidateScopes = true`），失败会带内部异常抛出。
7. 返回持有容器与插件上下文的 `NatterHost`。

注册顺序即覆盖语义：后注册的同服务实现获胜。`ports` 命令会列出**所有**绑定，因此默认适配器和插件覆盖都能看到；解析时返回最后注册的适配器。

## 4. Rx 领域事件流

```text
AggregateRoot.DomainEvents
        │  IDomainEventDispatcher.DispatchAsync(aggregate)
        ▼
DomainEventDispatcher ──publish──▶ RxDomainEventBus (Subject<T>)
                                         │
                          ┌──────────────┼─────────────────┐
                          ▼              ▼                 ▼
                  InMemoryEventJournal  CliEventPrinter   Plugin subscriptions
                  (回放 / doctor)       (--trace-events)  (IPluginLifecycle)
```

关键语义：

- `IDomainEventStream : IObservable<DomainEventEnvelope>` 是**热流**；晚订阅者不会收到历史事件。
- 每个 envelope 带单调递增 `Sequence`、`Origin`、`ObservedAt` 和具体事件对象。
- `RxDomainEventBus` 用 `Subject.Synchronize` 串行化推送，并用 `SafeObserver` 捕获订阅者异常——一个坏订阅者不会终止全流。
- `DispatchAsync` 先取出并清空聚合事件，再逐个发布；下游失败不会导致重复派发，但也不会自动重试。
- `NatterHost.StartAsync()` 会提前实例化 `IEventJournal` 并执行所有 `IPluginLifecycle.StartAsync`，因此插件在第一条事件发布前完成订阅。
- `IEventJournal` 默认容量 4096，`events list` 可回放；需要持久化审计时替换实现即可。
- `--trace-events` 由 `CliEventPrinter` 将每条事件写到当前宿主 console。

Rx 扩展方法位于 `Natter.Infrastructure/Events/RxDomainEventExtensions.cs`：

```csharp
stream.OfTypeEvent<CounterIncremented>()
      .SubscribeSafe(e => Console.WriteLine(e.Value), logger);
```

## 5. NLMP 聊天流

```text
Satori WS/HTTP（未来 transport）
        │ SatoriEvent
        ▼
SatoriChatUpstream / SatoriEventMapper (Natter.Chat.Satori)
        │ ChatEvent
        ▼
ChatIngressProcessor ── ChatEventDeduplicator（event id / message.created）
        │
        ▼
RxChatEventHub (IChatEventSource) ──▶ ChatOrchestrator / 功能插件
        │                                      │
        │                                      ▼
        │                              IChatResponder → ChatCommand
        │                                      │
        ▼                                      ▼
ChatIngressLifecycle                  ChatCommandDispatcher
（订阅所有 IChatUpstream）              （校验 / 能力降级 / 分片 / 幂等）
                                               │
                                               ▼
                                       IChatUpstream.ExecuteAsync
                                       （Satori / Discord / InMemory）
```

关键语义：

- **进程内优先**：NLMP 模型是 C# record 与接口，不需要握手或版本协商；JSON 投影只在需要时启用。
- **作用域**：所有路由、去重、缓存 key 都包含结构化 `Source`，不解析 `sourceKey` 字符串。
- **能力驱动**：`Capabilities` 缺失即不支持；`ChatCapabilityDegrader` 负责 Markdown/附件/回复/mention 降级，`ChatTextChunker` 负责 `maxMessageLength` 分片。
- **幂等与去重**：`ChatIdempotencyCache` 复用 ack；`ChatEventDeduplicator` 使用事件 ID + `message.created` 语义 key，窗口默认 10 分钟。
- **事件只表达事实**：`ChatOrchestrator` 消费事件后必须通过 `IChatCommandDispatcher` 发命令，不反向调用平台 API。
- **错误与重试**：`ChatAck`/`ChatError` 暴露 `retryable` 与 `retryAfterMs`；部分分片失败会带 `deliveredMessageIds`，避免盲目重发。
- **隐私**：`Interaction.ReplyToken` 在 `ToString()` 中脱敏；事件日志只记录 source/type/id。

`AddNatterFramework()` 会注册 `ChatCompositionModule`，因此所有宿主默认具备聊天事件端口；只有注册 `IChatUpstream` 后才有实际平台连接。

## 6. 插件加载

### 6.1 NPS v1

```text
plugins/
├── greetings/
│   ├── plugin.json          # schemaVersion: 1
│   ├── config.schema.json   # 可选
│   ├── config.json          # 用户维护
│   └── Natter.Sample.Plugin.Greetings.dll
└── metrics/
    └── ...
```

加载链路：

```text
PluginManifestReader ── NPS manifest / legacy detection
        │
        ▼
NpsPluginGraph ── hostApi / dependsOn / loadAfter / conflicts / cycle
        │
        ▼
NpsPluginRuntime.Prepare ── config schema / secrets / permissions
        │                    ALC + entry type + ConfigureAsync
        │                    staging contributions
        ▼
Host ServiceProvider ── port bridge / CLI contributor / runtime services
        │
        ▼
NpsPluginRuntime.StartAsync ── plugin-local provider
        │                        commit chat/CLI/port contributions
        │                        ActivateAsync
        │                        host-driven upstream ConnectAsync
        ▼
Activated / Failed / Blocked
```

关键语义：

- **Manifest first**：`plugin.json` 的 `schemaVersion: 1` 走 NPS；没有版本走 legacy loader。未知字段、非法 id、路径逃逸、host API 不兼容都在入口加载前失败。
- **依赖图**：必需依赖缺失/版本不匹配 → `Blocked`；冲突/循环 → `Failed`；`loadAfter`/`loadBefore` 只影响顺序；同层按 id ordinal 排序。
- **插件本地容器**：每个插件一个 `ServiceProvider`；`IPluginContext.Services.Collection` 只注册插件本地服务，宿主公开合约由 runtime 注入；插件不能访问宿主根容器。
- **Contribution staging**：`ConfigureAsync` 只声明 `chat.upstream` / `chat.responder` / `chat.event-subscriber` / `cli.command` / `port.adapter`；manifest 必须预先声明 `(type,id)`，重复或越权直接失败。
- **激活事务**：staging → commit → `ActivateAsync` → upstream connect；任一步失败都会回滚贡献、断开上游、释放插件 provider/ALC，并把依赖它的插件标记 `Blocked`。
- **端口适配器**：宿主 DI 中注册 late-bound 路由；插件激活时使用插件实现，失败/停止后回退到宿主原绑定。
- **停止顺序**：先断开所有 `chat.upstream`，再逆激活顺序调用 `DeactivateAsync`，释放 provider，最后 ALC best-effort unload。
- **诊断**：`plugins graph/contributions/permissions/doctor/config validate` 读取 `IPluginRuntimeDiagnostics`；`doctor` 汇总 active/failed/blocked/disabled 与 contribution 数。

### 6.2 Legacy 兼容

- 无 `schemaVersion` 的 `plugin.json` 继续使用 `LegacyPluginManifest` + `IPluginModule` 路径，注册到宿主根容器。
- NPS manifest 的 `entry.type` 如果只实现 `IPluginModule`，运行时走 legacy adapter，并提示“NPS contributions 不生效”。
- `IPluginLifecycle` 仍由宿主启动/停止；legacy 插件可继续订阅 Rx 领域事件。

### 6.3 加载上下文

- 每个文件插件一个 `PluginLoadContext`（`isCollectible: true`）：
  - `Natter.*`、`Microsoft.Extensions.*`、`System.*`、`System.Reactive*` 回落到默认上下文，保证**端口与契约类型身份共享**；
  - 其他依赖从插件目录解析（`AssemblyDependencyResolver`）。
- 单个插件失败（manifest、依赖、配置、权限、加载、activate）只影响自身与依赖方；宿主继续启动其他插件。
- `PluginCatalog` 拥有加载上下文；宿主释放时尝试 `Unload()`。

## 7. CLI 内核

- `CliCommandDescriptor` 声明命令名（支持 `plugins list` 这类多级名字）、别名、选项、位置参数、来源和处理器工厂。
- `CliCommandRegistry` 合并内建描述符与插件 `ICliCommandContributor`，按最长命令路径解析。
- `CliParser` 支持 `--name value`、`--name=value`、短选项、布尔 flag、`--` 终止解析与必填参数校验。
- `CliRunner` 统一处理帮助、用法错误（退出码 2）、取消（130）与未处理异常（1）。
- `exec` 在同一个宿主内递归调用 `CliRunner`，用于演示插件/日志的宿主级生命周期；内置最大嵌套深度 4。
- 宿主只需实现 `IConsole`：Console 宿主写 `System.Console`，Android 宿主写 `TextView`。

## 8. 宿主

- `Natter.Cli`：`net10.0` 控制台，发布自包含单文件。
- `Natter.Hosting.Android`：`net10.0-android`，Activity 中调用同一个 `NatterCli.RunAsync`；由于 APK 无法扫描外部插件目录，使用 `AddEmbeddedPlugin` 注册示例插件。
- 测试宿主：直接调用 `NatterApplicationBuilder` 或 `NatterCli.RunAsync`，用 `IConsole` 假实现捕获输出。

## 9. 已知边界

- 事件总线是进程内、内存态；不提供 outbox、持久化、重试或跨进程投递。
- 仓储与工作单元是脚手架适配器，不模拟事务/并发。
- 插件加载是宿主构建期行为；NPS v1 只保证重启生效，运行中热加载/HMR 未实现。
- NPS Phase 2–4（Discord transport、AI feature、启用/禁用 CLI、市场、签名、out-of-process 宿主）尚未交付。
- 权限是宿主治理与可观测性，不是进程内安全沙箱；不可信第三方插件需要独立进程/容器。
- NLMP 不包含 wire transport、远程插件、握手/服务发现；Satori WebSocket/HTTP 客户端与原生 Discord 适配器不在框架内。
- Satori message elements 采用有损文本化（v0.1），富文本/按钮/Modal 需要后续能力扩展。
- Android 为保持反射与插件类型完整，关闭了 trimming/AOT；启用时应补充 trim 注解与真机验证。
