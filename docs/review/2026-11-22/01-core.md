# Natter 核心层代码审查（01 · core）

> 审查对象：`/home/ndm/Projects/csharp/Natter`（.NET 10 / 六边形 + DDD / Natter 聊天机器人框架）
> 审查方式：只读静态审查。**未修改任何源码、配置或测试**；本文件是唯一产物。
> 审查日期：2026-11-22

---

## 0. 概览

### 0.1 范围

| 目录 | 文件数（.cs，排除 bin/obj） | 行数 | 说明 |
| --- | --- | --- | --- |
| `src/Natter.Abstractions` | 15 | 654 | 全量精读 |
| `src/Natter.Domain` | 1 | 61 | 全量精读 |
| `src/Natter.Application` | 47 | 7,923 | 全量精读（`PromptComposer`/`AiTaskWorker`/`TaskDispatcher`/`ChatCommandRouter` 逐行） |
| `src/Natter.Infrastructure` | 分段精读 | ~5,300 / 8,396 | Composition、Events、Adapters、Dispatching、Chat（含 SQLite store）、Admin、Modules 全量精读；`Plugins/**`（含 `Nps/**`，约 3,000 行）按分工留给插件子报告，仅在"系统性观察"中做最小交叉引用 |

文档基线：`README.md`、`docs/ARCHITECTURE.md`（§1 依赖规则、§2 DDD 基元、§3 组合根、§4 Rx 事件流、§5 NLMP 槽位语义、§6.7 profile/持久会话、§9 已知边界）。文中"设计取舍"标注指文档明确声明为刻意的行为。

### 0.2 方法

1. 先读 `README.md` / `docs/ARCHITECTURE.md` 建立"声称的契约"，作为后续判定的对照基线。
2. 逐文件精读源码，重点核对：依赖方向与 csproj 引用、锁与 `Lock` 使用、`async` 边界（`async void`、fire-and-forget、`Task.Run`、`CancellationToken` 传递）、`IDisposable/IAsyncDisposable` 归属、异常契约（同一端口不同适配器抛什么）、`Source`/`SessionId` 等结构化 key 的往返保真、领域基元的相等性与表达式可翻译性。
3. 对每条可疑点做交叉验证（grep 全仓调用点、核对 DI 注册顺序、核对两端适配器实现），无法从代码确证的标注「待确认」。
4. 未执行 `dotnet build` / `dotnet test`：仓库为只读审查，构建会写入 `bin/`、`obj/`。因此所有结论均为静态推断，凡依赖运行时行为处均注明推断链。

### 0.3 结论段

内核（`Natter.Abstractions`）质量最好：`Entity`/`AggregateRoot`/`DomainEvent`/`Guard` 契约小而稳，`Equals/GetHashCode` 用 `GetType()` + `Id` 判定身份，`DomainEventEnvelope` 只读；`Natter.Domain` 作为示例聚合也无可指摘。

问题集中在**运行时编排层**（`Natter.Application.Chat` + `Natter.Infrastructure.Chat`）与**组合根**：

- **事件 → 响应链路没有背压也没有生命周期**：`ChatOrchestrator` 对每条 NLMP 事件 `_ = HandleAsync(value)` 后立即返回，同一会话的多条消息会并发进入 responder，回复顺序不确定；`StopAsync` 只退订，不等待在途工作。
- **任务运行时的"非失败"路径会向用户发送失败消息**：租约争用 / 版本冲突被代码注释判定为"not a failure"，但 `finally` 仍然投递 `"Task failed."`。
- **同一端口的两套适配器语义不一致**（内存 vs SQLite）：`ListActiveAsync` 对 `WaitingForInput` 的处理相反，导致 `in-memory` 宿主上 `/tasks status:waiting` 恒为空；`CreateAsync` 一个静默覆盖、一个抛 `SqliteException`。
- **组合根是"只有成功路径"的**：`NatterHost.StartAsync` 在 `_started = true` 之后才做 profile 校验并可能抛异常，且不回收已启动的 lifecycle、不允许重试。
- **安全控制存在条件性失效**：`HttpFetchTool` 的连接期地址校验（DNS rebinding 防线）在宿主机配置了 HTTP 代理时被静默跳过，而工具层预检本身对 rebinding 无效。
- **文档声称与实现存在若干偏离**：`Specification` 组合表达式使用 `Expression.Invoke`（ORM 不可翻译，与 §2 相反）、`SqliteTaskStore` 反解析 `Source.Key`（与 `Source.Key` 的"Never parse it back" 相反）、`profiles.json` 存在两套规则不一致的校验器。

严重度分布：**P0 × 1，P1 × 4，P2 × 21，P3 × 8（共 34 条）**。没有发现内存破坏级缺陷；最需要立即处理的是 C-01（安全控制条件失效）、C-03（错误用户可见消息）与 C-04（组合根无回滚）。

---

## 1. 发现清单

严重度定义：P0 安全/数据损坏/崩溃；P1 功能错误/竞态；P2 可维护性/性能/契约不一致；P3 风格与死代码。
「设计取舍」= 文档明确声明为刻意行为，另附残余风险。

---

### P0

#### C-01 `HttpFetchTool` 的 SSRF 连接期防线在配置 HTTP 代理时被静默关闭

**证据** `src/Natter.Infrastructure/Chat/ChatCompositionModule.cs:31-60`

```csharp
ConnectCallback = async (context, cancellationToken) =>
{
    var endpoint = context.DnsEndPoint;
    var targetHost = context.InitialRequestMessage.RequestUri?.DnsSafeHost;
    // When an egress proxy is configured the callback receives the proxy endpoint, not
    // the target; only direct connections are address-checked here.
    var direct = string.IsNullOrEmpty(targetHost)
                 || string.Equals(endpoint.Host, targetHost, StringComparison.OrdinalIgnoreCase);
    ...
    if (direct)
    {
        foreach (var address in addresses)
            if (Natter.Application.Chat.HttpFetchTool.IsBlockedAddress(address))
                throw new HttpRequestException(...);
    }
```

配合 `src/Natter.Application/Chat/Tools/HttpFetchTool.cs:75-79,117-144`（工具层只做一次 DNS 预检）与 `:27` 的文档声明"the address actually used for the socket connection is validated (DNS rebinding / redirect defence)"。

**影响**：`HttpFetchTool` 声称的 rebinding 防线只在直连时生效。一旦宿主/容器/CI 设置了 `HTTPS_PROXY`（企业网络、k8s 出网代理、`HTTP_PROXY` 环境变量很常见），`SocketsHttpHandler` 会让 `ConnectCallback` 收到**代理**端点，`endpoint.Host != targetHost` → `direct == false` → **整段地址校验被跳过**。此时攻击链为：`NATTER_HTTP_FETCH_HOSTS` 白名单内的公网域名（或可被模型诱导访问的域名）→ 工具层 DNS 解析时返回公网 IP 通过预检 → 代理侧二次解析被 rebinding 到 `127.0.0.1` / `169.254.169.254` → 请求打到内网或云元数据。工具层预检无法防 rebinding（注释自己也这么说），所以这是防线的**唯一**一环。

**建议**
1. 建立"任何非直连路径都必须显式校验"的失败即拒绝：`direct == false` 时不要 `if (direct)` 跳过，而是校验 `targetHost` 的解析结果（对目标主机再解析一次）或直接 `throw new HttpRequestException("proxy egress is not supported by the fetch tool")`。
2. 在 `HttpFetchTool` 构造时显式传入一个**无代理**的 `SocketsHttpHandler { UseProxy = false, Proxy = null }`，让代理配置无法改变语义。
3. `ReadOnlyToolPolicy.AllowNetworkTools` 默认 `false` 已提供默认关闭，应把"网络工具 + 代理"组合在启动期直接拒绝并在 `doctor`/`health` 中显示。

---

### P1

#### C-02 `ChatOrchestrator` 对每条事件 fire-and-forget：乱序、无取消、停机不排空

**证据** `src/Natter.Application/Chat/ChatOrchestrator.cs:155-168`

```csharp
public void OnNext(ChatEvent value)
    => _ = HandleAsync(value);

private async Task HandleAsync(ChatEvent chatEvent)
{
    try { await orchestrator.HandleAsync(chatEvent).ConfigureAwait(false); }
    catch (Exception ex) { logger?.LogError(...); }
}
```

`src/Natter.Application/Chat/ChatOrchestrator.cs:46-55`（`StopAsync` 只 `_subscription?.Dispose()`）。

