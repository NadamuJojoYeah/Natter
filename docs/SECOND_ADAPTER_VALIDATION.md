# Second Upstream & Consumer Validation Plan：DingTalk + Spark

> **状态**：第一阶段已实现（DingTalk callback transport + Spark IChatClient + shared responder；fake/HTTP 契约测试通过，真实平台凭据未提供）  
> **日期**：2026-11-15  
> **目标**：验证 Natter harness 的抽象是否成立：新增第二个 transport（钉钉）和第二个 AI consumer（星火）时，不改 core、不复制 responder、不破坏 session/memory 边界。  
> **关联**：[Harness 架构](HARNESS_ARCHITECTURE.md)、[Context & Memory 插件](CONTEXT_MEMORY_PLUGIN.md)、[AI Provider 设计](AI_PROVIDER_DESIGN.md)、[Discord transport](DISCORD_PLUGIN_DESIGN.md)

---

## 1. 这次要验证什么

不是“再做一个平台适配”，而是回答四个架构问题：

1. **第二个 transport 能否只作为 NPS plugin 接入，core 不改？**
2. **第二个 AI consumer 能否复用同一个 `IChatResponder` / ContextAssembler，只换 provider？**
3. **Session / conversation / memory 是否能按 `source` 正确隔离？**
4. **平台能力差异能否通过 `Capabilities` 降级，而不是 platform 特判？**

如果为了接钉钉或星火，需要：

- 改 `ChatOrchestrator` 的平台分支；
- 在 DeepSeek responder 里加 Spark 判断；
- 在 memory key 里硬编码 Discord；
- 复制一套 responder；

说明抽象还有问题，应先修 harness，而不是继续加平台。

---

## 2. 范围

### 2.1 In scope

- 钉钉机器人：
  - 接收 @bot / 单聊消息；
  - 文本/少量 markdown；
  - 回复、@mention、消息 id、conversation/user 映射；
  - 签名/回调/Stream 模式按实际开通类型验证；
  - 去重、ack、限流、错误处理。
- 星火：
  - `IChatClient` provider；
  - 非流式、文本；
  - 超时/错误/usage；
  - provider 选择与配置；
  - 与现有 responder 复用。
- 交叉矩阵：
  - 钉钉 + 星火；
  - Discord + 星火；
  - 钉钉 + DeepSeek；
  - context/memory 的 source 隔离。

### 2.2 Out of scope

- 钉钉完整卡片/审批/工作流；
- 钉钉语音/图片/文件；
- 星火 streaming/tools/多模态；
- 多实例/集群；
- 生产 SLA/压测；
- 跨平台身份自动合并；
- 钉钉企业内部复杂权限。

---

## 2.3 本轮查到的协议事实

### 钉钉

