# Natter Discord Transport Plugin Design

> **状态**：Accepted design · Phase 1/2 核心已实现；真实 Discord 网关/REST 联调已跑通（26/26 live 复测通过；BUG-001/002 已关闭，L5 人工闸门待执行，见 [处置记录](testing/2026-11-15-live-deepseek-discord-response.md)）
> **日期**：2026-11-15
> **定位**：NPS v1 的 `transport` 插件，只负责 Discord 连接、事件映射、命令发送和能力声明，不包含 AI、prompt、记忆或业务规则。
> **关联**：[Natter Plugin Specification v1](PLUGIN_SPEC.md)、[NLMP](NLMP.md)、[插件开发指南](PLUGINS.md)

---

## 0. 决策摘要

1. Discord 是 **`chat.upstream` contribution**，不是 feature 插件。
2. 插件实现 `INatterPlugin`，注册 `chat.upstream` 和 `cli.command`，不直接访问宿主根 DI。
3. NPS runtime 在插件 `ActivateAsync` 之后统一调用 `IChatUpstream.ConnectAsync`；插件不得在 `ConfigureAsync` 里连接 Discord。
4. Discord 的 token 只来自 `IPluginSecrets`，不进入 manifest、config、日志或 `plugin.json`。
5. `Source.SelfId` 必须在配置中显式提供；NPS 注册 upstream 时 client 尚未登录，不能依赖 `CurrentUser`。
6. 第一版优先支持消息/@mention/私聊；slash command 依赖 `InteractionCreatedEvent` 路由，必须先补 core。
7. 所有出站消息默认 `AllowedMentions.None`；只允许 NLMP 显式声明的用户 mention。
8. v1 不做 AI、不做按钮/Modal、不做 stream edit、不做 sharding；这些按阶段引入。

---

## 1. 目标与非目标

### 1.1 目标

- 单个 Discord bot 接入 NLMP；
- 支持 guild channel、DM、Group DM、Thread 的消息接收；
- 支持 `message.send`、`message.update`、`message.delete`、`typing.start`；
- 支持斜杠命令 `/ask`（依赖 core 的 interaction 路由）；
- 能力驱动降级：不支持编辑/附件/slash 时由 NLMP 能力模型处理；
- 过滤 bot/self、guild/channel allowlist、mention 规则；
- 提供 `discord status` 诊断；
- 通过 fake transport 做 CI 测试，不连接真实 Discord。

### 1.2 非目标

- AI 调用、会话历史、prompt、persona、工具；
- Discord 之外的平台；
- 多 bot/多 token 动态管理（v1 单实例单 bot）；
- Sharding；
- 按钮/Modal/Select 完整交互（Phase 2）；
- Stream edit / 高频消息编辑（Phase 3）；
- 原生 Android 动态插件（后续 Android profile）；
- 进程内安全沙箱；
- 插件市场/自动更新。

---

## 2. 当前前提与缺口

### 2.1 NPS 已具备

- `INatterPlugin` / `IPluginContext` / `IContributionRegistry`；
- `chat.upstream` contribution registry；
- plugin-local DI；
- manifest、host API、权限、秘密、配置 schema；
- NPS runtime 在 `StartCandidateAsync` 后调用 `ConnectUpstreamsAsync`；
- 停止时先 `DisconnectAsync`、再 unregister、再 `DeactivateAsync`；
- `IChatUpstream` / `IChatCommandDispatcher` / NLMP 事件与命令。

### 2.2 当前缺口

1. **交互路由**：`ChatOrchestrator` 目前只处理 `MessageCreatedEvent` / `MessageUpdatedEvent`，不会把 `InteractionCreatedEvent` 交给 `IChatResponder`。slash 支持必须补这个分支。
2. **Source 身份**：`IChatUpstream.Source` 在构造时固定，早于 Discord 登录；必须配置 `selfId`。
3. **Core 依赖**：不要声明 `natter.chat.core` 作为插件依赖；它没有真实的插件 manifest，声明后会被 NPS 标记为 `Blocked`。核心契约由 `hostApi` 覆盖。
4. **消息编辑默认值**：`ChatOrchestrationOptions.ProcessEditedMessages` 已改为默认 `false`，避免 AI 对每次编辑重复回复；需要编辑事件的消费者显式开启。
5. **真实 Discord 传输**：`Discord.Net 3.20.2` 已接入，`DiscordSocketTransport` 实现 Gateway/REST；CI 仍使用 fake transport。