**影响**
1. **同会话乱序**：`RxChatEventHub.Publish`（`RxChatEventHub.cs:49-62`）在 upstream 的接收线程上同步 `OnNext`，`OnNext` 立刻丢出一个游离 Task。同一会话连续两条消息会**并发**进入 `ProfileAwareChatResponder` → `IConversationStore` 读写与回复投递顺序不确定；模型侧 `AiConversationGate` 之外的串行化假设被打破（用户看到"第二条先回、第一条后到"）。
2. **无背压**：事件速率只受 upstream 限制，任务数无上限；N 条并发消息 = N 个并发 AI 调用 + N 个 SQLite 写事务。
3. **无 `CancellationToken`**：`HandleAsync(value)` 用默认 token，宿主停机/宿主取消无法中断在途的 responder 调用。
4. **停机不排空**：`StopAsync` 退订后立刻返回，在途 Task 继续访问即将被 `ServiceProvider.DisposeAsync` 释放的 store/SQLite 连接；`ChatOrchestrator.Dispose` 同样只退订。这会在停机窗口产生 `ObjectDisposedException`（被 catch 记日志）与"回复发到一半"。

**建议**：为 orchestrator 引入"每会话串行 + 全局有界并发"的调度器（`System.Threading.Channels` 或 `SemaphoreSlim` + per-conversation `SerialGate`），把 `CancellationToken` 贯穿到 `RespondAsync`/`ExecuteAsync`；`StopAsync` 应 `await` 排空（带超时）后再返回，与 `NatterHost.StopAsync` 的逆序语义对齐。

#### C-03 任务"被别处推进 / 租约未取得"时仍向用户发送 `"Task failed."`

**证据** `src/Natter.Application/Chat/Tasks/TaskDispatcher.cs:112-134`（两条注释明确说"not a failure"）

```csharp
if (!await store.TryAcquireLeaseAsync(task.TaskId, _owner, LeaseTtl, cancellationToken)...)
{
    // Another owner is running (or just finished) this task: not a failure.
    ReleaseTracked(task.TaskId);
    return;
}
...
logger?.LogDebug(ex, "Task {TaskId} was advanced by another actor; skipping this run.", task.TaskId);
ReleaseTracked(task.TaskId);
return;
```

`src/Natter.Application/Chat/Tasks/TaskDispatcher.cs:206-217`（`finally` 无条件投递）+ `:310-320`

```csharp
finally
{
    try { await SendResultOnceAsync(task).ConfigureAwait(false); }
    ...
}
...
var text = completed.Status switch
{
    AgentTaskStatus.Completed => completed.ResultText ?? "Task completed.",
    ...
    AgentTaskStatus.Pending or AgentTaskStatus.Running or AgentTaskStatus.WaitingForInput
        => "Task failed.",
```

**影响**：两条"跳过"路径都会进入 `finally`，`store.GetAsync` 此时读到 `Pending`/`Running`，于是向聊天发送 `"Task failed."`。触发场景真实存在：同一 SQLite state dir 上的多进程/多实例宿主（`_owner = host:{ProcessId}` 天生支持多进程）、`TaskRecoveryLifecycle` 与正在运行的实例同时启动、`EnqueueAsync` 被重复调用。结果：**用真正成功或仍在进行的任务给用户发一条假的失败通知**，并写 `_resultsSent` 使得后续真结果不会再被该实例发送。

**建议**：把"是否需要投递结果"从"是否进入 finally"解耦——只在本次实例**真正持有终态转移**时投递（`MarkTerminal` 成功）+ 对跳过的路径用 `ReleaseTracked` 后 `return` 而不投递；或给 `SendResultOnceAsync` 增加 `onlyIfTerminal` 语义：状态非终态则不发、转交后台重试循环。

#### C-04 `NatterHost.StartAsync` 在副作用之后才做 profile 校验，失败无回滚且不可重试

**证据** `src/Natter.Infrastructure/Composition/NatterHost.cs:47-101`

```csharp
public async ValueTask StartAsync(CancellationToken cancellationToken = default)
{
    ...
    if (_started) return;
    _started = true;                       // ← 先置位
    ...
    foreach (var lifecycle in _rootProvider.GetServices<IPluginLifecycle>()) { ... _startedLifecycles.Add(lifecycle); }
    if (_npsRuntime is not null) { await _npsRuntime.StartAsync(...); }   // 已连接 upstream
    ...
    Natter.Application.Chat.RuntimeProfileValidator.Validate(profileStore, responderRegistry, components); // ← 可能 throw
```

`RuntimeProfileValidator.Validate`（`src/Natter.Application/Chat/RuntimeProfileValidator.cs:45-50`）在 Tier 1 provider 缺失时**故意抛 `InvalidOperationException`**（文档 §6.7："Tier 1 provider 缺失时 host 启动失败"）。

**影响**
1. 抛出时上游已 `ConnectAsync`、lifecycle 已订阅、NPS 插件已激活，但调用方拿到的是异常；`_started` 仍为 `true`，`StartAsync` 无法重试（`if (_started) return`），宿主处于"半启动"状态。
2. 若调用方（CLI `serve`）把异常当致命错误直接退出而不 `DisposeAsync`，NPS 的 `AssemblyLoadContext`、SQLite WAL 连接、upstream 连接都得不到释放。
3. 顺序上也自相矛盾：校验放在 NPS 启动**之后**（为了让插件注册的 provider 可见），但文档把它描述为启动期策略检查；失败点越靠后，需要回滚的东西越多。

**建议**：先 `Validate` 再启动（插件 provider 在 `NpsPluginRuntime.Prepare` 后即可通过 registry 观测）；或把 `StartAsync` 整体包进 `try/catch` → `await StopAsync()` → `_started = false` → 重抛，形成"启动事务"。

#### C-05 任务存储两套语义分歧：in-memory 宿主上 `/tasks status:waiting` 恒为空

**证据** `src/Natter.Application/Chat/Tasks/InMemoryTaskStore.cs:28-35`

```csharp
.Where(task => task.SessionId == sessionId && task.Status is AgentTaskStatus.Pending or AgentTaskStatus.Running)
```

对比 `src/Natter.Infrastructure/Chat/SqliteTaskStore.cs:46-49`

```sql
AND status IN ('Pending','Running','WaitingForInput')
```

而 `ChatCommandRouter` 明确支持该过滤器：`src/Natter.Application/Chat/Commands/ChatCommandRouter.cs:411-413,420-426`

```csharp
if (normalized is not ("pending" or "running" or "waiting"))
...
"waiting" => status == AgentTaskStatus.WaitingForInput,
```

**影响**：使用内存 store（无 `--state-dir` 的测试/嵌入式/Android embedded 宿主）时，`/tasks status:waiting` 永远返回 `"No active tasks."`，即使确实存在 `WaitingForInput` 任务；改用 SQLite 又正常。这是**同一端口两个适配器的可观察行为差异**，同时暴露了 `ITaskStore.ListActiveAsync` 的契约含糊（"active"是否含 `WaitingForInput` 未定义）。

**建议**：把 `StatusMatches` 的过滤下沉为 store 参数（`ListActiveAsync(sessionId, statuses)`），或统一两端都包含 `WaitingForInput` 并在接口 XML 注释里写死"active = Pending|Running|WaitingForInput"。

（同一处的次要分歧：`InMemoryTaskStore.CreateAsync` 是 `_tasks[task.TaskId] = task`，`src/Natter.Application/Chat/Tasks/InMemoryTaskStore.cs:16` **静默覆盖**已存在任务并把 `Version` 回滚；`SqliteTaskStore.CreateAsync`（`SqliteTaskStore.cs:19-28`）会因主键冲突抛 `SqliteException`。`ITaskStore.CreateAsync` 因此没有可依赖的重复语义。）

---

### P2

#### C-06 取消被当作降级：`HostContextPipeline` 四处 `catch (Exception)` 吞掉 `OperationCanceledException`

**证据** `src/Natter.Application/Chat/Context/HostContextPipeline.cs:111,135,147,159`

```csharp
catch (Exception ex)
{
    fallback = "memory:" + ex.GetType().Name;
}
```

**影响**：调用方取消（宿主停机、slash 命令超时）后，pipeline 会把取消记为 `fallback="memory:OperationCanceledException"` 并**继续**用它已取消的 token 发起后续调用（`summaries.GetLatestAsync`、`promptProvider.GetAsync`、`contextAssembler.AssembleAsync`），每个都立即再抛一次取消，最终返回一个 `recent-only-fallback` 上下文给 responder。表现为：取消后仍然做满一轮工作、诊断字段被污染、"已取消"变成"降级成功"。同文件 `:126-131` 还会在**读路径**（AssembleAsync）里 `SaveAsync` 写汇总，取消语义更模糊。

