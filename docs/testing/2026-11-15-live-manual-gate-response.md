# 开发侧处置记录：2026-11-15 人工闸门（真人 @bot）未通过

> **状态**：Temporary / Active（BUG-003 已修复；框架回归 177/177；真实 ingress 2/2；L5 人工闸门待复测）  
> **类型**：测试配对处置记录，不是长期设计文档  
> **对应测试**：[人工闸门（真人 @bot）未通过](2026-11-15-live-manual-gate.md)  
> **基线**：`HEAD=e6cd8c9`，工作区 clean；测试期间未修改产品代码  
> **归档**：BUG-003 修复、真实频道复测通过后，本记录与测试记录一并归档

---

## 1. 结论

人工闸门**未通过**，产品验收继续阻塞。

> **BUG-003**：guild 频道里真实 `@bot` 消息在 NLMP 层被静默丢弃。  
> typing 出现后消失，无回复、无 DeepSeek 调用、无 warning/error 日志。  
> DM 不受影响。

这不是测试环境问题，也不是 Discord Gateway 问题。  
消息已经进入宿主，问题在 **Discord mapper 与 ChatOrchestrator 的 mention 判定不一致**。

当前自动化结果为：

- `Natter.Framework.Tests`：177/177 通过；
- `DiscordMentionGateTests` 已从“特征化缺陷”翻转为回归测试：mapper 保留 self mention，orchestrator 能调用 responder；
- L1–L4 的 26/26 live 通过不能覆盖该缺陷，因为自动化 E2E 在 NLMP 边界注入事件，绕过了真实 mapper。

---

## 2. 现场事实

测试报告记录的现场：

- 13:24:26、13:25:03 两条真实 `<@1571154023493410836>` 消息；
- 操作者看到 typing 出现后消失；
- bot 没有发出任何回复；
- 13:23:39 启动日志之后**零增量**，没有 error/warning；
- DM 不受影响。

typing 出现说明：

1. Discord Gateway 收到了 `MESSAGE_CREATE`；
2. `DiscordEventMapper` 用原始 `MentionedUsers` 判定 `RequireMention` 通过；
3. `DiscordChatUpstream` 触发了 typing；
4. 但消息进入 `ChatOrchestrator` 后没有到达 responder。

---

## 3. 根因

两处 mention 判定使用了不同的数据：

### 3.1 Mapper 放行

`DiscordEventMapper.MapMessage`：

```csharp
var selfMentioned = message.MentionedUsers?.Any(user =>
    string.Equals(user.Id, _source.SelfId, StringComparison.Ordinal)) == true;

if (_options.RequireMention
    && conversation.Kind is ConversationKind.Guild or ConversationKind.Thread
    && !selfMentioned
    && !message.MentionEveryone)
{
    return null;
}
```

这里用的是**原始 Discord mention**，所以消息被放行。

### 3.2 Mapper 又剔除 self mention

`DiscordEventMapper.MapMentions`：

```csharp
foreach (var user in message.MentionedUsers ?? [])
{
    if (string.Equals(user.Id, _source.SelfId, StringComparison.Ordinal))
        continue;

    mentions.Add(new Mention(MentionKind.User, user.Id, user.DisplayName));
}
```

构造 NLMP `Content.Mentions` 时，bot 自己的 mention 被主动移除。  
原意是避免 NLMP 再 @ 自己，但副作用是**“是否被 @”这个信号在 NLMP 里丢失了**。

### 3.3 Orchestrator 二次判定失败

`ChatOrchestrator` 默认：

```csharp
ChatOrchestrationOptions.RequireMentionOrDirect = true;
```

判定逻辑：

```csharp
if (_options.RequireMentionOrDirect
    && message.Conversation.Kind != ConversationKind.Direct
    && !MentionsSelf(message, chatEvent.Source.SelfId))
{
    return;
}
```

`MentionsSelf` 只看 `message.Content.Mentions`。  
self mention 已被 mapper 剔除，因此这里必然为 false，消息被静默丢弃。

### 3.4 为什么没有日志

mention 门控的 `return` 没有日志。  
只有回复发送失败时才有 warning，所以线上表现为“typing 闪一下，什么都没发生”。

---

## 4. 修复决策

### 4.1 采用：NLMP 保留 self mention，出站过滤 self

**决策：`DiscordEventMapper.MapMentions` 不再剔除 self mention。**

理由：