---

## 3. 工程与包结构

```text
plugins/Natter.Plugin.Discord/
├── Natter.Plugin.Discord.csproj
├── plugin.json
├── config.schema.json
├── DiscordPlugin.cs
├── DiscordOptions.cs
├── DiscordBotToken.cs
├── DiscordChatUpstream.cs
├── DiscordTransport.cs              # IDiscordTransport + DTO
├── DiscordSocketTransport.cs        # Discord.Net 实现
├── DiscordEventMapper.cs
├── DiscordCommandMapper.cs
├── DiscordContentMapper.cs
├── DiscordCapabilities.cs
├── DiscordInteractionRegistry.cs
└── DiscordStatusCommand.cs
```

项目：

- `net10.0` 类库；
- 引用 `Natter.Plugin.Abstractions`、`Natter.Chat`、`Natter.Cli.Abstractions`；
- 引用 `Discord.Net`；
- `CopyLocalLockFileAssemblies=true`，插件包包含 Discord.Net 私有依赖；
- 框架共享程序集（`Natter.*`、`Microsoft.Extensions.*`）不打包进插件目录。

插件包由 NPS 文件加载器加载；Android 若使用 dynamic profile，按 [Android 插件宿主](ANDROID_PLUGIN_HOST.md) 的签名/私有目录规则处理。

---

## 4. Manifest

```json
{
  "schemaVersion": 1,
  "id": "natter.transport.discord",
  "name": "Discord Transport",
  "version": "0.1.0",
  "kind": "transport",
  "hostApi": ">=0.1.0 <1.0.0",

  "entry": {
    "assembly": "lib/Natter.Plugin.Discord.dll",
    "type": "Natter.Plugin.Discord.DiscordPlugin"
  },

  "description": "Discord upstream for Natter.",
  "author": "Natter",
  "license": "MIT",

  "permissions": [
    "network",
    "secrets:DISCORD_BOT_TOKEN",
    "chat.read",
    "chat.send",
    "chat.edit",
    "chat.delete",
    "chat.commands",
    "chat.interactions",
    "cli.commands"
  ],

  "secrets": [
    {
      "name": "DISCORD_BOT_TOKEN",
      "required": true
    }
  ],

  "configSchema": "config.schema.json",

  "contributions": [
    {
      "type": "chat.upstream",
      "id": "discord"
    },
    {
      "type": "cli.command",
      "id": "discord.status"
    }
  ],

  "platforms": ["windows", "linux", "macos"]
}
```

说明：

- **不要** 声明 `dependsOn: natter.chat.core`；NPS 没有这个插件，`hostApi` 已经表达核心兼容性。
- `chat.read` / `chat.send` 是 `chat.upstream` contribution 的必需权限；`chat.edit` / `chat.delete` / `chat.commands` / `chat.interactions` 是实际功能需要，仍应显式申请。
- `cli.commands` 只用于 `discord status`；不需要 CLI 时可以不声明。
- `platforms` 先列桌面服务端；Android 动态形态后续按 Android profile 加 `android`。

---

## 5. 配置

### 5.1 示例

```json
{
  "token": "${secret:DISCORD_BOT_TOKEN}",
  "selfId": "123456789012345678",

  "intents": [
    "Guilds",
    "GuildMessages",
    "DirectMessages"
  ],

  "guilds": ["111111111111111111"],
  "channels": ["222222222222222222"],
  "users": [],

  "requireMention": true,
  "messageCommands": true,
  "slashCommands": true,
  "commands": {
    "ask": true
  },

  "devGuildId": "111111111111111111",
  "typing": true,
  "maxAttachmentBytes": 26214400
}
```

### 5.2 字段语义