**建议**：每个 catch 前加 `catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }`；把汇总写入从读路径移到后台任务。

#### C-07 `CompositeChatResponder` 静默吞掉 responder 异常，无任何日志

**证据** `src/Natter.Application/Chat/ChatResponderRegistry.cs:111-128`

```csharp
catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
catch
{
    // A broken responder must not prevent the remaining plugins from answering.
}
```

**影响**：隔离本身是对的（文档"故障订阅者隔离"），但 `CompositeChatResponder` 没有注入 `ILogger`，插件 responder 抛出的每个异常都**完全无声**地消失，同时"继续问下一个 responder"意味着一个崩溃的插件会被静默地绕过——运维侧无法区分"插件拒绝回答"和"插件崩了"。与 `ChatOrchestrator`、`HostPromptFragmentSource` 都记日志的做法不一致。

**建议**：注入 `ILogger<CompositeChatResponder>`，`catch (Exception ex)` 记录 `owner/id/eventId/exception type`（不记 prompt 内容），并让 `ChatEventDiagnostics` 暴露"被隔离的 responder 次数"。

#### C-08 `ApplicationDispatcher`：handler 被解析两次、异常类型被 `TargetInvocationException` 包住、单例从根容器解析

**证据** `src/Natter.Application/Dispatching/ApplicationDispatcher.cs:16-19,28-37,46-47`

```csharp
private readonly IServiceProvider _serviceProvider;
public ApplicationDispatcher(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;
...
if (_serviceProvider.GetService(handlerType) is null) throw new InvalidOperationException(...);
var invoker = InvokeMethod.MakeGenericMethod(requestType, typeof(TResponse));
return (Task<TResponse>)invoker.Invoke(null, [_serviceProvider, request, cancellationToken])!;
...
var handler = serviceProvider.GetRequiredService<IRequestHandler<TRequest, TResponse>>();
var behaviors = serviceProvider.GetServices<IPipelineBehavior<TRequest, TResponse>>().Reverse().ToArray();
```

**影响**
1. `GetService(handlerType)` 只为判空就**构造了一个 transient handler**（`ApplicationCompositionModule.cs:17` 注册为 `AddTransient`），随后 `InvokeAsync` 再 `GetRequiredService` 拿第二个实例；若 handler 构造函数有副作用或持有状态，行为已改变，且构造异常会让"存在性检查"本身抛出。
2. `invoker.Invoke` 是反射调用：`InvokeAsync` 内部在返回 Task 之前抛出的异常（如 `GetRequiredService` 失败、behavior 构造失败）会被包装成 `TargetInvocationException`，破坏"未注册 handler → `InvalidOperationException`"的异常契约；调用方按类型区分失败原因会失效。
3. 单例 dispatcher 持有根 `IServiceProvider`：`BuildServiceProvider(ValidateScopes = true)`（`NatterApplicationBuilder.cs:237-241`）下解析 scoped handler 会直接抛错，而 transient handler 的 scoped 依赖会被固化到根作用域（DI 容器泄漏）。
4. `IPipelineBehavior` 注册为 open generic transient（`CoreCompositionModule.cs:93`），每次都新建；`Reverse()` 的语义（最后一次注册最外层）没有文档，容易被后续模块误改顺序。

**建议**：用 `Invoker` 缓存（`ConcurrentDictionary<(Type,Type), MethodInfo>`）避免每次 `MakeGenericMethod`；判空改为直接 `GetRequiredService` 并吞掉 `InvalidOperationException` 转成明确的 `NoHandlerException`；用 `try/catch (TargetInvocationException ex) when (ex.InnerException is not null)` 还原内层异常；在文档里固化 handler/behavior 的生命周期要求（singleton/transient，禁止 scoped）。

#### C-09 SQLite 任务事件序号用 `MAX(seq)+1`，并发写会主键冲突并让 `TransitionAsync` 失败

**证据** `src/Natter.Infrastructure/Chat/SqliteTaskStore.cs:183-197`

```csharp
command.CommandText = "INSERT INTO task_events (task_id, seq, kind, payload_json, created_at) VALUES ($id, (SELECT COALESCE(MAX(seq),0)+1 FROM task_events WHERE task_id=$id), $kind, $payload, $now);";
```

表定义 `PRIMARY KEY(task_id, seq)`（`src/Natter.Infrastructure/Chat/SqliteContextStores.cs:140-147`）。

**影响**：`TransitionAsync`（`SqliteTaskStore.cs:116`）在更新任务后立即追加事件，两者**不在同一事务**中。两个并发写入者（同一任务的取消路径 + worker 终态路径；或多实例）可能读到相同的 `MAX(seq)`，第二个 `INSERT` 触发 `PRIMARY KEY` 冲突 → `SqliteException` → `TransitionAsync` 抛出非 `InvalidOperationException` 异常。`TaskDispatcher` 把 `InvalidOperationException` 当作"版本冲突"处理（`TaskDispatcher.cs:248-252`），其他异常走通用失败路径（`:181-205`），于是**一次事件序号冲突会把任务标成 `Failed`**。另外 `:117` 的

```csharp
return (await GetAsync(taskId, cancellationToken).ConfigureAwait(false))!;
```

在新连接上重读；此间任务被删除/被并发推进时返回 `null` 或过期快照，`!` 只是抹掉了编译器警告。

**建议**：`TransitionAsync` 把 UPDATE + 事件插入放进同一 `SqliteTransaction`，序号用 `INTEGER PRIMARY KEY AUTOINCREMENT`（或 `SELECT MAX(seq) ... ` 后在同一事务内插入）；返回值改为用 `UPDATE ... RETURNING *` 或复用事务内读，去掉 `!`。

#### C-10 `SqliteTaskStore` 反解析 `Source.Key` 复原 `Source`，违反结构化 key 契约

**证据** `src/Natter.Infrastructure/Chat/SqliteTaskStore.cs:221-223`

```csharp
var sourceKey = reader.GetString(2);
var parts = sourceKey.Split(':', 3);
var source = Source.Create(parts.Length > 0 ? parts[0] : "unknown", ...);
```

契约明令禁止：`src/Natter.Chat/Model/Source.cs:11-16`

```csharp
/// Readable identifier for logs and diagnostics. Never parse it back: routing,
/// storage and deduplication must use the structured fields.
public string Key => $"{Protocol}:{Platform}:{SelfId}";
```

文档同样声称"所有路由、去重、缓存 key 都包含结构化 `Source`，不解析 `sourceKey` 字符串"（`docs/ARCHITECTURE.md` §5）。

**影响**：`AgentTask.Source` 中除 `(Protocol, Platform, SelfId)` 之外的任何信息都会丢失；更重要的是**往返不保真**：`Source.Create` 只 trim 不做字符限制，若 `SelfId`/`Protocol` 含 `:`，`Split(':', 3)` 的重组会错位（例如 `a:b` 型 selfId 被并入 platform/selfId 的错误位置），恢复出的 `Source` 与原值不相等 → 结果投递时 `InMemoryChatUpstream.ExecuteAsync` 的 `command.Source != Source` 检查（`InMemoryChatUpstream.cs:122-127`）会拒绝投递，任务结果永远送不出去。当前 SQLite 表里也**只存了 `source_key`**（`SqliteContextStores.cs:123-124`），没有分解列。

**建议**：`agent_tasks` 增列 `protocol/platform/self_id`（迁移 v4），读路径用列还原；`Source.Key` 在 `Source.Create` 里禁止 `:`（或用非 `:` 分隔符），并在 `SqliteTaskStore` 加一条"round-trip 断言"的守卫注释。

#### C-11 同一个 `natter.db` 被 4 个 store 各自打开并各自建表，`foreign_keys` pragma 不一致

**证据** `src/Natter.Infrastructure/Chat/ChatCompositionModule.cs:101-124,167-170` 分别 new 出 `SqliteConversationStore` / `SqliteMemoryStore` / `SqliteSummaryStore` / `SqliteTaskStore`，其中后三者各自 `new SqliteContextDatabase(path)`（`SqliteContextStores.cs:171,191; SqliteTaskStore.cs:13`），每个构造函数都同步 `Migrate()`（`SqliteContextStores.cs:54-156`，`user_version` 1→3）；`SqliteConversationStore` 另有自己的 `Initialize()`（`SqliteConversationStore.cs:167-201`，`PRAGMA journal_mode = WAL` + 建 `sessions`/`messages`，且**不写 `user_version`**）。

