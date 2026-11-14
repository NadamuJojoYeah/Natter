# Natter 开发指南

面向第一次参与 Natter 开发、或准备在它之上搭建业务系统的工程师。目标是把“环境 → 日常循环 → 扩展点 → 测试 → 调试 → 提交”讲清楚，并给出可直接照抄的代码骨架。

> 架构原理见 [架构设计](ARCHITECTURE.md)，插件细节见 [插件开发](PLUGINS.md)，发布见 [发布指南](PUBLISHING.md)。本文只讲**怎么开发**。

## 1. 环境准备

### 1.1 必需

- .NET SDK **10.0.1xx**（仓库根目录 [global.json](../global.json) 固定了基线，`rollForward: latestFeature`）。
- Git。
- 任意支持 `.slnx` 的 IDE（新版 Rider / Visual Studio / VS Code + C# Dev Kit），或纯 CLI。

```bash
dotnet --info
dotnet restore Natter.slnx
dotnet build src/Natter.Cli/Natter.Cli.csproj
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
dotnet run --project src/Natter.Cli -- doctor
```

### 1.2 Android（可选）

Android 宿主需要 `android` workload、Android SDK（API 36）与 JDK 17+：

```bash
dotnet workload install android
dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj
```

没有 Android 环境时，不要直接 `dotnet build Natter.slnx`（解决方案包含 Android 工程）。使用 [scripts/build.sh](../scripts/build.sh) 只构建桌面宿主与测试，或单独构建 `Natter.Cli`。

### 1.3 第一次跑通

```bash
# 1) 组合根自检：运行时、插件、端口适配器、事件总线
dotnet run --project src/Natter.Cli -- doctor

# 2) 查看端口 → 适配器绑定
dotnet run --project src/Natter.Cli -- ports

# 3) 同一宿主内跑完整链路：聚合 → 仓储 → 领域事件 → Rx → 插件消费
dotnet run --project src/Natter.Cli -- exec "demo --times 3" "metrics" "greetings recent"

# 4) 行为测试
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

## 2. 仓库地图与依赖规则

| 目录 | 职责 | 可以引用 |
| --- | --- | --- |
| [src/Natter.Abstractions](../src/Natter.Abstractions) | DDD 基元、端口、事件/插件/组合契约 | 仅 DI 抽象包 |
| [src/Natter.Domain](../src/Natter.Domain) | 领域模型（当前是 `Counter` 示例） | Abstractions |
| [src/Natter.Application](../src/Natter.Application) | 用例、请求分发器、管道行为 | Domain + Abstractions |
| [src/Natter.Infrastructure](../src/Natter.Infrastructure) | Rx 总线、默认适配器、组合根、插件加载、NLMP 入口/调度 | Application + Domain + Chat + Abstractions |
| [src/Natter.Chat](../src/Natter.Chat) | NLMP 0.1 模型、键、事件/命令、能力降级、幂等/去重、JSON | Abstractions |
| [src/Natter.Chat.Satori](../src/Natter.Chat.Satori) | Satori 事件/元素/feature/API 映射与会话序号 | Chat + Abstractions |
| [src/Natter.Cli.Abstractions](../src/Natter.Cli.Abstractions) | CLI 契约（`IConsole`、命令描述符） | 无 |
| [src/Natter.Cli.Core](../src/Natter.Cli.Core) | CLI 内核、内建命令、输出渲染 | Infrastructure 及以下 |
| [src/Natter.Cli](../src/Natter.Cli) | 桌面宿主 | Cli.Core |
| [src/Natter.Hosting.Android](../src/Natter.Hosting.Android) | Android 宿主 | Cli.Core + 示例插件 |
| [samples](../samples) | 可加载插件示例 | Abstractions + Cli.Abstractions |
| [tests/Natter.Framework.Tests](../tests/Natter.Framework.Tests) | 框架级测试 | 全部 + 示例插件 |

**依赖铁律**：

1. `Domain` / `Application` 不引用 `Infrastructure`、`Cli.*`、`System.Reactive` 或具体数据库 SDK。
2. `Infrastructure` 不引用任何宿主（CLI/Android）。
3. 宿主只通过 `NatterCli.RunAsync` 或 `NatterApplicationBuilder` 组装，不直接 new 适配器。
4. 领域层不读环境变量、不写 Console、不发 HTTP。
5. `Natter.Chat` 不引用 Infrastructure、宿主或 Satori 类型；`Natter.Chat.Satori` 只依赖 Chat + Abstractions。
6. 平台 SDK/协议类型只存在于协议适配器或 `Natter.Chat.Satori` 内部，不进入 NLMP 核心。

违反依赖方向通常意味着“端口定义放错了层”。端口接口应放在**使用它的那一层**：

- 跨领域通用端口（时钟、事件、仓储、UoW）放 `Natter.Abstractions`。
- 某个限界上下文专属的端口，优先放该上下文的 Domain/Application 项目，由 Infrastructure 实现。
- `IGreetingPort` 放在 Abstractions 是为了示例插件最小化依赖，不代表所有业务端口都要塞进内核。

## 3. 日常开发循环

### 3.1 修改 → 构建 → 运行

```bash
dotnet build src/Natter.Cli/Natter.Cli.csproj
dotnet run --project src/Natter.Cli -- <command> [options]
```

`Natter.Cli.csproj` 的 `CopySamplePlugins` 目标会在构建后把示例插件复制到：

```text
src/Natter.Cli/bin/<Configuration>/net10.0/plugins/<plugin-id>/
```

所以改完插件代码后重新构建，CLI 启动即可加载最新插件。

### 3.2 只跑相关测试

```bash
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~RxDomainEventBusTests"

dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~CliParserTests|FullyQualifiedName~PluginLoadingTests"
```

### 3.3 调试

- CLI 参数从 `Program.cs` 原样传给 `NatterCli.RunAsync`，可直接附加调试器。
- 需要看 DI/插件/事件流内部行为时：

```bash
dotnet run --project src/Natter.Cli -- --verbose doctor
dotnet run --project src/Natter.Cli -- --trace-events exec "demo --times 2" "metrics"
dotnet run --project src/Natter.Cli -- plugins inspect src/Natter.Cli/bin/Debug/net10.0/plugins/greetings
```

- `--verbose` 把 Debug 级日志写到 **stderr**，不会污染 `--json` 的 stdout。
- 日志由 [CliLoggerProvider](../src/Natter.Cli.Core/Logging/CliLoggerProvider.cs) 输出，格式为 `[level] Category: message`。

## 4. 常见开发任务

### 4.1 新增领域聚合

在领域项目里创建“聚合根 + 领域事件”，保持私有 setter、工厂方法和不变量校验：

```csharp
using Natter.Abstractions.Ddd;
using Natter.Abstractions.Events;

public sealed class Order : AggregateRoot<Guid>
{
    private Order(Guid id, string customer, bool isPaid) : base(id)
    {
        Customer = customer;
        IsPaid = isPaid;
    }

    public string Customer { get; }
    public bool IsPaid { get; private set; }

    public static Order Create(string customer)
    {
        Guard.NotNullOrWhiteSpace(customer, nameof(customer));
        var order = new Order(Guid.NewGuid(), customer.Trim(), isPaid: false);
        order.Raise(new OrderCreated(order.Id, order.Customer));
        return order;
    }

    public static Order Rehydrate(Guid id, string customer, bool isPaid)
        => new(id, customer, isPaid);

    public void Pay()
    {
        Guard.Ensure(!IsPaid, "Order is already paid.");
        IsPaid = true;
        Raise(new OrderPaid(Id));
    }
}

public sealed record OrderCreated(Guid OrderId, string Customer) : DomainEvent;