1. `Content.Mentions` 应该表示“消息里实际出现了哪些 mention”，不是“出站时允许 mention 谁”；
2. `ChatOrchestrator` 的通用 mention 门控依赖这个语义；
3. 其他 upstream（Satori 等）应保持同一语义；
4. 防止 self @ 是**发送路径**的职责，不是入站映射的职责；
5. 不引入新的 `Message` 字段，不改 NLMP 公共模型。

同时：

- `DiscordChatUpstream.SendAsync` 构造 `mentionIds` 时过滤 `Source.SelfId`；
- `UpdateAsync` 同样过滤；
- interaction 回复如果携带 mentions，也按同一规则过滤；
- 出站文本仍由 `AllowedMentions.None`/显式 user id 控制，不能因为保留 self mention 而重新 @ bot。

### 4.2 不采用：直接关掉 orchestrator 的 mention 门控

不把 `RequireMentionOrDirect` 全局设为 false。

原因：

- 会影响所有平台；
- 会让没有上游过滤能力的 transport 把普通 guild 消息都送进 AI；
- 成本和行为不可控；
- 只是绕过症状，没有修复语义丢失。

### 4.3 不采用：只给 Discord 加平台特判

也不建议在 `ChatOrchestrator` 里判断 `source.Protocol == "discord"`：

- 把平台知识泄漏到 Application；
- 后续 Satori/Slack 会重复；
- 真正的问题是 mapper 丢失了 mention 信号。

### 4.4 补充：mention 门控日志

无论最终实现如何，`ChatOrchestrator` 在 mention 门控丢弃时至少输出一条 debug 日志：

```text
NLMP message dropped by mention gate: source=discord:... conversation=... message=...
```

不记录正文，不记录用户隐私。  
否则线上无法区分“没收到”“收到了但被过滤”“responder 返回 null”。

### 4.5 附带复核：`@everyone`

当前 `DiscordEventMapper` 的 `RequireMention` 条件允许 `message.MentionEveryone` 通过。  
这会先触发 typing，再被 orchestrator 的 self mention 门控丢弃。

修复 BUG-003 时一并确认：

- `@everyone` 不应等价于“@bot”；
- 如产品不希望响应 `@everyone`，mapper 不应仅因 `MentionEveryone` 放行；
- 加测试固定“`@everyone` 不触发 AI”，避免再次出现 typing 闪烁。

---

## 5. 开发侧修复（本轮）

| 文件 | 修复 |
| --- | --- |
| `src/Natter.Chat.Discord/DiscordEventMapper.cs` | `MapMentions` 保留 self；`@everyone` 不再单独放行 `RequireMention` |
| `src/Natter.Chat.Discord/DiscordChatUpstream.cs` | 出站 send/update 的 mention id 过滤 `Source.SelfId` |
| `src/Natter.Application/Chat/ChatOrchestrator.cs` | mention 门控丢弃增加 debug 日志（不含正文） |
| `tests/Natter.Framework.Tests/DiscordMentionGateTests.cs` | 翻转为回归测试；mapper → orchestrator 串联；`@everyone` 守护 |
| `tests/Natter.Framework.Tests/DiscordTransportTests.cs` | 出站 self mention 过滤断言 |
| `tests/Natter.Live.Tests/GatewayIngressLiveTests.cs` | 真实 ingress 覆盖（self mention 结构信号在真实 bot 自发消息上不可见，单元 mapper 用例覆盖 human 入站） |

修复提交：`687f27f fix: 保留入站 self mention 并过滤出站 self`。

## 6. 实施清单

### 5.1 产品代码

| 文件 | 改动 |
| --- | --- |
| `src/Natter.Chat.Discord/DiscordEventMapper.cs` | `MapMentions` 保留 self mention；复核 `MentionEveryone` 放行条件 |
| `src/Natter.Chat.Discord/DiscordChatUpstream.cs` | 出站 `mentionIds` 过滤 `Source.SelfId`；update 同规则 |
| `src/Natter.Application/Chat/ChatOrchestrator.cs` | mention 门控丢弃增加 debug 日志 |
| `tests/Natter.Framework.Tests/DiscordMentionGateTests.cs` | 从“特征化缺陷”改为“修复回归” |
| `tests/Natter.Framework.Tests/DiscordTransportTests.cs` | 增加 mapper → orchestrator 串联断言 |

### 5.2 测试要求