`foreign_keys` 只在会话 store 的连接上开启（`SqliteConversationStore.cs:227`），`SqliteContextDatabase.OpenAsync`（`SqliteContextStores.cs:29-35`）**不设**该 pragma。

**影响**
1. 同一 schema（`sessions`）被两套 DDL 分别"拥有"：现在因为都是 `CREATE TABLE IF NOT EXISTS` 而**恰好**兼容，任何一方改列（例如给 `sessions` 加列）都会让另一个 store 的迁移静默失配；`user_version` 只有一方维护，`SqliteConversationStore` 的表完全在版本体系之外。
2. `SqliteSummaryStore.SaveAsync`（`:211-250`）在没有 FK 约束的连接上插入 `summaries.session_id` 引用，而删除走 `SqliteConversationStore.ClearAsync` 的 cascade（有 FK）——"能被建出来、却按另一套规则被清理"。一旦有人靠 `ON DELETE CASCADE` 做清理，`summaries` 可能留下孤儿行。
3. 构造期同步 IO：4 个 store 各在构造函数里 `connection.Open()` + 建表（`SqliteConversationStore.cs:167-201`、`SqliteContextStores.cs:54-156`），DI 解析即阻塞，且并发解析（`ValidateOnBuild` 之前多线程首解析）可能撞 `database is locked`。
4. `SecureFiles()` 在**每次** `OpenAsync` 都 `File.Exists/SetUnixFileMode` 三个文件（`SqliteConversationStore.cs:203-230`、`SqliteContextStores.cs:37-52`），每个消息都做 3 次 `stat` + 可能的 chmod。

**建议**：抽出单一的 `SqliteStateDatabase`（一个连接串、一份 migration、`user_version` 唯一维护者、`OpenAsync` 统一开 `foreign_keys=ON` + `busy_timeout`），四个 store 共享它；migration 移出构造函数改为显式 `InitializeAsync()`，由组合根在启动期调用一次。

#### C-12 `ChatCommandRouter` 没有拿到 `ToolLoopOptions`，`/status`、`/tasks` 报告的工具预算与实际 worker 不一致

**证据** `src/Natter.Infrastructure/Chat/ChatCompositionModule.cs:218-227`（只传 8 个参数，第 9 个 `ToolLoopOptions?` 省略 → `null`）

```csharp
new Natter.Application.Chat.ChatCommandRouter(
    ...GetRequiredService<IMemoryStore>(), ...<ISummaryStore>(), ...<IConversationStore>(),
    ...<ChatCommandFactory>(), ...<IContextComponentCatalog>(), ...<IProfileMemoryStoreResolver>(),
    ...<ITaskStore>(), ...<ITaskDispatcher>()));
```

对比同一文件 `:200-207` 的 `AiTaskWorker` 明确 `provider.GetService<ToolLoopOptions>()`，而 `ToolLoopOptions` 确实是单例（`:189`）。

**影响** `ChatCommandRouter.cs:237` 与 `:306`：

```csharp
var budget = Math.Max(1, toolLoopOptions?.MaxToolCallsHardCap ?? new ToolLoopOptions().MaxToolCallsHardCap);
```

宿主若通过 `ConfigureServices` 修改 `ToolLoopOptions.MaxToolCallsHardCap`（例如 5），worker 真正按 5 执行，而 `/status`、`/tasks` 永远显示 `tools=x/3`、`remaining=3-x`，可能显示 `remaining` 为 0 的假状态。同时每次调用都 `new ToolLoopOptions()` 分配默认值，掩盖了"没注入"这个事实。

**建议**：注册处补上 `provider.GetRequiredService<ToolLoopOptions>()`；把该参数改成必填（`ArgumentNullException`），让遗漏在 `ValidateOnBuild`/首次调用即暴露。

#### C-13 恢复期在 transport 就绪之前就开始跑任务，结果投递注定失败

**证据** `src/Natter.Application/Chat/Tasks/TaskRecoveryLifecycle.cs:11-22`

```csharp
public async ValueTask StartAsync(...)
{
    await store.ClaimExpiredLeasesAsync(DateTimeOffset.UtcNow, cancellationToken)...;
    foreach (var task in await store.ListRecoverableAsync(cancellationToken)...)
        if (task.Status == AgentTaskStatus.Pending)
            await dispatcher.EnqueueAsync(task, cancellationToken)...;   // ← EnqueueAsync 内部立刻 Task.Run
    _cts = new CancellationTokenSource();
    _watchdog = Task.Run(() => WatchdogAsync(_cts.Token), CancellationToken.None);
}
```

注册顺序（`ChatCompositionModule.cs:208,238,242`）为 `TaskRecoveryLifecycle` → `ChatIngressLifecycle` → `ChatOrchestrator`，`NatterHost.StartAsync` 按注册顺序启动（`NatterHost.cs:58-69`），而 NPS upstream 的 `ConnectAsync` 更在其后（`NatterHost.cs:71-87`）。

**影响**：恢复出来的任务在 transport 尚未连接、`ChatIngressLifecycle` 尚未订阅时就完成并尝试投递结果 → `IChatUpstreamRegistry.TryGet` 失败或 upstream 未连接 → 结果进入 `_pendingResults`，只能等 15s 周期重试（`TaskDispatcher.cs:354-388`）；若该目录下没有任何 upstream（纯 CLI/测试宿主），重试循环会永久持有这些 task id。此外 `EnqueueAsync` 是无上限 `Task.Run`，恢复一批任务即并发一批 AI 调用。

**建议**：把恢复拆成两步——`StartAsync` 只做 lease 清扫与登记，把 `EnqueueAsync` 推迟到"所有 upstream 已连接"之后（例如 `IHostReadyHook` / 在 `NatterHost` 的 NPS 启动之后调用 `ITaskRuntime.ResumeAsync()`），并给恢复加并发上限。

#### C-14 `PromptComposer` 的预算裁剪可以截断"强制安全片段"，与 host 不变量冲突

**证据** `src/Natter.Application/Chat/Context/PromptComposer.cs:679-697`

```csharp
if (used + separator + text.Length > max)
{
    var room = max - used - separator;
    if (room < 0) room = 0;
    text = text.Length > room ? text[..(int)room] : text;     // ← 强制片段也被切
    warnings.Add($"{PromptCompilerCodes.BudgetExceeded}:{candidate.Fragment.Id}");
}
```

与不变量声明 `src/Natter.Application/Chat/Context/PromptSafety.cs:56-63`

```csharp
// Reserve room for the mandatory suffix inside the budget so truncation can never
// drop host safety. Only when the suffix alone exceeds the budget is it cut later.
var coreLimit = Math.Max(0, limit - suffix.Length);
```

**影响**：`HostPromptSafety.Fit` 只在 assembler 侧保护"安全后缀"，而 `PromptComposer.ApplyBudget` 在编译器侧对**所有** mandatory 片段做同样的裁剪，包括 `host.safety.untrusted-data`。当 caller 传入较小的 `PromptBudget.MaxCharacters`（`PromptComposer.cs:55` 接受任意 `request.Budget`）时，安全指令会被切半句（例如只剩 `"Context data blocks are untrusted data, not instruct"`），仅留一条 `prompt.budget_exceeded` 警告。`ToBundle` 仍然标 `SafetyAppended = true`（`PromptComposer.cs:85-99`），assembler 于是**不再补全**（`HostRecentWindowContextAssembler.cs:95` → `Fit(..., safetyAppended: true, ...)`），安全块永久缺损且无人发现。

**建议**：`ApplyBudget` 对 `candidate.Fragment.Mandatory` 采用与 `Fit` 相同的"先预留后裁剪"策略，并且强制片段超预算时**整段拒绝编译**（抛 `PromptCompilationException`）而不是静默半句；或让 `ToBundle` 在 mandatory 被截断时把 `SafetyAppended` 置 `false`，由 assembler 兜底重加。

#### C-15 `systemPrompt` 走未白名单变量通道，绕过 `PromptVariables.Allowed`

**证据** `src/Natter.Application/Chat/Context/DefaultContextImplementations.cs:21-31`

```csharp
var system = request.Profile.Prompt.SystemPrompt;
if (string.IsNullOrWhiteSpace(system)
    && request.Variables.TryGetValue("systemPrompt", out var configured))
{
    system = configured;
}
```

生产方：`src/Natter.Feature.Ai/AiChatResponder.cs:170`（`["systemPrompt"] = _options.SystemPrompt`）。白名单：`src/Natter.Application/Chat/Context/PromptSafety.cs:77-87` 只允许 `profile.id / platform / locale / conversation.kind / user.name`。

