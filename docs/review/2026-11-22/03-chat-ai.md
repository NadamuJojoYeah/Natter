# Natter.Chat 协议适配与 AI 特性代码审查（NLMP v0.1）

- 审查对象（排除 `bin/obj`）：
  - `src/Natter.Chat`（Model、Keys、Events、Commands、Ack、Ports、Dispatch、Reliability、Degradation、Safety、Serialization）
  - `src/Natter.Chat.Satori`、`src/Natter.Chat.Discord`、`src/Natter.Chat.DingTalk`
  - `src/Natter.Feature.Ai`、`src/Natter.Feature.Ai.DeepSeek`、`src/Natter.Feature.Ai.Spark`
  - 参照 `docs/NLMP.md`、`docs/DISCORD_PLUGIN_DESIGN.md`、`docs/AI_PROVIDER_DESIGN.md`、`docs/SECOND_ADAPTER_VALIDATION.md`
  - 为判定"按会话串行是否真的串行"，另做了只读追链：`src/Natter.Infrastructure/Chat/{ChatIngressProcessor,RxChatEventHub,ChatIngressLifecycle,ChatCompositionModule}.cs`、`src/Natter.Application/Chat/ChatOrchestrator.cs`
- 方式：**只读静态审查**，未改动任何源码/配置/测试；`git status` 干净。本报告是唯一写入物。
- 运行时验证（两处，均在 `/tmp` 一次性临时工程内完成并丢弃，未触碰仓库）：
  1. `DateTimeOffset.TryParse(..., DateTimeStyles.RoundtripKind)` 在 .NET 10 下**不抛异常**（排除了一条误报）；
  2. Discord.Net **3.20.2**（仓库 `Directory.Packages.props:14`）的 `AllowedMentions` → `Discord.API.AllowedMentions` 线格式实测：`new AllowedMentions((AllowedMentionTypes?)null){ UserIds = … }` 生成 `parse: []`、`users: [id]`、`replied_user: false`；`AllowedMentions.None` 也是 `parse: []`（见 D-2 结论）。
- 结论速览：NLMP 模型层与安全护栏（私网/凭据 URL 拒绝、`everyone/here` 出站拦截、原子去重）设计是**认真**的；真实缺陷集中在 **①"会话串行"没有覆盖读历史/写历史（A-1）**、**②Satori 出站文本未转义导致元素/mention 注入（S-1）**、**③Discord 私聊绕过 guild/channel 白名单（D-1）**、**④DingTalk Stream 的 ack 时序 / 无接收超时 / 每消息新建 HttpClient（DT-1/2/3）**、**⑤幂等"检查-使用"竞态与只缓存成功 ack（C-1）**、**⑥provider 认证/余额类错误对用户完全静默（A-2）**。

## 0. 严重度统计

| 严重度 | 数量 | 分布 |
| --- | --- | --- |
| P0 | 0 | 未发现"必然导致数据损坏 / 凭据泄露 / 越权写"的确定性缺陷；最接近 P0 的是 S-1（外部可见的 mention 注入）与 A-1（同会话并发导致上下文串台） |
| P1 | 8 | 幂等竞态（C-1）、Satori 出站注入（S-1）、Discord 私聊白名单绕过（D-1）、DingTalk 每消息 HttpClient / ack 时序 / 无接收超时（DT-1/2/3）、会话串行失效 / 错误静默（A-1/2） |
| P2 | 27 | 去重键与窗口、分片部分失败、Satori sn 提交语义、能力抖动、Discord mention 线格式与缓存无界、DingTalk 重连/帧上限/webhook 校验、AI 截断未识别、thinking+tools、裸 HttpClient、gate 无界、限流无重试等 |
| P3 | 22 | 有损文本映射、markdown 分片、附件守卫覆盖面、序列号非单调、假 messageId、Dispose 顺序、截断无提示、tool_call 解析边界等 |
| 合计 | 57 | P1/P2 每条给出片段级证据；P3 汇总为表格（含 `file:line` 与修复方向） |

> 计数口径：C-* 为 `Natter.Chat` 核心，S-* 为 Satori，D-* 为 Discord，DT-* 为 DingTalk，A-* 为 AI 特性。跨模块问题只计一次，在正文标注交叉引用。

## 1. Top 8 发现

| # | 等级 | 位置 | 一句话 |
| --- | --- | --- | --- |
| 1 | P1 | `src/Natter.Feature.Ai/AiChatResponder.cs:163-206`（配合 `:104`） | `AiConversationGate` 只在"调模型"期间持有，**读历史在 gate 之前、写历史在 gate 之后**：同会话两条消息会基于同一份旧历史并发生成，历史写入顺序也可能颠倒 |
| 2 | P1 | `src/Natter.Chat.Satori/SatoriCommandMapper.cs:105-137` | 出站把 `content.Text` **原样拼进 Satori 元素串**，不转义：模型输出 `<at type="all"/>`、`<img src=...>` 会被 Satori 端当真元素解析，绕过 `ChatMentionGuard` |
| 3 | P1 | `src/Natter.Chat.Discord/DiscordEventMapper.cs:108-116` | `ConversationKind.Direct/Group` 直接 `return true`，**私聊完全绕过 `guilds/channels` 白名单**；`users` 为空时任何人都能私聊触发 AI |
| 4 | P1 | `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:229-256` | 接收循环只在 `Disconnect` 取消，`KeepAliveInterval` 只发 ping 帧却不检测 pong/空闲：半开连接会**永久挂起**且 `IsConnected` 仍为 true |
| 5 | P1 | `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:95` | 每条回复 `new HttpClient{...}`：连接池/TLS 会话不复用，并绕开注入的 `_http`（代理由此失效） |
| 6 | P1 | `src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs:53-55` + `:73-74` | 幂等是典型 check-then-act，且**只缓存 `ok=true` 的 ack**：同 key 并发重试会双发；部分分片失败后的重试无 reconciliation |
| 7 | P1 | `src/Natter.Feature.Ai/AiChatResponder.cs:233-242` | `Authentication / InsufficientBalance / InvalidRequest` 返回 `null` → **用户侧完全静默**（无兜底文案、无计数告警），与 `AI_PROVIDER_DESIGN.md:590` 的"重试耗尽后返回 unavailable fallback"不一致 |
| 8 | P1 | `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:277-299` | 先 `_events.OnNext(...)`（同步穿透到 ingress/hub）**后**才 ack：下游变慢即 ack 超时 → 钉钉重投 → 重复处理；ack 异常还会被当成 `stream_receive_failed` 触发重连 |

紧随其后、同样需要排期的是 `DT-2`（ack 时序已列 #8 的另一面）与 `C-7`（Satori 私聊回复被 dispatcher 判 `unsupported`，见 §3）。

## 2. 最关键的 5 个文件

1. `src/Natter.Feature.Ai/AiChatResponder.cs` — 共享 responder 的全部语义边界：gate 覆盖范围、历史写入时机、幂等键、截断判定、错误降级都在这里（P1 两处集中在 `:163-206` 与 `:233-242`）
2. `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs` — ack/重连/帧缓冲/发送路径（P1 三处：`:95`、`:229-256`、`:277-299`）
3. `src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs` — 唯一出站汇聚点：校验、能力降级、分片、幂等（`P1 C-1` 与 `P2 C-2/C-7` 均在此）
4. `src/Natter.Chat.Satori/SatoriCommandMapper.cs` + `SatoriChatUpstream.cs` — 出站元素拼装（注入点）与 sn 提交/能力覆盖（`P1 S-1`、`P2 S-2/S-4`）
5. `src/Natter.Chat.Discord/DiscordEventMapper.cs` — 入站准入（白名单/mention/bot 过滤）的唯一裁决点（`P1 D-1`、`P2 D-2`）

---

## 3. P1（8 条）

### C-1 幂等"检查-使用"竞态，且失败 ack 不入缓存 → 可双发/重发

`src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs:53-55`

```csharp
var key = command.IdempotencyKey;
if (!string.IsNullOrWhiteSpace(key) && _idempotency.TryGet(key, out var cached) && cached is not null)
    return cached;
```

`src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs:73-74`

```csharp
if (ack.Ok && !string.IsNullOrWhiteSpace(key))
    _idempotency.Store(key, ack);
```

- **影响**：`TryGet` 与 `Store` 之间没有 in-flight 占位（`ChatIdempotencyCache` 也没有 `TryReserve`）。同一个 `idempotencyKey` 的两个并发调用都会 miss 然后各发一次（例如 ingress 在 `ChatEventDeduplicator` 窗口外重放、或上层超时重试与原始请求重叠）→ 用户在频道里看到两条相同回复。`ChatIdempotencyCache.cs:45-84` 的 `TryGet/Store` 都是独立加锁，无法表达"正在发送"。
- 三个适配器复制了同一模式：`DiscordChatUpstream.cs:147-157`、`DingTalkChatUpstream.cs:118-119`、`SatoriChatUpstream.cs:165-167`，因此**三层幂等同时具备同一竞态**。
- **修复方向**：把缓存改成有状态机（`Pending/Ok/Failed`）并提供 `TryReserve(key)`；dispatcher 先 `TryReserve`，成功才发，失败按 `retryable` 决定是否释放 key。失败 ack 也入库（带短 TTL）以便重试命中"已失败"而不是重发真正的错误分支。