public sealed record OrderPaid(Guid OrderId) : DomainEvent;
```

检查清单：

- 事件只在状态变更成功后 `Raise`；
- 聚合内部不调用仓储、不发 HTTP、不读时钟（需要时间就通过 `IClock` 由用例传入）；
- 为持久化适配器提供 `Rehydrate`（或等价工厂）；
- 为不变量写失败用例（`DomainException`）。

### 4.2 新增用例（Command / Query）

在 Application 项目定义请求与处理器：

```csharp
using Natter.Abstractions.Application;
using Natter.Abstractions.Events;
using Natter.Abstractions.Ports;

public sealed record CreateOrderCommand(string Customer) : ICommand<Guid>;

public sealed class CreateOrderHandler : IRequestHandler<CreateOrderCommand, Guid>
{
    private readonly IRepository<Order, Guid> _repository;
    private readonly IDomainEventDispatcher _dispatcher;
    private readonly IUnitOfWork _unitOfWork;

    public CreateOrderHandler(
        IRepository<Order, Guid> repository,
        IDomainEventDispatcher dispatcher,
        IUnitOfWork unitOfWork)
    {
        _repository = repository;
        _dispatcher = dispatcher;
        _unitOfWork = unitOfWork;
    }

    public async Task<Guid> HandleAsync(CreateOrderCommand request, CancellationToken ct = default)
    {
        var order = Order.Create(request.Customer);

        await _repository.AddAsync(order, ct);
        await _dispatcher.DispatchAsync(order, ct); // 聚合事件 → Rx 流
        await _unitOfWork.SaveChangesAsync(ct);

        return order.Id;
    }
}
```

注册到组合模块：

```csharp
public sealed class OrdersCompositionModule : ICompositionModule
{
    public string Name => "Orders";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        services.AddTransient<IRequestHandler<CreateOrderCommand, Guid>, CreateOrderHandler>();
    }
}
```

调用：

```csharp
var orderId = await dispatcher.SendAsync(new CreateOrderCommand("YG"));
```

说明：

- `IApplicationDispatcher` 按**运行时请求类型**解析 `IRequestHandler<TRequest, TResponse>`，请求必须注册精确的处理器。
- 管道行为按注册顺序包装；框架已注册开放泛型 `LoggingPipelineBehavior<,>`。
- 事件派发在 UoW 提交之前还是之后是业务决策；示例放在提交前，失败时不重复派发。
- 需要事务性 outbox 时，新增 `IUnitOfWork` 适配器并调整派发时机，不要在聚合里发消息。

### 4.3 新增端口与适配器

**第一步：定义端口**（放在使用方所在的层）：

```csharp
public interface IPaymentGateway
{
    Task ChargeAsync(Guid orderId, decimal amount, CancellationToken ct = default);
}
```

**第二步：实现适配器**（Infrastructure 或独立 Adapter 项目）：

```csharp
public sealed class PaymentGatewayAdapter : IPaymentGateway
{
    private readonly HttpClient _httpClient;

    public PaymentGatewayAdapter(HttpClient httpClient) => _httpClient = httpClient;

    public async Task ChargeAsync(Guid orderId, decimal amount, CancellationToken ct = default)
    {
        // 只做协议转换，不写领域规则
    }
}
```

**第三步：在组合模块里注册端口 → 适配器**：

```csharp
using Natter.Infrastructure.Composition;

public sealed class PaymentsModule : ICompositionModule
{
    public string Name => "Payments";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        services.AddSingleton(_ => new HttpClient());
        services.AddPortAdapter<IPaymentGateway, PaymentGatewayAdapter>(
            context.Adapters,
            ServiceLifetime.Singleton,
            origin: "infra:payments",
            description: "Payment gateway adapter.");
    }
}
```

**第四步：宿主注册模块**：

```csharp
var builder = NatterApplicationBuilder.Create()
    .AddNatterFramework()
    .AddModule<CliCoreCompositionModule>()
    .AddModule<PaymentsModule>();