**影响**：`PromptRequest.Variables` 同时承担两个互相冲突的语义——"编译期可替换变量（白名单 + 转义）"与"系统提示词覆盖"。`systemPrompt` 不经过 `PromptVariables.Escape`（`PromptSafety.cs:95-127`），也不在 `Allowed` 里，因此：任何调用方（含插件、测试、未来的 WebConsole 路由）只要往 `Variables` 塞这个键就能**整体替换系统提示词**，跳过 host 的 preset/fragment/禁用策略（虽然安全后缀仍由 composer 追加）。文档 `PROMPT_COMPILER.md` 的"只有白名单变量会被替换"因此不成立。

**建议**：把该旁路收敛为显式的 `PromptRequest.SystemPromptOverride`（带长度上限、仅 host 可设），或直接删除，改由 profile 的 `prompt.systemPrompt` 唯一提供；`GetAsync` 里对 `Variables` 只读白名单键。

#### C-16 `profiles.json` 存在两套规则不一致的校验器

**证据** 写路径：`src/Natter.Application/Admin/ProfilesConfigValidator.cs:19-197`；读路径：`src/Natter.Application/Chat/Profiles/RuntimeProfiles.cs:219-256`。
差异示例（validator 要求 `schemaVersion` 必须显式存在：`:46-63`）：

```csharp
else { issues.Add(new AdminConfigIssue("/schemaVersion", ..., "schemaVersion is required.", "1", null)); }
```

而 `RuntimeProfileStore.Validate` 只拒绝 `!= 1`（`:221-222`），缺失时由 `RuntimeProfileSet.SchemaVersion` 的 `init` 默认值 `1` 兜底（`RuntimeProfiles.cs:9`）。
反向差异：`RuntimeProfileStore` 强制校验每个 profile 的 `match` 五个列表非 null（`:252-254`），validator 只在字段存在且非数组时报告（`:164-173`）。

**影响**：`ProfilesConfigValidator` 的注释声称 "Mirrors the rules enforced by `RuntimeProfileStore`"（`:6-10`），实际是两套独立实现：Administrator 通过 admin 接口**无法保存**一份 host 实际能正常加载的 `profiles.json`（缺 `schemaVersion`），也可能放过 host 启动时才炸的文档。这类"双份规则"会随着字段演进持续漂移，且已有测试覆盖会各自通过。

**建议**：把 `RuntimeProfileStore.Validate` 抽成 `RuntimeProfileSetValidator.Validate(JsonElement)`，admin 侧只做"解析 → 调用同一函数"，`ProfilesConfigValidator` 退化为薄包装。

#### C-17 `ProfileResolver` 对不匹配的来源回落到 `defaultProfile`，造成跨平台误路由

**证据** `src/Natter.Application/Chat/Profiles/RuntimeProfiles.cs:292-313`

```csharp
var profiles = set.Profiles
    .Where(item => item.Score >= 0 && item.Profile.Policy.Enabled)
    ...
if (profiles.Count > 0) return profiles[0];

return set.Profiles.FirstOrDefault(profile => string.Equals(profile.Id, set.OverrideProfile ?? set.DefaultProfile, ...));
```

`Score`（`:315-339`）在 `Protocols/Platforms` 不匹配时返回 `-1`。

**影响**：当 `profiles.json` 只有 `discord` 与 `dingtalk` 两条 profile，而实际来源是第三种协议（satori、in-memory 测试源、未来的 webconsole）时，resolver 返回 `tier1-default`（`discord` profile，默认 provider 为 `natter.feature.ai.deepseek#deepseek`）。于是"不匹配任何 profile 的来源"被**当作 Discord 流量**处理：prompt/context/memory/history 策略全部套用错误 profile，`ProfileContext.Current` 也随之错误，`ChatCommandRouter.Store()`（`ChatCommandRouter.cs:431-441`）可能选到不该用的 memory store。代码注释只解释了"provider 未加载时 fallback"（`ProfileAwareChatResponder.cs:65-69`），并未解释"来源不匹配"也走同一条回落。

**建议**：区分两种回落——无匹配 profile 时返回 `null`（由 `ProfileAwareChatResponder` 落到 `CompositeChatResponder` 并记一条 warning），仅 `OverrideProfile` / `DefaultProfile` **显式**命中时才返回；`RuntimeProfileValidator` 增加"默认 profile 的 match 覆盖不到任何已知 platform"的启动期诊断。

#### C-18 Admin 保存时无条件 chmod 配置根目录，符号链接检查只覆盖末级文件

**证据** `src/Natter.Infrastructure/Admin/HostAdminConfigStore.cs:224-227`

```csharp
// OBS-WC-CONFIG-MODE: tighten a pre-existing config directory too; chmod does not
// touch the document contents.
Directory.CreateDirectory(root);
SetUnixMode(root, PrivateDirectoryMode);
```

符号链接：`HostAdminConfigStore.cs:509-545`（`new FileInfo(path).LinkTarget`，只检查目标文件本身）。

**影响**
1. `--config-dir` 可以指向用户已有目录（例如仓库里的 `config/`，README 的示例正是仓库内目录）。每次保存都会把该目录 chmod 成 `0700`；多用户共享目录、容器挂载卷（`config` 由另一个 uid 拥有）会因此改变属主语义，或直接 `UnauthorizedAccessException` 被吞进 `SetUnixMode`（`:598-613`）后**静默无效**。
2. 只检查末级文件的 `LinkTarget`：父目录是符号链接（或在检查与 `File.Move` 之间被替换成符号链接，`WriteAtomically` 在 `:572-595`）时，写入仍会落到 `root` 之外；文档声称"never follows a symlinked document"（`:11-15`）比实现更强。
3. `_writeLocks`（`:30,207`）以绝对路径为 key 的 `ConcurrentDictionary<string, SemaphoreSlim>` 永不移除、`SemaphoreSlim` 永不释放。

**建议**：把"目录权限收紧"变成显式选项（默认只对**本次创建**的目录生效），对已存在目录只告警；用 `Path.GetFullPath` + `FileSystemInfo.LinkTarget` 逐级（或 `File.ResolveLinkTarget(returnFinalTarget: true)`）校验整条路径；`_writeLocks` 在文档集合边界内做固定容量池（或改用 `ReaderWriterLockSlim` 字典 + 定期清理）。

#### C-19 `ChatIngressLifecycle` 在自己的锁内调用 upstream 的外部实现

**证据** `src/Natter.Infrastructure/Chat/ChatIngressLifecycle.cs:83-114`

```csharp
private void OnUpstreamRegistered(IChatUpstream upstream)
{
    lock (_gate) { if (_started) SubscribeLocked(upstream); }
}
private void SubscribeLocked(IChatUpstream upstream)
{
    ...
    var subscription = upstream.Events.Subscribe(new IngressObserver(upstream, _ingress, _logger));
    _subscriptions.Add((upstream, subscription));
}
```

`ChatUpstreamRegistry.Register` 在**释放**自己的锁之后才发事件（`ChatUpstreamRegistry.cs:40-43`），这一点是对的；问题在订阅侧：`upstream.Events.Subscribe` 是第三方/插件实现（`Natter.Chat.Satori`、`Discord`、`DingTalk`、示例插件），可能在订阅时同步回调（立即推送 `UpstreamStatus` 之类的初始事件），而 `IngressObserver.OnNext` → `ingress.TryPublish` → `RxChatEventHub.Publish` → 各 observer（可能包含插件对 `IChatUpstreamRegistry`/`ChatIngressLifecycle` 的回调）——全部在 `_gate` 内执行。`StopAsync` 也在锁内 `subscription.Dispose()`（`:59-62`），Rx 的退订会同步触发 `SafeObserver` 的清理路径。

**影响**：这是典型的"持有内部锁调用外部代码"：一旦某个 observer 回调到 `ChatUpstreamRegistry.Register/Unregister` 或再次进入 `ChatIngressLifecycle`，就是同线程可重入以外的**跨线程死锁**（`Register` 在锁外发事件 → 若 A 线程 Register 后同步进 OnUpstreamRegistered 拿 `_gate`，而 B 线程持 `_gate` 等在 `Register` 的锁上）。当前实现没有已知的触发路径，故标注**待确认**，但它是插件契约暴露面里的隐患。

**建议**：锁内只做"是否需要订阅"的判定与列表登记，`Subscribe/Dispose` 移到锁外（两阶段：`bool shouldSubscribe = false;` → 退锁 → 订阅 → 再入锁登记，用 id 幂等）。

#### C-20 `Specification` 组合用 `Expression.Invoke`，无法被 ORM 翻译（与文档相反）

**证据** `src/Natter.Abstractions/Ddd/Specification.cs:21-56`