### S-1 Satori 出站文本未转义，可注入元素与 mention（绕过出站 mention 护栏）

`src/Natter.Chat.Satori/SatoriCommandMapper.cs:109-137`

```csharp
var text = content.Text ?? string.Empty;
if (content.Attachments is not { Count: > 0 } attachments)
    return text;                      // ← 纯文本也原样返回，作为 Satori elements 发送
...
builder.Append('<').Append(tag)
    .Append(" src=\"").Append(EscapeAttribute(attachment.Url)).Append("\"")
    .Append("/>");
```

- **影响**：Satori 的 `message.create` 的 `content` 是 **XHTML 风格元素串**，不是纯文本。这里只对附件 URL 做了转义，正文完全透传。于是：
  - 模型/上游任何来源的文本里出现 `<at type="all"/>` 或 `<at id="all"/>`，Satori 客户端会渲染成 `@everyone` —— 而 `ChatMentionGuard.Validate`（`Safety/ChatMentionGuard.cs:27-37`）只看**结构化** `Content.Mentions`，此处被完全绕过（入站方向 `SatoriContentMapper.cs:66-79` 正好承认 `at type="all"` 等价 `MentionKind.Everyone`）；
  - `<img src="https://..."/>`、`<a href=...>` 会让客户端去拉取攻击者/模型指定的 URL（客户端侧 SSRF/追踪像素），并为 `<at id="...">` 提供直接 ping 任意用户的能力；
  - 用户文本里的普通 `<`、`&` 会破坏元素解析（`SatoriContentMapper.TryParse` 失败后走 `HtmlDecodeAndStrip` 正则剥离）→ **回复内容被静默改写成残缺文本**（数据丢失）。
- **修复方向**：出站先对正文做 XML 实体转义（`& < > " '`）再拼装；或改用 Satori 的纯文本字段（若协议支持 `content` 之外的 text 表达）。同时在 `ChatCommandDispatcher` 出口增加"文本中出现 `@everyone/@here` 且策略禁止 → 拒绝"的兜底（与 `ChatMentionGuard` 同源策略，扫描文本而非仅结构化 mention）。
- **待确认**：Satori v1 是否接受 `content` 中的裸文本（本仓库无 wire transport 实现，`ISatoriTransport` 只有接口，见 `SatoriChatUpstream.cs:20-29`），若接受，则本条降级为"数据完整性 + 客户端渲染异常"，但注入面仍成立。

### D-1 Discord 私聊/群私聊绕过 guild/channel 白名单

`src/Natter.Chat.Discord/DiscordEventMapper.cs:108-116`

```csharp
private bool IsConversationAllowed(Conversation conversation, string? channelId, string? guildId)
{
    // DMs and group DMs have no guild and their channel ids are not part of the
    // guild/channel allowlist. User allowlisting still applies to them.
    if (conversation.Kind is ConversationKind.Direct or ConversationKind.Group)
        return true;

    return _options.IsGuildAllowed(guildId) && _options.IsChannelAllowed(channelId);
}
```

- **影响**：`DiscordOptions.Users` 默认 `[]`，而 `IsUserAllowed` 在空列表时**恒真**（`DiscordModels.cs:81-82`）。因此默认配置下：任何人给 bot 发私信 → 通过 allowlist → `RequireMention` 只对 Guild/Thread 生效（`:34-41`）→ 直接进入 AI responder。后果是**未授权用户可无限消耗 LLM 配额**、绕过运营方以为已生效的频道封锁，并可对模型做自由输入（提示注入/内容风险）。
- 与 `docs/DISCORD_PLUGIN_DESIGN.md:209-212` 的表述（"`guilds/channels` 空数组由插件策略决定是否全部禁用""DM 不受 requireMention 限制"）只部分吻合：文档没说 DM 绕过白名单，代码注释把"绕过"当成设计；**待确认**是否为有意策略。
- **修复方向**：给 DM 增加独立开关 `AllowDirectMessages`（默认 false）+ `dmUsers` 白名单；或规定 `Guilds/Channels` 非空时默认拒 DM。同时在 `/health`/`discord status` 暴露"当前 DM 是否开放"。

### DT-1 每条回复新建 `HttpClient`

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:93-107`

```csharp
// Stream mode still returns a sessionWebhook for replies. Keep the same send path as
// the HTTP callback transport; the bot can later add an active API sender.
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
...
using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
```

- **影响**：每次 `message.send` 都新建 `HttpClient` + 新建连接池 + 完整 TLS 握手 + `Dispose`：高频回复时 TIME_WAIT/端口耗尽、握手延迟（钉钉 sessionWebhook 回复本身有时效，握手占掉的时间直接压缩可用窗口）；同时该类已经持有 `_http`（`:40`，可能由宿主注入代理/证书策略），这里完全绕开它 —— 配置了代理的环境回包走直连，注入的 `MaxConnectionsPerServer`/证书回调全部失效。
- 与 `docs/SECOND_ADAPTER_VALIDATION.md:355` 的"日志不打印 clientSecret/sessionWebhook 查询串"同一批可靠性要求相关（sessionWebhook 含会话令牌，直连泄露面更大）。
- **修复方向**：复用 `_http`（`_ownsHttp` 已有生命周期管理）；如需独立超时，用 per-request `CancellationTokenSource`，而不是新建客户端。

### DT-2 ack 在事件发布之后、且同步等待下游

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:277-299`

```csharp
if (string.Equals(frame.Type, "CALLBACK", StringComparison.OrdinalIgnoreCase))
{
    var parsed = DingTalkCallbackParser.Parse(frame.Data ?? string.Empty);
    if (parsed.Ok && parsed.Message is not null)
    {
        _events.OnNext(new DingTalkTransportEvent { Kind = ..., Message = parsed.Message });
        await SendAckAsync(socket, frame.MessageId, 200, "OK", cancellationToken).ConfigureAwait(false);
    }
```

- **影响**：`_events` 是 `Subject.Synchronize` 的**同步**主题，`OnNext` 会一路跑到 ingress → `RxChatEventHub` → 所有订阅者才返回。虽然 `ChatOrchestrator` 用了 `_ = HandleAsync(...)` 脱离（`ChatOrchestrator.cs:155-156`），但任何同步订阅者（记忆/任务/审计插件）都会把延迟压进 ack 路径；一旦超过钉钉的 ack 期限，服务端重投同一 `messageId`，而上游去重只靠 `ChatEventDeduplicator`（10 分钟窗口）兜着 —— 窗口外的重投就是**重复 AI 回复**。
  反之 `OnNext` 若把异常抛回（`Subject.Synchronize` 会向调用方传播），会被 `RunLoopAsync:159-162` 吞成 `stream_receive_failed` 并**触发重连**，等于一条坏消息打掉整条连接。
- **修复方向**：先 ack、后 publish；或 publish 到有界队列（`Channel<T>`）后立刻 ack，由后台消费者处理。ack 失败要单独记录（见 DT-7），不得与接收异常共用重连分支。