1. **入站映射**
   - 真实 `<@bot>` 消息映射后，`Content.Text` 不含 mention 文本；
   - `Content.Mentions` 保留 `MentionKind.User` 的 self；
   - `@everyone` 不产生 self mention。

2. **编排**
   - guild 消息带 self mention → responder 被调用；
   - guild 消息不带 self mention → responder 不被调用；
   - DM → 不受 mention 门控影响；
   - 门控丢弃有 debug 日志。

3. **出站**
   - `SendAsync` 的 `mentionIds` 不包含 self；
   - `UpdateAsync` 的 `mentionIds` 不包含 self；
   - 其他用户 mention 仍然保留；
   - `AllowedMentions` 不允许 `@everyone`/`@here`。

4. **真实链路**
   - `DiscordMentionGateTests` 翻转断言；
   - `scripts/live-test.sh ingress`；
   - 重新部署长驻实例，真实频道 `@bot` 两条消息；
   - 确认 typing → DeepSeek 调用 → 回复；
   - 确认日志有事件与 responder 记录。

### 5.3 不做

- 不把测试侧的临时绕过写进产品；
- 不把 `RequireMentionOrDirect` 默认改为 false；
- 不在 Discord plugin 里直接调用 AI；
- 不用“关闭 requireMention”作为验收方案。

---

## 6. 回归与验收闸门

BUG-003 关闭前：

- [x] `DiscordMentionGateTests` 从“复现缺陷”改为“验证修复”；
- [x] 新增 mapper → orchestrator 串联测试；
- [x] 新增出站 self mention 过滤测试；
- [x] 177/177 框架回归通过；
- [x] 真实 ingress 2/2 通过；L1–L4 自动 live 仍通过；
- [ ] 真实频道人工 `@bot` 收到回复（L5 待复测）；
- [x] mention 门控丢弃有 debug 日志（不含正文）；
- [x] DM、非 mention guild 消息行为不变；
- [x] `@everyone` 行为有明确测试；
- [ ] 测试记录与处置记录归档（待 L5 复测）。

验收顺序：

```text
单元/特征化测试
  → mapper → orchestrator 串联
  → live ingress
  → scripts/live-test.sh all
  → 长驻实例人工闸门
```

---

## 7. OBS-003：残留测试消息

测试记录发现 13:05:54 有一条 `natter-live-send` bot 消息，判断为上一轮清理遗漏，13:29 已删除（204）。

处置：

- 不修产品；
- live 测试的 `finally` 清理在进程中断/超时时可能失效；
- 测试脚本在收尾阶段增加一次频道级清扫；
- 测试记录保留 message id 和删除结果，避免误判为产品消息；
- 后续 live run 开始/结束都做残留检查。

---

## 8. 临时工作区

本次测试侧**没有修改产品代码**，只新增：

- `docs/testing/2026-11-15-live-manual-gate.md`
- `tests/Natter.Framework.Tests/DiscordMentionGateTests.cs`
- `scripts/live-serve.sh`

这些是测试资产，可以保留。  
BUG-003 修复必须由开发侧提交，不把特征化测试的“通过”当成修复完成。

---

## 9. 安全

- 继续使用 `artifacts/live/.env`，不记录 key/token；
- 长驻实例日志只记录 source/conversation/message id，不记录正文和凭据；
- 人工闸门测试消息测试后清理；
- 归档前扫描日志、脚本、文档中的 token/API key；
- 真实频道测试结束停止长驻进程，避免残留 bot 在线。

---

## 10. 归档规则

本文是临时配对文档。

归档条件：

- BUG-003 关闭；
- 真实频道人工闸门通过；
- 干净 HEAD 框架回归 + live 回归通过；
- OBS-003 清理策略落地；
- 测试记录补充最终结论。

归档动作：

1. 与 [人工闸门测试记录](2026-11-15-live-manual-gate.md) 一起移入归档目录；
2. 标记 `Archived`；
3. 更新 [测试记录索引](README.md)；
4. 保留 BUG-003 编号、修复提交 SHA、人工闸门证据；
5. 不保留临时日志和测试消息。

---

## 11. 一句话结论

**BUG-003 不是“Discord 没收到”，而是“收到了、被 mapper 洗掉了 mention 信号、被 orchestrator 静默丢掉”。**  
修复方向是：**入站保留 self mention，出站过滤 self mention，门控加日志，补 mapper→orchestrator 串联测试**；不要在 orchestrator 里关掉通用 mention 门控来绕过。