```csharp
var body = Expression.AndAlso(
    Expression.Invoke(left.ToExpression(), parameter),
    Expression.Invoke(right.ToExpression(), parameter));
```

文档：`docs/ARCHITECTURE.md` §2 ——"`Specification<T>`：可组合的 `Expression<Func<T,bool>>`，可交给 ORM 适配器翻译"。

**影响**：`Invoke` 节点是 EF Core / Dapper 表达式翻译器的**已知不可翻译**构造（EF 会抛 "The LINQ expression could not be translated"）。也就是说 `And/Or/Not` 组合出来的 spec 只能 `IsSatisfiedBy` 内存求值，唯一"可翻译"的是未组合的叶子 spec。任何按文档把组合 spec 传给仓储的适配器都会在运行时炸。附带的 `IsSatisfiedBy`（`:13`）每次调用都 `Compile()`，是内存求值路径上的固定开销。

**建议**：实现 `ExpressionVisitor` 做参数替换（把 `Invoke` 展开为 `parameter` 代入的 body），或改用 `ExpressionCombiner` 在组合时直接拼接 body；`IsSatisfiedBy` 缓存编译结果（`Lazy<Func<T,bool>>`）。

#### C-21 `NatterApplicationBuilder`：插件 `Configure` 部分成功不回滚；`BuildServiceProvider` 失败时 ALC 不卸载

**证据** `src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:384-394`

```csharp
try
{
    module.Configure(registration);       // 已经向 _services 写入了一部分注册
    _services.AddSingleton(typeof(IPluginModule), module);
    _pluginCatalog.UpdateCapabilities(descriptor, registration.Capabilities);
}
catch (Exception ex) { _pluginCatalog.MarkFailed(descriptor, ex); }   // ← 注册不回滚
```

`:234-251`（`BuildServiceProvider` 抛异常时 `scan` 里已加载的 `PluginLoadContext` 没有 `Unload()`）。

**影响**：legacy 插件在 `Configure` 中途抛异常后，它已经注册的服务仍留在容器里，`MarkFailed` 只是标记——之后容器会解析到"失败插件"的半成品适配器，而 `ports` 输出显示该插件 `Failed`，两个视图矛盾。NPS 路径有 staging/回滚（文档 §6.1），legacy 路径没有。构建失败时所有已加载插件 ALC 泄漏，且 `NatterHost` 从未创建，调用方无从释放。

**建议**：`ConfigurePlugin` 记录 `_services.Count` 快照，失败时回滚到快照（`RemoveRange`）；`Build()` 用 `try/catch` 包住 `BuildServiceProvider`，失败时 `_pluginCatalog.Dispose()` 再重抛。

#### C-22 `JsonlAdminAuditSink` 把取消当作"跳过审计"

**证据** `src/Natter.Infrastructure/Admin/JsonlAdminAuditSink.cs:57-67`

```csharp
await File.AppendAllTextAsync(path, line + "\n", Utf8NoBom, cancellationToken)...;
...
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or OperationCanceledException)
{
    if (ex is OperationCanceledException) return;    // ← 审计行直接消失，无日志
    _logger.LogWarning(...);
}
```

**影响**：`HostAdminConfigStore.SaveAsync` 用 `_audit.RecordAsync(...)` 且**不传** token（`HostAdminConfigStore.cs:321-322` 调用 → `:370-397` 的 `WriteAuditAsync`），所以现在是安全的；但 `HostAdminLogReader.ReadAsync` 把**调用方的 token** 传下去（`AdminLogBufferLoggerProvider.cs:111-120`），一次客户端断开就会静默丢掉"谁读过日志"这条审计（`AdminAuditActions.ReadLogs`）。审计的完整性要求"要么落盘、要么明确报错"，当前是第三种：默默不落。另外 `File.AppendAllTextAsync` 被取消时可能留下不完整 JSON 行，污染后续 JSONL 解析。

**建议**：审计写入始终用 `CancellationToken.None`，取消只影响返回体；写前先拼整行再一次性 `WriteAsync`，并对不完整行做"启动时截断到最后一个 `\n`"的修复。

#### C-23 内存 memory store 与 SQLite store 的可见性/去重语义不一致

**证据** 内存实现用 `MemoryScope.IsVisible`：`src/Natter.Application/Chat/Context/DefaultContextImplementations.cs:241-253`

```csharp
.Where(item => MemoryScope.IsVisible(item, query.Source, query.Conversation, query.User, query.IncludePrivate))
```

SQLite 实现用"4 个 scope id + sensitivity"：`src/Natter.Infrastructure/Chat/SqliteContextStores.cs:226-259`

```csharp
WHERE scope_id IN ($s0, $s1, $s2, $s3) AND (expires_at IS NULL OR expires_at > $now)
  {(query.IncludePrivate ? "" : "AND sensitivity <> 'private'")}
```

去重：内存 `StringComparison.OrdinalIgnoreCase`（`:216`），SQLite `lower(text) = lower($text)`（`:185`）——SQLite 的 `lower()` 只处理 ASCII，中文/带重音字符在两端行为不同。

**影响**：同一份 `IMemoryStore` 契约下，`IncludePrivate` 的语义从"过滤 `Sensitivity == private`"（`MemoryScope.IsVisible`，`MemoryScope.cs:20-22`）变成"SQL 里的大小写敏感字符串比较"；`sensitivity` 值为 `"Private"`/`" PRIVATE"` 时两端结果不同。去重规则不一致意味着"同一句话"在内存宿主里被合并、在 SQLite 宿主里生成两条记忆。二者都会让 `P3_CONTEXT_MEMORY.md` 的"同一语义"承诺失效，且 `RuntimeProfileValidator` 无法发现。

**建议**：把可见性判定与去重键抽到共享规格（例如 `MemorySpecifications.Visible(...)` → SQL 片段 + 内存谓词的双实现测试），或让 SQLite 侧复用 `IncludePrivate` 语义（`sensitivity NOT IN ('private')` 用 `NOCASE` 排序规则 + 写入时规范化小写）。

#### C-24 `TaskDispatcher` 结果/跟踪字典无界增长

**证据** `src/Natter.Application/Chat/Tasks/TaskDispatcher.cs:21-24,286-295`

```csharp
private readonly ConcurrentDictionary<string, byte> _resultsSent = new(StringComparer.Ordinal);
private readonly ConcurrentDictionary<string, byte> _pendingResults = new(StringComparer.Ordinal);
...
if (_resultsSent.ContainsKey(task.TaskId)) return;
if (await TryDeliverResultAsync(...)) { _resultsSent[task.TaskId] = 0; _pendingResults.TryRemove(task.TaskId, out _); return; }
```

**影响**：`_resultsSent` **从不移除**，每个已完成任务永久留一个条目（key 是 `task_` + 16 hex + 字符串开销）。长跑宿主（文档 §9 提到 soak/长跑）下这是稳定泄漏；`_cancellations`/`_tracked` 只在异常路径之外被清理，`MarkTerminal` 里 `TryRemove` 失败时（例如已被 `ReleaseTracked` 移除）计数也会与 `Created()` 失配。

**建议**：`_resultsSent` 改成有界 LRU（或直接用 store 的终态 + 幂等键做去重，不必在内存里记账）；`TaskMetrics` 的 active 计数改为从 store 派生。

#### C-25 `ContextComponentCatalog` 对不含 `#` 的引用生成无法被删除的描述符

**证据** `src/Natter.Application/Chat/Context/ContextComponentCatalog.cs:140-147`

```csharp
var separator = reference.IndexOf('#');
var owner = separator <= 0 ? "host" : reference[..separator];
var id = separator <= 0 ? reference : reference[(separator + 1)..];
_components.RemoveAll(item => string.Equals($"{item.Owner}#{item.Id}", reference, StringComparison.Ordinal));
_components.Add(new ContextComponentDescriptor(owner, ..., id));
```

**影响**：当 `reference` 不含 `#`（例如插件/宿主注册 `"static"`、`"inline"`），描述符 key 变为 `"host#static"`，而 `Unregister(reference)`（`:128-138`）与 `UnregisterFragmentProvider`（`:99-106`）都用**原始** `reference` 拼接比较 → `RemoveAll` 永不命中：重复注册会不断堆积描述符，`/ports`、诊断输出出现重复行，且 `Unregister` 之后 `Components` 仍显示已卸载组件（`TryGet*` 已为 false），自相矛盾。当前内置引用都带 `#`（`ChatCompositionModule.cs:128-137`），故标注**待确认**是否已有插件用无 `#` 引用。

