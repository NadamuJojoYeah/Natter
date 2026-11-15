# Natter AI Provider Design（Microsoft.Extensions.AI + DeepSeek）

> **状态**：Accepted design · Phase 1/2 核心已实现；真实 DeepSeek + Discord 联调已跑通（26/26 live 复测通过；BUG-001/002 已关闭，L5 人工闸门待执行，见 [处置记录](testing/2026-11-15-live-deepseek-discord-response.md)）
> **日期**：2026-11-15
> **定位**：Natter 的 AI provider/responder 设计。DeepSeek 作为第一个真实模型 provider；`Microsoft.Extensions.AI` 作为模型调用抽象；NPS 负责插件、权限、秘密和生命周期。
> **关联**：[NPS v1](PLUGIN_SPEC.md)、[NLMP](NLMP.md)、[Discord transport 设计](DISCORD_PLUGIN_DESIGN.md)

---

## 0. 决策摘要

1. **使用 `Microsoft.Extensions.AI` 的 `IChatClient` 作为模型调用抽象**，不自己再发明第二套 chat 接口。
2. **DeepSeek 不直接等同于标准 OpenAI provider**：`thinking`、`reasoning_content`、思考模式下的 tools 回传、usage 细节都需要适配。
3. **推荐 `IChatClient` + 自定义 `DeepSeekChatClient`**；`Microsoft.Extensions.AI.OpenAI` + 自定义 endpoint 只作为快速 spike。
4. DeepSeek 作为第一个 `chat.responder` feature 插件；不依赖 Discord，不 import Discord.Net。
5. 第一版策略：**`deepseek-flash` + 非思考模式 + 非流式 + 无 tools + 内存历史**。
6. 第一版单个插件内同时包含 responder 和 provider；后续拆成 `ai.provider` contribution + responder feature。
7. `DEEPSEEK_API_KEY` 只从 NPS secret provider 读取；`network` 权限仅给 provider。
8. 不把 `reasoning_content` 发给 Discord、不写日志、不进普通会话历史。
9. `ai.complete` 权限预留给“宿主 AI port”；当前 NPS 还没有宿主 AI port，第一版不用它作为已实现能力。
10. Natter 不引入 Semantic Kernel 的 agent/plugin 体系；NPS 已经是插件系统，M.E.AI 只负责模型调用层。

---

## 1. DeepSeek 文档事实

以 2026-11 的 DeepSeek 文档为准：

| 项 | 当前值 |
| --- | --- |
| OpenAI base URL | `https://api.deepseek.com` |
| Anthropic base URL | `https://api.deepseek.com/anthropic` |
| Chat Completions | `POST /chat/completions` |
| 当前模型 | `deepseek-flash`、`deepseek-v4-pro` |
| 思考模式 | 支持，**默认开启** |
| 思考控制 | `thinking.type = enabled/disabled`、`reasoning_effort = low/high/max` |
| 思考输出 | `reasoning_content` |
| 上下文 | 1M |
| 最大输出 | 384K |
| Tool Calls | 支持 |
| JSON Output | 支持 |
| Responses API | 支持 |
| 图像理解 | `deepseek-flash` 支持，`deepseek-v4-pro` 不支持 |

关键含义：

1. **不要使用旧的 `deepseek-chat` / `deepseek-reasoner` 作为默认模型名**。
   当前应使用 `deepseek-flash` 或 `deepseek-v4-pro`，并允许配置覆盖。
2. **不要省略 thinking 配置**。
   不传 `thinking` 且不传 `reasoning_effort` 时默认开启思考；Discord 聊天会变慢且成本更高。v1 应显式 `thinking: disabled`。
3. **思考模式下 `temperature` 无效**，`presence_penalty` / `frequency_penalty` 已 deprecated。不要依赖这些参数。
4. **思考 + tools 的历史轮次必须回传 `reasoning_content`**，否则可能 400。标准 OpenAI 消息模型不保证这一点，因此 v1 不开 tools。
5. **`reasoning_content` 不是 `content`**。最终回复只取 `content`；思考内容不得展示给 Discord 用户。
6. **usage 包含缓存命中/未命中、reasoning_tokens 等扩展字段**。通用 `UsageDetails` 可能只能映射一部分，需要 adapter 保留原始 usage。
7. **DeepSeek 同时提供 OpenAI、Anthropic、Responses API**。第一版选择 Chat Completions，控制力最好；Responses API 后续再评估。

