# 2026-11-15 · 人工闸门（真人 @bot）未通过

> **开发侧处置**：[开发侧处置记录](2026-11-15-live-manual-gate-response.md)（临时配对文档，BUG-003 修复并复测后归档）。

## 1. 概要

- **测试时间**：2026-11-15 13:21–13:29（Asia/Shanghai）
  - 13:22 试运行 30s（验证宿主启动）
  - 13:23:39 正式长驻实例启动
  - 13:24:26 / 13:25:03 操作者发送两条真实 @bot 消息
  - 13:26:40 检查，日志无任何增量
  - 13:27 定位根因并写出复现测试
  - 13:28 停止实例、清理频道残留
- **被测对象**：`natter.transport.discord` + `natter.feature.ai.deepseek` 的真实人工链路（L5 人工闸门）
- **基线**：`git HEAD=e6cd8c9`（工作区 clean，已包含 3947c04 加载策略修复、366d5d6 DeepSeek 重试）
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；bot `大肥鱼酱` id `1571154023493410836`；guild `1123031326354841661`；频道 `#常规` `1123031326354841665`
- **结论**：**未通过**。真实 @bot 消息在 NLMP 层被静默丢弃，typing 出现后无回复；根因是「mapper 剔除 self mention」与「orchestrator 用 mentions 判定是否被 @」两套判定冲突 → [BUG-003](#bug-003--guild-中真实-bot-mention-被编排器静默丢弃)。
- **回归**：`Natter.Framework.Tests` **176/176 通过**（含本次新增的特征化测试 `DiscordMentionGateTests`）。

## 2. 部署配置

```bash
# 凭据来自 artifacts/live/.env（git-ignored）
scripts/live-serve.sh --foreground 3600   # 前台长驻（PID namespace 沙箱内 setsid 会被回收）
# 实际执行：
dotnet src/Natter.Cli/bin/Debug/net10.0/natter.dll \
  --plugin-dir artifacts/live/plugins --verbose \
  events watch --seconds 3600
```

| 配置项 | 值 |
| --- | --- |
| `token` / `apiKey` | `${secret:DISCORD_BOT_TOKEN}` / `${secret:DEEPSEEK_API_KEY}` |
| `selfId` | `1571154023493410836` |
| `intents` | Guilds, GuildMessages, DirectMessages, MessageContent |
| `guilds` / `channels` | `1123031326354841661` / `1123031326354841665` |
| `requireMention` / `ignoreBots` / `ignoreSelf` / `typing` | `true` / `true` / `true` / `true` |
| DeepSeek | `deepseek-flash`、`thinking=disabled`、`historyMaxMessages=20`、`maxOutputTokens=400` |
| 日志 | `artifacts/live/serve/natter-live-1323.log` |

启动日志（13:23:39，此后无任何新增）：

```text
[debug] ChatIngressLifecycle: NLMP ingress subscribed to upstream discord:discord:1571154023493410836.
[information] NatterHost: Natter host started with 4 plugin(s) and 3 lifecycle hook(s).
Watching domain events for 3600s (Ctrl+C to stop)...
```

## 3. 现场证据

频道消息（REST `/channels/1123031326354841665/messages`）：

| 时间 | 消息 id | 作者 | 内容 |
| --- | --- | --- | --- |
| 13:24:26 | `1571395431676510340` | nadamu. | `<@1571154023493410836> 你好呀` |
| 13:25:03 | `1571395590493831240` | nadamu. | `<@1571154023493410836> 你好` |

- 两条消息均为**真实 mention**（内容里是 `<@id>`），不是纯文本。
- 操作者观察到：**typing 指示出现后消失，无回复**。
- bot 未发出任何新消息；日志在 13:23:39 后**零增量**（无 error、无 warning、无 DeepSeek 调用记录）。
- typing 出现说明 `DiscordChatUpstream` 已收到 `MESSAGE_CREATE`、`DiscordEventMapper` 已放行并触发了 typing；也就是说消息已经进入宿主，问题在 NLMP 之后。

## 4. 根因

两处 mention 判定不一致：

1. `DiscordEventMapper.MapMessage`
   - 用**原始** `DiscordMessage.MentionedUsers` 判断 `selfMentioned`，满足 `RequireMention` 后放行（因此 typing 会亮）。
   - 构造 NLMP `Content.Mentions` 时，`MapMentions` **主动剔除** self（`if (user.Id == source.SelfId) continue;`），目的是避免 NLMP 再 @ 自己。
2. `ChatOrchestrator.HandleAsync`
   - 默认 `ChatOrchestrationOptions.RequireMentionOrDirect = true`。
   - `MentionsSelf` 只检查 `message.Content.Mentions` 里是否有 `source.SelfId`。
   - 由于第 1 步已剔除 self，判定必然失败 → `return`，且**该分支没有任何日志**（warning 级别只在回复发送失败时打印）。

结果：guild 频道里所有真实 @bot 消息都被丢弃；DM 不受影响（`RequireMentionOrDirect` 对 Direct 直接放行）。自动化 E2E 之所以此前通过，是因为它**在 NLMP 边界注入**了带 self mention 的事件，绕过了 mapper 的剔除逻辑。

### 复现测试

新增 [DiscordMentionGateTests.cs](../../tests/Natter.Framework.Tests/DiscordMentionGateTests.cs)（特征化测试，`Passed` 即代表缺陷被复现）：

```text
Guild_self_mention_is_stripped_from_nlmp_and_then_dropped_by_the_mention_gate
  Assert.Equal("hello", mapped.Content.Text)                       // mention 被剥离后的正文
  Assert.True(mapped.Content.Mentions is null or { Count: 0 })      // 事实：self mention 已不在 NLMP 里
  Assert.Empty(responder.Seen)                                      // 事实：responder 从未被调用
  Assert.Empty(upstream.Messages)                                   // 事实：没有任何回复
```

运行：

```bash
dotnet test tests/Natter.Framework.Tests --filter "FullyQualifiedName~DiscordMentionGateTests"
```

## 5. 具体问题

### BUG-003 · guild 中真实 bot mention 被编排器静默丢弃

- **发现时间**：13:26
- **严重度**：**高**（阻塞真实用户主流程：频道里 @bot 永远无回复，且无任何日志）
- **影响**：`-` 频道内所有 guild 消息不触发 AI；用户只看到 typing 闪烁。DM 不受影响。
- **复现**：按 §2 部署，在频道内真实 @bot 发一句话 → 无回复、日志无增量；或运行 §4 的特征化测试。
- **证据**：§3 的消息表与零增量日志；§4 的代码路径与测试输出。
- **测试侧处置**：新增特征化测试固定该行为；停止实例、清理频道残留。**未修改产品代码。**
- **建议**（给开发，选择其一并补测试）：
  1. **单一判定**：把「是否 @ 了 bot」的判定收敛到上游（mapper 已用原始 mention 判过 `RequireMention`），orchestrator 不再用 `Content.Mentions` 二次判定；或明确 `RequireMentionOrDirect` 仅对没有上游过滤能力的平台生效。
  2. **保留可判定信号**：mapper 在 NLMP 中保留 self mention（或新增 `Message.AddressedToSelf` 之类的显式字段），发送路径（`DiscordChatUpstream.SendAsync` 的 `mentionUserIds`）继续过滤 self，避免自 @。
  3. **补日志**：无论选哪种，mention 门控丢弃应至少有一条 debug/information 日志，否则线上无法自证「收到了但没回」。
- **补充**：现有自动化用例（`ChatOrchestratorTests.Guild_messages_require_a_mention...`）是手工构造带 self mention 的 NLMP 消息，无法覆盖真实映射路径，建议补一条「mapper → orchestrator」串联用例。

## 6. 附带发现

### OBS-003 · 频道内残留测试消息

- 13:05:54 存在一条 bot 消息 `natter-live-send`（id `1571390770714509404`），内容与 `DiscordUpstreamLiveTests.Send_update_delete_round_trip` 的发送内容一致，判断为**上一次复测的清理遗漏**。
- 13:29 已用 REST 删除（`204`）。测试用例的 `finally` 清理在进程被中断/超时时可能失效，建议在测试收尾阶段加一次频道级清扫或在记录中跟踪。

## 7. 结论与后续

- 人工闸门**未通过**；在 BUG-003 修复前，频道 @bot 不可用，不需要重复部署验证。
- 修复后回归顺序：`DiscordMentionGateTests`（应翻转断言）→ `scripts/live-test.sh ingress` → 重新部署人工闸门（§2）→ 复测两条真实消息。
- 自动化补强建议：增加一条**真实入站**的 mention 用例（需第二身份；或临时关闭 mapper 的 `requireMention` 并断言 orchestrator 仍能响应），避免再次出现「注入通过、真实入站失败」的盲区。
