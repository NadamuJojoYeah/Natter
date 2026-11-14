# Natter Lightweight Message Protocol (NLMP) v0.1

> **状态**：Draft · v0.1 已实现（模型 / 事件 / 命令 / 能力 / Satori 映射 / 诊断）  
> **更新时间**：2026-11-14  
> **定位**：Natter 的轻量聊天语义模型 + 可选 JSON 投影；不是平台 wire protocol，也不是 Satori 的替代实现。  
> **实现**：`src/Natter.Chat`、`src/Natter.Chat.Satori`、`src/Natter.Infrastructure/Chat`、`src/Natter.Application/Chat`；映射与测试见 §16。  
> **参考**：[Satori Protocol v1](https://satori.chat/en-US/protocol/)、[Satori Message API](https://satori.chat/en-US/resources/message.html)、[Satori Message Encoding](https://satori.chat/en-US/protocol/message.html)

---

## 0. 摘要

NLMP 定义 Natter 在多聊天平台之间传递消息所需的最小模型：

- 消息从哪个 **协议 / 平台 / Bot 账号** 来；
- 属于哪个 **会话（频道 / 群 / 私聊 / 线程）**；
- 谁发的、发了什么、回复谁；
- 上游支持哪些 **能力**；
- 如何 **发送 / 编辑 / 删除 / 响应交互**；
- 如何表达 **事件** 与 **命令结果**。

它不是“另一套 Satori”：

| | Satori | NLMP |
| --- | --- | --- |
| 目标 | 跨平台聊天 wire protocol | AI harness 的会话语义模型 |
| 覆盖 | 完整平台资源、事件、API | 只覆盖 AI 对话需要的字段 |
| 传输 | WebSocket + HTTP + WebHook | 默认进程内接口；JSON 只是可选投影 |
| 版本 | Satori v1 | NLMP 0.1（独立演进） |
| 平台 API | 完整 Guild/Member/Role/Interaction 等 | 不复制；通过能力扩展或原生适配器访问 |

**设计结论**：

1. NLMP 是 **模型优先**，不是先造 wire protocol。
2. Natter 进程内插件不需要序列化、握手、版本协商；只需要接口和不可变数据。
3. 只有未来需要远程插件/独立适配器进程时，才启用 JSON binding。
4. Satori 是 NLMP 的一个 **adapter/provider**；Satori 的完整 DTO 只存在于适配器内部。
5. 如果某天 NLMP 与 Satori 完全同构且没有任何 AI 特化，应合并或直接采用 Satori 模型，而不是维护两套完整协议。

---

## 1. 范围

### 1.1 目标

- 用同一个模型接入 Discord、Satori、Slack、Telegram、OneBot、Matrix 等上游。
- 让 AI 编排器只关心“会话 + 消息 + 能力”，不关心平台 SDK。
- 支持消息的接收、发送、编辑、删除、回复、typing、交互响应。
- 支持多平台、多 Bot、多租户的会话隔离。
- 支持能力协商：功能插件在能力不足时降级，而不是猜测。
- 为未来的远程插件/适配器保留可选序列化能力。

### 1.2 非目标

- 不复制 Satori 的完整 Guild/Member/Role/Emoji/Reaction 资源图。
- 不定义平台 API 的完整 CRUD；需要时通过可选能力接口或原生扩展访问。
- 不覆盖语音、直播、Presence、富媒体编辑器、复杂 Embeds。
- v0.1 不定义完整的富文本元素树（见 §14 开放问题）。
- v0.1 不定义远程插件 transport；只定义模型与可选 JSON 投影。
- 不承载 token、密钥、平台原始凭据。

### 1.3 与 Satori 的关系

```text
Satori server / adapters
        │ Satori WS + HTTP
Satori C# client（wire DTO / 重连 / 认证）
        │ ACL 映射
NLMP（Natter Chat Model）
        │
AI orchestrator + feature plugins + native adapters
```

- Satori 的 `Session`、`Message`、`Channel`、`Guild`、`Login` 等类型 **不进入** NLMP 核心。
- Satori 的 `message-created`、`message-updated`、`message-deleted`、interaction 事件映射为 NLMP 事件。
- Satori 的 `message.create/update/delete` 映射为 NLMP 命令。
- Satori 的 `login.features` 映射为 NLMP capabilities。

---

## 2. 设计原则

1. **窄**：只保留 AI 会话需要的字段；平台扩展放到 `extensions`。
2. **ID 不透明**：所有平台 ID 都是字符串，不假设是数字、不解析、不排序。
3. **Source 作用域**：`conversationId`、`messageId` 只在同一 `source` 内唯一。
4. **能力驱动**：不要假设所有上游都支持编辑、线程、按钮、Markdown。
5. **文本优先**：核心内容是可读文本 + 附件 + mentions；富文本元素不进 v0.1 核心。
6. **有损是显式的**：平台专属信息放入命名空间化的 `extensions`，功能插件不得强依赖。
7. **事件不可变**：事件是已发生事实；修正通过新的 `message.updated` / `message.deleted` 表达。
8. **命令幂等**：发送命令带 `idempotencyKey`，适配器负责去重。
9. **版本化**：模型版本独立于 Satori 版本和平台版本。
10. **无秘密**：token、密钥、原始凭据不属于任何 NLMP 对象。

本规范中的 **MUST / SHOULD / MAY** 按 RFC 2119 语义理解。

---

## 3. 术语

| 术语 | 含义 |
| --- | --- |
| Protocol | 接入协议/适配器，如 `satori`、`discord`、`onebot` |
| Platform | 底层聊天平台，如 `discord`、`telegram`、`slack`、`qq` |
| Source | `(protocol, platform, selfId)` 三元组，定位一个 Bot 账号接入 |
| Conversation | 消息会话：私聊、群、Guild 频道、线程 |
| Message | 一条可被引用、编辑、删除的消息 |
| Event | 上游推送的事实：消息创建/更新/删除、交互等 |
| Command | Natter 发给上游的操作：发送、编辑、删除、typing、交互响应 |
| Capability | 上游/会话支持的功能集合 |
| Adapter | 把平台协议翻译成 NLMP 的插件/组件 |
| Orchestrator | 把 NLMP 消息接到 AI、记忆、工具的 Application 层 |

---

## 4. 标识与作用域

### 4.1 Source

```json
{
  "protocol": "satori",
  "platform": "discord",
  "selfId": "1234567890"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `protocol` | string | 是 | 接入协议/适配器 ID |
| `platform` | string | 是 | 底层平台 ID |
| `selfId` | string | 是 | 该平台上的 Bot 账号 ID |

规则：

- 三个字段 MUST 非空。
- `sourceKey = protocol + ":" + platform + ":" + selfId`，仅用于日志、诊断和错误信息中的可读标识。
- 原生 Discord 适配器可用 `protocol = "discord"`、`platform = "discord"`。
- Satori 适配器用 `protocol = "satori"`、`platform = "discord"`、`selfId` 为 Satori `login.user.id`。
- 同一个 Satori 连接包含多个平台/多个 Bot 时，必须产生多个 `source`，不能只用一个 `"satori"` 身份。
- `sourceKey` 仅用于日志/显示；存储、路由和去重 MUST 使用结构化字段，禁止反向解析 `sourceKey`。

### 4.2 Conversation

```json
{
  "id": "5555555555",
  "kind": "guild",
  "guildId": "4444444444",
  "title": "general"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 会话 ID，在 `source` 内唯一 |
| `kind` | enum | 是 | `direct` / `group` / `guild` / `thread` / `unknown` |
| `parentId` | string? | 否 | 线程父频道；`kind=thread` 时 SHOULD 提供 |
| `guildId` | string? | 否 | Guild/服务器 ID |
| `title` | string? | 否 | 会话标题，仅用于展示，不用于路由 |

`kind` 语义：

- `direct`：一对一私聊；
- `group`：无 Guild 层级的多人群聊（QQ 群、部分 Telegram 群等）；
- `guild`：Guild/服务器下的频道；
- `thread`：线程/子频道，`parentId` 指向父频道；
- `unknown`：适配器无法判断时的保底值。

### 4.3 复合键

```text
ConversationKey = (source, conversation.id)
MessageKey      = (source, conversation.id, message.id)
InteractionKey  = (source, interaction.id)
```

规则：

- `conversation.id` 不得跨 `source` 复用。
- `message.id` 在同一会话内唯一；跨会话不保证唯一。
- 所有持久化/缓存/去重 key MUST 包含 `source`（以及线程 ID，如果线程独立）。
- Satori 的 `Satori-Platform` 和 `Satori-User-ID` 请求头直接由 `source` 推导，见 §9.3。

---

## 5. 数据模型

### 5.1 编码约定

- JSON 字段使用 **lowerCamelCase**。
- 时间使用 **RFC 3339 UTC** 字符串，例如 `2026-11-14T14:00:00Z`。
- 所有 ID 使用 **string**。
- 可选字段缺失或为 `null` 等价；生产者 SHOULD 省略未知字段。
- 消费者 MUST 忽略未知字段、未知枚举值、未知事件类型。
- 金额、长度、大小使用整数；时间戳不使用 Unix 秒（adapter 在边界转换）。
- `extensions` 的 key MUST 使用命名空间，例如 `satori`、`discord`、`natter.trace`。

### 5.2 User

```json
{
  "id": "3333333333",
  "name": "yg",
  "displayName": "YG",
  "kind": "human",
  "avatarUrl": "https://cdn.example/avatar.png"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 平台用户 ID |
| `name` | string | 是 | 稳定 handle；未知时用 `id` 兜底 |
| `displayName` | string? | 否 | 昵称 |
| `kind` | enum | 是 | `human` / `bot` / `system` / `unknown` |
| `avatarUrl` | string? | 否 | 头像 URL |

规则：

- `kind` MUST 由适配器尽量准确设置；无法判断时用 `unknown`，消费者不得默认当作 `human`。
- 功能插件默认忽略 `bot` / `system` 消息，除非显式配置。
- v0.1 不包含 roles、permissions、joinedAt；需要时通过能力接口或 `extensions`。

### 5.3 Attachment

```json
{
  "id": "att-1",
  "url": "https://cdn.example/image.png",
  "fileName": "image.png",
  "mimeType": "image/png",
  "size": 12345,
  "kind": "image",
  "width": 800,
  "height": 600,
  "expiresAt": "2026-11-15T00:00:00Z"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string? | 否 | 平台附件 ID |
| `url` | string | 是 | 绝对 `http(s)` URL |
| `fileName` | string? | 否 | 文件名 |
| `mimeType` | string? | 否 | MIME 类型 |
| `size` | integer? | 否 | 字节数 |
| `kind` | enum | 否 | `image` / `audio` / `video` / `file` / `unknown` |
| `width` / `height` | integer? | 否 | 像素尺寸 |
| `expiresAt` | string? | 否 | CDN URL 过期时间 |

规则：

- v0.1 不允许 base64 或本地路径进入模型。
- 适配器下载附件前 MUST 校验 URL scheme、大小、MIME，并考虑 SSRF。
- 出站附件 v0.1 仅支持 URL；本地文件上传属于适配器扩展能力。
- `url` 可能短期有效；需要长期保存时由 Natter 存储层负责复制/转存（另行设计）。

### 5.4 Mention

```json
{
  "kind": "user",
  "id": "1234567890",
  "name": "Natter"
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `kind` | enum | 是 | `user` / `role` / `channel` / `everyone` / `here` |
| `id` | string? | 条件 | `user` / `role` / `channel` 时需要 |
| `name` | string? | 否 | 展示名 |

规则：

- `text` 中的 mention 占位符使用 `@name` 或 `@id`，不使用平台私有标记。
- `everyone` / `here` 是高风险 mention；默认禁止出站，必须经过策略显式允许。
- mentions 数组是触发/权限判断的权威来源；不要只靠文本解析。

### 5.5 Content

```json
{
  "text": "hello @Natter",
  "format": "plain",
  "attachments": [],
  "mentions": [
    { "kind": "user", "id": "1234567890", "name": "Natter" }
  ]
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `text` | string | 是 | 归一化可读文本，可为空 |
| `format` | enum | 是 | `plain` / `markdown` |
| `attachments` | array | 否 | 附件 |
| `mentions` | array | 否 | 语义 mention |

规则：

- `text` 与 `attachments` 至少有一个非空。
- 入站内容默认 `format = "plain"`；即使平台支持 Markdown，也由适配器决定是否保留。
- 出站 `format = "markdown"` 只能在 `capabilities.markdown = true` 时使用；否则适配器 MUST 降级为纯文本。
- v0.1 **不包含** Satori message elements、按钮树、Embeds、组件树。富文本/交互内容通过能力扩展或后续版本引入。
- 文本长度不设全局上限；由 `capabilities.maxMessageLength` 和适配器分片决定。

### 5.6 Message

```json
{
  "id": "9876543210",
  "conversation": {
    "id": "5555555555",
    "kind": "guild",
    "guildId": "4444444444",
    "title": "general"
  },
  "sender": {
    "id": "3333333333",
    "name": "yg",
    "displayName": "YG",
    "kind": "human"
  },
  "content": {
    "text": "hello @Natter",
    "format": "plain",
    "attachments": [],
    "mentions": [
      { "kind": "user", "id": "1234567890", "name": "Natter" }
    ]
  },
  "replyTo": null,
  "createdAt": "2026-11-14T14:00:00Z",
  "editedAt": null,
  "extensions": {}
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 消息 ID，会话内唯一 |
| `conversation` | Conversation | 是 | 所属会话 |
| `sender` | User | 是 | 发送者；系统消息用 `kind=system` |
| `content` | Content | 是 | 内容 |
| `replyTo` | string? | 否 | 同一会话内被回复的消息 ID |
| `createdAt` | string | 是 | 平台创建时间；缺失时用接收时间 |
| `editedAt` | string? | 否 | 最后编辑时间 |
| `extensions` | object? | 否 | 命名空间扩展 |

规则：

- `message.id` 在同一 `(source, conversation.id)` 内唯一。
- `replyTo` 只表达引用关系；被引用内容预览放在 `extensions`，v0.1 不要求。
- 消息不可变；编辑通过 `message.updated` 表达，删除通过 `message.deleted` 表达。
- `sender.kind=bot/system` 的消息可以进入事件流，但 AI 编排器默认不处理；由策略决定。
- 历史存储键必须使用 `(source, conversation.id, message.id)`。

### 5.7 Interaction

Interaction 用于斜杠命令、按钮、选择器、Modal 等。v0.1 只定义最小可用字段。

```json
{
  "id": "int-123",
  "kind": "slash",
  "command": "ask",
  "options": { "prompt": "你好" },
  "values": [],
  "user": {
    "id": "3333333333",
    "name": "yg",
    "kind": "human"
  },
  "conversation": {
    "id": "5555555555",
    "kind": "guild"
  },
  "replyToken": "opaque-token",
  "createdAt": "2026-11-14T14:00:00Z",
  "extensions": {}
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `id` | string | 是 | 交互 ID |
| `kind` | enum | 是 | `slash` / `button` / `select` / `modal` / `unknown` |
| `command` | string? | 否 | 归一化命令名，不带 `/` 前缀，如 `ask` |
| `options` | object? | 否 | 命令参数，值可为 string/number/boolean |
| `values` | array? | 否 | 选择器/多选值 |
| `user` | User | 是 | 触发者 |
| `conversation` | Conversation | 是 | 触发会话 |
| `replyToken` | string? | 否 | 适配器私有响应令牌，消费者不得解析 |
| `createdAt` | string | 是 | 触发时间 |

规则：

- `replyToken` 是不透明值，只允许适配器使用；不得写入日志、持久化或 AI prompt。
- 交互响应通过 `interaction.respond` 命令完成，见 §7.2。
- v0.1 不定义 Modal 表单结构；`extensions` 可携带。
- Discord 斜杠命令的 3 秒 ACK 由适配器处理，应用层不应依赖平台计时器。

### 5.8 Capabilities

Capabilities 描述某个 `(source, conversation?)` 支持什么。建议按 `source` 缓存，必要时按会话细化。

```json
{
  "conversationKinds": ["direct", "group", "guild", "thread"],
  "maxMessageLength": 2000,
  "markdown": false,
  "attachments": true,
  "mentions": true,
  "reply": true,
  "edit": true,
  "streamEdit": false,
  "delete": true,
  "typing": true,
  "reactions": false,
  "ephemeral": false,
  "slashCommands": true,
  "buttons": false,
  "modals": false,
  "maxAttachmentBytes": 8388608,
  "messagesPerSecond": 5
}
```

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `conversationKinds` | array | 支持的会话类型 |
| `maxMessageLength` | integer? | 单条消息最大字符数；未知则省略 |
| `markdown` | boolean | 是否保证 Markdown 渲染 |
| `attachments` | boolean | 是否支持附件 |
| `mentions` | boolean | 是否支持 mention |
| `reply` | boolean | 是否支持引用回复 |
| `edit` | boolean | 是否支持消息编辑 |
| `streamEdit` | boolean | 是否支持高频编辑/流式更新 |
| `delete` | boolean | 是否支持删除 |
| `typing` | boolean | 是否支持 typing |
| `reactions` | boolean | 是否支持表情回应 |
| `ephemeral` | boolean | 是否支持仅发送者可见的消息 |
| `slashCommands` | boolean | 是否支持斜杠命令 |
| `buttons` | boolean | 是否支持按钮 |
| `modals` | boolean | 是否支持 Modal |
| `maxAttachmentBytes` | integer? | 单附件最大字节数 |
| `messagesPerSecond` | number? | 建议发送速率 |

规则：

- 缺失布尔字段视为 **未知/不支持**；功能插件不得假设为 true。
- `maxMessageLength` 缺失时适配器负责分片；功能插件不应硬编码 2000。
- `streamEdit=true` 隐含 `edit=true`。
- Capabilities 可以随登录/平台会话变化；适配器负责缓存与失效。
- Satori 的 `login.features` 映射见 §9.5；没有 feature 的能力不得假设支持。

---

## 6. 事件模型

### 6.1 Event Envelope

```json
{
  "version": "0.1",
  "id": "evt:satori:discord:1234567890:42",
  "type": "message.created",
  "source": {
    "protocol": "satori",
    "platform": "discord",
    "selfId": "1234567890"
  },
  "sequence": 42,
  "occurredAt": "2026-11-14T14:00:00Z",
  "receivedAt": "2026-11-14T14:00:00.120Z",
  "payload": {
    "message": {
      "id": "9876543210",
      "conversation": {
        "id": "5555555555",
        "kind": "guild",
        "guildId": "4444444444",
        "title": "general"
      },
      "sender": {
        "id": "3333333333",
        "name": "yg",
        "displayName": "YG",
        "kind": "human"
      },
      "content": {
        "text": "hello @Natter",
        "format": "plain",
        "attachments": [],
        "mentions": [
          { "kind": "user", "id": "1234567890", "name": "Natter" }
        ]
      },
      "replyTo": null,
      "createdAt": "2026-11-14T14:00:00Z"
    }
  },
  "extensions": {
    "satori": { "sn": 42 }
  }
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `version` | string | 是（JSON binding） | NLMP 模型版本，如 `0.1` |
| `id` | string | 是 | 事件唯一 ID，用于去重 |
| `type` | string | 是 | 事件类型，点分命名 |
| `source` | Source | 是 | 事件来源 |
| `sequence` | integer? | 否 | 同一 source 内单调递增；Satori `sn` 可映射 |
| `occurredAt` | string | 是 | 平台事件时间 |
| `receivedAt` | string | 是 | 适配器接收时间 |
| `payload` | object | 是 | 事件负载 |
| `extensions` | object? | 否 | 命名空间扩展 |

规则：

- `id` MUST 在合理去重窗口内唯一；Satori 无事件 ID 时可用 `(source, sn)` 派生。
- `sequence` 只保证同一 source 内有序；不得跨 source 比较。
- 事件可能重复（Satori session recovery、重连），消费者 MUST 按 `id` 或 `(source, messageId)` 去重。
- 未知 `type` MUST 被忽略并记录，不得导致连接中断。
- 事件是事实，不是命令；不要用事件反向驱动平台 API。

### 6.2 事件类型 v0.1

| type | 必需 | 说明 |
| --- | --- | --- |
| `message.created` | 是 | 新消息 |
| `message.updated` | 是 | 消息编辑/流式更新 |
| `message.deleted` | 是 | 消息删除 |
| `interaction.created` | 否 | 斜杠/按钮/选择器/Modal |
| `upstream.status` | 否 | 连接状态；可只用于诊断 |

v0.1 不要求 member join/leave、reaction、typing、presence 事件。需要时作为可选事件新增，消费者必须忽略未知类型。

### 6.3 消息事件负载

#### message.created

```json
{
  "message": { }
}
```

`message` 为 §5.6 的完整对象。

#### message.updated

```json
{
  "message": { }
}
```

- 必须携带完整消息快照；消费者按 `message.id` 替换。
- `message.editedAt` SHOULD 更新。
- 流式输出通过重复 `message.updated` 实现；是否可行取决于 `capabilities.streamEdit`。
- v0.1 不定义 delta/patch 事件；需要时在 v0.2 增加。

#### message.deleted

```json
{
  "conversation": {
    "id": "5555555555",
    "kind": "guild"
  },
  "messageId": "9876543210",
  "deletedAt": "2026-11-14T14:01:00Z"
}
```

### 6.4 interaction.created

```json
{
  "interaction": {
    "id": "int-123",
    "kind": "slash",
    "command": "ask",
    "options": { "prompt": "你好" },
    "values": [],
    "user": {
      "id": "3333333333",
      "name": "yg",
      "kind": "human"
    },
    "conversation": {
      "id": "5555555555",
      "kind": "guild"
    },
    "replyToken": "opaque-token",
    "createdAt": "2026-11-14T14:00:00Z"
  }
}
```

### 6.5 upstream.status（可选）

```json
{
  "state": "ready",
  "detail": null
}
```

`state`：`ready` / `disconnected` / `error`。  
该事件只用于诊断/健康检查，不参与消息处理；v0.1 不要求所有适配器实现。

### 6.6 顺序、去重与重放

- `sequence` 可选；没有时消费者不得依赖排序。
- Satori 的 `sn` 是恢复会话的关键；适配器 MUST 持久化最后处理的 `sn`，重连时从 `sn` 恢复（如果 Satori 支持）。
- 会话恢复会重放断线期间的事件；适配器/消费者 MUST 按事件 ID 去重。
- 事件处理必须是幂等的：`message.created` 重放不应触发两次 AI 回复。
- 推荐去重 key：`(source, conversation.id, message.id, "created")`。
- 推荐去重窗口：至少覆盖 Satori 重连恢复窗口；默认 10 分钟，可配置。

---

## 7. 命令模型

命令由 Natter 发往上游。进程内表现为方法调用；JSON binding 使用命令 envelope。

### 7.1 Command Envelope

```json
{
  "version": "0.1",
  "id": "cmd:run-123:chunk-0",
  "type": "message.send",
  "source": {
    "protocol": "satori",
    "platform": "discord",
    "selfId": "1234567890"
  },
  "issuedAt": "2026-11-14T14:00:01Z",
  "idempotencyKey": "run-123:reply:chunk-0",
  "payload": { },
  "extensions": {}
}
```

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `version` | string | 是（JSON） | NLMP 版本 |
| `id` | string | 是 | 命令 ID |
| `type` | string | 是 | 命令类型 |
| `source` | Source | 是 | 目标 Bot 账号 |
| `issuedAt` | string | 是 | 发出时间 |
| `idempotencyKey` | string? | 发送时必填 | 去重键 |
| `payload` | object | 是 | 命令参数 |
| `extensions` | object? | 否 | 扩展 |

### 7.2 命令类型 v0.1

#### message.send

```json
{
  "conversation": {
    "id": "5555555555",
    "kind": "guild"
  },
  "content": {
    "text": "你好，我是 Natter。",
    "format": "plain",
    "attachments": [],
    "mentions": []
  },
  "replyTo": "9876543210",
  "ephemeral": false
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `conversation` | 是 | 目标会话；至少包含 `id`/`kind` |
| `content` | 是 | 内容 |
| `replyTo` | 否 | 引用回复的消息 ID |
| `ephemeral` | 否 | 仅部分平台支持；默认 false |

#### message.update

```json
{
  "conversation": { "id": "5555555555", "kind": "guild" },
  "messageId": "9876543210",
  "content": {
    "text": "正在生成……",
    "format": "plain"
  }
}
```

#### message.delete

```json
{
  "conversation": { "id": "5555555555", "kind": "guild" },
  "messageId": "9876543210"
}
```

#### typing.start

```json
{
  "conversation": { "id": "5555555555", "kind": "guild" }
}
```

#### interaction.respond

```json
{
  "interactionId": "int-123",
  "replyToken": "opaque-token",
  "content": {
    "text": "你好",
    "format": "plain"
  },
  "ephemeral": false,
  "updateOriginal": false
}
```

### 7.3 Ack

```json
{
  "version": "0.1",
  "id": "ack:cmd:run-123:chunk-0",
  "replyTo": "cmd:run-123:chunk-0",
  "ok": true,
  "result": {
    "messageIds": ["9999999999"],
    "deliveredAt": "2026-11-14T14:00:01.100Z"
  },
  "error": null
}
```

规则：

- `message.send` 可能因为平台分片返回多个 `messageIds`。
- 若没有任何消息送达，`ok=false`。
- 部分分片失败时，适配器 SHOULD 返回 `ok=false` 并在 `error.details` 中说明已送达的 ID，避免上层盲目重试。
- `message.update/delete/typing.start/interaction.respond` 的 `result` 可为空对象。

### 7.4 Error

```json
{
  "version": "0.1",
  "id": "ack:cmd-1",
  "replyTo": "cmd-1",
  "ok": false,
  "result": null,
  "error": {
    "code": "rate_limited",
    "message": "Discord rate limit exceeded.",
    "retryable": true,
    "retryAfterMs": 1200,
    "details": { "route": "/channels/555/messages" }
  }
}
```

错误码 v0.1：

| code | 可重试 | 说明 |
| --- | --- | --- |
| `invalid_request` | 否 | 字段缺失/非法 |
| `unauthorized` | 否 | token/认证失败 |
| `forbidden` | 否 | 权限不足 |
| `not_found` | 否 | 会话/消息不存在 |
| `unsupported` | 否 | 平台/适配器不支持该能力 |
| `rate_limited` | 是 | 限流；看 `retryAfterMs` |
| `unavailable` | 是 | 上游暂时不可用 |
| `timeout` | 是 | 请求超时 |
| `conflict` | 否 | 资源状态冲突 |
| `internal` | 否 | 适配器内部错误 |

### 7.5 幂等与重试

- `message.send` MUST 提供 `idempotencyKey`；适配器 SHOULD 在窗口内（建议 ≥10 分钟）去重。
- 上层重试 MUST 复用同一个 `idempotencyKey`。
- `message.update/delete` 可以不带 key，但重试应保证语义幂等。
- 只有 `retryable=true` 才建议自动重试；`rate_limited` 遵守 `retryAfterMs`。
- 流式编辑应节流（如每 500–1000ms 一次），避免触发平台限流；`streamEdit=false` 时只发最终结果。

---

## 8. 能力协商与降级

### 8.1 查询

能力按 `(source, conversation?)` 查询：

```text
GetCapabilities(source) -> CapabilitySet
GetCapabilities(source, conversation) -> CapabilitySet  // 可选细化
```

### 8.2 降级规则

| 缺失能力 | 降级行为 |
| --- | --- |
| `markdown=false` | 转为纯文本（保留可读性，不保证格式） |
| `attachments=false` | 只发文本；附件以 URL 文本形式追加 |
| `reply=false` | 不带 `replyTo`，普通发送 |
| `edit=false` / `streamEdit=false` | 只发最终消息，不做中间更新 |
| `delete=false` | 不自动清理中间消息；记录 warning |
| `typing=false` | 跳过 typing |
| `maxMessageLength` 未知 | 适配器自行分片 |
| `slashCommands=false` | 不注册/不展示斜杠命令 |
| `buttons=false` | 不发送按钮，退化为文本选项 |

### 8.3 Satori `login.features`

Satori 的 `Login.features` 是能力发现的主要来源。适配器应按如下方式映射（缺失即不假设）：

| Satori feature | NLMP capability |
| --- | --- |
| `message.create` | 基础发送（隐含） |
| `message.update` | `edit = true` |
| `message.delete` | `delete = true` |
| `message.attachments`（如存在） | `attachments = true` |
| `guild.plain` | `conversationKinds` 调整 |
| `message.buttons`（如存在） | `buttons = true` |

注意：

- Satori 标准只标准化了 API 名称；不同适配器可能暴露不同 feature 字符串。
- 未知 feature 必须忽略，不得猜测。
- `message.update` 存在不代表适合高频流式更新；`streamEdit` 由适配器根据平台/自身策略决定。
- Satori 没有 Markdown 保证；`markdown` 默认 `false`，除非适配器明确转换。

---

## 9. Satori 映射

### 9.1 Source 与事件 envelope

| Satori | NLMP | 说明 |
| --- | --- | --- |
| `event.login.platform` | `source.platform` | 非登录事件 |
| `event.login.user.id` | `source.selfId` | Bot 账号 |
| 固定值 `satori` | `source.protocol` | 适配器 ID |
| `event.sn` | `sequence` | 会话恢复序号 |
| `event.timestamp` | `occurredAt` | Unix 秒 → RFC 3339 |
| 适配器时钟 | `receivedAt` | 接收时间 |
| 派生 `satori:{platform}:{selfId}:{sn}` | `id` | 事件去重 ID |

### 9.2 事件映射

| Satori event | NLMP event |
| --- | --- |
| `message-created` | `message.created` |
| `message-updated` | `message.updated` |
| `message-deleted` | `message.deleted` |
| `interaction/button` 等 interaction 事件 | `interaction.created` |
| `login-added` / `login-removed` | 更新 source 注册表；可发 `upstream.status` |
| 其他事件 | 忽略，或放入 `extensions` |

### 9.3 消息资源映射

| Satori | NLMP |
| --- | --- |
| `message.id` | `message.id` |
| `channel.id` | `conversation.id` |
| `channel.type` | `conversation.kind`；未知 → `unknown` |
| `guild.id` | `conversation.guildId` |
| `channel.name` | `conversation.title` |
| `user.id` | `sender.id` |
| `user.name` | `sender.name` / `displayName` |
| `user.is_bot` | `sender.kind = bot/human` |
| `message.content`（elements） | `content.text` + `attachments` + `mentions` |
| `message.created_at` | `message.createdAt` |
| `message.updated_at` | `message.editedAt` |

### 9.4 Message elements → Content

Satori 使用 XHTML-like message elements；NLMP v0.1 只保留文本/附件/mentions：

| Satori element | NLMP |
| --- | --- |
| 纯文本 | `content.text` |
| `<at id name type>` | `mentions[]`；文本占位 `@name` / `@id`；`type=all/here` → `everyone/here` |
| `<sharp id name>` | `mentions[]`（channel）；文本 `#name` |
| `<img src title>` | `attachments[]`，`kind=image` |
| `<audio>` / `<video>` / `<file>` | `attachments[]` 对应 kind |
| `<quote>` | 尽量映射 `replyTo`；引用预览放 `extensions` |
| `<a href>` | 文本 + URL（必要时追加 ` (href)`） |
| `<b>/<i>/<u>/<s>/<code>` | 去标签保留文本；`format=plain` |
| `<br>` / `<p>` | 换行 |
| `<emoji>` | 文本形式 `:name:` 或 name |
| `<message>` 多个消息元素 | 适配器可拆分多条 NLMP 消息；v0.1 默认一条 |
| `<button>` / 富交互 | 不进入 v0.1 核心；放 `extensions` 或后续能力 |

这是**有损映射**，是 v0.1 的明确取舍：AI 主要需要可读文本和附件，不需要完整元素树。

### 9.5 API 映射

| Satori API | NLMP command | 说明 |
| --- | --- | --- |
| `POST /v1/message.create` | `message.send` | 请求头 `Satori-Platform` / `Satori-User-ID` 由 `source` 生成 |
| `POST /v1/message.update` | `message.update` | 需要 `message.update` feature |
| `POST /v1/message.delete` | `message.delete` | 需要 `message.delete` feature |
| interaction 响应 API（如适用） | `interaction.respond` | 适配器负责 token/延迟响应 |

`message.create` 可能返回消息数组；适配器映射为 `ack.result.messageIds`。

### 9.6 WebSocket / 会话恢复

Satori WebSocket：

- 地址形如 `/{version}/events`，当前 `v1`。
- 连接后发送 `IDENTIFY`（`token` + 可选 `sn`），收到 `READY`。
- 每 10 秒发送 `PING`，等待 `PONG`。
- 事件通过 `EVENT` 推送。
- `sn` 用于断线恢复；SDK 会重放断线期间的事件。
- 登录事件不参与恢复；以 `READY.logins` 为准。

适配器要求：

- 持久化最后处理的 `sn`（或至少内存中保留，用于短重连）。
- 重放事件按 `id`/`messageId` 去重。
- `READY.logins` 更新 source/capabilities 缓存，是 `login.features` 的初始化来源。
- 重连采用指数退避，并有最大间隔。
- 连接状态通过日志/`upstream.status` 暴露，不影响消息处理主链路。

### 9.7 限制

- Satori 的 `message.content` 是元素字符串，NLMP v0.1 做有损文本化；富交互/按钮/复杂布局可能丢失。
- Satori 适配器对平台能力的支持不一致；必须依赖 `login.features`，不能假设。
- `message-updated` 事件若字段不完整，适配器应合并缓存快照；无法获得完整消息时可跳过该次更新，而不是发送残缺 Message。
- Satori 是外部协议；Satori C# 客户端应独立于 NLMP 版本，通过契约测试保证映射稳定。
- 平台原生能力（如 Discord Modal）如果 Satori 不支持，需要原生 adapter 或 `extensions` 旁路，不能硬塞进核心模型。

---

## 10. 传输绑定

### 10.1 进程内（默认）

- NLMP 以 C# 记录/接口形式存在，不序列化。
- `IChatUpstream` 负责连接与发送；`IChatMessageHandler`/事件流消费消息。
- 不要求 `version` 字段、JSON、握手。
- 这是 Natter 当前的主要形态。

### 10.2 可选 JSON binding

仅在未来需要“独立适配器进程/远程插件”时启用：

- UTF-8 JSON；lowerCamelCase；RFC 3339；ID 字符串。
- WebSocket 文本帧或 HTTP POST，一次一个 envelope。
- 每个 envelope 必须带 `version`。
- 认证、TLS、重连、背压属于 transport，不属于 NLMP 模型。
- 未知字段忽略；未知事件类型忽略；`version` 主版本不匹配则拒绝。
- 这不是当前必须实现的 wire protocol；先不要为它写握手/路由/服务发现。

### 10.3 Satori binding

Satori 是独立 binding：

- Satori adapter 内部使用 Satori wire DTO；
- 对外只产生/消费 NLMP；
- NLMP 不依赖 Satori 类型；
- 反之，原生 Discord adapter 也不依赖 Satori。

---

## 11. 版本与兼容

- 当前模型版本：`0.1`（Draft）。
- 版本格式：`major.minor`。
- **minor**：新增可选字段、事件类型、错误码、能力字段；消费者必须忽略未知内容。
- **major**：删除/重命名字段、改变语义、改变必填性。
- 禁止复用已删除字段名表达新语义。
- `extensions` 不受版本约束，但必须命名空间化。
- 能力协商优先于版本判断：不要因为版本高就假设能力存在。
- Satori 协议版本与 NLMP 版本独立；Satori adapter 配置中固定 Satori protocol version。

---

## 12. 安全与隐私

- **不可信输入**：所有入站文本/附件/mention 都视为不可信；AI prompt injection 防护由 Application 层负责。
- **mention 安全**：出站默认禁止 `everyone`/`here`；平台 mention 必须显式允许。
- **附件安全**：只允许 `http(s)`；限制大小/MIME；下载前防 SSRF（禁止内网地址/重定向到内网）；不要自动执行附件。
- **token 安全**：Satori token、Discord token 不进入 NLMP；不写日志；不放 `plugin.json`。
- **interaction token**：`replyToken` 是短期秘密；不日志、不持久化、不进 AI prompt。
- **隐私**：消息原文可能含 PII；日志默认只记录 source/conversation/message id 和长度，不记录全文；`extensions` 可能含平台原始数据，需脱敏。
- **多租户隔离**：所有 key 必须包含 source；禁止跨 source 查询会话/消息。
- **限流**：按 source/conversation/user 限流；AI 调用与平台发送都要有超时/取消。
- **幂等**：重试必须复用 idempotencyKey；去重窗口覆盖 Satori 会话恢复窗口。
- **删除/保留**：message.deleted 是 tombstone；存储层按产品策略清理历史。

---

## 13. 一致性测试

适配器/模型实现至少覆盖：

1. Source/Conversation/Message key 的作用域隔离。
2. 多平台、多 Bot 同名频道不串话。
3. `message.created` → `message.updated` → `message.deleted` 生命周期。
4. `replyTo`、mentions、attachments 的往返。
5. 长文本分片与多 `messageIds` ack。
6. `markdown=false` 降级为纯文本。
7. `edit=false`/`streamEdit=false` 只发最终消息。
8. `rate_limited` 的 `retryAfterMs` 与重试。
9. 相同 `idempotencyKey` 不重复发送。
10. Satori `sn` 重放/重复事件去重。
11. Satori message elements 映射（at/img/quote/a/br/unknown）。
12. Satori `login.features` 缺失时不假设 edit/delete。
13. 未知事件类型/未知字段不崩溃。
14. 附件 URL scheme/大小/SSRF 防护。
15. interaction token 不落日志、不持久化。
16. 时区/时间格式转换（Unix 秒 → RFC 3339）。

建议使用录制的 Satori event fixtures 做 golden tests，不依赖真实平台。

---

## 14. 开放问题与后续版本

| 问题 | 当前决定 | 后续 |
| --- | --- | --- |
| 是否引入富文本 segments | v0.1 不做，text-first | v0.2 评估 `segments[]`（text/mention/image/file） |
| 是否支持按钮/Modal | 仅 capability + 后续扩展 | v0.2 定义 interaction 组件模型 |
| 流式输出 | 用重复 `message.updated` | 评估 `message.delta` 与节流策略 |
| 命令确认/背压 | ack + retryable | 远程 binding 时定义窗口/流控 |
| 远程插件 transport | 不定义 | 评估 JSON-RPC/gRPC，而非自造聊天 wire protocol |
| Satori-native 模式 | 不采用为核心 | 若产品选择 Satori-native，可单独发布 profile |
| 会话发现/历史分页 | 不在模型内 | 作为 adapter capability/API 扩展 |
| 本地文件上传 | v0.1 仅 URL | v0.2 定义 upload token/流式上传 |
| Markdown 转换 | 能力驱动 | 富文本 adapter 映射 |
| 事件保留/序列号 | 可选 sequence | 多 source 的全局顺序不承诺 |

上表的“后续”列在 v0.1 实现中均未启用；实现取舍见 §16.4。

---

## 15. 与 Natter 现有架构的衔接

- **Domain**：不依赖 NLMP；领域事件与聊天事件保持分离。
- **Application**：`ChatOrchestrator` 消费 NLMP 事件，调用 `IChatCompletionPort`，产生 NLMP reply/command。
- **Infrastructure**：Satori/Discord adapter 实现 NLMP 的 ingress/egress；NLMP 不是 DI 容器本身。
- **CLI**：提供 `upstreams` / `discord status` / `satori status` 诊断；不把平台 SDK 暴露给命令层。
- **插件**：功能插件依赖 NLMP 接口；协议插件依赖 NLMP + 平台 SDK；Satori 类型只存在于协议插件内部。
- **Rx**：NLMP 事件可以投影到 Rx 流供宿主/插件订阅，但 NLMP 本身不要求 Rx；Rx 是 Natter 内部事件分发机制，不是 wire protocol。

---

## 16. 本仓库实现（v0.1）

### 16.1 代码映射

| 规范章节 | 实现位置 |
| --- | --- |
| §4 标识与作用域 | `src/Natter.Chat/Model/Source.cs`、`Model/Conversation.cs`、`Keys/ChatKeys.cs`；注册表 `src/Natter.Infrastructure/Chat/ChatUpstreamRegistry.cs` |
| §5.2–5.5 User / Attachment / Mention / Content | `src/Natter.Chat/Model/*.cs`、`Safety/ChatAttachmentGuard.cs`、`Safety/ChatMentionGuard.cs` |
| §5.6 Message / §5.7 Interaction | `Model/Message.cs`、`Model/Interaction.cs`（token 在 `ToString()` 中脱敏） |
| §5.8 Capabilities | `Model/Capabilities.cs`；Satori 映射 `SatoriCapabilityMapper` |
| §6 事件模型 | `Events/*.cs`（envelope + 4 类 payload + `upstream.status`）；入口 `ChatIngressProcessor` + `RxChatEventHub` |
| §7 命令 / Ack / Error | `Commands/*.cs`、`Ack/ChatAck.cs`；调度 `Dispatch/ChatCommandDispatcher.cs` |
| §7.5 幂等 | `Reliability/ChatIdempotencyCache.cs`（默认 10 分钟窗口） |
| §6.6 / §13 去重 | `Reliability/ChatEventDeduplicator.cs`（事件 ID + `message.created` 语义键） |
| §8 能力降级 | `Degradation/ChatCapabilityDegrader.cs`、`Degradation/ChatTextChunker.cs` |
| §9 Satori 映射 | `src/Natter.Chat.Satori/`：`SatoriEventMapper`、`SatoriContentMapper`、`SatoriCapabilityMapper`、`SatoriCommandMapper`、`SatoriSnTracker` |
| §10.1 进程内绑定 | `IChatUpstream` / `IChatCommandDispatcher` / `IChatEventSource`；`InMemoryChatUpstream` 用于测试与嵌入宿主 |
| §10.2 JSON 投影 | `Serialization/NlmpJson.cs`、`Serialization/NlmpEnumConverter.cs`（不包含 transport/握手） |
| §15 Application 衔接 | `src/Natter.Application/Chat/ChatOrchestrator.cs` + `IChatResponder` + `AddChatOrchestrator<T>()` |
| §15 CLI 诊断 | `src/Natter.Cli.Core/Commands/ChatCommands.cs`：`upstreams`、`satori status`；`doctor` 展示 NLMP 计数 |

依赖方向：

```text
Natter.Abstractions
   ▲        ▲          ▲
Domain   Application   Natter.Chat ──▶ Natter.Chat.Satori
                           ▲                ▲
                     Natter.Infrastructure  │
                           ▲                │
                       Natter.Cli.Core ─────┘
```

### 16.2 最小用法

```csharp
var source = Source.Create("satori", "discord", "1234567890");
var conversation = new Conversation("5555555555", ConversationKind.Guild);

// 1) 适配器注册与连接（真实适配器在 ConnectAsync 内建 WebSocket/HTTP）
var upstream = new InMemoryChatUpstream(source, Capabilities.TextOnly, clock);
registry.Register(upstream);
await upstream.ConnectAsync();

// 2) 发送：自动应用能力降级、长文本分片、idempotencyKey 去重
var factory = new ChatCommandFactory(clock);
var ack = await dispatcher.ExecuteAsync(
    factory.Send(source, conversation, new Content("你好，我是 Natter。")));

// 3) 入站：Satori 事件 → NLMP → 去重入口 → 事件流
var chatEvent = new SatoriEventMapper(clock).Map(satoriEvent);
if (chatEvent is not null)
    ingress.TryPublish(chatEvent);
```

```bash
# 查看已注册上游与协商能力
natter upstreams --json

# 预览 Satori login.features 的映射结果
natter satori status --features message.update,message.delete,message.attachments
```

### 16.3 一致性测试映射（对应 §13）

| §13 项 | 测试 |
| --- | --- |
| 1 键作用域隔离 | `NlmpModelTests.Source_and_composite_keys_are_structured_and_source_scoped` |
| 2 多平台/多 Bot 同名频道 | `NlmpEventTests.Two_bots_with_the_same_conversation_id_do_not_cross_talk` |
| 3 created → updated → deleted | `NlmpEventTests.Message_lifecycle_produces_created_updated_and_deleted_events` |
| 4 replyTo / mentions / attachments 往返 | 同上 + `NlmpJsonTests.Events_commands_and_acks_round_trip_through_the_json_projection` |
| 5 长文本分片与多 `messageIds` | `NlmpCommandTests.Long_text_is_chunked_and_returns_multiple_message_ids` |
| 6 `markdown=false` 降级 | `NlmpCommandTests.Markdown_is_downgraded_when_the_capability_is_missing` |
| 7 edit/streamEdit=false | `NlmpCommandTests.Unsupported_edit_and_delete_are_rejected_instead_of_guessed`、`A_single_final_edit_is_allowed_when_streaming_is_disabled` |
| 8 `rate_limited` 与重试 | `NlmpCommandTests.Rate_limit_ack_exposes_retry_after_and_retries_reuse_the_idempotency_key` |
| 9 相同 `idempotencyKey` 不重复发送 | 同上 + `The_same_idempotency_key_does_not_send_twice` |
| 10 Satori `sn` 重放去重 | `SatoriMappingTests.Replayed_satori_events_are_rejected_by_the_deduplicator`、`NlmpEventTests.Ingress_deduplicates_by_event_id_and_message_creation_key` |
| 11 message elements 映射 | `SatoriMappingTests.Message_elements_map_to_text_mentions_attachments_and_quote`、`At_all_and_here_become_high_risk_mentions` |
| 12 features 缺失不假设 edit/delete | `SatoriMappingTests.Login_features_map_without_guessing_missing_capabilities` |
| 13 未知事件/字段不崩溃 | `NlmpJsonTests.Unknown_event_types_are_ignored`、`Unknown_fields_and_enum_values_do_not_break_binding` |
| 14 附件 URL/大小/SSRF | `NlmpModelTests.Content_requires_text_or_attachments_and_rejects_unsafe_urls`、`SatoriMappingTests.Satori_attachment_policy_rejects_private_and_oversized_attachments` |
| 15 interaction token 不落日志/持久化 | `NlmpModelTests.Interaction_display_masks_reply_tokens`、`SatoriMappingTests.Interaction_tokens_do_not_leak_into_ids_extensions_or_display` |
| 16 Unix 秒 → RFC 3339 | `NlmpJsonTests.Events_commands_and_acks_round_trip_through_the_json_projection`、`SatoriMappingTests.Satori_event_maps_source_conversation_sender_and_time` |

### 16.4 实现边界

以下内容按本规范 §10.2 / §14 的“后续”处理，当前**不实现**：

1. **Satori wire transport**：`SatoriEventMapper` / `SatoriCommandMapper` 只做语义映射，不含 WebSocket、HTTP、认证、重连；`SatoriSnTracker` 只保存内存序号，持久化由适配器宿主负责。
2. **远程 JSON binding**：`NlmpJson` 只提供 envelope 投影与版本校验，不提供握手、路由、服务发现或背压。
3. **原生 Discord adapter / `discord status`**：当前只有 `satori` 映射与通用 `upstreams` 诊断；Discord 适配器可按 §10.3 独立实现。
4. **流式编辑节流**：`Capabilities.StreamEdit` 已暴露，节流策略按 §7.5 由适配器决定；核心不做定时器/合并。
5. **富文本/按钮/Modal**：`Content` 仍是 text-first；按钮/Modal 只有 capability 与 `Interaction` 最小字段。
6. **Extensions 命名空间校验**：约定 key 必须命名空间化（如 `satori`、`natter.trace`），当前不做运行时强校验。
7. **Satori thread**：`channel.parent_id` 的语义依赖适配器；核心 `ConversationKind.Thread` 已支持，映射在能确定线程时由适配器设置。
8. **历史分页 / 本地文件上传 / 消息 delta**：均未进入 v0.1 模型。
9. **纯引用无正文消息**：`quote`-only 消息无法满足 `content` 非空约束，Satori 映射当前跳过；适配器可补占位文本后再映射。

### 16.5 版本与兼容

- 当前实现只签发与接受 `0.1`（minor 兼容、major 拒绝）。
- 新增可选字段/事件/能力时提升 minor；消费者必须忽略未知字段、未知枚举值、未知事件类型（`NlmpJson` 已实现）。
- 删除/重命名字段或改变必填性才提升 major；禁止复用已删除字段名。

## 17. 参考资料

- [Satori Protocol Overview](https://satori.chat/en-US/protocol/)
- [Satori Events](https://satori.chat/en-US/protocol/events.html)
- [Satori Message API](https://satori.chat/en-US/resources/message.html)
- [Satori Message Encoding](https://satori.chat/en-US/protocol/message.html)
- [Satori Standard Elements](https://satori.chat/en-US/protocol/elements.html)
- [Satori SDK Overview](https://satori.chat/en-US/sdk/index.html)