参考：

- [DeepSeek 首次调用 API](https://api-docs.deepseek.com/zh-cn/)
- [DeepSeek 模型 & 价格](https://api-docs.deepseek.com/zh-cn/quick_start/pricing)
- [DeepSeek Chat Completions API](https://api-docs.deepseek.com/zh-cn/api/create-chat-completion)
- [DeepSeek 思考模式](https://api-docs.deepseek.com/zh-cn/guides/thinking_mode)
- [DeepSeek Tool Calls](https://api-docs.deepseek.com/zh-cn/guides/tool_calls)

---

## 2. Microsoft.Extensions.AI 的定位

`Microsoft.Extensions.AI` 提供：

- `IChatClient`；
- `ChatMessage` / `ChatOptions` / `ChatResponse` / `ChatResponseUpdate`；
- `GetResponseAsync` / `GetStreamingResponseAsync`；
- DI 注册；
- 中间件管道：
  - logging；
  - OpenTelemetry；
  - distributed cache；
  - function invocation；
  - rate limiting；
  - custom `DelegatingChatClient`。

它适合 Natter：

- responder 不直接绑定 DeepSeek；
- fake `IChatClient` 可以做零网络测试；
- 日志/OTel/限流/缓存可以复用；
- 以后接 OpenAI、Anthropic、本地模型时，上层 responder 不变。

参考：

- [Microsoft.Extensions.AI 库](https://learn.microsoft.com/zh-cn/dotnet/ai/microsoft-extensions-ai)
- [使用 IChatClient 接口](https://learn.microsoft.com/zh-cn/dotnet/ai/ichatclient)

### 2.1 但 OpenAI connector 不是 DeepSeek adapter

`Microsoft.Extensions.AI.OpenAI` 可以通过自定义 endpoint 接 OpenAI-compatible 服务，适合快速 spike，但不能假设完整支持 DeepSeek：

| DeepSeek 特性 | OpenAI connector 风险 |
| --- | --- |
| `thinking` / `reasoning_effort` | 非标准字段，可能无法透传 |
| `reasoning_content` | 非标准 message 内容，可能丢失 |
| 思考模式 + tools 的 reasoning 回传 | 标准 OpenAI 消息模型不保证 |
| `prompt_cache_hit_tokens` / `reasoning_tokens` | 可能只保留部分 usage |
| DeepSeek Responses API / Anthropic API | 不是 OpenAI Chat Completions 语义 |
| `deepseek-flash` 图像理解 | 需要多模态映射，不一定自动支持 |

结论：

- **快速验证**：`Microsoft.Extensions.AI.OpenAI` + custom endpoint + 非思考 + 无 tools。
- **正式 DeepSeek 适配**：自定义 `DeepSeekChatClient : IChatClient`。

### 2.2 不建议 Semantic Kernel

Semantic Kernel 有自己的 kernel/plugin/agent/planner 体系。
Natter 已经有 NPS plugin system 和 `IChatResponder`，再引入 SK 的插件模型会产生两套扩展点。

M.E.AI 更轻，只负责模型调用抽象；这正合适。

---

## 3. 接入前置

在实现 DeepSeek responder 之前，宿主需要满足：

1. **注册 `ChatOrchestrator`**：`ChatCompositionModule` 现在默认注册 `AddChatOrchestrator()`，`chat.responder` 可以从 CLI/Android 宿主的 NLMP 事件流被调用；测试可直接使用 `TestHost`。
2. **长驻宿主**：`chat.responder` 需要持续订阅 NLMP 事件；一次性 CLI 命令结束后宿主会停止。真实 Discord + AI 测试需要 `serve` 或专用长驻宿主。
3. **配置目录/秘密**：DeepSeek key 通过 `IPluginSecrets`；`DEEPSEEK_API_KEY` 不写入插件包和配置样例。
4. **包版本集中管理**：`Microsoft.Extensions.AI.*` 和 provider 依赖应写入 `Directory.Packages.props`，并锁定版本；不要依赖浮动 latest。

这些不是 DeepSeek 插件本身的问题，但没有它们，实际 bot 不会回复。

---

## 4. 推荐架构

```text
Discord / NLMP ChatEvent
        │
ChatOrchestrator
        │ IChatResponder
DeepSeekResponder（Natter feature plugin）
        │ IChatClient
M.E.AI middleware
  ├── Logging
  ├── OpenTelemetry
  ├── Rate limiting（可选）
  └── Function invocation（后续）
        │
DeepSeekChatClient（自定义 IChatClient）
        │ HTTP /chat/completions
DeepSeek API
```

### 4.1 第一版：一个插件

```text
Natter.Feature.Ai.DeepSeek
├── DeepSeekPlugin : INatterPlugin
├── DeepSeekResponder : IChatResponder
├── DeepSeekChatClient : IChatClient
├── DeepSeekOptions
├── DeepSeekConversationStore
└── config.schema.json
```

优点：

- NPS 还没有 `ai.provider` contribution；
- 不需要插件间服务发现；
- key 只存在这个插件里；
- 快速验证模型和 responder。

缺点：

- responder 和 provider 耦合；
- 后续多模型需要拆分；
- 其他 feature 插件不能复用 provider。

### 4.2 后续：拆 provider

```text
Natter.Feature.Ai.DeepSeek (ai.provider)
        │ IChatClient
        ▼
Host AI port / registry（ai.complete）
        ▲
        │
Natter.Feature.Ai.Responder (chat.responder)
```

需要新增：

- `ai.provider` contribution type；
- host `IAiClientRegistry`；
- `ai.complete` 权限；
- 插件 provider 不直接暴露 key，只暴露受控 `IChatClient`。

第一版不阻塞在拆分上，但接口要按可拆分方式设计。

---

## 5. 插件 Manifest

```json
{
  "schemaVersion": 1,
  "id": "natter.feature.ai.deepseek",
  "name": "DeepSeek AI Responder",
  "version": "0.1.0",
  "kind": "feature",
  "hostApi": ">=0.1.0 <1.0.0",

  "entry": {
    "assembly": "lib/Natter.Feature.Ai.DeepSeek.dll",
    "type": "Natter.Feature.Ai.DeepSeek.DeepSeekPlugin"
  },

  "description": "DeepSeek-backed chat responder for Natter.",
  "author": "Natter",
  "license": "MIT",

  "permissions": [
    "network",
    "secrets:DEEPSEEK_API_KEY",
    "chat.read",
    "chat.send",
    "chat.interactions"
  ],

  "secrets": [
    {
      "name": "DEEPSEEK_API_KEY",
      "required": true
    }
  ],

  "configSchema": "config.schema.json",

  "contributions": [
    {
      "type": "chat.responder",
      "id": "deepseek",
      "priority": 10
    }
  ],

  "platforms": ["windows", "linux", "macos", "android"]
}
```

说明：

- `chat.read` / `chat.send` 是 `chat.responder` 的必需权限。
- `chat.interactions` 用于 slash 的 `InteractionRespondCommand`。
- `network` 用于 DeepSeek HTTP。
- `secrets:DEEPSEEK_API_KEY` 用于读取 API key。
- 第一版不声明 `ai.complete`，因为宿主还没有 AI port；直接 provider 使用 network。
- 不依赖 Discord 插件，也不依赖 `natter.chat.core`；`hostApi` 表达核心兼容。
- `priority` 要配合 echo/test responder；同一事件只允许一个 responder 返回命令。

---

## 6. 配置

```json
{
  "apiKey": "${secret:DEEPSEEK_API_KEY}",
  "baseUrl": "https://api.deepseek.com",
  "model": "deepseek-flash",

  "thinking": "disabled",
  "reasoningEffort": "none",

  "systemPrompt": "You are Natter, a concise Discord assistant.",
  "temperature": 0.7,
  "maxOutputTokens": 1024,

  "historyMaxMessages": 20,
  "historyMaxCharacters": 24000,
  "timeoutSeconds": 60,
  "maxRetries": 2,
  "retryBaseDelayMs": 250,
  "retryMaxDelayMs": 2000,

  "streaming": false,
  "tools": []
}
```

### 6.1 字段语义

| 字段 | 说明 |
| --- | --- |
| `apiKey` | 只允许 `${secret:DEEPSEEK_API_KEY}` 引用；实际值从 `IPluginSecrets` 读取 |
| `baseUrl` | 默认 `https://api.deepseek.com`；允许私有代理 |
| `model` | `deepseek-flash` / `deepseek-v4-pro`；不要硬编码旧模型名 |
| `thinking` | v1 默认 `disabled`；可显式 `enabled` |
| `reasoningEffort` | `none/low/high/max`；thinking disabled 时无效 |
| `systemPrompt` | 系统提示；保持稳定前缀以提高 DeepSeek 上下文缓存命中 |
| `temperature` | 仅非思考模式有效；thinking 模式下会被忽略 |
| `maxOutputTokens` | 限制输出和成本 |
| `historyMaxMessages` | 历史消息条数上限 |
| `historyMaxCharacters` | 历史字符数上限 |
| `timeoutSeconds` | 单次 HTTP 超时 |
| `maxRetries` | 可重试错误的最大重试次数（0–5，默认 2） |
| `retryBaseDelayMs` / `retryMaxDelayMs` | 指数退避基数/上限；429 的 Retry-After 优先并受上限约束 |
| `streaming` | v1 固定 false；后续再开 |
| `tools` | v1 固定空；开启需要处理 reasoning_content 回传 |

### 6.2 默认关闭 thinking

DeepSeek 文档明确：不传 `thinking` 且不传 `reasoning_effort` 时默认开启思考模式。
对 Discord 聊天，这通常意味着：

- 首 token 更慢；
- 输出 token 更多；
- 成本更高；
- 可能产生 `reasoning_content`。

所以推荐：

```json
"thinking": "disabled"
```

如果以后要做深度推理，再显式开启并处理 reasoning 内容。

---

## 7. IChatClient 设计

### 7.1 接口

使用 M.E.AI 的 `IChatClient`：

```csharp
public interface IChatClient : IDisposable
{
    Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default);

    ChatClientMetadata Metadata { get; }

    object? GetService(Type serviceType, object? serviceKey = null);
}
```

### 7.2 快速路线

```csharp
var client = new OpenAIClient(
        new ApiKeyCredential(apiKey),
        new OpenAIClientOptions { Endpoint = new Uri(baseUrl) })
    .GetChatClient(model)
    .AsIChatClient();
```

用于：

- 非思考模式；
- 无 tools；
- 只需要最终 `content`；
- 快速验证 DeepSeek 可用。

必须实测：

- endpoint 是否 `/chat/completions`；
- base URL 是否需要 `/v1`；
- OpenAI connector 是否保留 DeepSeek usage；
- thinking 是否被忽略或拒绝。

### 7.3 正式路线：自定义 `DeepSeekChatClient`

DeepSeek 的 `thinking`、`reasoning_effort`、`safety_check` 等 provider 扩展不要进入 responder 公共逻辑；由 `DeepSeekOptions` 和 adapter 在内部应用。若未来需要 per-request override，使用 M.E.AI 的 `ChatOptions.RawRepresentationFactory` 或内部 `DeepSeekChatOptions`，不要污染 `IChatResponder`。

职责：

- `POST {baseUrl}/chat/completions`；
- `Authorization: Bearer <key>`；
- 请求体：
  - `model`
  - `messages`
  - `thinking`
  - `reasoning_effort`
  - `max_tokens`
  - `temperature`（仅非思考）
  - `stream`
  - `tools` / `tool_choice`（后续）
- 响应解析：
  - `choices[].message.content`
  - `choices[].message.reasoning_content`
  - `choices[].message.tool_calls`
  - `finish_reason`
  - `usage`（含 cache hit/miss、reasoning_tokens）
- 映射到 M.E.AI：
  - `ChatResponse` / `ChatResponseUpdate`
  - `ChatMessage`
  - `TextContent`
  - `UsageDetails`
  - 自定义 `AdditionalProperties` 或内部扩展保存 DeepSeek 字段
- SSE 流式（后续）：
  - 解析 `data: ...` 和 `[DONE]`
  - `reasoning_content` 只进内部，不进 Discord 文本
  - 合并 usage
- 错误：
  - 401 / 402 / 429 / 5xx
  - 超时
  - 取消
  - content_filter / insufficient_system_resource
- 日志：
  - 不记录 API key
  - 不记录完整 prompt/response；只记录长度、model、usage、latency、finish_reason

### 7.4 中间件

推荐使用：

```csharp
IChatClient client = new DeepSeekChatClient(httpClient, options)
    .AsBuilder()
    .UseLogging(loggerFactory)
    .UseOpenTelemetry("Natter.Ai")
    .Build();
```

后续可加：

- `UseFunctionInvocation()`（tools）
- rate limiting
- custom usage/audit middleware
- custom redaction middleware

不要一开始就加太多中间件；先保证请求/响应正确。

---

## 8. Responder 设计

```csharp
public sealed class DeepSeekResponder : IChatResponder
{
    private readonly IChatClient _chat;
    private readonly IDeepSeekConversationStore _history;
    private readonly ChatCommandFactory _commands;
    private readonly DeepSeekOptions _options;
    private readonly IPluginLog _log;
    private readonly DeepSeekConversationGate _gate;
}
```

### 8.1 普通消息

```text
MessageCreatedEvent
  → 提取 Message.Content.Text
  → 读取 (source, conversation.id) 历史
  → system + history + user
  → IChatClient.GetResponseAsync
  → 保存 assistant 文本
  → ChatCommandFactory.Send(source, conversation, content, replyTo)
```

规则：

- 空文本、纯附件、bot/self 已在 orchestrator/上游过滤；
- 回复使用 `replyTo`；
- 内容超出 Discord 能力时由 `ChatCommandDispatcher` 分片；
- 发送失败只记录 ack 错误，不重试整段 AI 调用。

### 8.2 Slash

```text
InteractionCreatedEvent
  → Interaction.Options["prompt"]
  → 读取历史
  → IChatClient.GetResponseAsync
  → ChatCommandFactory.Respond(source, interaction, content)
```

Discord transport 已经 `DeferAsync`；AI 慢一点可以 `FollowupAsync`。
Interaction token 有过期时间；超时返回友好错误。

### 8.3 Typing

如果 `Capabilities.Typing` 为 true：

```csharp
await dispatcher.ExecuteAsync(commands.Typing(source, conversation), cancellationToken);
```

typing 失败不影响 AI 回复。

### 8.4 错误

- 401：配置错误，提示管理员，不回复用户堆栈；
- 402：余额不足，提示管理员；
- 429：退避；不要在 Discord 里刷错误；
- 5xx/timeout：回复“暂时不可用”；
- content_filter：给安全兜底回复；
- 空 content：使用兜底文案，不把 reasoning_content 发出去。

---

## 9. 历史与会话

### 9.1 Key

```text
(source, conversation.id)
```

Discord：

- DM channel id；
- guild channel id；
- thread id。

是否在多人频道共享上下文，需要配置；默认建议：

- DM/thread 独立；
- guild channel 共享频道上下文，或按用户隔离，由 persona 配置决定。

### 9.2 v1 存储

内存 `DeepSeekConversationStore`：

- 最近 N 条；
- 字符/token 估算裁剪；
- 重启丢失；
- 不把 `reasoning_content` 存进普通历史；
- 不把系统提示重复写入历史。

后续：

- `IConversationStore` 端口 + 持久化 adapter；
- 需要 `storage` 权限；
- 多进程/多实例需要共享存储。

### 9.3 上下文缓存

DeepSeek 支持上下文缓存。为了命中：

- system prompt 保持稳定；
- 不要把时间戳/随机 ID 放在 system prompt 前缀；
- 动态信息放 user 消息或后续 system 消息；
- 记录 cache hit/miss token，用于成本和调优。

---

## 10. 并发、幂等与限流

### 10.1 当前风险

`ChatOrchestrator` 的 observer 是 fire-and-forget：

```csharp
public void OnNext(ChatEvent value)
    => _ = HandleAsync(value);
```

同一会话连续消息可能并发调用 DeepSeek，导致：

- 回复乱序；
- 历史交错；
- 重复计费；
- 多个回复同时发到 Discord。

### 10.2 建议

- **per-conversation gate**：同一 `(source, conversation.id)` 串行；
- 全局并发上限（例如 2–4）；
- 请求超时和取消；
- 同一 `event.Id` / `message.Id` 幂等；
- ingress 去重之外，responder 本地保留短期 processed set；
- 失败重试只在“未产生输出”时进行，避免重复回复。

### 10.3 限流

- 每个用户/频道限制请求频率；
- DeepSeek 429/5xx/网络/超时有界重试：默认最多 2 次，指数退避 + jitter；401/402/400/content_filter 不重试；
- 重试发生在返回用户可见输出之前，responder 只生成一条回复；
- 重试耗尽后由 responder 返回统一 unavailable fallback；
- Discord 发送侧已有 `ChatCommandDispatcher` 和上游限流；不要让 AI 重试风暴打爆 Discord。

---

## 11. Thinking / Tools / Streaming 策略

### 11.1 Thinking

v1：

- `thinking: disabled`；
- 不使用 `reasoning_content`；
- 追求低延迟和可预测成本。

后续：

- 显式 `thinking: enabled` + `reasoning_effort`；
- `reasoning_content` 仅内部使用，不发送到 Discord、不写日志；
- 如果开启 tools，必须按 DeepSeek 要求回传历史 `reasoning_content`。

### 11.2 Tools

v1 不做。
后续用 M.E.AI `AIFunctionFactory` + `UseFunctionInvocation`，但必须：

- Natter tool 权限/审计；
- DeepSeek thinking + tools 的 reasoning 回传；
- 工具执行失败/超时；
- 不把工具原始结果直接展示给用户；
- 不在 Discord 插件里实现工具。

### 11.3 Streaming

v1 不做。
后续：

- M.E.AI `GetStreamingResponseAsync`；
- DeepSeek SSE；
- `reasoning_content` 过滤；
- Discord message edit 节流；
- `Capabilities.StreamEdit` 未支持时只发最终消息。

---

## 12. 安全

- Discord 消息是不可信输入；prompt injection 由 system prompt/策略/工具权限共同处理；
- Discord bot 会收到自己发送的消息；默认三层过滤（IgnoreBots / IgnoreSelf / IgnoreSelfMessages）必须保持开启，否则 AI 可能自问自答；
- 不把 Discord 用户文本拼进 system prompt；
- 限制用户消息长度、历史长度、输出 token；
- API key 只从 `IPluginSecrets` 读取，不进入日志、异常、manifest、config 样例；
- `reasoning_content` 不展示、不日志、不持久化到普通历史；
- 不记录完整 prompt/response，只记录 model、latency、usage、finish_reason；
- 不把 DeepSeek 原始错误响应直接发给用户；
- 多租户：history key 必须包含 source；不要跨 Discord/Slack/Satori 复用会话；
- 工具调用必须有显式权限和审计，不能由 Discord 文本直接驱动；
- NPS permissions 是治理，不是沙箱；DeepSeek 插件是可信第一方插件。

---

## 13. 诊断

建议提供：

```bash
natter ai status
natter ai usage --since 24h
```

`ai status`：

- provider id；
- model；
- thinking enabled/disabled；
- base URL host（不含 token）；
- last error；
- request count / error count；
- p50/p95 latency；
- cache hit/miss tokens；
- output tokens。

`plugins doctor natter.feature.ai.deepseek`：

- manifest/hostApi；
- secret 是否提供；
- 权限是否授予；
- config schema；
- responder registry 状态；
- 最近 activation error。

日志不包含 prompt 和 API key。

---

## 14. 测试

### 14.1 Fake `IChatClient`

不需要网络：

- MessageCreatedEvent → SendCommand；
- InteractionCreatedEvent → RespondCommand；
- 空 content/refusal；
- timeout/cancellation；
- 429/401/402/5xx；
- history 裁剪；
- 并发 gate；
- 幂等。

### 14.2 Fake HTTP

测 `DeepSeekChatClient`：

- 请求 body：model/messages/thinking/reasoning_effort/max_tokens；
- `Authorization` header；
- 非流式 response；
- SSE response；
- `reasoning_content` 不进入回复文本；
- usage（cache hit/miss、reasoning_tokens）；
- 401/402/429/5xx；
- timeout。

### 14.3 真实 DeepSeek + fake Discord

- 真实 API key；
- 限制预算；
- 一条普通消息、一条 slash；
- 验证模型名、thinking 关闭、usage；
- 不接真实 Discord。

### 14.4 真实 Discord + 真实 DeepSeek

- 测试 guild；
- DM、mention、`/ask`；
- 并发两条消息；
- 429/断线；
- 重启后的历史行为；
- 确认没有重复回复和 token 泄漏。

CI 不调用真实 DeepSeek。

---

## 15. 实施顺序

### Phase 1：M.E.AI spike（已交付）

- ✅ 引入 `Microsoft.Extensions.AI`；
- ✅ 自定义 `DeepSeekChatClient` 接 `deepseek-flash`；
- ✅ 非思考、非流式、无 tools；
- ✅ base URL/endpoint/usage 映射与测试。

### Phase 2：DeepSeek provider + responder（已交付）

- ✅ `DeepSeekChatClient : IChatClient`；
- ✅ `DeepSeekResponder : IChatResponder`；
- ✅ 内存历史、per-conversation gate、幂等；
- ✅ 普通消息 + slash；
- ✅ fake IChatClient / fake HTTP 测试；
- ✅ manifest/config/secret/permissions。

### Phase 3：真实 API + fake Discord（已通过）

- ✅ 真实 DeepSeek key，限额；
- ✅ 验证错误、超时、usage；
- ✅ fake Discord / NLMP 下发路径与真实 API 往返。

### Phase 4：真实 Discord + 真实 DeepSeek（自动用例已通过，L5 人工待执行）

- ✅ 测试 guild；
- ✅ 先 echo/连接冒烟，再 AI；
- ✅ 自动覆盖文件插件、独立 CLI、idempotency；L5 真人 gate 待执行；
- ✅ 记录成本和延迟（`ai status`）。

### Phase 5：拆分 provider 与 responder

- `ai.provider` contribution；
- host AI registry / `ai.complete`；
- feature 插件不再直接持有 API key；
- 多 provider 路由/故障转移。

### Phase 6：thinking / tools / streaming

- thinking 开关；
- reasoning_content 处理；
- tools + DeepSeek reasoning 回传；
- M.E.AI function invocation；
- Discord 流式编辑节流。

---

## 16. 验收标准

- [x] `Microsoft.Extensions.AI` 包正确引入，`IChatClient` 可注入/替换；
- [x] DeepSeek 请求使用当前模型名，不硬编码旧名；
- [x] v1 显式关闭 thinking；
- [x] `DEEPSEEK_API_KEY` 只来自 secret provider，日志/配置不泄漏；
- [x] 普通消息 → NLMP Command；
- [x] slash → InteractionRespondCommand；
- [x] 历史裁剪、并发 gate、幂等生效；
- [x] 401/402/429/5xx/timeout 有结构化错误；
- [x] 429/5xx/网络/超时有界重试，401/402/400 不重试；
- [x] usage 有记录，reasoning_content 不输出；
- [x] fake `IChatClient` / fake HTTP 测试全绿；
- [x] 真实 DeepSeek + fake Discord 通过（L1/L3/L4；CI 默认不调用）；
- [x] 真实 Discord + 真实 DeepSeek 测试 guild 自动用例通过（26/26）；
- [ ] L5 真人 @bot 人工闸门通过；
- [x] Discord 插件不 import DeepSeek/AI SDK；
- [x] DeepSeek 插件不 import Discord.Net。

---

## 17. 开放问题

- `Microsoft.Extensions.AI.OpenAI` 对 DeepSeek 自定义 endpoint 的实际兼容度；
- DeepSeek `thinking` 是否能通过 `ChatOptions.AdditionalProperties` 透传；
- `reasoning_content` 在 M.E.AI `ChatResponse` 中的保留方式；
- thinking + tools 的 reasoning 回传；
- usage/cache token 的标准化；
- 多 provider 的 `ai.provider` contribution 设计；
- 会话持久化与多实例；
- 流式输出与 Discord 编辑节流；
- 模型路由/故障转移；
- DeepSeek 图像理解与 Discord 附件。

---

## 18. 本仓库实现

### 18.1 代码映射

| 设计章节 | 实现 |
| --- | --- |
| §4 推荐架构 | `src/Natter.Feature.Ai.DeepSeek` 单插件；`DeepSeekPlugin` + `DeepSeekResponder` + `DeepSeekChatClient` |
| §5 Manifest | `plugin.json`：`chat.responder/deepseek` priority 10、`cli.command/ai.status`、secret `DEEPSEEK_API_KEY` |
| §6 配置 | `DeepSeekOptions` + `config.schema.json`；thinking 默认 disabled |
| §7 IChatClient | 自定义 `DeepSeekChatClient`；`POST {baseUrl}/chat/completions`；M.E.AI `ChatResponse`/`UsageDetails` |
| §8 Responder | `DeepSeekResponder`：普通消息、slash、replyTo、历史、gate、幂等、fallback |
| §9 历史 | `DeepSeekConversationStore`：内存、条数/字符裁剪、source+conversation key |
| §10 并发 | `DeepSeekConversationGate`：per-conversation 串行 + 全局并发上限；短期 processed/inflight 集；`DeepSeekChatClient` 有界重试/退避 |
| §11 thinking/tools/streaming | thinking disabled 默认；reasoning_content 丢弃；tools/streaming 明确拒绝 |
| §12 安全 | API key 仅 secret；不记录 prompt/response/key；错误脱敏 |
| §13 诊断 | `ai status [--json]`：model/thinking、请求/错误、p50/p95、token/cache |
| §14 测试 | `DeepSeekChatClientTests`、`DeepSeekResponderTests`、`DeepSeekPluginTests` |

### 18.2 已验证

- fake HTTP 验证请求 body、Authorization、thinking 开关、usage/reasoning 隔离、401/402/429/5xx/timeout；
- fake `IChatClient` 验证消息/slash、历史裁剪、gate 串行、幂等、fallback；
- NPS 全链路：插件加载、config/secret、`ChatOrchestrator` 自动调用 responder、`InMemoryChatUpstream` 收到回复、`ai status` 命令；
- 175 个框架级测试全部通过（含 550/5xx 重试、401 不重试、自发消息三层过滤、加载上下文与 deps staging 守护）。

### 18.3 未交付

- L5 真人 @bot 人工闸门；
- 流式输出 / SSE；
- tools / function invocation；
- 持久化会话、多实例共享；
- 多 provider `ai.provider` contribution / `ai.complete` 宿主端口；
- 多 persona、多 guild 策略。

## 19. 一句话结论

**用 Microsoft.Extensions.AI 做模型抽象，用自定义 `DeepSeekChatClient` 做 DeepSeek 适配，用 `chat.responder` 做业务。**
第一版只做非思考、非流式、无 tools 的 DeepSeek responder；先 fake 测试，再真实 API，最后真实 Discord。不要用 OpenAI connector 假装自己已经完整支持 DeepSeek。