**建议**：descriptor key 统一由 `NormalizeReference(reference)`（无 `#` 时补 `host#`）产出，`Register/Unregister` 两侧都走它。

#### C-26 `TaskDispatcher` 停机不排空在跑的工作

**证据** `src/Natter.Application/Chat/Tasks/TaskDispatcher.cs:62,79-97`

```csharp
_ = Task.Run(() => ExecuteAsync(current, cts.Token), CancellationToken.None);
...
public async ValueTask DisposeAsync()
{
    foreach (var cts in _cancellations.Values) { cts.Cancel(); cts.Dispose(); }
    ...
    _retryCancellation.Dispose();
    await ValueTask.CompletedTask;     // ← 没有任何进行中任务的句柄
}
```

**影响**：`EnqueueAsync` 不保存 `Task` 句柄，`DisposeAsync` 只能取消 token 就返回。容器随后释放 `ITaskStore`（SQLite）与 `IConversationStore`；在途 worker 仍在 `store.TransitionAsync` / `AppendEventAsync` / `commands.ExecuteAsync` 上运行，可能命中已释放连接（`ObjectDisposedException` 被 `TaskDispatcher` 捕获记日志，或被 `AiTaskWorker.TraceAsync` 吞掉）；同时 `cts.Dispose()` 与 worker 的 `_cancellations.TryRemove(...).Dispose()` 形成对同一 `CancellationTokenSource` 的双重释放（`Dispose` 幂等，但 `CancelAfter` 已注册的回调在释放后取消会抛 `ObjectDisposedException`）。任务最终状态依赖竞态：可能被标为 `Cancelled`，也可能留在 `Running` 等下次启动的 watchdog 判 `Expired`。

**建议**：用 `ConcurrentDictionary<string, Task>` 保存 worker 任务，`DisposeAsync` 先 `Cancel()`，再 `await Task.WhenAll(tasks).WaitAsync(shutdownTimeout)`，最后释放 CTS；`CancellationTokenSource` 的所有权只留在 `ExecuteAsync` 的 `finally` 一处。

---

### P3

#### C-27 `EnsureStateDirectory()` 在 `_built` 检查之前执行

`src/Natter.Infrastructure/Composition/NatterApplicationBuilder.cs:150-156`

```csharp
public NatterHost Build()
{
    EnsureStateDirectory();
    if (_built) throw new InvalidOperationException("This composition builder has already been built.");
```

第二次 `Build()` 会先创建目录（并把权限改到 0700），再抛"已构建"。建议把守卫提前到第一行。

#### C-28 `TaskMetrics` 计数可为负，`Snapshot` 用 `Math.Max(0, ...)` 掩盖

`src/Natter.Application/Chat/Tasks/TaskMetrics.cs:17-23,49`：`Completed/Failed/Cancelled/Released` 都无条件 `Decrement(ref _active)`，`MarkTerminal` 与 `ReleaseTracked` 互相之间没有互斥保证（`TaskDispatcher.cs:255-282`），重复递减后 `Snapshot` 用 `Math.Max(0, ...)` 掩盖。建议用"每个 taskId 只结算一次"的集合（或 `Channel` 记账）替代裸计数器。

#### C-29 `AdminLogBufferLoggerProvider.IsEnabled` 恒真

`src/Natter.Infrastructure/Admin/AdminLogBufferLoggerProvider.cs:45-46`

```csharp
public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;
```

宿主通过 `AddLogging(b => b.SetMinimumLevel(Information))`（`NatterApplicationBuilder.cs:158-162`）设的全局过滤对该 provider 依然生效，所以当前不会真的收 Debug；但 provider 自己声明"我什么都收"，任何后续给该 provider 配 `SetMinimumLevel` 的调用都会把整机 Trace 灌进 500 条环形缓冲（每条还要过一次正则脱敏）。建议按 `LogLevel.Information` 显式过滤。

#### C-30 Rx 事件总线：订阅计数异常泄漏 + 锁类型不一致

`src/Natter.Infrastructure/Events/RxDomainEventBus.cs:38-46`（先 `Interlocked.Increment` 再 `Subscribe`，`Subscribe` 抛 `ObjectDisposedException` 时计数不回退）；`src/Natter.Infrastructure/Chat/RxChatEventHub.cs:39-46` 同构。另 `InMemoryEventJournal.cs:13` 用 `private readonly object _gate`，而全仓（`ConversationStore`、`TaskDispatcher`、`AdminLogBuffer`、`PortAdapterRegistry`…）统一用 .NET 9+ 的 `System.Threading.Lock`，建议统一。另注：`SafeObserver` 只记日志不转到 `OnError`，坏订阅者会永久静默，与 C-07 同一类问题。

#### C-31 死代码 / 未消费的契约

- `AdminAuditActions.ReadConfig`、`ValidateConfig`（`src/Natter.Application/Admin/AdminContracts.cs:47-49`）在 `src/` 中无任何生产者；`ReadLogs`、`RunCommand`、`SaveConfig` 有。审计面因此不是对称的（读配置不留痕）。
- `IChatMessageHandler`（`src/Natter.Chat/Ports/IChatUpstream.cs:46`）在 `ChatOrchestrationExtensions.cs:19,36` 注册，但全仓无消费者。
- `InMemoryMemoryStore.ExportAll()`（`DefaultContextImplementations.cs:314-318`）未被使用。
- `AdminLogBufferLoggerProvider.cs` 的 `HostAdminLogReader` 与 `IAdminLogReader` 只有 WebConsole 侧消费（跨项目，不计入本条），但 `AdminOptions.MaxLogEntries/MaxLogLimit/MaxLogMessageLength` 三个上限之间没有一致性校验（`MaxLogLimit > Capacity` 时 `Math.Min` 兜底，行为正确但配置无效）。

#### C-32 `PortAdapterRegistry.TryGet` 的后缀匹配可能命中同名类型

`src/Natter.Infrastructure/Composition/PortAdapterRegistry.cs:40-42`

```csharp
descriptor = _adapters.LastOrDefault(adapter =>
    adapter.Port == port || adapter.Port.EndsWith("." + port, StringComparison.Ordinal));
```

`GetDisplayName` 对非泛型类型返回 `type.FullName`（`ServiceCollectionPortExtensions.cs:55-58`），所以 `TryGet("IClock")` 依赖后缀匹配。两个不同命名空间下的同简称端口（例如未来 `Natter.Chat.IClock` 与 `Natter.Abstractions.Ports.IClock`）会同时命中，取"最后注册"，结果依赖注册顺序且无诊断。建议在 `Register` 时校验简称唯一性并在 `ports` 输出重复简称。

#### C-33 `Natter.Infrastructure` 对 `Natter.Domain` 的项目引用只用于字符串，且把示例聚合纳入插件共享闭包

`src/Natter.Infrastructure/Natter.Infrastructure.csproj` 引用了 `Natter.Domain`，但全仓只有两处"使用"它，且都是字符串字面量：`Plugins/PluginLoadContextOptions.cs:30`（`"Natter.Domain"` 加入 `PluginContractAssemblies.All`）与 `Plugins/PluginDiscovery.cs:8`（入口程序集排除前缀）。即：**示例聚合所在的程序集被声明为"插件契约闭包"的一部分**，所有插件都与 `Counter` 共享类型身份，而组合根并不需要编译期依赖它。建议从 csproj 移除该 `ProjectReference`，并重新审视 `PluginContractAssemblies` 是否应包含 `Natter.Domain`（插件契约不应包含示例业务聚合）。插件加载细节归插件子报告，此处只记依赖方向问题。

#### C-34 恒等表达式与语义误用

- `src/Natter.Application/Chat/Context/HostContextPipeline.cs:91-92`：`Math.Max(budget.MaxRecentCharacters, budget.MaxRecentCharacters * 2)` 对非负值恒等于 `*2`（且 `int` 溢出为负数时 `Max` 会选错），意图（"多取一倍做摘要"）表达失败，建议直接写 `checked(budget.MaxRecentCharacters * 2)` 或抽出命名常量。
- `src/Natter.Application/Chat/ConversationStore.cs:64`：`new ConversationState(SessionId(key), key, messages.Count)` 把"消息条数"当 `Version` 返回；`SqliteConversationStore` 返回的是 `sessions.version`（真正的版本列）。`ConversationState.Version` 因此没有可依赖语义（当前无消费者，但契约已被污染）。

---

## 2. 架构层面的系统性观察

### 2.1 依赖方向：内核干净，但"示例"侵蚀了契约边界

