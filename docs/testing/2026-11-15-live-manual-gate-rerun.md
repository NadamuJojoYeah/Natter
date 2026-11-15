# 2026-11-15 · 人工闸门复测（通过）

## 1. 概要

- **测试时间**：2026-11-15 13:49:50–13:50:46（Asia/Shanghai）
  - 13:49:50 实例启动（`NatterHost started with 4 plugin(s)`）
  - 13:50:43 操作者在 `#常规` 真实 @bot 发送「你好」
  - 13:50:46 bot 回复「你好！有什么可以帮你的吗？」并引用该消息
- **被测对象**：`natter.transport.discord` + `natter.feature.ai.deepseek` 的真实人工链路（L5 人工闸门复测）
- **基线**：`git HEAD=a3b6263`（工作区 clean），包含修复 `687f27f fix: 保留入站 self mention 并过滤出站 self`
- **对应首测**：[人工闸门（未通过）](2026-11-15-live-manual-gate.md) · **问题**：`BUG-003`
- **结论**：**通过**。真实 @bot → 网关 → NLMP → orchestrator → DeepSeek → Discord 回复全链路可用，延迟约 3s，reply 引用正确；`BUG-003` 关闭。

## 2. 部署配置

```bash
scripts/live-serve.sh --foreground 3600
# 等效：dotnet src/Natter.Cli/bin/Debug/net10.0/natter.dll \
#         --plugin-dir artifacts/live/plugins --verbose events watch --seconds 3600
```

| 项 | 值 |
| --- | --- |
| 日志 | `artifacts/live/serve/natter-fg-20261115-134950.log` |
| 插件 | 4 个（greetings、metrics、discord、deepseek），ingress 已订阅 `discord:discord:1571154023493410836` |
| Discord | `requireMention=true`、`ignoreBots=true`、`ignoreSelf=true`、`typing=true`、`messageCommands=true` |
| DeepSeek | `deepseek-flash`、`thinking=disabled`、`historyMaxMessages=20`、`maxOutputTokens=400` |
| 凭据 | `artifacts/live/.env`（git-ignored），配置内仅 `${secret:...}` |

## 3. 复测前自动验证

| 项 | 结果 |
| --- | --- |
| `Natter.Framework.Tests` | **177/177 通过**（含 `DiscordMentionGateTests`：self mention 保留 + 出站过滤 + `@everyone` 不再等价于 @bot） |
| `RealInboundMentionLiveTests`（本次新增） | **22s 通过**：真实网关入站 `observed events=1`，`mentions=User:1571154023493410836`，`requests=1`，回复 `9 + 4 = 13.` |
| `scripts/live-test.sh all`（27 项） | **27/27 通过**，2.88 分钟（含新增的真实入站用例） |

> 该用例专门覆盖「真实入站」路径，避免再次出现「NLMP 注入通过、真实网关失败」的盲区。

## 4. 人工复测证据

### 4.1 操作与观察

操作者在 `#常规`（`1123031326354841665`）用 Discord 的 @ 选中 `大肥鱼酱`，发送「你好」。观察到 bot 在约 3 秒内以**回复引用**的形式作答。

### 4.2 REST 复核（`GET /channels/{id}/messages`）

| 时间 | 消息 id | 作者 | 内容 | 引用 |
| --- | --- | --- | --- | --- |
| 13:50:43 | `1571402049789362258` | nadamu.（human） | `<@1571154023493410836> 你好` | — |
| 13:50:46 | `1571402059666825297` | 大肥鱼酱（bot） | `你好！有什么可以帮你的吗？` | `1571402049789362258` |

### 4.3 实例日志

```text
[debug] ChatIngressLifecycle: NLMP ingress subscribed to upstream discord:discord:1571154023493410836.
[information] NatterHost: Natter host started with 4 plugin(s) and 3 lifecycle hook(s).
Watching domain events for 3600s (Ctrl+C to stop)...
[information] deepseek: natter.feature.ai.deepseek: deepseek replied conversation={ConversationId} replyChars={ReplyChars} 1123031326354841665, 13
```

日志出现了修复时新增的 responder 记录（13 字符），与 REST 内容一致。

## 5. 判定

| 判据 | 结果 |
| --- | --- |
| 真实 @bot 触发（不是注入） | 通过 |
| 有回复且非空 | 通过（13 字符中文） |
| reply 引用指向用户消息 | 通过 |
| 延迟可接受 | 约 3s（网关 + DeepSeek + 发送） |
| 无 `reasoning_content` / 无密钥泄漏 | 通过（回复为正常自然语言） |
| 不自问自答 | 通过（回复无 mention，mapper 的 require-mention 门控丢弃） |
| 门控丢弃有日志 | 本次未触发丢弃；修复已补 debug 行，后续可复查 |

## 6. 遗留与建议

1. **多轮上下文**未在人工复测中覆盖（只发了一条）。建议再发 2–3 轮追问，确认 `historyMaxMessages=20` 的记忆与指代；不影响本次判定。
2. **`@everyone` 语义变更**（不再等价于 @bot）由 `DiscordMentionGateTests.Everyone_alone_is_not_a_bot_mention` 覆盖，人工侧未复测。
3. **频道保留历史证据**：13:24/13:25 两条首测消息与本次 13:50 消息+回复仍在频道中；如需清理可手动删除，测试未自动清理人工消息。
4. **实例状态**：13:49:50 启动，按 3600s 窗口于 **14:49:56 正常退出**（`exit code 0`，日志末行 `Watched 0 event(s).`，无 error/warning）。窗口期内仅 13:50 一次人工交互，频道无新增消息；如需继续试用，`scripts/live-serve.sh --foreground 3600` 可随时重启。

## 7. 结论

- `BUG-003`：**关闭**（修复 `687f27f` + 自动化 177/177 + 真实入站 22s + 人工复测通过）。
- 至此 L1–L5 全部通过；本轮真实联调可以归档。