| 字段 | 说明 |
| --- | --- |
| `token` | 只允许 `${secret:DISCORD_BOT_TOKEN}` 引用；实际值从 `IPluginSecrets` 读取 |
| `selfId` | 稳定的 bot 身份，建议 Discord bot user id；登录后必须与 `CurrentUser.Id` 一致 |
| `intents` | `Guilds` / `GuildMessages` / `DirectMessages` / `MessageContent`；`MessageContent` 是特权 intent |
| `guilds` | guild 白名单；空数组由插件策略决定是否全部禁用 |
| `channels` | channel 白名单；空数组同上 |
| `users` | 可选用户白名单；空数组表示不额外限制 |
| `requireMention` | guild channel 中是否必须 mention bot；DM 不受限制 |
| `messageCommands` | 是否处理普通消息；需要 `MessageContent` intent |
| `slashCommands` | 是否注册/处理 slash command |
| `commands` | 允许注册的命令名，如 `ask` |
| `devGuildId` | 开发期注册 guild command 的 guild；全局命令传播慢，开发期必须用 guild command |
| `typing` | 收到消息后是否触发 typing indicator |
| `maxAttachmentBytes` | 出站/入站附件的大小上限；与 `Capabilities.MaxAttachmentBytes` 对应 |

### 5.3 `selfId` 是硬要求

`IChatUpstream.Source` 在注册 upstream 时就要可用，而 NPS 此时还没有调用 `ConnectAsync`，无法从 `DiscordSocketClient.CurrentUser` 读取 bot id。

因此：

- 配置必须提供 `selfId`；
- `Source = new Source("discord", "discord", selfId)`；
- `ConnectAsync` 在 `Ready` 后校验 `client.CurrentUser.Id == selfId`；
- 不一致则连接失败，避免事件 Source 与 registry key 不一致。

如果未来支持多 bot，每个 bot 需要独立 upstream/plugin instance，不能靠一个 `selfId` 加运行时替换。

### 5.4 Intents 策略

- **slash-only**：`Guilds`，DM slash 再加 `DirectMessages`；不需要 `MessageContent`。
- **message/@mention**：`Guilds | GuildMessages | DirectMessages | MessageContent`；必须在 Discord Developer Portal 开启 Message Content Intent。
- 没有 `MessageContent` 时，`SocketMessage.Content` 可能为空；插件不得把空内容当成正常消息。

---

## 6. 插件入口

```csharp
public sealed class DiscordPlugin : INatterPlugin
{
    public ValueTask ConfigureAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        var options = context.Config.Get<DiscordOptions>();
        var token = context.Secrets.GetRequired("DISCORD_BOT_TOKEN");

        context.Services.Collection.AddSingleton(options);
        context.Services.Collection.AddSingleton(new DiscordBotToken(token));
        context.Services.Collection.AddSingleton<IDiscordTransport, DiscordSocketTransport>();
        context.Services.Collection.AddSingleton<DiscordChatUpstream>();

        context.Contributions.AddChatUpstream<DiscordChatUpstream>("discord");
        context.Contributions.AddCliCommand(DiscordStatusCommand.Descriptor);

        return ValueTask.CompletedTask;
    }

    public ValueTask ActivateAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default)
    {
        // 不做网络连接；NPS 会在 Activated 后调用 upstream.ConnectAsync
        return ValueTask.CompletedTask;
    }

    public ValueTask DeactivateAsync(
        IPluginContext context,
        CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
```

规则：

- `ConfigureAsync` 只做配置读取、服务注册、contribution 声明；
- 不在 `ConfigureAsync` 中 `LoginAsync` / `StartAsync` / HTTP 请求；
- `ActivateAsync` 不重复连接；
- `DiscordSocketClient` 由插件 provider 负责 dispose；
- `DiscordChatUpstream.DisconnectAsync` 负责断开与注销事件。

---

## 7. 传输边界

建议把 Discord.Net 封装在 `IDiscordTransport` 后面：

```csharp
public interface IDiscordTransport
{
    IObservable<DiscordTransportEvent> Events { get; }

    ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    ValueTask DisconnectAsync(CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> SendMessageAsync(
        DiscordSendMessage request,
        CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> UpdateMessageAsync(
        DiscordUpdateMessage request,
        CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> DeleteMessageAsync(
        DiscordDeleteMessage request,
        CancellationToken cancellationToken = default);

    ValueTask TriggerTypingAsync(
        DiscordConversationRef conversation,
        CancellationToken cancellationToken = default);

    ValueTask<DiscordSendResult> RespondInteractionAsync(
        DiscordInteractionResponse response,
        CancellationToken cancellationToken = default);
}
```