`Natter.Abstractions` 只依赖 `Microsoft.Extensions.DependencyInjection.Abstractions`，`Natter.Domain` 只依赖 Abstractions，`Natter.Application` 不引用宿主——文档 §1 的规则在 csproj 层面成立。真实问题不是"反向引用"，而是**契约面被示例与实现细节浸泡**：

- `Natter.Domain`（示例 `Counter`）被写进插件共享闭包（C-33）；
- `Natter.Application` 的公开契约为 `Microsoft.Extensions.AI.ChatMessage`（`ContextContracts.cs:75-80`）与 `Microsoft.Extensions.AI` 的 `ChatRole`，application 层因此与某个 SDK 绑定，任何 IChatClient 生态变化都直接改端口；
- `Natter.Application.Admin`（含 `IAdminConfigStore`、`IAdminCommandRunner`）与 `Natter.Application.Chat`（含 `HttpFetchTool`、`SqliteTaskStore` 的邻居）混在同一程序集里，"应用层"事实上承担了 admin 表面协议与网络工具实现。

建议：把 `Admin` 契约上提到 `Abstractions` 或独立 `Natter.Admin.Abstractions`；`ChatMessage` 用自有 DTO 隔离（或在文档中明确"M.E.AI 是应用层契约的一部分"并加版本约束）。

### 2.2 组合根是"成功路径唯一"的设计

`NatterApplicationBuilder.Build()` 与 `NatterHost.StartAsync()` 都没有失败回滚（C-04、C-21）：插件 ALC、DI 部分注册、已连接 upstream、已订阅 lifecycle 在异常时各自漂流。文档 §3 把"注册顺序即覆盖语义"讲得很清楚，但对"构建/启动失败时容器处于什么状态"没有承诺——这恰好是运维最需要的语义。建议引入显式的启动事务（`StartAsync` 失败 ⇒ `StopAsync` + 释放插件运行时），并在 `doctor` 中暴露"上一次启动是否回滚"。

### 2.3 同一端口的多个适配器缺乏一致性测试

`IConversationStore`、`ITaskStore`、`IMemoryStore`、`ISummaryStore`、`IChatUpstream` 都有 ≥2 个实现（内存 / SQLite / Satori / Discord / DingTalk / InMemoryChatUpstream）。本次发现的行为分歧（C-05、C-23、以及 `CreateAsync` 的覆盖 vs 抛异常）全部属于"契约没有被写死"的产物。文档 §5 强调结构化 key 与幂等语义，但端口 XML 注释里没有把"active 状态集合""去重是否大小写敏感""重复创建是覆盖还是报错"写成可断言条款。建议为每个端口增加**契约测试基类**（同一组 [Theory] 跑所有实现），这是本仓库投入产出最高的结构性改进。

### 2.4 事件链路：Rx 用得很"框架"，但缺少背压与生命周期语义

`RxChatEventHub`/`RxDomainEventBus` 用 `Subject.Synchronize` + `SafeObserver` 实现了"坏订阅者不杀死流"，这部分是扎实的。缺的是**投递语义**：`ChatOrchestrator` 的 `OnNext` 立即返回（C-02），意味着"流"在语义上只是"派发通知"，真正的处理是在任意线程池线程上的独立任务。这带来三个系统性后果：无背压、无顺序保证、无停机排空。文档 §4/§5 描述的是"事件 → 命令"的单向事实流，没有承诺顺序，但**用户可见的回复顺序**必须有承诺。建议在文档中明确（并实现）"同一会话串行、跨会话并发"这一条，作为 NLMP 运行时契约的一部分。

### 2.5 可测试性与可观测性

- 可测试性总体不错：时间走 `IClock`/`TimeProvider`，store 有内存实现，`AdminOptions`/`CompositionOptions` 可注入。扣分项是 `AiTaskWorker` 直接持有根 `IServiceProvider` 并做 `GetService`（`AiTaskWorker.cs:30-45`），使 worker 的依赖图在编译期不可见、在测试里必须靠容器补齐；`ChatCommandRouter` 的 8 个可选依赖同理（C-12 正是这种构造方式的产物）。
- 可观测性：诊断模型（`ContextDiagnostics`、`TaskTracePayload`、`AdminAuditEvent`）设计得很克制（只记 id/code/count，不记正文），红线清楚。但**故障可见性**有洞：`CompositeChatResponder` 静默（C-07）、`TaskDispatcher` 的两条跳过路径只在 Debug 记一行、`HostContextPipeline` 把真实异常压缩成 `fallback = "memory:TypeName"`。建议统一"降级计数器"（`IDegradationMetrics`），让 `health`/`doctor` 能报"今天有 N 次 responder 崩溃、M 次上下文降级"。

### 2.6 文档揭示的三处真实偏离（汇总）

| 文档声明 | 实现 | 条目 |
| --- | --- | --- |
| `Specification` 可交给 ORM 翻译（§2） | 组合使用 `Expression.Invoke`，EF 不可翻译 | C-20 |
| 路由/存储/去重**不解析** `sourceKey`（§5） | `SqliteTaskStore` 反解析 `Source.Key` 还原 `Source` | C-10 |
| Tier 1 缺失 ⇒ host 启动失败（§6.7） | 校验发生在启动副作用之后，失败留半启动状态 | C-04 |
| `profiles.json` 校验与运行时一致（`ProfilesConfigValidator` 注释） | 两套独立规则 | C-16 |
| 连接期地址校验抵御 DNS rebinding（`HttpFetchTool` 注释/§9） | 配代理时整段跳过 | C-01 |

---

## 3. 建议优先级

### 立刻（本周，P0/P1）

1. **C-01**：给 `HttpFetchTool` 配 `UseProxy = false` 的 handler，并把 `direct == false` 改成"拒绝"；同时在 `doctor` 输出"fetch 工具 egress 模式"。这是唯一的安全类发现，且修复成本极低。
2. **C-03**：`TaskDispatcher` 只在本实例持有终态转移时投递结果；跳过路径不投递。这是唯一"用户直接看到错误信息"的缺陷。
3. **C-04**：`StartAsync` 加启动事务（失败 ⇒ `StopAsync` ⇒ `_started = false` ⇒ 重抛），并把 `RuntimeProfileValidator.Validate` 前移到第一个 lifecycle 之前。
4. **C-02**：给 `ChatOrchestrator` 加"同会话串行 + 全局有界并发 + 取消贯穿 + 停机排空"。这是本次审查中架构影响最大的改动，建议单独立项。

### 近期（P2，按投入产出排序）

5. **C-11 → C-09 → C-10**：SQLite 状态层重构（单一 migration 所有者、`TransitionAsync` 事务化、`agent_tasks` 结构化 source 列）。三者同属一处代码，一起做最省。
6. **C-05 + 2.3**：为 `ITaskStore`/`IMemoryStore`/`IConversationStore` 建契约测试基类，先把"active 集合""重复创建""去重大小写"写死，再修两端实现。
7. **C-07 + C-15 + C-13 + C-14**：可观测性与 prompt 安全一致性打包处理（`CompositeChatResponder` 记日志、删掉 `systemPrompt` 旁路、恢复期推迟到 transport 就绪、mandatory 片段超预算即拒绝编译）。
8. **C-12 / C-08 / C-16 / C-17 / C-25**：契约收敛类小修（补注入、异常类型还原、校验器合并、resolver 返回 null、descriptor key 归一化）。

### 计划（P3 清理）

9. **C-18 / C-22 / C-26 / C-24**：Admin 与任务运行时的资源与审计细节（目录权限选项化、审计不被取消、worker 句柄可等待、字典有界）。
10. **C-27–C-34**：死代码、恒等表达式、`Lock`/`object` 统一、无用项目引用。建议顺手加 `.editorconfig` 规则（`csharp_style_namespace_declarations` 已是 warning，可再加 `dotnet_diagnostic.IDE0055` 与"未使用 using"），并在 CI 打开 `EnableNETAnalyzers`（当前 `Directory.Build.props` 显式关闭）。

### 未覆盖 / 需后续确认

- `src/Natter.Infrastructure/Plugins/**`（含 `Nps/**`，约 3,000 行）与 `Natter.Plugin.*`、`Natter.Chat.*` 适配器、`Natter.Feature.Ai*` 不在本次范围内；C-13（启动顺序）、C-19（锁内回调）、C-21（ALC 回收）、C-33（契约闭包）都指向那片代码，建议与插件子报告对照阅读。
- C-19、C-25 标注了「待确认」（需要插件侧调用点或运行期 trace 佐证），其余条目均有直接的代码证据。
- 所有结论基于静态审查；未运行 `dotnet build`/`dotnet test`（只读约束），因此"是否已有测试覆盖该路径"未逐一核实。