- 文档：[机器人接收消息协议](https://open.dingtalk.com/document/robots/receive-message.md)
- HTTP callback header：`timestamp`（毫秒）与 `sign`；
- 签名算法：`HmacSHA256(timestamp + "\n" + appSecret)` 后 Base64；时间戳与服务端相差超过 1 小时拒绝；
- 消息体关键字段：`msgId`、`conversationId`、`conversationType`（1 单聊 / 2 群聊）、`senderStaffId` / `senderId`、`senderNick`、`text.content`、`atUsers`、`isInAtList`、`sessionWebhook`、`sessionWebhookExpiredTime`、`createAt`；
- 回复通过 `sessionWebhook` 发送 `{ "msgtype": "text", "text": { "content": "..." } }`；
- callback 必须先 ACK，且必须幂等处理重复 `msgId`。

### 讯飞星火

- 文档：[星火 HTTP 接口](https://www.xfyun.cn/doc/spark/HTTP%E8%B0%83%E7%94%A8%E6%96%87%E6%A1%A3.html)
- OpenAI-compatible endpoint：`https://spark-api-open.xf-yun.com/v1/chat/completions`；
- 鉴权：`Authorization: Bearer <APIPassword>`；
- 请求：`model`、`messages`、`stream`、`temperature`、`max_tokens`；
- 当前模型名示例：`4.0Ultra`、`generalv3.5`、`max-32k`、`generalv3`、`pro-128k`、`lite`；
- 响应：`choices[].message.content` + `usage`；
- 错误：HTTP error 或顶层 `code/message`；10013/10014/10019 内容审核、10007 并发/流控、11200 授权、11201–11203 限流、10907 token 超限。

## 3. 钉钉 adapter 设计

### 3.1 接入形态

以实际开通的机器人类型为准，可能是：

- 自定义机器人 outgoing webhook；
- 企业内部机器人 HTTP callback；
- Stream 模式（无需公网回调）。

必须先确认：

- 能否收到 `@bot` 消息；
- 是否有 `conversationId` / `senderStaffId` / `msgId`；
- 回复是同步 HTTP response、sessionWebhook，还是主动 API；
- 是否需要 timestamp + sign；
- 是否需要公网 URL / 内网穿透；
- 回调 ack 超时限制；
- 消息去重字段。

### 3.2 NLMP 映射

| 钉钉 | NLMP |
| --- | --- |
| `conversationType` 1:1 | `ConversationKind.Direct` |
| 群聊 | `ConversationKind.Group` / `Guild`（按模型） |
| `conversationId` | `Conversation.Id` |
| `senderStaffId` / `senderId` | `User.Id` |
| `msgId` | `Message.Id` |
| `text.content` | `Content.Text` |
| `atUsers` | `Content.Mentions` |
| `createAt` | `Message.CreatedAt` |
| `sessionWebhook` / reply token | interaction reply token 或 adapter 内部引用 |

规则：

- `Source = ("dingtalk", "dingtalk", botId)`；
- 不把钉钉的 `conversationId` 当成 Discord channel id；
- 不支持 edit/typing/thread 时，`Capabilities` 设为 false；
- 回复超长按 `MaxMessageLength` 分片；
- 默认不允许 `@everyone` 类广播；
- 回调必须幂等，重复 `msgId` 只处理一次；
- 签名失败/时间戳漂移直接拒绝。

### 3.3 需要测试的失败模式

- 签名错误；
- 重复回调；
- 回调超时；
- 非文本消息；
- 被移出群/无权限；
- 限流；
- 回复 token 过期；
- 长文本分片；
- @bot 与非 @bot 的区分。

---

## 4. 星火 consumer 设计

### 4.1 先确认协议

星火可能提供：

- OpenAI-compatible HTTP；
- 自有 HTTP；
- WebSocket；
- 不同的鉴权方式（Bearer / APPID+APIKey+APISecret / 签名）。

不要假设它和 DeepSeek 一样。  
先做一个最小 probe：

```text
GET/POST 最小请求
  → 认证是否通过
  → 返回 JSON/SSE/WebSocket frame 形状
  → usage/error/finish_reason 字段
  → 是否需要 session/conversation id
```

### 4.2 `IChatClient`

如果星火兼容 OpenAI Chat Completions：

- 可用 `Microsoft.Extensions.AI.OpenAI` + custom endpoint；
- 仍需验证流式/usage/错误映射。

如果不兼容：

- 实现 `SparkChatClient : IChatClient`；
- 映射 `ChatMessage` / `ChatOptions` / `ChatResponse`；
- 自己处理鉴权、SSE/WebSocket、usage、错误；
- 仍复用 M.E.AI 中间件。

### 4.3 复用边界

必须复用：

- `IChatResponder`；
- `IContextAssembler`；
- `IMemoryStore`；
- `ChatCommandFactory`；
- `ChatCommandDispatcher`。

不能复制：

- DeepSeek responder；
- DeepSeek prompt 逻辑；
- Discord 发送逻辑。

### 4.4 Provider 选择

v1 可以：

- `DeepSeekPlugin` 和 `SparkPlugin` 各自在插件内构造 `IChatClient`；
- 通过 config 选择 active responder/provider；
- 保证同一时间只有一个 `chat.responder` 返回命令。

v2 应：

- 拆 `ai.provider` contribution；
- host AI registry；
- `ai.complete` 权限；
- responder 只依赖 `IChatClient`，不依赖具体 provider。

---

## 5. 测试矩阵

| # | Transport | Provider | 目的 |
| --- | --- | --- | --- |
| 1 | Fake DingTalk | Fake Spark | 纯契约/映射测试 |
| 2 | DingTalk | Fake Spark | 真实回调 + fake AI |
| 3 | Fake DingTalk | Spark | provider 真实调用，不接钉钉 |
| 4 | DingTalk | Spark | 第二个上游 + 第二个 consumer 全链路 |
| 5 | Discord | Spark | 验证 responder/provider 可换 |
| 6 | DingTalk | DeepSeek | 验证 transport 可换、AI 不变 |
| 7 | Discord + DingTalk | Spark | 验证 source/session/memory 隔离 |

### 5.1 必须断言

- 同一 `IChatResponder` 不因 transport 改变；
- 同一 `IContextAssembler` 不因 provider 改变；
- `ConversationSession` key 包含 source；
- memory 不跨 source 泄漏；
- 钉钉不支持的 capability 被正确降级；
- 星火错误映射不泄漏 key；
- 无重复回复/自循环；
- 插件从 `lib/` 文件加载成功；
- 停止/重启后连接和状态正常。

---

## 6. 环境与凭据

不要写进仓库。

建议：

```text
DINGTALK_BOT_ID
DINGTALK_CLIENT_ID / APP_KEY
DINGTALK_CLIENT_SECRET / APP_SECRET
DINGTALK_SIGN_SECRET
DINGTALK_CALLBACK_URL
SPARK_API_KEY
SPARK_BASE_URL
SPARK_MODEL
SPARK_APP_ID / API_SECRET（如协议需要）
```

规则：

- 使用 `artifacts/live/.env` 或外部 secret provider；
- `.env` 权限 600，git-ignored；
- 测试结束轮换/删除；
- 回调日志不打印 token/sign；
- 星火请求日志不打印 prompt/key；
- 不在截图/测试记录中出现完整 secret。

---

## 7. 执行顺序

### Step 0：协议 probe

- 钉钉：发一条测试消息，确认回调/回复路径；
- 星火：最小请求，确认协议、模型、usage、错误。

### Step 1：Fake 测试

- DingTalk event → NLMP；
- DingTalk command → reply；
- Spark request/response；
- responder/context 复用。

### Step 2：真实单边

- DingTalk + fake AI；
- Spark + fake transport。

### Step 3：交叉

- DingTalk + Spark；
- Discord + Spark；
- DingTalk + DeepSeek。

### Step 4：隔离

- 两个 source 同时在线；
- DM/guild 记忆隔离；
- 同一用户跨平台不自动合并。

### Step 5：失败模式

- 签名错误；
- 重复消息；
- 超时；
- 限流；
- provider 5xx；
- 插件重启。

### Step 6：人工闸门

- 真人在钉钉 @bot；
- 收到 Spark 回复；
- Discord 同 provider 仍正常；
- 日志无密钥。

---

## 8. 验收标准

- [x] 钉钉 transport 作为 NPS plugin 加载，无需改 core；
- [x] 星火 provider 实现 `IChatClient`，无需改 responder；
- [x] `AiChatResponder` / `IAiContextAssembler` / `IAiConversationStore` 跨平台复用；
- [x] `Source` / `Conversation` / `User` 映射正确（DingTalk 单聊/群聊、Discord/其他保持 NLMP）；
- [x] capability 降级正确（DingTalk 无 edit/delete/typing/slash，长文本分片）；
- [x] 签名/ack 正确；重复 `msgId` 由 ingress + command idempotency 处理；
- [x] Spark 错误不泄漏 key；
- [x] 两个 source 的 session/memory key 隔离（单元）；
- [x] fake DingTalk + fake Spark NPS 交叉链路通过；
- [ ] 真实钉钉 callback + 真实星火凭据（环境未提供）；
- [ ] 文件插件包真实平台部署（本机已生成 `lib/` + deps.json，未接真实平台）；
- [ ] 真实多 source 在线长跑、限流/重连/人工闸门。

## 8.1 本轮实现映射

| 组件 | 文件 |
| --- | --- |
| 共享 responder / context / memory | `src/Natter.Feature.Ai` |
| DeepSeek 复用 shared responder | `src/Natter.Feature.Ai.DeepSeek` |
| 钉钉 NLMP transport | `src/Natter.Chat.DingTalk` + `samples/Natter.Sample.Plugin.DingTalk` |
| 星火 IChatClient + 插件 | `src/Natter.Feature.Ai.Spark` |
| 契约/交叉测试 | `DingTalkTests`、`SparkChatClientTests`、`DeepSeek*Tests` |

测试结果：`Natter.Framework.Tests` **188/188**；DingTalk 回调 HTTP 签名/ACK、Spark fake HTTP、DingTalk+Spark NPS 交叉链路均通过。真实钉钉/星火凭据未提供，因此没有真实平台 live 数据。

---

## 9. 风险与待确认

- 钉钉机器人具体类型和回调模式；
- 钉钉是否需要公网 URL / 内网穿透；
- 钉钉回复 token 的生命周期；
- 钉钉 markdown / @mention / 分片限制；
- 星火是否为 OpenAI-compatible；
- 星火鉴权方式和模型名；
- 星火 usage/错误字段；
- 星火是否支持 system/multi-turn；
- 是否需要 provider 拆分（`ai.provider`）；
- 第二平台是否暴露 core 的平台特判。

---

## 10. 结论

这次测试的价值不在“多一个平台”，而在于验证：

> **Natter 的 core 是否真的与 transport/provider 解耦。**

如果钉钉和星火都能以插件接入，且 responder/context/memory 不变，那么 Natter 的 harness 架构成立；  
如果必须改 core，说明应该先修抽象，而不是继续增加平台。