目的：

- `DiscordChatUpstream` 只做 NLMP ↔ Discord DTO 映射；
- CI 用 `FakeDiscordTransport`；
- Discord.Net 升级不改变 NLMP 契约；
- 可以精确测试 3 秒 defer、分片、429/403 错误。

`DiscordSocketTransport` 内部持有：

- `DiscordSocketClient`
- `DiscordRestClient`（必要时）
- token（只在 Login 使用）
- `ILogger`/`IPluginLog`
- `ILog` 事件桥接

---

## 8. 入站映射

### 8.1 普通消息

`MessageReceived`：

1. 忽略 `Author.IsBot` 和 `CurrentUser`；
2. guild/channel/user allowlist；
3. `requireMention` 时检查 mention；DM 不要求 mention；
4. 去掉 bot mention；空文本且无附件则忽略；
5. 映射 `Message`：
   - `Conversation`；
   - `User`；
   - `Content`；
   - `ReplyTo`；
   - `CreatedAt` / `EditedAt`；
6. 发布 `MessageCreatedEvent`。

### 8.2 Conversation 映射

| Discord | NLMP |
| --- | --- |
| `IDMChannel` | `ConversationKind.Direct` |
| `IGroupChannel`（Group DM） | `ConversationKind.Group` |
| `SocketThreadChannel` | `ConversationKind.Thread`，`ParentId = parent channel id` |
| Guild text/announcement/forum/media channel | `ConversationKind.Guild`，`GuildId = guild id` |
| 其他/无法判断 | `ConversationKind.Unknown` |

`Conversation.Id` 使用 Discord channel/thread id；`GuildId` 使用 guild id；`Title` 使用 channel name。

### 8.3 User / Content

- `User.Id` = author id；
- `User.Name` = username；
- `User.DisplayName` = global/nick name（可用时）；
- `User.Kind` = `Bot` / `Human` / `Unknown`；
- `Content.Text` = 清洗后的 message content；
- `Content.Format = Markdown`（Discord 支持 Markdown）；
- `Content.Attachments` = Discord 附件 URL/文件名/MIME/大小；
- `Content.Mentions` = 结构化 mention；入站保留 self mention（它是 orchestrator 判断"是否被 @"的唯一可移植信号），正文中的 `<@bot>` 仍会被清洗；出站时由 upstream 过滤 self mention。
- `Message.ReplyTo` = `MessageReference.MessageId`。

### 8.4 Slash command

```csharp
private async Task OnSlashCommandAsync(SocketSlashCommand command)
{
    // Discord 要求 3 秒内 ACK
    await command.DeferAsync();

    var interaction = new Interaction(
        command.Id.ToString(),
        InteractionKind.Slash,
        MapUser(command.User),
        MapConversation(command.Channel),
        DateTimeOffset.UtcNow,
        Command: command.CommandName,
        Options: MapOptions(command.Data.Options),
        Values: [],
        ReplyToken: command.Token);

    _events.OnNext(new InteractionCreatedEvent
    {
        Id = ...,
        Source = Source,
        Sequence = Interlocked.Increment(ref _sequence),
        OccurredAt = DateTimeOffset.UtcNow,
        ReceivedAt = DateTimeOffset.UtcNow,
        Payload = new InteractionPayload(interaction),
    });
}
```

注意：

- `DeferAsync` 必须在发布事件前或与发布同步执行；
- `ReplyToken` 不日志、不持久化，内存 TTL 建议 15 分钟；
- Discord interaction token 过期后返回 `not_found`/`timeout`；
- 全局命令传播慢，开发期用 `devGuildId` 注册 guild command。

### 8.5 编辑/删除

- `MessageUpdated` → `MessageUpdatedEvent`（完整快照；拿不到完整快照就跳过或从缓存合并）；
- `MessageDeleted` → `MessageDeletedEvent`；
- `ChatOrchestrator.ProcessEditedMessages` 建议设为 `false`，避免 AI 对每次编辑重复回复。

---

## 9. Slash 的 Core 前置改动

当前 `ChatOrchestrator` 的 `HandleAsync` 只提取：

```csharp
MessageCreatedEvent
MessageUpdatedEvent (when ProcessEditedMessages)
```