```

**第五步：验证**：

```bash
natter ports --port IPaymentGateway
```

插件覆盖同一端口时，后注册者生效；`ports` 会同时列出默认实现与插件实现，`Origin` 分别为 `framework:core` / `infra:payments` / `plugin:<id>`。

**生命周期选择**：

| 生命周期 | 适用场景 | 注意 |
| --- | --- | --- |
| `Singleton` | 无状态、线程安全、长连接管理 | 不要注入 Scoped 服务 |
| `Scoped` | 每请求/每用例一个上下文（DbContext 等） | 不要从根容器解析 |
| `Transient` | 轻量无状态对象 | 频繁解析成本 |

CLI 内核默认从根容器解析命令处理器；如果新增 Scoped 适配器，请在宿主/命令里显式创建作用域（`NatterHost.CreateScope()` 或 `IServiceScopeFactory`），否则 `ValidateScopes` 会在运行时报错。

### 4.4 新增 CLI 命令

CLI 命令由“描述符 + 处理器”组成。宿主和插件使用同一套契约。

**处理器**：

```csharp
using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;

public sealed class ExportOrdersCommand : ICliCommandHandler
{
    private readonly IClock _clock;

    public ExportOrdersCommand(IClock clock) => _clock = clock;

    public ValueTask<int> ExecuteAsync(
        CliInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        var path = invocation.GetPositional(0, "orders.json");
        var limit = invocation.GetIntOption("limit", 100);

        // 用例编排放在 Application 层；命令只负责解析参数和呈现结果。
        invocation.Console.Out.WriteLine(
            $"[{_clock.UtcNow:HH:mm:ss}] exporting up to {limit} order(s) to {path}");

        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
```

**描述符与 DI 注册**（宿主组合模块）：

```csharp
services.AddTransient<ExportOrdersCommand>();

services.AddSingleton(new CliCommandDescriptor
{
    Name = "orders export",
    Description = "Export orders to a JSON file.",
    Arguments = [new CliArgumentDescriptor("path", "Output file.", Required: false)],
    Options = [new CliOptionDescriptor("limit", "Maximum rows.", TakesValue: true, DefaultValue: "100")],
    Origin = "infra:orders",
    HandlerFactory = provider => provider.GetRequiredService<ExportOrdersCommand>(),
});
```

**约定**：

- 命令名可含空格，形成 `plugins list` 这类多级命令；注册表按最长路径优先匹配。
- 支持 `--name value`、`--name=value`、`-n value`、布尔 flag 和 `--` 终止解析。
- `CliExitCodes`：`0` 成功、`1` 失败、`2` 用法错误、`130` 取消。
- 机器可读输出请加 `--json`，并使用 `JsonOutput`（camelCase、枚举字符串）。
- 不要在命令里直接 `Console.WriteLine`，使用 `invocation.Console` 或注入 `IConsoleAccessor`。
- 处理器本身必须注册到 DI；只注册描述符会在运行时报 `No service for type ...`。

**测试命令**：使用内存 `IConsole` 调 `NatterCli.RunAsync`，不要启动子进程：

```csharp
var console = new StringConsole();
var exitCode = await NatterCli.RunAsync(
    ["orders", "export", "out.json", "--limit", "10"],
    console,
    new NatterCliOptions { LoadPlugins = false });

Assert.Equal(CliExitCodes.Success, exitCode);
Assert.Contains("exporting up to 10", console.Output);
```

### 4.5 新增插件

最小步骤：

1. 新建 `net10.0` 类库，引用 `Natter.Abstractions`、`Natter.Cli.Abstractions`（贡献命令时）与 `System.Reactive`（订阅时）。
2. 实现 `IPluginModule`（公共无参构造函数）。
3. 在 `Configure(IPluginRegistrationContext)` 中注册适配器、命令贡献者、生命周期。
4. 提供 `plugin.json`。
5. 把输出复制到 `<CLI>/plugins/<id>/`，或 `--plugin-dir` 指定目录。

```csharp
public sealed class OrdersPlugin : IPluginModule
{
    public PluginMetadata Metadata { get; } = new(
        Id: "orders",
        Name: "Orders Plugin",
        Version: "0.1.0");

    public void Configure(IPluginRegistrationContext context)
    {
        context.AddPortAdapter<IPaymentGateway, FakePaymentGatewayAdapter>();
        context.Services.AddSingleton<ICliCommandContributor, OrdersCommands>();
        context.Services.AddTransient<ExportOrdersCommand>();
        context.Services.AddSingleton<IPluginLifecycle, OrderEventsSubscriber>();
        context.AddCapability("cli:orders");
    }
}
```

```json
{
  "id": "orders",
  "name": "Orders Plugin",
  "version": "0.1.0",
  "entryAssembly": "Orders.Plugin.dll",
  "capabilities": ["port:IPaymentGateway", "cli:orders"]
}
```

验证：

```bash
natter plugins inspect <plugin-dir>
natter --plugin-dir <plugin-dir> plugins list
natter --plugin-dir <plugin-dir> orders export
```

插件边界：

- 只依赖契约，不引用 Infrastructure/宿主，避免 ALC 共享冲突。
- 不要捆绑不同版本的 `Natter.*`、`Microsoft.Extensions.*`、`System.Reactive*`，这些由宿主共享。
- 每个入口程序集只加载一个 `IPluginModule`（按类型名排序取第一个）。
- 插件注册失败会进入 `Failed` 状态，宿主继续运行；用 `plugins list --failed` 查看。

更完整的契约、清单字段与排查见 [插件开发](PLUGINS.md)。

### 4.6 订阅领域事件 / 编写 Rx 处理器

订阅必须在 `IPluginLifecycle.StartAsync` 建立，保证早于任何命令发布事件：

```csharp
using Microsoft.Extensions.Logging;
using System.Reactive.Linq;
using Natter.Abstractions.Events;
using Natter.Abstractions.Plugins;

public sealed class OrderEventsSubscriber : IPluginLifecycle, IDisposable
{
    private readonly IDomainEventStream _stream;
    private readonly ILogger<OrderEventsSubscriber> _logger;
    private IDisposable? _subscription;

    public OrderEventsSubscriber(IDomainEventStream stream, ILogger<OrderEventsSubscriber> logger)
    {
        _stream = stream;
        _logger = logger;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _subscription ??= _stream
            .Where(envelope => envelope.Event is OrderPaid)
            .Select(envelope => (OrderPaid)envelope.Event)
            .Subscribe(order => _logger.LogInformation("paid {OrderId}", order.OrderId));

        return ValueTask.CompletedTask;
    }

    public ValueTask StopAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _subscription?.Dispose();
        _subscription = null;
        return ValueTask.CompletedTask;
    }

    public void Dispose() => _subscription?.Dispose();
}
```

Rx 使用规则：

- 这是**进程内热流**：订阅前发布的事件不会补发；需要历史用 `IEventJournal`。
- `OnNext` 内不要执行长耗时/阻塞操作；必要时投递到 `Channel` 或后台任务。
- 订阅者异常会被 `SafeObserver` 隔离并记录，但不要依赖这个兜底。
- 订阅一定要可释放；宿主停止时 `StopAsync` 必须释放。
- 类型过滤优先用 `OfTypeEvent<T>()`，不要在每个订阅里手写 `is` 判断。
- `OfTypeEvent` / `SubscribeSafe` 位于 `Natter.Infrastructure.Events`，适合宿主/基础设施代码；只依赖 `Natter.Abstractions` 的插件可用 `System.Reactive.Linq` 的 `Where/Select/Subscribe`（总线本身已用 `SafeObserver` 隔离异常）。
- 需要跨进程/可靠投递时，新增 `IDomainEventPublisher`/`IUnitOfWork` 适配器，而不是在订阅者里做补偿。

### 4.7 新增宿主

宿主 = `IConsole` 实现 + 组合配置 + `NatterCli.RunAsync`：

```csharp
var options = new NatterCliOptions { LoadPlugins = false };

var exitCode = await NatterCli.RunAsync(
    ["doctor"],
    console,
    options,
    builder => builder
        .AddEmbeddedPlugin<OrdersPlugin>()
        .AddModule<PaymentsModule>());
```

实现 `IConsole` 时只需提供 `Out` / `Error` / `IsInteractive` / `ReadLine`。Android 宿主 [MainActivity.cs](../src/Natter.Hosting.Android/MainActivity.cs) 就是把输出重定向到 `TextView` 的完整示例。

### 4.8 接入聊天平台（NLMP）

协议适配器实现 `IChatUpstream`：把平台事件翻译成 `ChatEvent`，把 `ChatCommand` 翻译成平台 API。NLMP 对象**不携带 token、密钥或平台原始凭据**。

```csharp
public sealed class MyChatUpstream : IChatUpstream
{
    private readonly Subject<ChatEvent> _events = new();

