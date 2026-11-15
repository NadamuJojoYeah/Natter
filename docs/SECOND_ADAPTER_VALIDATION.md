# Second Upstream & Consumer Validation Plan：DingTalk Stream + Spark MaaS

> **状态**：Preflight review · **实现差距已修复**（DingTalk Stream transport 已实现、Spark MaaS 参数已对齐；真实平台凭据待接入）
> **日期**：2026-11-15
> **实际模式**：钉钉 = **Stream 模式**；星火 = **MaaS `spark-x2.5-1.7b`**
> **目标**：验证 Natter harness 的抽象是否成立：新增第二个 transport 和第二个 AI consumer 时，不改 core、不复制 responder、不破坏 session/memory 边界。
> **关联**：[Harness 架构](HARNESS_ARCHITECTURE.md)、[Context & Memory 插件](CONTEXT_MEMORY_PLUGIN.md)、[AI Provider 设计](AI_PROVIDER_DESIGN.md)、[Discord transport](DISCORD_PLUGIN_DESIGN.md)

---

## 0. 当前结论

**实现差距已关闭：**

- 已实现 `DingTalkStreamTransport`：gateway HTTP open → ticket → `ClientWebSocket` → 订阅 `/v1.0/im/bot/messages/get` → callback parse → ack → 退避重连；
- `DingTalkCallbackParser` / `DingTalkChatUpstream` / sessionWebhook 回复逻辑复用，HTTP callback transport 保留为 legacy；
- 钉钉插件默认 transport 已切换为 Stream；配置改为 `clientId` + `${secret:DINGTALK_CLIENT_SECRET}` + `botId` + `robotCode`；
- Spark 默认对齐 MaaS：`https://maas-api.cn-huabei-1.xf-yun.com/v2` / `spark-x2.5-1.7b`，请求发 `max_completion_tokens` + `thinking.type=disabled`，不再发 `max_tokens` / `temperature`；
- Stream 协议、本机 WebSocket connect/ack、Spark MaaS 请求、共享 responder 交叉链路均有测试。

当前工程状态：

- `dotnet build Natter.slnx`：通过（1 个 Android/Java warning）；
- `Natter.Framework.Tests`：**190/190**；
- `plugins inspect`：DingTalk / Spark manifest 均 valid；
- **真实 DingTalk Stream / 真实 Spark 未接入**（凭据未提供），因此真实平台验收仍未完成。

---

## 1. 这次要验证什么

不是“再做一个平台适配”，而是回答四个架构问题：

1. **第二个 transport 能否只作为 NPS plugin 接入，core 不改？**
2. **第二个 AI consumer 能否复用同一个 `IChatResponder` / ContextAssembler，只换 provider？**
3. **Session / conversation / memory 是否能按 `source` 正确隔离？**
4. **平台能力差异能否通过 `Capabilities` 降级，而不是 platform 特判？**

如果为了接钉钉或星火，需要：

- 改 `ChatOrchestrator` 的平台分支；
- 在 `AiChatResponder` 里加 Spark 判断；
- 在 memory key 里硬编码 Discord；
- 复制一套 responder；

说明抽象还有问题，应先修 harness，而不是继续加平台。

---

## 2. 范围

### 2.1 In scope

- 钉钉 **Stream**：
  - WebSocket 连接、鉴权、订阅机器人消息；
  - 接收 @bot / 单聊消息；
  - 文本消息；
  - 回复、@mention、conversation/user 映射；
  - ack、去重、重连、心跳；
  - sessionWebhook 或主动发送路径。
- 星火 MaaS：
  - `IChatClient` provider；
  - 非流式、文本；
  - 超时/错误/usage；
  - provider 选择与配置；
  - 与现有 shared responder 复用。
- 交叉矩阵：
  - DingTalk Stream + Spark；
  - Discord + Spark；
  - DingTalk Stream + DeepSeek；
  - context/memory 的 source 隔离。

### 2.2 Out of scope