`InteractionCreatedEvent` 会被忽略。Discord slash 可用前必须补：

```csharp
if (chatEvent is InteractionCreatedEvent interaction)
{
    var command = await _responder.RespondAsync(interaction, cancellationToken);
    if (command is not null)
        await _dispatcher.ExecuteAsync(command, cancellationToken);
    return;
}
```

或者引入专门的 `IChatInteractionResponder`，但 v1 用 `IChatResponder` 已经足够。

`ChatCommandFactory.Respond` 会使用 `Interaction.ReplyToken` 生成 `InteractionRespondCommand`。
在 core 路由补好之前，Discord 插件只能交付消息/@mention 路径，不要宣称 slash 已完成。

---

## 10. 出站命令映射

| NLMP command | Discord 动作 |
| --- | --- |
| `message.send` | 解析 channel，分片，`SendMessageAsync` |
| `message.update` | `IMessage.ModifyAsync`，只编辑 bot 自己的消息 |
| `message.delete` | 删除 bot 自己的消息 |
| `typing.start` | `TriggerTypingAsync` |
| `interaction.respond` | 用保存的 `SocketInteraction` token 做 `FollowupAsync` / `ModifyOriginalResponseAsync` |

规则：

- `message.send` 必须带 `IdempotencyKey`；Discord 插件内部仍要去重，防止重试重复发送。
- `MessageReference` 设置 reply；`failIfNotExists: false`。
- 默认 `AllowedMentions.None`；只对 NLMP 明确声明的 `MentionKind.User` 放行。
- 长文本按 `Capabilities.MaxMessageLength`（建议 1900）分片；插件再做防御性切分。
- 附件 v1 可选：
  - 若实现，下载 URL 后上传 `FileAttachment`；
  - 若未实现，`Capabilities.Attachments=false`，由 NLMP 降级为 URL 文本；
  - 下载必须复用 `ChatAttachmentGuard`，限制大小/MIME/私网/重定向。
- `message.update` 只编辑 bot 自己的消息；找不到消息 → `not_found`。
- 429 映射 `rate_limited` + `retryAfterMs`；403 → `forbidden`；404 → `not_found`；内容非法 → `invalid_request`。
- `StreamEdit` 先设 `false`；高频编辑容易触发限流，Phase 3 再加节流。

---

## 11. Capabilities

```csharp
new Capabilities
{
    ConversationKinds =
    [
        ConversationKind.Direct,
        ConversationKind.Group,
        ConversationKind.Guild,
        ConversationKind.Thread,
    ],
    MaxMessageLength = 1900,
    Markdown = true,
    Attachments = true,       // 只有在 v1 真正实现上传时
    Mentions = true,
    Reply = true,
    Edit = true,
    StreamEdit = false,
    Delete = true,
    Typing = true,
    SlashCommands = true,     // 依赖 core interaction 路由
    Ephemeral = true,
    Buttons = false,
    Modals = false,
    MaxAttachmentBytes = 25 * 1024 * 1024,
};
```

能力按真实实现声明：

- 没实现附件上传就 `Attachments=false`；
- 没补 interaction 路由就 `SlashCommands=false`；
- 没实现按钮就 `Buttons=false`；
- 不要用 capability 假装功能存在。

---

## 12. 权限与安全

- `DISCORD_BOT_TOKEN` 只从 `IPluginSecrets` 读取；manifest/config/日志不出现值。
- `network` 用于 Gateway/HTTP；`secrets:DISCORD_BOT_TOKEN` 用于 token；`chat.*` 用于 contribution 与命令；`cli.commands` 用于状态命令。
- `chat.upstream` 当前 NPS 至少要求 `chat.read` + `chat.send`；`chat.edit` / `chat.delete` / `chat.commands` / `chat.interactions` 是功能需要，必须显式申请。
- guild/channel/user allowlist；忽略 bot/self。
- 默认 `AllowedMentions.None`；禁止 AI 输出触发 `@everyone`/`@here`。
- interaction token 是短期秘密：不日志、不持久化、不进 AI prompt。
- 只允许编辑/删除 bot 自己的消息。
- 插件无法访问宿主根 DI；只使用插件 provider 和宿主公开契约。
- NPS permissions 是治理声明，不是进程内安全沙箱；Discord 插件是可信第一方插件。