    public Source Source { get; } = Source.Create("myproto", "myplatform", "bot-id");

    public bool IsConnected { get; private set; }

    public IObservable<ChatEvent> Events => _events;

    public ValueTask ConnectAsync(CancellationToken ct = default)
    {
        IsConnected = true;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisconnectAsync(CancellationToken ct = default)
    {
        IsConnected = false;
        return ValueTask.CompletedTask;
    }

    public ValueTask<Capabilities> GetCapabilitiesAsync(Conversation? conversation = null, CancellationToken ct = default)
        => ValueTask.FromResult(new Capabilities
        {
            ConversationKinds = [ConversationKind.Guild],
            Edit = true,
            Delete = true,
            MaxMessageLength = 2000,
        });

    public ValueTask<ChatAck> ExecuteAsync(ChatCommand command, CancellationToken ct = default)
        => throw new NotImplementedException("Map commands to the platform API here.");
}
```

注册到组合根：

```csharp
builder.ConfigureServices(services => services.AddSingleton<IChatUpstream>(provider =>
{
    var upstream = new MyChatUpstream();
    provider.GetRequiredService<IChatUpstreamRegistry>().Register(upstream);
    return upstream;
}));
```

`NatterHost.StartAsync` 启动 `ChatIngressLifecycle`，把上游 `Events` 接到去重入口；注册发生在启动之后也会被 `UpstreamRegistered` 事件补订阅。

发送消息不要让功能插件直接调用平台 SDK：

```csharp
var command = dispatcherFactory.Send(source, conversation, new Content("你好"));
var ack = await host.Services.GetRequiredService<IChatCommandDispatcher>().ExecuteAsync(command);
```

- `ChatCommandDispatcher` 负责能力降级、长文本分片和 `idempotencyKey` 去重；适配器仍需做 wire 级校验与自己的幂等窗口。
- 入站事件先进入 `IChatEventIngress`（事件 ID + `message.created` 语义键去重），再进入 `IChatEventSource`。
- Satori 适配器可直接复用 `Natter.Chat.Satori` 的 `SatoriEventMapper` / `SatoriCommandMapper` / `SatoriCapabilityMapper`；不要在业务插件里解析 Satori elements。
- 诊断：`natter upstreams --json` 看 source/能力；`natter satori status --features ...` 预览 feature 映射。

## 5. 组合根与 DI 约定

### 5.1 模块

```csharp
public sealed class MyModule : ICompositionModule
{
    public string Name => "MyModule";

