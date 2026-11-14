# 插件开发指南

插件是对外扩展点，不修改框架代码即可：

- 覆盖或新增**端口适配器**；
- 贡献 **CLI 命令**；
- 订阅 **Rx 领域事件流**；
- 注册任意 DI 服务、管道行为或后台组件。

## 1. 最小插件

插件是一个普通类库（`net10.0`），引用：

- `Natter.Abstractions`：`IPluginModule`、端口、领域事件；
- `Natter.Cli.Abstractions`：若要贡献 CLI 命令；
- `Microsoft.Extensions.DependencyInjection.Abstractions`：注册服务；
- `System.Reactive`：若要直接使用 Rx 操作符。

```csharp
using Microsoft.Extensions.DependencyInjection;
using Natter.Abstractions.Plugins;
using Natter.Abstractions.Ports;
using Natter.Cli.Abstractions;

public sealed class HelloPlugin : IPluginModule
{
    public PluginMetadata Metadata { get; } = new(
        Id: "hello",
        Name: "Hello Plugin",
        Version: "0.1.0",
        Description: "示例插件");

    public void Configure(IPluginRegistrationContext context)
    {
        // 覆盖已有端口适配器（后注册获胜）
        context.AddPortAdapter<IGreetingPort, HelloGreetingAdapter>();

        // 贡献 CLI 命令
        context.Services.AddSingleton<ICliCommandContributor, HelloCommands>();
        context.Services.AddTransient<HelloCommandHandler>();

        // 订阅事件（宿主 StartAsync 时触发）
        context.Services.AddSingleton<IPluginLifecycle, HelloLifecycle>();

        context.AddCapability("cli:hello");
    }
}
```

插件类必须：

- 实现 `IPluginModule`；
- 公共、非abstract；
- 有公共无参构造函数；
- 程序集与依赖位于同一插件目录。

每个插件入口程序集只会实例化一个模块（按类型全名排序取第一个）。需要多个模块时，拆分为多个插件目录/程序集。

## 2. `plugin.json` 清单

```json
{
  "id": "hello",
  "name": "Hello Plugin",
  "version": "0.1.0",
  "description": "示例插件",
  "author": "team",
  "entryAssembly": "Hello.Plugin.dll",
  "enabled": true,
  "capabilities": [
    "port:IGreetingPort",
    "cli:hello",
    "events:subscriber"
  ]
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | 推荐 | 唯一插件 Id；重复会被标记为失败 |
| `name` / `version` | 推荐 | 诊断与 `plugins list` 展示 |
| `entryAssembly` | 推荐 | 入口 DLL 文件名（同目录） |
| `enabled` | 否 | `false` 时跳过加载，状态为 `Skipped` |
| `capabilities` | 否 | 静态能力标签；运行期能力会合并显示 |
| `description` / `author` | 否 | 元数据 |

没有清单时，加载器会按目录名匹配程序集名，并排除 `Natter.*`、`Microsoft.*`、`System.*` 等框架程序集后选择入口。

## 3. 覆盖端口适配器

```csharp
public sealed class HelloGreetingAdapter(IClock clock) : IGreetingPort
{
    public ValueTask<string> GreetAsync(string name, CancellationToken ct = default)
        => ValueTask.FromResult($"Hello {name} from plugin @ {clock.UtcNow:HH:mm:ss} UTC");
}
```

注册后：

- `IGreetingPort` 的**最后一次注册**生效，因此插件会覆盖框架默认 `DefaultGreetingAdapter`；
- `natter ports` 会同时列出默认绑定与插件绑定，`Origin` 分别为 `framework:core` 和 `plugin:hello`；
- 应用层只依赖端口，无需感知插件。

同一端口需要多实现编排时，可注册 `IEnumerable<TService>` 并在组合模块中注入集合；框架不强制单实现语义。

## 4. 贡献 CLI 命令

```csharp
public sealed class HelloCommands : ICliCommandContributor
{
    public IEnumerable<CliCommandDescriptor> GetCommands()
    {
        yield return new CliCommandDescriptor
        {
            Name = "hello",
            Description = "Say hello from a plugin.",
            Arguments = [new CliArgumentDescriptor("name", "Name to greet.", Required: true)],
            Options = [new CliOptionDescriptor("json", "Emit JSON.")],
            Origin = "plugin:hello",
            HandlerFactory = services => services.GetRequiredService<HelloCommandHandler>(),
        };
    }
}