- 钉钉卡片/审批/工作流；
- 钉钉语音/图片/文件；
- 星火 streaming/tools/多模态；
- 多实例/集群；
- 生产 SLA/压测；
- 跨平台身份自动合并；
- 钉钉企业内部复杂权限。

---

## 3. 平台事实

### 3.1 钉钉：Stream 模式

文档：

- [服务端 Stream 模式](https://help.dingtalk.io/zh/open/development/introduction-to-stream-mode)
- [机器人接收消息](https://help.dingtalk.io/zh/open/dingstart/robot-receive-message)

事实：

- 客户端主动建立 **WebSocket** 连接，不是钉钉回调公网 URL；
- 认证使用应用/机器人的 `clientId` + `clientSecret`（AppKey/AppSecret 体系，具体字段名以开通页面为准）；
- 客户端订阅机器人消息 topic；
- 收到消息后必须 **ack**；
- 需要处理心跳、断线重连、重复消息；
- 机器人回复通常仍通过消息中的 `sessionWebhook`，或主动发送 API；需 probe 确认；
- 不再需要 `timestamp/sign` HTTP 回调签名；
- 消息字段与 callback 版本大体相同：`msgId`、`conversationId`、`conversationType`、`senderStaffId` / `senderId`、`senderNick`、`text.content`、`atUsers`、`isInAtList`、`sessionWebhook`、`sessionWebhookExpiredTime`、`createAt`。

**实现状态：**

- `DingTalkStreamTransport` 已实现 Stream WebSocket 路径；
- 原 `DingTalkWebhookTransport` 保留为 HTTP callback legacy；
- `DingTalkCallbackParser`、`DingTalkChatUpstream`、sessionWebhook 回复逻辑复用。

### 3.2 星火：MaaS Chat API

文档：[MaaS Chat API](https://maas.xfyun.cn/doc/guide/3%E3%80%81API%20%E6%8E%A5%E5%8F%A3/3.1%20%E6%98%9F%E7%81%AB%E6%A8%A1%E5%9E%8B%20API/3.1.1%20Chat.html)

事实：

| 项 | 值 |
| --- | --- |
| Endpoint | `https://maas-api.cn-huabei-1.xf-yun.com/v2/chat/completions` |
| 模型 | `spark-x2.5-1.7b`（当前账号） |
| 鉴权 | `Authorization: Bearer <APIKEY>`，APIKEY 形如 `ak-...` |
| 请求 | `model`、`messages`、`stream`、`max_completion_tokens`、`thinking` 等 |
| `max_tokens` | **已废弃**，仅兼容旧版；应改用 `max_completion_tokens` |
| `temperature` | 固定 `1` 生效，建议不要显式传 |
| `top_p` | 固定 `0.95` 生效，建议不要显式传 |
| `presence_penalty` / `frequency_penalty` | 已废弃，无效果 |
| 思考模式 | `thinking.type = enabled/disabled/auto`，默认 `enabled` |
| 响应 | `choices[].message.content`、`reasoning_content`、`usage` |
| usage 扩展 | `prompt_tokens_details.cached_tokens`、`completion_tokens_details.reasoning_tokens` |
| 流式 | SSE，`data: [DONE]` 结束 |

当前 `SparkChatClient` 的方向基本正确（OpenAI-compatible + Bearer + `IChatClient`），但需要对齐：

1. base URL / model 默认值；
2. `max_completion_tokens`；
3. `thinking` 配置（v1 默认 disabled）；
4. 不发送 `temperature=0.7`（MaaS 固定 1）；
5. 解析 `reasoning_content` 时只作为内部数据，不发给 Discord/钉钉；
6. usage 扩展字段可选记录。

---

## 4. 钉钉 Stream adapter 设计

### 4.1 目标结构

```text
DingTalkStreamTransport : IDingTalkTransport
  ├── WebSocket connect / auth
  ├── subscribe bot message topic
  ├── heartbeat / reconnect
  ├── ack
  └── DingTalkTransportEvent
          │
DingTalkChatUpstream（复用）
  ├── NLMP mapping
  ├── allowlist / requireMention
  └── sessionWebhook reply
```

### 4.2 复用与替换

复用：

- `DingTalkCallbackMessage` / parser（按 Stream payload 字段微调）；
- `DingTalkChatUpstream`；
- `DingTalkReply` / `SessionWebhook` 逻辑；
- `DingTalkTests` 的映射/分片测试。

替换：

- `DingTalkWebhookTransport`（legacy）与 `DingTalkStreamTransport` 并存；
- HTTP callback 签名/ACK 测试 → Stream connect/ack/reconnect 测试；
- `listenPrefix` 配置 → Stream `clientId` / `clientSecret` / `robotCode` / topic 配置。

HTTP callback transport 可以保留为可选 legacy，但**不是本次真实测试目标**。

### 4.3 配置建议

```json
{
  "clientId": "${secret:DINGTALK_CLIENT_ID}",
  "clientSecret": "${secret:DINGTALK_CLIENT_SECRET}",
  "botId": "真实的 chatbotUserId",
  "robotCode": "机器人 code（如协议需要）",
  "requireMention": true,
  "conversations": [],
  "users": [],
  "maxMessageLength": 2000,
  "reconnectBaseDelayMs": 500,
  "reconnectMaxDelayMs": 30000
}
```

注意：

- `clientId` 不一定是 secret，但按敏感配置处理；
- `clientSecret` 只从 `IPluginSecrets` 读取；
- `botId` 用实际 `chatbotUserId`，不是 AppKey；
- Stream 不需要 `listenPrefix` / `public callback URL`；
- topic / gateway 地址以官方 SDK/协议为准，不要硬编码猜测。

### 4.4 Stream 生命周期

```text
ConnectAsync
  → 获取/建立 WebSocket
  → 鉴权
  → 订阅机器人消息
  → 进入 receive loop
  → 收到消息 → publish event → ack
  → 心跳 / 断线 → 退避重连
DisconnectAsync
  → 停止 receive loop
  → 取消订阅 / 关闭 socket
```

要求：

- ack 失败要记录并重试/丢弃策略明确；
- 重连后重新订阅；
- 重复 `msgId` 由 ingress + command idempotency 处理；
- 连接状态暴露给 `dingtalk status`；
- 不在日志打印 clientSecret / sessionWebhook 查询串。

### 4.5 失败模式

- clientId/clientSecret 错误；
- WebSocket 连接失败；
- 鉴权失败；
- 订阅 topic 失败；
- 心跳超时；
- 断线重连；
- ack 超时；
- 重复消息；
- 非文本消息；
- sessionWebhook 过期；
- 被移出群/无权限；
- 限流；
- 长文本分片。

---

## 5. 星火 adapter 对齐

### 5.1 配置

```json
{
  "apiKey": "${secret:SPARK_API_KEY}",
  "baseUrl": "https://maas-api.cn-huabei-1.xf-yun.com/v2",
  "model": "spark-x2.5-1.7b",
  "thinking": "disabled",
  "maxOutputTokens": 1024,
  "timeoutSeconds": 60,
  "maxRetries": 2,
  "streaming": false,
  "tools": []
}
```

### 5.2 请求映射

| 当前 | 目标 |
| --- | --- |
| `max_tokens` | `max_completion_tokens` |
| `temperature=0.7` | 不发送，或固定 `1` |
| 无 `thinking` | `thinking: { "type": "disabled" }`（v1） |
| `stream=false` | 保持 |
| `tools=[]` | 保持 |

### 5.3 响应映射

- `choices[0].message.content` → `ChatResponse.Text`；
- `reasoning_content` → 内部字段/诊断，不进入用户回复、不写日志；
- `usage.prompt_tokens` / `completion_tokens` / `total_tokens`；
- 可选记录 `cached_tokens` / `reasoning_tokens`；
- `code != 0` 按 provider error 处理。

### 5.4 Provider 选择

DeepSeek priority = 10，Spark priority = 20；`CompositeChatResponder` 取最小 priority。
如果两个插件同时加载，**DeepSeek 会先应答**。

真实 Spark 测试必须：

- 只加载 `dingtalk` + `spark`；或
- 显式禁用 DeepSeek；或
- 后续改为显式 provider 选择，不依赖 priority。

### 5.5 复用边界

必须复用：

- `AiChatResponder`；
- `IAiContextAssembler`；
- `IAiConversationStore`；
- `ChatCommandFactory`；
- `ChatCommandDispatcher`。

不能复制：

- DeepSeek responder；
- DeepSeek prompt 逻辑；
- Discord 发送逻辑。

---

## 6. 测试矩阵

| # | Transport | Provider | 目的 |
| --- | --- | --- | --- |
| 1 | Fake DingTalk Stream | Fake Spark | 纯契约/映射测试 |
| 2 | DingTalk Stream | Fake Spark | 真实 Stream + fake AI |
| 3 | Fake DingTalk | Spark MaaS | provider 真实调用，不接钉钉 |
| 4 | DingTalk Stream | Spark MaaS | 第二个上游 + 第二个 consumer 全链路 |
| 5 | Discord | Spark MaaS | 验证 responder/provider 可换 |
| 6 | DingTalk Stream | DeepSeek | 验证 transport 可换、AI 不变 |
| 7 | Discord + DingTalk Stream | Spark | 验证 source/session/memory 隔离 |

### 6.1 必须断言

- 同一 `IChatResponder` 不因 transport 改变；
- 同一 `IAiContextAssembler` 不因 provider 改变；
- `ConversationSession` / conversation store key 包含 source；
- memory 不跨 source 泄漏；
- Stream ack / 重连 / 去重正确；
- 钉钉不支持的 capability 被正确降级；
- 星火错误映射不泄漏 key；
- 无重复回复/自循环；
- 插件从 `lib/` 文件加载成功；
- 停止/重启后连接和状态正常。

---

## 7. 环境与凭据

不要写进仓库。

```text
DINGTALK_CLIENT_ID
DINGTALK_CLIENT_SECRET
DINGTALK_BOT_ID
DINGTALK_ROBOT_CODE          # 如 Stream 协议需要
SPARK_API_KEY
SPARK_BASE_URL=https://maas-api.cn-huabei-1.xf-yun.com/v2
SPARK_MODEL=spark-x2.5-1.7b
```

规则：

- 使用 `artifacts/live/.env` 或外部 secret provider；
- `.env` 权限 600，git-ignored；
- 测试结束轮换/删除；
- 日志不打印 clientSecret / APIKEY / prompt / sessionWebhook 查询串；
- 不在截图/测试记录中出现完整 secret。

---

## 8. 执行顺序

### Step 0：实现前 probe

- 钉钉：用官方 Stream SDK/示例确认：
  - gateway/鉴权；
  - topic；
  - 消息 payload；
  - ack 方式；
  - 回复路径（sessionWebhook 或主动 API）。
- 星火：最小 `POST` 确认：
  - endpoint / model / APIKEY；
  - `max_completion_tokens`；
  - `thinking=disabled`；
  - usage / reasoning_content 形状。

### Step 1：Stream transport + Spark 单元测试

- Fake Stream transport → NLMP；
- ack / reconnect / dedupe；
- Spark request 使用 MaaS 字段；
- shared responder 复用。

### Step 2：真实单边

- DingTalk Stream + fake AI；
- Spark MaaS + fake transport。

### Step 3：交叉

- DingTalk Stream + Spark；
- Discord + Spark；
- DingTalk Stream + DeepSeek。

### Step 4：隔离

- 两个 source 同时在线；
- DM/guild 记忆隔离；
- 同一用户跨平台不自动合并。

### Step 5：失败模式

- clientSecret 错误；
- WebSocket 断线；
- ack 超时；
- 重复消息；
- Spark 401/429/5xx/timeout；
- 插件重启。

### Step 6：人工闸门

- 真人在钉钉 @bot；
- 收到 Spark 回复；
- Discord 同 provider 仍正常；
- 日志无密钥。

---

## 9. 验收标准

当前已通过：

- [x] 共享 `AiChatResponder` / `IAiContextAssembler` / `IAiConversationStore` 跨平台复用；
- [x] DingTalk NLMP 映射与 HTTP callback fake 测试；
- [x] `DingTalkStreamTransport` 协议、connect、ack 本机 WebSocket 测试；
- [x] Stream 消息 → NLMP → shared responder → sessionWebhook 接口保持；
- [x] Spark MaaS base URL / model / `max_completion_tokens` / `thinking=disabled` 对齐；
- [x] DingTalk + Spark NPS 交叉链路（fake transport + fake provider）；
- [x] `Natter.Framework.Tests` 190/190；
- [x] `plugins inspect` 对两个 manifest 校验通过。

真实平台前仍需完成：

- [ ] 真实钉钉 Stream clientId/clientSecret 连接、topic、ack、重连；
- [ ] 真实钉钉 Stream → 真实 Spark（只加载 Spark，避免 DeepSeek priority 抢先）；
- [ ] 真实 Discord + Spark；
- [ ] 真实 source/memory 隔离；
- [ ] 密钥清理与测试记录。

---

## 10. 当前实现映射

| 组件 | 文件 | 状态 |
| --- | --- | --- |
| 钉钉 Stream transport | `src/Natter.Chat.DingTalk/DingTalkStreamTransport.cs` | **已实现** |
| 钉钉 callback parser / upstream | `src/Natter.Chat.DingTalk` | 复用 |
| 钉钉 HTTP callback transport | `src/Natter.Chat.DingTalk/DingTalkWebhookTransport.cs` | legacy，可保留 |
| 钉钉插件 | `samples/Natter.Sample.Plugin.DingTalk` | **已切换 Stream + clientSecret** |
| 星火 client / plugin | `src/Natter.Feature.Ai.Spark` | **已对齐 MaaS** |
| 共享 responder / context | `src/Natter.Feature.Ai` | 已复用 |
| 契约/交叉测试 | `DingTalkTests`、`SparkChatClientTests` | 基于 fake/HTTP callback |

---

## 11. 风险与待确认

### 钉钉 Stream

- 具体 gateway / topic / ack 协议以官方 SDK 为准；
- 回复是 sessionWebhook 还是主动 API；
- Stream 重连、心跳、限流；
- 机器人类型/权限；
- `chatbotUserId` / `robotCode` 的实际字段；
- 是否需要企业内网/代理。

### 星火 MaaS

- APIKEY 权限是否包含 `spark-x2.5-1.7b`；
- `thinking` 关闭后是否仍返回 `reasoning_content`；
- 错误码与限流策略；
- `max_completion_tokens` 上限；
- 是否支持 system/multi-turn；
- 是否需要 `user` 字段；
- 流式是否后续启用。

### 架构

- 是否需要 provider 拆分（`ai.provider`）；
- 第二平台是否暴露 core 平台特判；
- Stream transport 是否需要独立进程/生命周期；
- memory/session 的 source 隔离测试；
- 真实多 source 在线长跑。

---

## 12. 结论

这次测试的价值不在“多一个平台”，而在于验证：

> **Natter 的 core 是否真的与 transport/provider 解耦。**

实现顺序已经完成：

```text
DingTalkStreamTransport
  → Spark MaaS 参数对齐
  → fake/Stream 本机 WebSocket 测试
  → 待真实钉钉 Stream + 真实 Spark
  → 交叉矩阵
```

剩余工作只在真实凭据与平台长跑，不再是代码缺口。