---

### 12.1 bot 自发消息与三层过滤

真实 Discord 测试确认：bot 会收到自己发送的 `MESSAGE_CREATE`。若 `ignoreSelf=false`，AI 回复可能被当成新输入，形成自问自答。默认安全行为是三层过滤：

1. `DiscordOptions.IgnoreBots = true`：过滤所有 bot（包括自己）消息；
2. `DiscordOptions.IgnoreSelf = true`：按 `Source.SelfId` 过滤自己；
3. `ChatOrchestrationOptions.IgnoreSelfMessages = true`：即使上游放行，orchestrator 也不把 self 消息交给 responder。

配置 schema 保留 `ignoreBots` / `ignoreSelf` 开关，但默认 true，并标注关闭风险。守护测试覆盖上游 self 过滤与 orchestrator self 过滤。

## 13. 诊断

`discord status` 应显示：

- `Source`（protocol/platform/selfId）；
- 连接状态；
- guild 数/缓存 channel 数；
- gateway latency（如可用）；
- 最近错误（脱敏）；
- slash command 注册状态；
- 当前 capabilities；
- token 缺失/权限拒绝时给出明确错误，不显示 token。

`plugins list --detail` 应显示：

- `natter.transport.discord` 的 manifest/state/trust/permissions/contributions；
- `chat.upstream` 是否 active；
- 失败阶段（manifest/permission/config/activate/upstream-connect）。

---

## 14. 测试

### 14.1 无真实 Discord

`FakeDiscordTransport` 产生消息/交互事件，记录发送命令；CI 不连接真实网关。

覆盖：

- DM/Guild/Thread/Group DM 映射；
- bot/self 过滤、allowlist、mention 规则；
- 空内容/附件-only；
- 回复引用；
- 长文本分片；
- `AllowedMentions.None`；
- `/ask` defer + followup；
- interaction token TTL；
- 429/403/404 错误映射；
- `selfId` 不匹配；
- token 缺失 / 权限拒绝；
- NPS activation 后自动 `ConnectAsync`、停止时自动 `DisconnectAsync`；
- `discord status` JSON。

### 14.2 手动集成

- 测试 guild + 测试 bot；
- 开启/关闭 Message Content Intent；
- 注册 guild slash command；
- 验证真实网关重连、429、权限错误；
- 不在 CI 中使用生产 token。

---

## 15. 实施顺序

### Phase 1：消息上游最小闭环（已交付）

- ✅ manifest/config/schema；
- ✅ `selfId` 与 token；
- ✅ `IDiscordTransport` + `DiscordSocketTransport`；
- ✅ `MessageReceived` → NLMP；
- ✅ `message.send` / `typing.start`；
- ✅ guild/channel allowlist、bot/self 过滤；
- ✅ `discord status`；
- 附件上传仍未实现；编辑/删除在 Phase 1 之后一并交付。

### Phase 2：slash / interaction（核心已交付）

- ✅ 补 `ChatOrchestrator` 的 `InteractionCreatedEvent` 路由；
- ✅ slash defer + followup；
- ✅ interaction token 缓存与 TTL；
- ✅ `chat.commands` / `chat.interactions` 权限声明；
- ✅ `Capabilities.SlashCommands` 由配置控制；
- ✅ 配置驱动的 guild/global 命令注册（`commands` + `devGuildId`）；按钮/Modal 待补。

### Phase 3：富内容

- 附件上传；
- 编辑/删除；
- 分片节流与 stream edit；
- 按钮/Modal；
- 多 guild、多 bot、sharding。

### Phase 4：AI feature

- 独立 `chat.responder` 插件；
- 会话存储、prompt、工具；
- Discord 插件不变。

---

## 16. 验收标准