public sealed class HelloCommandHandler : ICliCommandHandler
{
    public ValueTask<int> ExecuteAsync(CliInvocation invocation, CancellationToken ct = default)
    {
        invocation.Console.Out.WriteLine($"hello {invocation.GetPositional(0, "world")}");
        return ValueTask.FromResult(CliExitCodes.Success);
    }
}
```

- 命令名可以包含空格，形成 `plugins list` 这类多级命令；
- 处理器通过 `HandlerFactory` 从 DI 解析；
- 别忘了在 `Configure` 中注册处理器本身（否则运行期会报 `No service for type ...`）；
- 与内建命令重名时，插件后注册，会覆盖内建命令（命令来源会显示在 `help <command>`）。

## 5. 订阅领域事件

```csharp
public sealed class HelloLifecycle : IPluginLifecycle, IDisposable
{
    private readonly IDomainEventStream _stream;
    private readonly ILogger<HelloLifecycle> _logger;
    private IDisposable? _subscription;

    public HelloLifecycle(IDomainEventStream stream, ILogger<HelloLifecycle> logger)
    {
        _stream = stream;
        _logger = logger;
    }

    public ValueTask StartAsync(IServiceProvider services, CancellationToken ct = default)
    {
        _subscription ??= _stream.OfTypeEvent<CounterIncremented>()
            .SubscribeSafe(e => _logger.LogInformation("counter = {Value}", e.Value), _logger);
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

说明：

- `NatterHost.StartAsync()` 会在执行任何命令前调用所有 `IPluginLifecycle.StartAsync`；
- `IDomainEventStream` 是热流，订阅晚于事件发布就会错过；
- 使用 `SubscribeSafe`（`Natter.Infrastructure.Events`）隔离异常；直接用 `Subscribe` 时请确保处理器不抛异常；
- 订阅必须可释放，避免宿主停止后继续回调。

若插件只引用 `Natter.Abstractions` 和 `System.Reactive`，可直接：

```csharp
_subscription = _stream.Subscribe(envelope => { /* ... */ });
```

## 6. 部署与探测

```bash
# 构建插件类库
dotnet publish samples/Natter.Sample.Plugin.Greetings -c Release -o /tmp/greetings

# 探测清单/入口/程序集
natter plugins inspect /tmp/greetings

# 加载插件（也可放到 <CLI 目录>/plugins 下自动加载）
natter --plugin-dir /tmp/greetings plugins list
natter --plugin-dir /tmp/greetings greet YG
```

插件目录约定：

```text
my-plugin/
├── plugin.json
├── My.Plugin.dll
└── <plugin-private-dependencies>.dll
```

框架程序集（`Natter.*`、`Microsoft.Extensions.*`、`System.*`、`System.Reactive*`）由默认 `AssemblyLoadContext` 共享，**不要**在插件目录重复携带不同版本，否则会出现类型身份冲突。

## 7. 嵌入插件

无法从文件系统加载插件的宿主（Android APK、单文件特殊场景、测试）使用：

```csharp
var exitCode = await NatterCli.RunAsync(
    ["doctor"],
    console,
    new NatterCliOptions { LoadPlugins = false },
    builder => builder
        .AddEmbeddedPlugin<GreetingsPlugin>()
        .AddEmbeddedPlugin<MetricsPlugin>());
```

嵌入插件同样走 `IPluginRegistrationContext`，在 `plugins list` 中 `ORIGIN` 显示为 `embedded`。

## 8. 排查

| 现象 | 排查 |
| --- | --- |
| `plugins list` 显示 `Failed` | 查看 `Error` 列；检查清单 JSON、入口 DLL、无参构造函数 |
| 命令不存在 | 确认 `ICliCommandContributor` 已注册，且程序集已加载 |
| 命令存在但报 `No service for type` | 处理器类型未注册到 DI |
| 端口没有使用插件实现 | 确认插件注册顺序在框架模块之后；`natter ports` 查看最后绑定 |
| 事件收不到 | 订阅必须在 `IPluginLifecycle.StartAsync` 中建立，且宿主已 `StartAsync` |
| 插件 Id 重复 | 每个目录使用唯一 `id`；重复项会标记失败并被卸载 |
| 类型加载异常 | 插件与框架目标框架不兼容，或携带了冲突的共享程序集 |
