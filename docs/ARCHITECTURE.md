# 架构设计

## 1. 六边形映射

| 六边形概念 | 本项目落地 |
| --- | --- |
| 领域内核 | `Natter.Domain` + `Natter.Abstractions/Ddd` |
| 入站端口（driving） | `IApplicationDispatcher`、`ICliCommandHandler` |
| 出站端口（driven） | `IRepository<,>`、`IUnitOfWork`、`IClock`、`IGreetingPort`、`IDomainEventPublisher`、`IEventJournal` |
| 入站适配器 | CLI 命令处理器、Android Activity 宿主、测试 |
| 出站适配器 | `RxDomainEventBus`、`InMemoryRepository<,>`、`InMemoryUnitOfWork`、`SystemClock`、`InMemoryEventJournal`、`DefaultGreetingAdapter` |
| 组合根 | `NatterApplicationBuilder`、`NatterHost`、`ICompositionModule` |
| 依赖注入 | `Microsoft.Extensions.DependencyInjection` |
| 事件流 | `System.Reactive` 的 `Subject<T>` + 安全观察者包装 |

依赖规则：

```text
Natter.Abstractions
   ▲        ▲          ▲
   │        │          │
Domain   Application  Cli.Abstractions
            ▲
            │
       Infrastructure ◀── Cli.Core ◀── Cli / Android
```

- `Natter.Abstractions` 不引用任何产品程序集，只依赖 `Microsoft.Extensions.DependencyInjection.Abstractions`（用于契约中的服务注册）。
- `Natter.Domain` 只依赖 Abstractions。
- `Natter.Application` 依赖 Domain + Abstractions。
- `Natter.Infrastructure` 依赖 Application/Domain/Abstractions，**不依赖宿主**。
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

## 5. 插件加载

```text
plugins/
├── greetings/
│   ├── plugin.json
│   └── Natter.Sample.Plugin.Greetings.dll
└── metrics/
    ├── plugin.json
    └── Natter.Sample.Plugin.Metrics.dll
```

- 每个子目录是一个插件；也可以直接指向包含 `plugin.json` 的目录。
- `PluginLoader` 读取清单，优先使用 `entryAssembly`；没有清单时按目录名/程序集名启发式选择入口。
- 每个插件一个 `PluginLoadContext`（`isCollectible: true`），并重写 `Load`：
  - `Natter.*`、`Microsoft.Extensions.*`、`System.*`、`System.Reactive*` 回落到默认上下文，保证**端口与契约类型身份共享**；
  - 其他依赖从插件目录解析（`AssemblyDependencyResolver`）。
- 插件模块必须有公共无参构造函数；扫描 `IPluginModule` 实现。
- 单个插件失败（清单损坏、入口缺失、类型加载异常、重复 Id）会记录为 `PluginState.Failed` 并继续加载其他插件。
- `PluginCatalog` 拥有加载上下文；宿主释放时尝试 `Unload()`。由于 DI 单例在容器生命周期内持有插件对象，通常要等容器释放后 GC 才真正卸载。

## 6. CLI 内核

- `CliCommandDescriptor` 声明命令名（支持 `plugins list` 这类多级名字）、别名、选项、位置参数、来源和处理器工厂。
- `CliCommandRegistry` 合并内建描述符与插件 `ICliCommandContributor`，按最长命令路径解析。
- `CliParser` 支持 `--name value`、`--name=value`、短选项、布尔 flag、`--` 终止解析与必填参数校验。
- `CliRunner` 统一处理帮助、用法错误（退出码 2）、取消（130）与未处理异常（1）。
- `exec` 在同一个宿主内递归调用 `CliRunner`，用于演示插件/日志的宿主级生命周期；内置最大嵌套深度 4。
- 宿主只需实现 `IConsole`：Console 宿主写 `System.Console`，Android 宿主写 `TextView`。

## 7. 宿主

- `Natter.Cli`：`net10.0` 控制台，发布自包含单文件。
- `Natter.Hosting.Android`：`net10.0-android`，Activity 中调用同一个 `NatterCli.RunAsync`；由于 APK 无法扫描外部插件目录，使用 `AddEmbeddedPlugin` 注册示例插件。
- 测试宿主：直接调用 `NatterApplicationBuilder` 或 `NatterCli.RunAsync`，用 `IConsole` 假实现捕获输出。

## 8. 已知边界

- 事件总线是进程内、内存态；不提供 outbox、持久化、重试或跨进程投递。
- 仓储与工作单元是脚手架适配器，不模拟事务/并发。
- 插件加载是宿主构建期行为；运行中热加载需要重建容器，未在本框架实现。
- Android 为保持反射与插件类型完整，关闭了 trimming/AOT；启用时应补充 trim 注解与真机验证。