- [x] NPS 能加载 `natter.transport.discord`，manifest/权限/config/secret 校验通过（manifest/config 测试）；
- [x] 插件 `ActivateAsync` 不进行网络连接；
- [x] NPS 自动调用 `ConnectAsync`，`discord status` 显示 connected（fake transport）；
- [x] 消息/@mention/DM/Group DM/Thread 正确映射为 NLMP；
- [x] `message.send`/`typing.start` 通过 fake transport 验证；
- [x] `InteractionCreatedEvent` core 路由、transport defer/followup、配置驱动命令注册已实现；真实命令注册待手动验证；
- [x] bot/self/allowlist/mention 过滤正确；
- [x] `AllowedMentions.None` / 显式 user id 输入生效；
- [x] 429/403/404 映射为 NLMP 错误；
- [x] 停止时自动 `DisconnectAsync`，无残留订阅；
- [x] 没有 AI 逻辑进入 Discord 插件；
- [x] 所有 CI 测试不依赖真实 Discord。
- [ ] 真实 Discord guild + 真实 token 手动集成（不属于 CI）。

---

## 17. 开放问题

- Discord.Net 版本与 .NET 10 兼容矩阵；
- 多 bot/多 token 管理；
- sharding 阈值；
- 全局命令 vs guild command 的生产策略；
- 附件上传的 CDN/大小限制；
- 按钮/Modal/Select 的完整 interaction 模型；
- Stream edit 节流策略；
- Android dynamic Discord 插件；
- Discord 消息缓存与删除事件的一致性；
- 与 Satori/Slack 共用 transport 抽象的边界。

---

## 18. 本仓库实现

### 18.1 代码映射

| 设计章节 | 实现 |
| --- | --- |
| §3 工程结构 | `src/Natter.Chat.Discord`（库）+ `samples/Natter.Sample.Plugin.Discord`（NPS 插件） |
| §4 Manifest | `samples/.../plugin.json`：`kind=transport`、`chat.upstream/discord`、`cli.command/discord.status` |
| §5 配置 | `DiscordOptions` + `config.schema.json`；token 只允许 `${secret:DISCORD_BOT_TOKEN}` |
| §6 插件入口 | `DiscordPlugin.ConfigureAsync` 只注册服务/contribution；`ActivateAsync` 不联网 |
| §7 传输边界 | `IDiscordTransport`、`DiscordSocketTransport`（Discord.Net）、`DiscordFakeTransport`（测试） |
| §8 入站映射 | `DiscordEventMapper`：DM/Group/Guild/Thread、mention、attachment、reply、edit/delete、slash |
| §9 slash 前置 | `ChatOrchestrator` 新增 `InteractionCreatedEvent` 分支 |
| §10 出站命令 | `DiscordChatUpstream.ExecuteAsync`：send/update/delete/typing/interaction |
| §11 Capabilities | `DiscordCapabilities.Create`：1900 字、Markdown、Mentions、Edit/Delete、Slash，Attachments/Buttons/Modals=false |
| §12 安全 | token 仅 secret；bot/self/allowlist；默认 `AllowedMentions.None`，仅显式 user id；只编辑/删除 bot 自己的消息 |
| §13 诊断 | `discord status [--json]`（`DiscordStatusCommandHandler`）+ `plugins list --detail` |
| §14 测试 | `DiscordTransportTests`、`DiscordNpsTests`、`DiscordConfigTests` |

### 18.2 已验证

- fake transport 覆盖 DM/Group/Guild/Thread、bot/self/allowlist/mention、空内容/附件、reply、分片、`AllowedMentions` 输入、slash defer/followup 语义、429/403/404、selfId 不匹配、NPS 自动 Connect/Disconnect；
- `ChatOrchestrator` interaction 路由测试；
- NPS 插件 manifest/config/schema 与 Discord 权限测试；
- 示例插件经 NPS 加载、secret/config 校验、fake transport 自动 Connect/Disconnect、`discord status` 命令；
- `DiscordSocketTransport` 编译通过；配置驱动 slash 命令注册已实现；真实网关/真实 token 未在 CI 使用。

### 18.3 未交付

- 按钮/Modal/Select；
- 附件上传、按钮/Modal/Select、stream edit；
- 真实 Discord guild 的手动集成矩阵（Message Content Intent、429、重连、权限错误）；
- 多 bot/sharding、跨进程。

## 19. 一句话结论

**Discord 插件只做 transport：`chat.upstream` + `IDiscordTransport` + NLMP 映射 + 能力声明。**
**先补 `InteractionCreatedEvent` 路由，再谈 slash；先保证 `selfId`、权限、secret 和 NPS 生命周期，再谈 AI。**