    public void Configure(IServiceCollection services, CompositionContext context)
    {
        // 1) 端口适配器
        // 2) 用例处理器
        // 3) 后台服务/管道行为
        // 4) 通过 context.Adapters 记录端口绑定
    }
}
```

`Build()` 顺序固定：

1. 基础服务与插件扫描；
2. 框架/宿主模块；
3. 插件模块；
4. `ConfigureServices` 追加注册；
5. `ServiceProvider` 校验与创建。

因此：**后注册覆盖先注册**。需要插件覆盖默认实现时，把插件注册放在框架模块之后；需要宿主最终兜底时，用 `ConfigureServices`。

### 5.2 选项

`CompositionOptions`（`CompositionContext.Options` / DI 单例）控制环境、事件日志、插件扫描与构建校验：

```csharp
builder.ConfigureOptions(options =>
{
    options.EnvironmentName = "Development";
    options.EnablePluginLoading = true;
    options.EnableEventJournal = true;
});
```

CLI 全局选项：`--plugin-dir`（可重复）、`--no-plugins`、`--verbose/-v`、`--trace-events`；环境变量 `NATTER_PLUGIN_DIR` 与 `NATTER_ENVIRONMENT` 也会生效。

### 5.3 常见 DI 错误

| 错误 | 原因 | 处理 |
| --- | --- | --- |
| `No service for type 'X'` | 服务/处理器/命令处理器未注册 | 在模块里注册，确认注册顺序 |
| 构建时报作用域校验 | Singleton 依赖 Scoped，或从根解析 Scoped | 改生命周期，或在 `host.CreateScope()` 内解析 |
| 插件加载失败但 Ty 服务存在 | 插件 `Configure` 抛异常 | `plugins list --failed` 查看 `Error` |
| 端口解析到默认实现 | 插件注册早于框架模块，或被后续注册覆盖 | 调整模块顺序，用 `ports` 验证最后绑定 |

## 6. 测试策略

### 6.1 分层测试

| 层 | 测什么 | 方式 |
| --- | --- | --- |
| Domain | 不变量、事件缓冲、值对象相等 | 纯单元测试，无 DI |
| Application | 处理器编排、管道顺序 | 手搭 `ServiceCollection` |
| Infrastructure | Rx 分发/隔离、插件加载、适配器 | 真实 `RxDomainEventBus`、临时目录 |
| CLI | 解析、帮助、退出码、输出 | `StringConsole` + `NatterCli.RunAsync` |
| Chat/NLMP | 键作用域、降级、分片、幂等、去重、Satori 映射 | 纯单元测试 + 真实 `TestHost` 组合 |
| Plugin | 清单探测、适配器覆盖、命令贡献 | 构建宿主并解析真实插件输出 |

### 6.2 假 Console

```csharp
internal sealed class StringConsole : IConsole
{
    public StringWriter OutWriter { get; } = new();
    public StringWriter ErrorWriter { get; } = new();
    public TextWriter Out => OutWriter;
    public TextWriter Error => ErrorWriter;
    public bool IsInteractive => false;
    public string? ReadLine() => null;