### DT-3 无接收超时 / 无 pong 检测：半开连接永久挂起

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:216-220`、`:229-256`

```csharp
var socket = new ClientWebSocket();
socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
...
while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
{
    var result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
```

- **影响**：`cancellationToken` 只来自 `_cts`（`Disconnect`/`Dispose` 才取消）。`KeepAliveInterval` 只保证**定期发 ping 帧**，`ClientWebSocket` 不会因长时间无数据而超时；NAT/负载均衡静默丢流时 `ClientWebSocket.State` 仍是 `Open`，`ReceiveAsync` 永久挂起 → 机器人"在线但不再收到任何消息"，`IsConnected` 仍返回 true，健康检查无法发现。`docs/SECOND_ADAPTER_VALIDATION.md:100`（"需要处理心跳、断线重连"）与 `:232`（"心跳超时"用例）都要求这一点。
- **修复方向**：给 `ReceiveAsync` 加空闲超时（`linked CTS` + `CancelAfter(IdleTimeout)`），超时即 `Abort()` 并走重连；或实现应用层 ping/pong 计数（连续 N 次无 pong 判定死链）。

### A-1 "按会话串行"没有覆盖读历史 / 写历史

`src/Natter.Feature.Ai/AiChatResponder.cs:163-206`

```csharp
if (_contextPipeline is not null) { ... messages = bundle.Messages; }   // ← 组装（含读历史）在 gate 之外
else { messages = await _contextAssembler.AssembleAsync(..., await _history.GetMessagesAsync(conversationKey, ...)); }

try
{
    using var lease = await _gate.AcquireAsync(conversationKey, cancellationToken).ConfigureAwait(false);
    ...
    var text = await CallAsync(messages, options, cancellationToken).ConfigureAwait(false);
```

`src/Natter.Feature.Ai/AiChatResponder.cs:100-104`

```csharp
var reply = generated.Text;
await _history.AppendTurnAsync(key, text, reply, chatEvent.Id, cancellationToken).ConfigureAwait(false);
```

- **影响**：`AiConversationGate` 的注释（`AiConversationGate.cs:3-6`）宣称"This prevents overlapping AI calls from racing on history or producing out-of-order replies"，`docs/AI_PROVIDER_DESIGN.md:578` 也写"同一 `(source, conversation.id)` 串行"。实际顺序是 **读历史 → （并发窗口）→ gate → 调模型 → 释放 gate → 写历史**：
  1. 同会话两条消息 A、B 同时到达（`ChatOrchestrator` 是 `_ = HandleAsync(...)` 全并发）→ A、B 都读到同一份旧历史 → B 的回复看不到 A，且两条回复都“不知道自己前面还有一条”；
  2. 写完历史的顺序由模型返回时间决定，B 可能先于 A 落库 → 历史时间线错乱（对"用户说/助手答"成对追加，错序后语义错位）；
  3. 若 A 生成失败（fallback）后 B 成功，历史里只剩 B 的轮次，A 的用户消息永久丢失。
- **修复方向**：把 gate 提升到 `RespondAsync` 整个消息处理（read → assemble → call → append），或改为每会话串行队列（`Channel`/`SemaphoreSlim` + 单消费者），保证"读取-生成-追加"是一个临界区。`HistoryScope=user` 时 key 含 senderId（`AiConversationStore.cs:13-20`），同频道多用户会并发 —— 需明确这是否符合"per-conversation"承诺。

### A-2 认证 / 余额 / 非法请求类错误对用户完全静默

`src/Natter.Feature.Ai/AiChatResponder.cs:233-242`

```csharp
catch (Exception ex) when (ex is IAiProviderException provider)
{
    _log.Warning("ai request failed: kind={Kind} status={Status} code={Code}", ...);
    return provider.Kind is AiErrorKind.Timeout
        or AiErrorKind.Unavailable
        or AiErrorKind.ContentFilter
        or AiErrorKind.Unknown
        ? new GenerationResult(FallbackText())
        : new GenerationResult(null);      // ← Authentication / InsufficientBalance / InvalidRequest
}
```

- **影响**：`Authentication`（key 失效/被轮换）、`InsufficientBalance`（欠费）、`InvalidRequest`（模型名写错、参数越界）三种情况下 `RespondAsync` 返回 `null`，dispatcher 不发任何命令 —— 用户看到的是"bot 已读不回"，运维侧只有一条 `Warning`（不含 `LastError` 计数、无健康状态变化）。这类错误恰恰是最需要立刻可见的配置/账务故障。DeepSeek 侧 `Retryable` 不含这三类（`DeepSeekErrors.cs:43`），所以不会重试，失败是确定性的。
- 与 `docs/AI_PROVIDER_DESIGN.md:590`（"重试耗尽后由 responder 返回统一 unavailable fallback"）不一致。
- **修复方向**：把"可诊断错误"与"内容策略错误"分开：`Authentication/InsufficientBalance/InvalidRequest` 至少回一条通用文案（或对管理员频道告警）+ 计入 `DeepSeekMetrics` 的 `fatalErrors` 并驱动 `ai status`/健康检查；`InvalidRequest` 的脱敏原因要能被运维看到（当前 `safeMessage` 在 `DeepSeekErrors.cs:89` 已保留 message，但只进日志、没进状态）。

## 4. P2（21 条；AI 段 6 条见 §5，合计 27）

### C-2 部分分片失败：已送达分片状态未回写，重试路径依赖适配器缓存

`src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs:158-172`

```csharp
var chunkAck = await upstream.ExecuteAsync(chunkCommand, cancellationToken).ConfigureAwait(false);
if (!chunkAck.Ok)
{
    var error = chunkAck.Error ?? ChatError.Create(...);
    return ChatAck.Failure(command, WithDeliveredIds(error, messageIds));
}
```

- **影响**：`WithDeliveredIds` 只把已送达 ID 塞进 `error.details`（符合 `docs/NLMP.md:755` 的 SHOULD），但 dispatch 层没有任何"重试从 chunk-k 继续"的接口，重试会用同一个 key 从 chunk-0 重新发。目前不双发的唯一原因是各适配器按 `{key}:chunk-{n}` 缓存了成功分片（`ChatCommandDispatcher.cs:148-150`）；一旦适配器缓存窗口（10 分钟）先于用户重试过期，或分片边界因降级结果不同而变化（`MaxMessageLength` 变更、`AppendUrls` 顺序变化），就会重复投递前半段。
- 另外 `ExecuteSendAsync` 在 chunk 循环里没有把 `messageIds` 落到任何可重放的位置，ack 失败后这信息只剩日志。
- **修复方向**：`ChatAck` 增加 `deliveredChunkIndex`（或 `resumeFrom`），并让 dispatcher 缓存"已送达前缀"；重试时跳过已送达 chunk。分片键应固定（例如按 `content.Text` 的稳定切分结果 + 内容哈希），避免边界漂移。

### C-3 编辑事件 ID 恒定 → 同一消息的第二次编辑被去重误杀

`src/Natter.Chat.Discord/DiscordChatUpstream.cs:445-454`

```csharp
ChatEvent chatEvent = isEdit
    ? new MessageUpdatedEvent { Id = $"discord:message:{message.Id}:updated", ... }
```

`src/Natter.Chat/Reliability/ChatEventDeduplicator.cs:79-80` 只按 `Id` 判重（`message` 语义键仅对 `MessageCreatedEvent` 生效）。

- **影响**：同一条消息被编辑两次时，两个事件 `Id` 完全相同 → 第二次（以及 10 分钟窗口内所有后续编辑）被判为重复并丢弃。`ProcessEditedMessages` 默认 false（`ChatOrchestrationOptions`）掩盖了它，但一旦开启，编辑内容会随机丢失。
- **修复方向**：`Id` 纳入编辑版本（Discord 的 `edited_timestamp` 或 payload 哈希），例如 `discord:message:{id}:updated:{EditedAt:O}`；或让 dedupe 键包含内容哈希。

### C-4 去重窗口（10 分钟）短于钉钉签名重放窗口（60 分钟）

`src/Natter.Chat/Reliability/ChatEventDeduplicator.cs:21`（默认 `TimeSpan.FromMinutes(10)`）
`src/Natter.Chat.DingTalk/DingTalkOptions.cs:26`（`MaxClockSkewMinutes = 60`，被 `DingTalkWebhookTransport.cs:173-178` 用作时间戳容差）

- **影响**：一个被截获的合法签名回调在 60 分钟内都可重放通过签名校验；而重放保护只有 10 分钟的去重窗口。重放发生在 10–60 分钟之间 → 上游无 ack 拒绝、去重已过期 → 触发第二次 AI 回复（真实计费）。
- **修复方向**：`maxClockSkew` 收敛到 1–5 分钟（钉钉本身建议窗口很小），或把去重窗口设为 ≥ 签名窗口；理想做法是在 transport 维护 `(timestamp, sign, msgId)` 的 nonce 集合，让重放在签名层就被拒。

### C-5 去重器的 prune/trim 在锁内做全表扫描

`src/Natter.Chat/Reliability/ChatEventDeduplicator.cs:112-134`

```csharp
var expired = _seen.Where(pair => pair.Value <= now).Select(pair => pair.Key).ToList();
foreach (var key in expired) _seen.Remove(key);
...
foreach (var key in _seen.OrderBy(pair => pair.Value).Take(excess).Select(pair => pair.Key).ToList())
```

- **影响**：`TryAccept` 每次都 `Prune`（O(n) 分配 + 遍历），容量满时再 `OrderBy`（O(n log n)）。默认容量 10,000 且 `TryAccept` 在**上游接收线程**上执行（`ChatIngressLifecycle.IngressObserver.OnNext` → `ChatIngressProcessor.TryPublish`），Discord 是把 gateway 事件回调线程直接借出来用的（`DiscordSocketTransport.OnMessageReceivedAsync`）。10k 条目、每秒数十条时，仅 prune 就是几十万次比较/秒并持有同一把锁 → gateway 心跳/钉钉 ack 被延迟，进而触发断线与重投（与 C-4/DT-2 叠加放大）。
- **修复方向**：按过期时间分桶（`SortedSet<DateTimeOffset, HashSet<string>>` 或时间轮），prune 摊销 O(k)；trim 用插入序队列（FIFO）而不是每次排序；把去重放到独立消费线程而不是上游回调线程。

### C-6 NLMP 时间戳解析比契约宽松，会按本机时区解释

`src/Natter.Chat/Serialization/NlmpJson.cs:15-25`

```csharp
if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
    throw new JsonException($"'{text}' is not a valid RFC 3339 timestamp.");
```

- **影响**：`docs/NLMP.md` 要求 RFC 3339；`TryParse` 实际接受 `"2026-11-14 14:00:00"`（无偏移）并按**运行机器的本地时区**换算成 UTC（已实测不抛异常，问题在于宽容）。容器（UTC）与开发机（+08:00）读取同一份 JSON 会得到相差 8 小时的 `OccurredAt`，而 `OccurredAt` 又参与无人值守去重 fallback key（`ChatEventDeduplicator.cs:98-104`）和 ack 的 `deliveredAt` 语义。
- **修复方向**：用 `DateTimeOffset.TryParseExact` + 显式格式清单（含/不含小数秒、`Z` 或 `±HH:mm` 强制要求偏移），无偏移直接判为非法。注释里"Unix seconds 在适配器边界转换"的约定也应在此处强制。

### C-7 `ConversationKinds` 与 `SupportsConversation` 组合，导致 Satori 私聊回复被判 unsupported

`src/Natter.Chat.Satori/SatoriCapabilityMapper.cs:82-91`

```csharp
private static List<ConversationKind> DeriveConversationKinds(HashSet<string> features)
{
    var kinds = new List<ConversationKind>();
    // §8.3: guild.plain adjusts conversation kinds; nothing else is assumed.
    if (features.Contains("guild.plain"))
        kinds.Add(ConversationKind.Guild);
    return kinds;
}
```

`src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs:129-136`

```csharp
if (!capabilities.SupportsConversation(payload.Conversation.Kind)
    && capabilities.ConversationKinds.Count > 0)
    return Fail(command, ChatErrorCodes.Unsupported, $"Conversation kind '{...}' is not supported ...");
```

- **影响**：只要 Satori `login.features` 里有 `guild.plain`，`ConversationKinds` 就变成**只有 Guild**（不含 `Direct`/`Group`，也没有 `Unknown` 通配）。于是私聊里收到的消息能被处理（入站无 gate），但回复阶段被 dispatcher 判 `unsupported` 拒发 —— 表现为"bot 在私聊里不回复，日志只有 unsupported"。反之，若 features 里没有 `guild.plain`，`ConversationKinds` 为空 → dispatcher 因 `Count > 0` 短路而全部放行（另一种不一致）。
- **修复方向**：`DeriveConversationKinds` 默认应包含 `Direct`（Satori 私聊普遍支持），或改为"未声明即不限制"（`ConversationKinds` 为空表示未知而非不支持）；同时 `SupportsConversation` 的 `Unknown` 通配语义（`Capabilities.cs:60-61`）应显式文档化。

### S-2 sn 先提交后映射，且只增不减 → 事件丢失 / 永久黑洞

`src/Natter.Chat.Satori/SatoriChatUpstream.cs:229-242`

```csharp
// Advance sn for every frame, including ones NLMP ignores: a reconnect must not
// process an ignored frame again.
if (satoriEvent.Sn is { } sn && !_snTracker.TryAdvance(sn))
    return;

var chatEvent = _mapper.Map(satoriEvent);
if (chatEvent is not null) _events.OnNext(chatEvent);
}
catch (Exception exception) { LastError = exception.Message; }   // ← 映射异常被吞
```

`src/Natter.Chat.Satori/SatoriSnTracker.cs:24-34`

```csharp
if (_lastSn is { } last && sn <= last) return false;
_lastSn = sn; return true;
```

- **影响**：
  1. sn 在映射/发布**之前**提交：映射抛异常（`Message.Validate()` 失败、`ChatEventId.Derive` 空值等）或被下游 observer 抛异常时，sn 已经前进 → 该事件在重连后不会被重放 → **at-most-once 丢消息**，且只留一条 `LastError`（无计数、无告警）。
  2. 追踪器只接受严格递增 sn：Satori 服务端重启/换节点/会话重建后 sn 可能从较小值重新开始，此时**所有后续事件都被静默丢弃**，且没有任何检测或 `Reset()` 调用（`SatoriSnTracker.Reset` 在全仓库无调用点）。
  3. `LastError` 无锁写（`SatoriChatUpstream.cs:80`），与 `_connected`（`:99`）、`_capabilities`（`:227`）一样缺少内存屏障；`GetCapabilitiesAsync` 可能长期读到旧能力。
- **修复方向**：映射成功后再提交 sn（先 `TryPeek` 后 `Commit`）；检测"sn 倒退超过阈值/连续 N 帧被拒"时 `Reset()` 并重新 IDENTIFY（不带 sn）；映射失败要计入丢弃计数并上报 `upstream.status`。

### S-3 `Dispose` 不断开也不释放 transport

`src/Natter.Chat.Satori/SatoriChatUpstream.cs:205-220`

```csharp
public void Dispose()
{
    ...
    lock (_gate) { _subscription?.Dispose(); _subscription = null; }
    _connected = false;
    _subject.Dispose();
}
```

- **影响**：对比 `DiscordChatUpstream.Dispose`（`:199-210`，会 `dispose` 掉 transport）与 `DingTalkChatUpstream.Dispose`（`:143-153`），Satori 适配器既不 `await DisconnectAsync`，也不在 `_transport is IDisposable` 时释放它 → WebSocket / HttpClient / 心跳定时器泄漏；进程内热重载插件时旧连接继续消费事件（并可能与新实例抢同一 token）。
- **修复方向**：`Dispose` 中调用 `DisconnectAsync`（同步等待或有界等待）并对 `IDisposable` transport 调用 `Dispose`；实现 `IAsyncDisposable` 承载真实关闭。

### S-4 单帧 `login.features` 整体覆盖能力，可能被降级

`src/Natter.Chat.Satori/SatoriChatUpstream.cs:226-227`

```csharp
if (satoriEvent.Login?.Features is { Count: > 0 } features)
    _capabilities = SatoriCapabilityMapper.Map(features, _capabilityOptions);
```

- **影响**：只判断"非空"，任何携带部分 feature 的 login 帧都会整体替换能力集。若某帧只带 `message.attachments`，能力会瞬间丢失 `message.delete`/`message.update`（`SatoriCapabilityMapper.cs:56-70`），`Edit/Delete` 判定随之翻转 → dispatcher 开始拒发更新/删除。**待确认**：Satori 是否保证每个 login 帧的 `features` 是全量。
- **修复方向**：按 login 身份（`platform + selfId`）累积合并 features，或只在 `login-added` 且 `READY` 帧上覆盖；能力变更应发布 `upstream.status` 以便观测。

### D-2 `BuildAllowedMentions` 未指定 `AllowedTypes`，显式 mention 实际不生效

`src/Natter.Chat.Discord/DiscordSocketTransport.cs:657-675`

```csharp
return ids.Count == 0
    ? AllowedMentions.None
    : new AllowedMentions((AllowedMentionTypes?)null)
    {
        UserIds = ids,
        MentionRepliedUser = false,
    };
```

- **运行时实测（Discord.Net 3.20.2）**：`new AllowedMentions((AllowedMentionTypes?)null){UserIds=[1]}` → API 模型 `Parse: specified=True value=[]`、`Users: [1]`、`RepliedUser: false`。即线上请求体是 `allowed_mentions: { parse: [], roles: [], users: [...], replied_user: false }`。
- **影响**：`parse: []` 表示**不从正文解析任何 mention 类型**，因此 `OutboundMentionIds` 精心收集的 `MentionKind.User` ID 只是"白名单"，正文里 `@user`/`<@id>` 不会被解析成 ping —— 与 `docs/DISCORD_PLUGIN_DESIGN.md:18/486/660`（"只允许 NLMP 显式声明的用户 mention""显式 user id 输入生效"）不一致：**功能上是静默失效，不是安全漏洞**。反向确认：默认 `AllowedMentions.None` 同样是 `parse: []`，因此模型输出里的 `@everyone` 不会被 ping（这条是好消息，值得写进注释）。
- **修复方向**：显式传 `new AllowedMentions(AllowedMentionTypes.Users) { UserIds = ids, MentionRepliedUser = false }`（实测该组合生成 `parse: ["users"]`）；并对 `MentionKind.Role` 明确"不支持"（当前被 `OutboundMentionIds` 静默丢弃，`DiscordChatUpstream.cs:552-560`）。

### D-3 交互缓存与频道缓存只在 Ready 时清理，存在无界增长

`src/Natter.Chat.Discord/DiscordSocketTransport.cs:751-759`、`:715-723`

```csharp
private void CleanupInteractions()
{
    var now = DateTimeOffset.UtcNow;
    foreach (var (id, cached) in _interactions)
        if (cached.ExpiresAt <= now) _interactions.TryRemove(id, out _);
}
```

- **影响**：`_interactions` 只在 `OnReadyAsync`（`:346-347`）里清理。稳定长连（数天不断线）时，每个 slash 交互都留下一个持有 `SocketInteraction`（内含 channel/user/guild 引用）的条目 → 内存单调增长；`InteractionTtlSeconds` 默认 900 却没有按 TTL 主动淘汰。`_channels` 同理（`CacheChannel` 只在达到 500 容量时批量淘汰一次，且用 `FirstOrDefault()` 取"最旧" —— `ConcurrentDictionary` 枚举顺序与插入顺序无关，实际是**随机淘汰**，`：705-709`）。
- **修复方向**：写入路径上顺带淘汰（`TryRemove` 过期项 + 计数上限 + LRU），或引入 `MemoryCache` 的滑动过期；不要依赖 `ConcurrentDictionary` 的枚举顺序做 LRU。

### D-4 `_idempotency` 与 DingTalk 同类字典无界

`src/Natter.Chat.Discord/DiscordChatUpstream.cs:20`、`:147-157`

```csharp
private readonly ConcurrentDictionary<string, ChatAck> _idempotency = new(StringComparer.Ordinal);
...
_idempotency.TryAdd(idempotencyKey, ack);
```

- **影响**：`idempotencyKey` 由 AI responder 生成（`ai:message:{source}:{messageId}`），每条消息一个新 key，永不删除 → 长期运行内存随消息数线性增长（每条 ack 含 messageIds 字符串）。`DingTalkChatUpstream.cs:20` 完全相同。核心层已有带窗口/容量的 `ChatIdempotencyCache`（`Reliability/ChatIdempotencyCache.cs`），适配器却各自造了一个无界字典。
- **修复方向**：统一复用 `ChatIdempotencyCache`（10 分钟窗口 + 容量上限），或至少加 TTL + 上限。

### D-5 429/限流不重试，`retryAfterMs` 只透传

`src/Natter.Chat.Discord/DiscordSocketTransport.cs:731-749`

```csharp
RateLimitedException => DiscordSendResult.Failure("rate_limited", "Discord rate limit."),
...
return ChatError.Create(code, result.ErrorMessage ?? "Discord request failed.", result.RetryAfterMs);
```

- **影响**：`ChatCommandDispatcher` 没有任何重试循环（`:57-76` 只捕获异常），`ChatErrorCodes.IsRetryable` 仅用于标记。因此一次 429 就等于**这条回复彻底丢失**（用户看不到任何回复），而 `docs/NLMP.md:797`（"`rate_limited` 遵守 `retryAfterMs`"）与 `:1140` 的测试意图都假定上层会重试。Discord 与钉钉（`DingTalkChatUpstream.ToError`）两条路径都如此。
- **修复方向**：在 dispatcher 增加有界重试（仅 `retryable`，指数退避 + 尊重 `retryAfterMs`，复用同一 `idempotencyKey`），并把重试次数暴露到 ack/日志。

### DT-4 重连成功后上游无法感知，健康状态永久为"断线"

`src/Natter.Chat.DingTalk/DingTalkChatUpstream.cs:198-204`

```csharp
case DingTalkTransportEventKind.Disconnected:
    lock (_gate) _connected = false;
    _lastDisconnectAt = _clock.UtcNow;
    Interlocked.Increment(ref _reconnectCount);
    PublishStatus(UpstreamState.Disconnected);
```

`src/Natter.Chat.DingTalk/DingTalkModels.cs:124-129` 的事件种类只有 `Message / Error / Disconnected`。

- **影响**：Stream transport 在 `RunLoopAsync` 里自动重连（`DingTalkStreamTransport.cs:172-181`），但从不发"已连接/已恢复"事件。上游 `_connected` 一旦被置 false 就再也不会变 true（只有 `ConnectAsync` 开头会置 true，而重连发生在 transport 内部）→ `IsConnected`、`Health`、`upstream.status` 长期误报断线；依赖 `IsConnected` 的上层（例如"断线时拒绝发送"或运维告警）全部失真。
- **修复方向**：给 `DingTalkTransportEventKind` 加 `Connected`（或 `Ready`），`ConnectOnceAsync` 成功后发布；上游在 `Ready` 分支恢复 `_connected = true` 并清 `LastError`。

### DT-5 "连上即断"场景下退避退化为固定 ~500ms 的风暴

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:145-191`

```csharp
attempt++;
var delay = Math.Min(Math.Max(0, _options.ReconnectBaseDelayMs) * Math.Pow(2, Math.Min(attempt, 8)), ...) + Random.Shared.Next(0, 251);
try { await Task.Delay(...); await ConnectOnceAsync(cancellationToken); attempt = 0; }   // ← 连接成功即清零
```

- **影响**：`attempt` 在 `ConnectOnceAsync` 成功返回后就清零，而"服务端接受 WS 后立即断开"（ticket 失效、topic 未授权、网关抖动）恰恰是成功路径 → 退避永远停在 `2 × ReconnectBaseDelayMs ≈ 1000ms` 量级（默认 base 500ms），形成持续的建连/断开风暴（每条连接都要走一次 gateway HTTP open + TLS + ticket）。
- **修复方向**：只有"收到过消息或稳定存活超过阈值"才清零 `attempt`；用 `attempt` 累积到 `ReconnectMaxDelayMs`；对同一错误码连续出现做熔断。

### DT-6 单帧缓冲无上限

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:237-253`

```csharp
var buffer = new byte[8192];
var message = new MemoryStream();
...
await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
if (!result.EndOfMessage) continue;
var json = Encoding.UTF8.GetString(message.ToArray());
```

- **影响**：`MemoryStream` 一直累加到 `EndOfMessage`，没有上限；对端（或中间人）发一个声明超大的文本帧即可让进程内存线性膨胀直到 OOM。`ToArray()` 每帧再复制一次（正常路径下也是双倍分配）。
- **修复方向**：设 `MaxFrameBytes`（如 1–2 MB），超限即 `Abort()` + 记录 `stream_frame_too_large`；用 `ArrayPool<byte>` + `Encoding.UTF8.GetString(span)` 避免复制。

### DT-7 ack 静默丢弃；ack 异常被误判为接收失败

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:302-323`

```csharp
if (string.IsNullOrWhiteSpace(messageId) || socket.State != WebSocketState.Open)
    return;                       // ← 无日志
...
await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
```

- **影响**：socket 已在关闭过程中时 ack 直接丢弃（服务端会重投，但本地无任何痕迹，无法区分"对方没发"与"我们没回"）；`SendAsync` 抛出的 `WebSocketException` 一路冒到 `RunLoopAsync:159-162` 被记成 `stream_receive_failed` 并重连 —— 把"ack 写失败"错误归类成"接收失败"，排障方向被带偏。`docs/SECOND_ADAPTER_VALIDATION.md:220` 要求"ack 失败要记录并重试/丢弃策略明确"。
- **修复方向**：丢弃/失败分别发 `Error(code="ack_dropped"|"ack_failed")`；不要复用接收异常分支；对失败 ack 至少记 `messageId`（不含 webhook 查询串）。

### DT-8 `sessionWebhook` 原样 POST，无 scheme/host 校验（SSRF 面）

`src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:103`

```csharp
using var request = new HttpRequestMessage(HttpMethod.Post, reply.SessionWebhook)
```

- **影响**：`sessionWebhook` 来自回调 JSON（`DingTalkCallbackParser` 只判空，`DingTalkModels.cs:88`），存储时也未校验（`DingTalkChatUpstream.cs:227-232`）。它被直接当作绝对 URL 发请求：允许 `http://`（明文外泄回复内容）、允许指向内网地址（`169.254.169.254`、内网管理口）。仓库里已有现成的 `ChatAttachmentGuard.IsPrivateNetworkUrl` / `IsAllowedUrl`（`Safety/ChatAttachmentGuard.cs:58-72`）却在发送路径未使用。伪造回调需要能通过签名（Stream 通道内是加密长连，风险偏低），但**纵深防御缺失**，且 legacy HTTP 通道的 `DingTalkWebhookTransport.cs:105` 同样问题。
- **修复方向**：发送前校验 `https` + `*.dingtalk.com` 域白名单（或至少 `ChatAttachmentGuard` 的私网拒绝），失败返回 `unavailable` 并记录（不含完整 URL）。

### DT-9 `createAt` / `conversationType` 缺省值带来的映射错误

`src/Natter.Chat.DingTalk/DingTalkModels.cs:69`

```csharp
var createdAt = DateTimeOffset.FromUnixTimeMilliseconds(Int64(root, "createAt") ?? 0);
```

- **影响**：`createAt` 缺失/非法（字符串解析失败也落到 `?? 0`）时 `CreatedAt = 1970-01-01`，直接进入 `Message.CreatedAt`（`DingTalkChatUpstream.cs:254`）并流向 `OccurredAt`、历史时间线、`ChatEventDeduplicator` 的 fallback key。下游按时间排序/展示会得到 1970 年消息。
- **修复方向**：缺失时回退到 `_clock.UtcNow`（与 Satori 的 `?? receivedAt` 一致，`SatoriEventMapper.cs:121`），并对异常值（负数、远期未来）做范围校验。

同文件 `:77` 的 `String(root, "conversationType") ?? "2"`：字段缺失时默认"群"，导致私聊被当作群——`RequireMention` 随即生效（`DingTalkChatUpstream.cs:221`），用户不再收到回复。**修复**：缺失时用 `ConversationKind.Unknown` 并放行（或按 `conversationTitle` 是否存在推断）。

### DT-10 `_sessions` 只写不删

`src/Natter.Chat.DingTalk/DingTalkChatUpstream.cs:227-232`

```csharp
_sessions[message.ConversationId] = new SessionWebhook(message.SessionWebhook, message.SessionWebhookExpiresAt ?? _clock.UtcNow.AddMinutes(30));
```

- **影响**：每个会话一条，过期条目（`ExpiresAt`）仅在读取时判断（`:157-159`），从不移除；群/私聊数量大或会话 id 被平台复用（首聊/群重建生成新 id）时字典单调增长，且旧的 webhook 字符串（含会话令牌）常驻内存。
- **修复方向**：写入时顺带删除过期项 + 容量上限；过期后主动置空并发布诊断计数。

### DT-11 legacy HTTP 监听：无请求体上限 + 不跟踪的 fire-and-forget

`src/Natter.Chat.DingTalk/DingTalkWebhookTransport.cs:186-188`、`:163`

```csharp
using (var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8))
    body = await reader.ReadToEndAsync(cancellationToken);
```

```csharp
_ = HandleContextAsync(context, cancellationToken);
```

- **影响**：签名校验通过后（`appSecret` 泄露或内网可达时）任何大小的请求体都会被一次性读入内存；`HandleContextAsync` 无跟踪、无并发上限，`Dispose` 时也不会等待 → 关闭过程中可能写已释放的 `_events`/`HttpListener`。同时 `MaxClockSkewMinutes=60` 构成 C-4 描述的重放面。
- **修复方向**：`ContentLength64` 上限（如 256 KB）+ 流式限额读取；用 `Task.Run` + `ConcurrentDictionary`/`CancellationToken` 跟踪在途请求并在 `Dispose` 里等待。

## 5. P2（AI 段，6 条）

### A-3 `finish_reason=length` 的截断未被识别

`src/Natter.Feature.Ai.DeepSeek/DeepSeekChatClient.cs:255-258`

```csharp
var finishReason = choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String
    ? finish.GetString() ?? "stop" : "stop";
```

`src/Natter.Feature.Ai/AiChatResponder.cs:297-301`

```csharp
private static bool IsClipped(string? text)
    => text is not null
       && (text.Contains("<response clipped>", ...) || text.Contains("<response truncated>", ...) || text.Contains("[response clipped]", ...));
```

- **影响**：DeepSeek 把 `finish_reason` 放进 `ChatFinishReason`（`:279`），但 responder 只看 `response.Text` 的字面标记；`max_tokens` 触发的截断（`finish_reason="length"`）对 DeepSeek/Spark 都是**静默半句话**：不重试、不降级、不告警。Spark 连 `finish_reason` 都不读（`SparkChatClient.cs:180-216`）。`docs/AI_PROVIDER_DESIGN.md:508` 的"空 content 兜底"覆盖不到"非空但被截断"。
- **修复方向**：`CallAsync` 返回 `(Text, FinishReason)`；`FinishReason == "length"` 时按 `IsClipped` 同一路径处理（一次续写重试或 fallback + 指标）。

### A-4 thinking + 原生 tools 未回传 `reasoning_content`

`src/Natter.Feature.Ai.DeepSeek/DeepSeekChatClient.cs:297-320`

```csharp
if (role == "assistant")
{
    var toolCalls = (message.Contents ?? []).OfType<FunctionCallContent>().ToArray();
    if (toolCalls.Length > 0)
    {
        return new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = "assistant",
            ["content"] = string.IsNullOrEmpty(message.Text) ? null : message.Text,
            ["tool_calls"] = ...
```

- **影响**：`ParseResponse`（`:249-274`）不读 `message.reasoning_content`，`MapMessage` 也不会回传它。`docs/AI_PROVIDER_DESIGN.md:52` 明确写"思考 + tools 的历史轮次必须回传 `reasoning_content`，否则可能 400"，而 `AiChatResponder.TaskToolLoop` 在 `ProtocolCapabilities=NativeAndText`（`DeepSeekPlugin.cs:89`）下确实会走原生工具循环（`AiChatResponder.TaskToolLoop.cs:46-50`、`BuildTaskOptions:167-184`）→ 开启 `thinking: enabled` + 任务工具时存在 400 风险（当前 `DeepSeekPlugin.Validate` 只禁了配置里的 `tools`，不禁任务循环的原生工具）。
- **修复方向**：在 `ChatMessage.AdditionalProperties` 或 `FunctionCallContent` 旁保留 `reasoning_content`，并在组请求时按 provider 要求回填；或在 thinking 模式下强制 `ToolProtocolCapabilities.TextOnly`。**待确认**（取决于 deepseek-reasoner 版本行为）。

### A-5 以裸单例注册 `HttpClient`

`src/Natter.Feature.Ai.DeepSeek/DeepSeekPlugin.cs:71`

```csharp
services.AddSingleton<HttpClient>(_ => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });
```

`src/Natter.Feature.Ai.Spark/SparkPlugin.cs:65` 相同；`src/Natter.Infrastructure/Chat/ChatCompositionModule.cs:180` 也注册了另一个 `HttpClient`（20s），同一容器里出现多个同类型单例 → 谁拿到哪个取决于注册/解析顺序。

- **影响**：没有 `SocketsHttpHandler` 的 `PooledConnectionLifetime`/`ConnectTimeout`，长跑进程无法感知 DNS 变更；`Timeout.InfiniteTimeSpan` 依赖每个调用点的 per-request CTS（DeepSeek `:100-101` 有，Spark `:144-145` 有），一旦未来某处漏加就无限挂起。多插件共享/覆盖同类型单例是典型的 DI 陷阱。
- **修复方向**：`services.AddHttpClient("deepseek", c => ...)` 命名客户端 + `ConfigurePrimaryHttpMessageHandler` 设置 `PooledConnectionLifetime`，provider 构造函数改为接收 `IHttpClientFactory`。

### A-6 `AiConversationGate` 的会话字典无界，且 `Dispose` 与在途 lease 竞争

`src/Natter.Feature.Ai/AiConversationGate.cs:9-46`

```csharp
private readonly Dictionary<string, SemaphoreSlim> _conversations = new(StringComparer.Ordinal);
...
if (!_conversations.TryGetValue(key, out conversation))
{
    conversation = new SemaphoreSlim(1, 1);
    _conversations[key] = conversation;      // 永不回收
}
```

`src/Natter.Feature.Ai/AiChatResponder.TaskToolLoop.cs:46`

```csharp
using var lease = await _gate.AcquireAsync($"task-loop:{step.TaskId}", cancellationToken)
```

- **影响**：每个会话 key 一个 `SemaphoreSlim`，任务循环还按 `taskId` 再灌一批 key；Discord 的 channel/thread 数量增长时字典单调增长（每个 `SemaphoreSlim` 还带 `AvailableWaitHandle` 惰性资源）。`Dispose` 直接 `Dispose` 所有信号量（`:48-62`），若仍有 lease 在途，`Lease.Dispose` 里的 `Release()` 会抛 `ObjectDisposedException`（在 `finally` 链上，最终变成未观测异常或把用户请求打成 500）。
- **修复方向**：用引用计数 + 空闲回收（`SemaphoreSlim` 释放时若 `CurrentCount==1` 且无等待者则移除条目）；`Dispose` 只置标志，等待在途 lease 归零后再释放。

### A-7 clipped 重试无退避、双倍计费，且忽略取消语义

`src/Natter.Feature.Ai/AiChatResponder.cs:212-222`

```csharp
if (IsClipped(text))
{
    _log.Warning("AI provider returned a clipped response; retrying once.");
    text = await CallAsync(messages, options, cancellationToken).ConfigureAwait(false);
}
```

- **影响**：`IsClipped` 命中即**同步重发同一请求**（同一 prompt、无退避、无预算判断）→ token 成本翻倍；若第二次仍被截断，则直接返回 `FallbackText()` 丢弃已生成内容。该路径发生在 gate 内，会额外占用会话槽位（对同会话后续消息造成排队）。`IsClipped` 本身是"厂商信封标记"的启发式，与 A-3 的真实截断信号（`finish_reason`）不是一回事，两者混用会互相掩盖。
- **修复方向**：以 `finish_reason` 为主判据；重试前检查剩余预算/时间，并记录一次 `clipped_retry` 指标；第二次失败时返回"已生成部分 + 截断提示"而不是整段丢弃。

### A-8 输入长度上限分散且不一致，缺少不可信内容隔离

`src/Natter.Feature.Ai/IAiContextAssembler.cs:35-36`

```csharp
var max = Math.Max(1, _options.MaxInputCharacters);
var text = userText.Length <= max ? userText : userText[..max];
```

`src/Natter.Feature.Ai/AiChatResponder.TaskToolLoop.cs:258-265`

```csharp
private static string FormatObservation(TaskToolObservation observation)
    => $"[tool observation] {observation.Tool} ({Status(observation)})\n{FormatObservationBody(observation)}";
```

- **影响**：
  1. 长度治理分散：单条用户消息 12k（`AiResponderOptions.MaxInputCharacters`，默认 12000）、历史 24k（`HistoryMaxCharacters`，由 store 裁剪）、**工具观察结果完全没有上限**（`FormatObservationBody` 直接拼模型/工具输出）。工具返回大文本时提示词无界膨胀 → 成本与延迟失控，甚至触发 provider 的 400。
  2. 不可信内容没有边界标记：用户文本直接作为 `ChatRole.User` 追加，工具观察以纯文本 `[tool observation] ...` 追加，系统提示未声明"以下内容不可信"。工具输出里的 `</tool_call>`、`忽略之前指令` 之类文本与指令处于同一通道（`ParseTaskReply` 只解析模型返回，风险面较小，但观察内容被模型当作指令的概率真实存在）。
- **修复方向**：在 assembler 统一做预算分配（system / history / user / observations 各自上限）；工具观察用结构化 `ChatRole.Tool` + 明确分隔（已在 native 路径如此，text 路径应一致）；系统提示中显式标注不可信数据段。

## 6. P3（22 条）

| 编号 | 位置 | 问题（含片段） | 影响 | 修复方向 |
| --- | --- | --- | --- | --- |
| C-8 | `src/Natter.Chat/Degradation/ChatTextChunker.cs:26-33` | 每片都保留 `content.Format`（Markdown），`SplitText` 会在 `\n`/空格处切 | 代码块、列表、表格被切断后渲染破损；引用块/围栏语义丢失 | 分片感知围栏（切点避开三反引号围栏区间）或对非首片降级为 plain + 续片标记 |
| C-9 | `src/Natter.Chat/Degradation/MarkdownToPlainText.cs:21-26` | 先全局删除 `**`/`__`/`` ` ``/`~~`，再用 `SingleEmphasisPattern` 处理单强调 | `_snake_case_`、`a*b*c` 被误删字符；`[a](b)` 变 `a (b)` 与正文 URL 重复 | 先处理强调再删除标记；对下划线加词边界断言；链接仅在 href 不在文本中时追加 |
| C-10 | `src/Natter.Chat/Safety/ChatAttachmentGuard.cs:74-98` | 主机名不解析 DNS（注释已声明），未覆盖 `*.svc`、`*.cluster.local`、`host.docker.internal`；`IsPrivateAddress` 未覆盖 `192.0.0.0/24`、`198.18.0.0/15`、组播/保留段 | 依赖下载的适配器若不复检会有 SSRF 缺口（本仓库内暂无下载路径） | 提供"解析后复检 + 每跳重定向复检"的公共 helper；补齐保留网段表 |
| C-11 | `src/Natter.Chat/Model/Capabilities.cs:23-61` | `Edit`（init 写 `_edit`）与 `SupportsEdit`（`Edit` 或 `StreamEdit`）双入口；`SupportsConversation` 把 `Unknown` 当通配 | 适配器只要声明 `Unknown` 就放行所有会话类型（等于放弃会话类型约束） | 只保留 `SupportsEdit` 一个判定入口；`Unknown` 通配改为显式 `AnyConversation` 开关 |
| C-12 | `src/Natter.Chat/Dispatch/ChatCommandDispatcher.cs:66-71`、`:270` | `ChatError.Create(Internal, $"...: {exception.Message}")` 直接把异常文本外泄；`ExecuteRespondAsync` 调 `GetCapabilitiesAsync(cancellationToken:)` 不传 conversation | 异常文本可能含 URL/内部路径并进入日志/ack；交互响应拿到的是源级能力，无法按频道细化 | 对外只保留异常类型与错误码，细节进结构化 details/日志；传入 `payload` 关联的 conversation（若有） |
| S-5 | `src/Natter.Chat.Satori/SatoriContentMapper.cs:111-118`、`:64-88`、`:164-165` | `<a>` 无条件追加 `(href)`；`<at type="role">` 落成 `MentionKind.User`；`<quote>` 只取第一个 id 且不进文本；附件 URL 非法时静默丢弃 | 文本重复/有损；role 提及被当作 user（下游策略错判）；附件丢失无日志 | 去重 href；识别 role 类型（否则不映射为 mention）；丢弃附件时计+记 |
| S-6 | `src/Natter.Chat.Satori/SatoriContentMapper.cs:202-216` | `XDocument.Parse($"<root>{elements}</root>")` 无长度上限（DTD 默认禁止，无 XXE） | 超大 content 造成 CPU/内存尖峰（解析 + 后备正则） | 加 `MaxContentChars` 上限，超限走文本截断 |
| S-7 | `src/Natter.Chat.Satori/SatoriMessageCache.cs:26-41` | `MinBy(pair => pair.Value.Sequence)` 每次 O(n)；`if (oldest.Key != default)` 在 key 为默认值时跳过淘汰；按写入序号而非最近访问淘汰 | 容量 2048 时每条 store O(n)；极端情况不淘汰；热消息被冷消息挤出导致更新合并失败 | 用 LRU（`LinkedList` + 字典）或按 TTL 淘汰；淘汰条件改为 `Count > capacity` 无条件移除 |
| S-8 | `src/Natter.Chat.Satori/SatoriEventMapper.cs:108-128` | `dto.Content` 为空串时 `Map("")` 返回空 `Content` → `Message.Validate()` 失败 → 返回 null，且不回退缓存 | "清空消息内容"的 edit 事件被静默丢弃（有损） | 空串时回退 `cached?.Content`，或明确映射为一条空内容墓碑事件 |
| D-6 | `src/Natter.Chat.Discord/DiscordSocketTransport.cs:72`、`:85` | `ConnectAsync` 每次都 `_ready = NewReadySource()`，但 `_started` 已为 true 时不会再触发 `Ready` | 二次 `ConnectAsync` 必然超时（`ReadyTimeoutSeconds`）并报 `identify_ready` 阶段失败 | 仅在首次 start 或重连时重建 TCS；已连接直接返回 |
| D-7 | `src/Natter.Chat.Discord/DiscordSocketTransport.cs:468-481`、`DiscordChatUpstream.cs:493-495` | 删除事件解析不到频道即丢弃；`OccurredAt` 用本地时钟而非平台时间 | 无缓存频道的删除事件丢失；时间语义与其它事件不一致 | 用 `channel.Id` 调 REST 兜底；`OccurredAt` 采用平台时间或明确标注为接收时间 |
| D-8 | `src/Natter.Chat.Discord/DiscordModels.cs:56-67`、`DiscordSocketTransport.cs:366-443` | 默认注册 `status/tasks/cancel/task` 等命令，插件未提供 handler 时仍注册 | 用户看到命令但得到 unsupported；与 `SlashCommands` 开关语义混淆 | 注册前按已注册的 responder/handler 过滤；未实现则不注册 |
| D-9 | `src/Natter.Chat.Discord/DiscordChatUpstream.cs:529-540`（DingTalk `:279-288` 同构） | `Id` 里 `Interlocked.Increment` 后 `Sequence = _sequence` 又读一次字段 | `sequence` 非单调/跳号，破坏"同一 source 内单调递增"的契约 | 用 `Interlocked.Increment` 的返回值赋给 `Sequence` |
| D-10 | `src/Natter.Chat.Discord/DiscordCapabilities.cs:14-27`、`DiscordModels.cs:51` | `MaxMessageLength=1900`（Discord 上限 2000）；`Attachments=false` 但 `MaxAttachmentBytes` 非空；`Ephemeral=true` 对 message.send 无意义 | 分片边界保守但 UTF-16/码点计数差异下仍有超限可能；能力声明自相矛盾 | 明确计数口径（按码点/字素）；分离"消息能力"与"交互能力"两组字段 |
| DT-12 | `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:130-143` | `_sendGate.Dispose()`、`_events.Dispose()` 早于 run loop 结束；未等待 `_runTask`；`_cts` 双 Dispose | run loop 内 `WaitAsync`/`OnNext` 抛 `ObjectDisposedException`（未观测任务异常）；关闭不彻底 | 先 cancel → await run loop（有界）→ 再 Dispose 门/主题/CTS（幂等释放） |
| DT-13 | `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs:52-61`、`DingTalkChatUpstream.cs:177` | 二次 `ConnectAsync` 覆盖 `_cts`/`_runTask` 泄漏旧实例与旧循环；回复用 `"dingtalk:" + Guid` 伪造 messageId | 重入后出现两个接收循环抢同一 socket；ack 里 `messageIds` 无实际意义（无法用于引用/删除/去重） | `ConnectAsync` 加锁 + 幂等；messageId 用 `msgId`+序号或留空（如实声明平台不返回 ID） |
| DT-14 | `src/Natter.Chat.DingTalk/DingTalkChatUpstream.cs:234-238` | 群会话把 `GuildId = message.ConversationId` | 下游按 guild 聚合/记忆隔离时 guild 与 conversation 混为一谈，跨会话键碰撞 | 群会话 `GuildId` 置 null（钉钉单体群无 guild 概念），另设 `ContainerId` 语义 |
| A-9 | `src/Natter.Feature.Ai/AiConversationStore.cs:42-62` | `AppendTurnAsync(..., sourceEventId, ...)` 完全忽略 `sourceEventId` | 内存后端无幂等，宿主 SQLite 后端有（`SqliteConversationStore.cs:90-96`）→ 同一次事件重放时两种后端行为不一致 | 内存后端维护 `HashSet<string> seenEventIds`（有界窗口）或统一走宿主后端 |
| A-10 | `src/Natter.Feature.Ai/IAiContextAssembler.cs:36` | `userText[..max]` 硬截断，无省略标记、不告知模型 | 用户长消息被静默截断，模型答非所问；用户无从得知 | 截断处插入 `…[truncated N chars]` 或优先保留首尾 |
| A-11 | `src/Natter.Feature.Ai/AiChatResponder.cs:147-152`、`:126-131` | `interaction.respond` 未带 `idempotencyKey`；空 prompt 分支每次都回一条 | 交互响应重试会重复 followup；空 prompt 可被反复触发刷屏 | 交互命令也生成确定性 key（`ai:interaction:{id}`）；空 prompt 分支做短时去重 |
| A-12 | `src/Natter.Feature.Ai/AiChatResponder.TaskToolLoop.cs:83-126`、`AiChatResponder.cs:279-295` | `ToolCallPattern` 只匹配首个 `{...}`（多 tool_call 或嵌套对象时依赖回溯，实测可匹配但多调用只能取一个）；`StripResponseEnvelope` 固定 4 轮 | 多工具并行请求被截断为 1 个；深层嵌套信封残留标签进入用户可见文本 | 用 `JsonDocument` 逐字符扫描配平花括号；剥离改为循环至稳定（带上限+异常保护） |
| A-13 | `src/Natter.Feature.Ai/AiChatResponder.cs:322-329` | `_completed` 超 2048 时 `Take(count-1024)` 随机删（`ConcurrentDictionary` 枚举无序） | 刚完成的事件 ID 可能被删掉 → 紧接着的重放会再次生成回复（10 分钟窗口内） | 用带上限的 LRU/环形缓冲，按时间淘汰 |

## 7. 其它观察（非缺陷，但会影响排障）

1. **审查范围内的 `src/Natter.Chat/State/` 目录为空**（无任何 `.cs`）。任务清单里把 State 列入范围，实际该模块未落地；若计划放状态机/持久化，注意不要与 `Natter.Application.Chat.IConversationStore` 重叠。
2. **`ChatOutboundPolicy` 只拦 `Everyone/Here`**（`Safety/ChatMentionGuard.cs:27-37`），不拦 `MentionKind.Role`；Discord 的 role ping 与 `@everyone` 危害同级，当前靠适配器侧 `OutboundMentionIds` 过滤 User-only（`DiscordChatUpstream.cs:552-560`）间接规避 —— 建议在 core 策略里显式加 `AllowRoleMention`（默认 false）。
3. **`ChatEventDeduplicator` 的 fallback key 含 `OccurredAt.ToString("O")`**（`:98-104`）：同一事件因重放时间不同（若适配器用接收时间填 `OccurredAt`）会生成不同 key，fallback 去重失效。当前三个适配器都填平台时间，暂不触发。
4. **`NlmpJson.Options` 同时注册 `NlmpEnumConverterFactory` 与 `JsonStringEnumConverter`**（`:43-48`）：前者覆盖已知枚举，后者兜底；一旦新增枚举忘记登记到 `CanConvert`，反序列化将走**抛异常**的严格路径，破坏"未知枚举值必须忽略"的契约（`NlmpEnumConverter.cs:6-9` 注释）。
5. **`SatoriStatus.Describe` 把 `TypingStart/InteractionRespond` 声明为 unsupported**（`SatoriStatus.cs:37-41`），与 `SatoriCommandMapper.TryMap`（`:79-81`）一致 —— 但 `ChatCommandDispatcher.ExecuteTypingAsync` 在 `capabilities.Typing=false` 时返回 **Success**（`:243-245`），所以上层看到 typing"成功"而实际什么都没做；诊断输出与运行时语义不一致。

## 8. 已排除的疑似问题（含验证方法，避免误报）

| 疑似问题 | 结论 | 依据 |
| --- | --- | --- |
| `NlmpJson` 用 `DateTimeStyles.RoundtripKind` 解析 `DateTimeOffset` 会抛 `ArgumentException` | **不成立** | 在 .NET 10 实测 `DateTimeOffset.TryParse("2024-01-02T03:04:05Z", InvariantCulture, RoundtripKind, out _)` → `ok=True`；真实问题是宽容度（见 C-6） |
| `ToolCallPattern` 的非贪婪 `\{.*?\}` 遇到嵌套 JSON 会截断 | **不成立**（单调用场景） | 实测 `{"tool":"t","input":{"a":1}}` 与 `"a}b"` 均能完整匹配（正则回溯到 `</tool_call>` 前的最后一个 `}`）；多调用场景才是问题（见 A-12） |
| `http://0x7f.1/`、`http://2130706433/`、`[::ffff:127.0.0.1]`、`localhost.` 能绕过私网检查 | **不成立** | 实测 `Uri.Host` 归一化为 `127.0.0.1` / `::ffff:127.0.0.1` / `localhost.`，`IsPrivateNetworkHost` 的 `TrimEnd('.')` + `MapToIPv4` 均能拦下 |
| Discord 出站默认 `AllowedMentions` 允许 `@everyone`（安全漏洞） | **不成立**，反而过严 | 实测两条分支都生成 `parse: []`；`@everyone` 不会被解析，代价是显式 user mention 也失效（见 D-2） |
| `SatoriChatUpstream` 的 `_events` 直接暴露 `Subject`，订阅者异常会打挂事件流 | **不成立** | `Subject.Synchronize` + `Subject.Synchronize(_subject)`（`:65`）；下游还有 `SafeObserver`（`RxChatEventHub.cs:40`）与 `ChatIngressLifecycle.IngressObserver` 的 try/catch（`:127-137`）双保险 |
| `ChatOrchestrator` 在上游线程同步跑 AI 会导致 ack/心跳饥饿 | **部分不成立** | `OrchestratorObserver.OnNext` 是 `_ = HandleAsync(value)`（`ChatOrchestrator.cs:155-156`），已脱离调用线程；真实风险来自**同步订阅者**与去重器的 O(n)（见 C-5、DT-2） |

## 9. 修复优先级建议（按投入产出）

1. **第一批（1–2 天，收益最大）**：A-1（gate 覆盖读/写）、DT-1（复用 `_http`）、DT-3（接收超时）、D-1（DM 白名单）、C-1（`TryReserve` + 失败入库）。
2. **第二批**：S-1（出站转义 + 文本级 mention 兜底）、A-2（错误可见化 + 指标）、DT-2（ack 前置/异步化）、DT-4（恢复事件）、C-7（Satori 会话类型）。
3. **第三批**：C-3/C-4/C-5（去重键与窗口、prune 数据结构）、D-2/D-5（mention 线格式、429 重试）、A-3/A-4（截断与 reasoning 回传）、DT-5/DT-6/DT-8。
4. **清理项**：D-3/D-4/A-6（无界缓存统一到 `ChatIdempotencyCache`/LRU）、A-5（`IHttpClientFactory`）、§6 表内 P3。

---

审查完成时间：2026-11-22（Asia/Shanghai）。全程只读，仓库 `git status` 无改动；本报告为唯一产出。