    public string Output => OutWriter.ToString();
    public string ErrorText => ErrorWriter.ToString();
}
```

### 6.3 Rx 测试

总线是同步的，测试不需要等待：

```csharp
var bus = new RxDomainEventBus(NullLogger<RxDomainEventBus>.Instance);
var received = new List<CounterIncremented>();

using var subscription = bus.OfTypeEvent<CounterIncremented>()
    .Subscribe(received.Add); // System.Reactive 的 Action<T> 重载

await bus.PublishAsync(new CounterIncremented(Guid.NewGuid(), 1, 1));

Assert.Single(received);
```

### 6.4 插件测试

测试项目通过 `ProjectReference ... ReferenceOutputAssembly="false"` + MSBuild `Copy` 目标把插件复制到 `$(OutDir)plugins/`（参考 [测试 csproj](../tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj)），然后用真实目录构建宿主：

```csharp
var builder = NatterApplicationBuilder.Create()
    .AddNatterFramework()
    .AddModule<CliCoreCompositionModule>()
    .AddPluginDirectory(Path.Combine(AppContext.BaseDirectory, "plugins"));

await using var host = builder.Build();
await host.StartAsync();

var gateway = host.Services.GetRequiredService<IPaymentGateway>();
// 断言插件覆盖了默认适配器
```

## 7. 调试与诊断速查

| 需求 | 命令 |
| --- | --- |
| 看组合结果 | `natter doctor` / `doctor --json` |
| 看端口绑定 | `natter ports` / `ports --port <filter>` |
| 看插件状态 | `natter plugins list` / `--failed` / `--json` |
| 预检插件目录 | `natter plugins inspect <dir>` |
| 实时事件 | `natter --trace-events <command>` / `natter events watch` |
| 回放事件 | `natter events list --limit 50` |
| 同宿主串联 | `natter exec "demo" "metrics" "greetings recent"` |
| 聊天上游 | `natter upstreams [--source <过滤>] [--json]` |
| Satori 能力映射 | `natter satori status --features message.update,message.delete` |
| 详细日志 | `natter --verbose <command>`（stderr） |
| 环境切换 | `NATTER_ENVIRONMENT=Development` |
| Android 日志 | `adb logcat`（UI 同时显示 CLI 输出） |

## 8. 编码约定

- 文件级 namespace、启用 nullable、隐式 using；遵守 [.editorconfig](../.editorconfig)。
- 异步方法以 `Async` 结尾，传递 `CancellationToken`；基础库内部使用 `ConfigureAwait(false)`。
- Domain 不抛 `InvalidOperationException` 表示业务错误，使用 `DomainException` / `Guard`。
- 核心层不直接写 Console；通过 `IConsole`。
- 公共 API 写 XML 注释；内部实现保持小而清晰。
- 新增 NuGet 包必须写入 [Directory.Packages.props](../Directory.Packages.props)（中央包版本）。
- JSON 输出保持 camelCase；枚举使用字符串（`JsonOutput` 已配置）。
- 不提交构建产物、`.packages`、密钥、keystore；参考 [.gitignore](../.gitignore)。

提交信息按 `type: 简评` 每行一条，例如：

```text
feat: 增加订单支付端口与适配器
test: 覆盖支付处理器管道顺序
docs: 补充支付模块开发说明
```

## 9. 功能交付检查清单

- [ ] `dotnet build Natter.slnx`（或桌面等价命令）无新增错误；
- [ ] `dotnet test` 全绿，新增逻辑有对应测试；
- [ ] `natter doctor` 能看到新插件/新适配器；
- [ ] `natter ports` 中端口绑定来源正确（含插件覆盖）；
- [ ] `natter plugins list` 状态为 `Loaded`；
- [ ] `--json` 输出可解析且不混入日志；
- [ ] 失败路径返回正确退出码并输出可读错误；
- [ ] 文档（README/ARCHITECTURE/PLUGINS）同步更新；
- [ ] 没有把业务逻辑写入 CLI 命令或适配器；
- [ ] 没有新增对 Infrastructure/宿主的反向依赖。
- [ ] NLMP 变更同步了 `Natter.Chat` 契约、一致性测试与 [NLMP](NLMP.md) 实现映射。

## 10. 常用命令速查

```bash
# 构建与测试
dotnet build src/Natter.Cli/Natter.Cli.csproj
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
./scripts/build.sh
./scripts/test.sh

# 运行
dotnet run --project src/Natter.Cli -- doctor
dotnet run --project src/Natter.Cli -- exec "demo --times 3" "metrics"
dotnet run --project src/Natter.Cli -- --verbose plugins list --failed
dotnet run --project src/Natter.Cli -- upstreams --json
dotnet run --project src/Natter.Cli -- satori status --features message.update

# 发布
./scripts/publish.sh linux-x64
./scripts/publish-android.sh
```
