# 2026-11-15 · 真实 API 联调：DingTalk Stream + iFlytek Spark（端到端通过）

## 1. 概要

- **测试时间**：2026-11-15 15:35–15:40（Asia/Shanghai）
  - 15:35 凭据冒烟（DingTalk gateway open、Spark models/chat）
  - 15:37 插件 staging + CLI 加载验证
  - 15:37 长驻实例启动（钉钉 Stream 连接）
  - 15:39 操作者在钉钉群「NDM」@机器人 发送「你好」，**收到回复**
  - 15:41 补齐自动化：`SparkLiveTests` 7/7、`DingTalkStreamLiveTests` 2/2
  - 15:42 长驻实例重启（等待后续人工试用）
  - 15:43 取证与归档
- **被测对象**：`natter.transport.dingtalk`（`Natter.Chat.DingTalk` + `Natter.Sample.Plugin.DingTalk`）、`natter.feature.ai.spark`（`Natter.Feature.Ai.Spark`）
- **基线**：`git HEAD=969341d`（工作区 clean）；`Natter.Framework.Tests` **190/190 通过**
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；钉钉机器人 `dingeojh87nyyna3lqpg`；Spark MaaS `spark-x2.5-1.7b`
- **结论**：**端到端通过**。凭据有效、插件可加载、钉钉 Stream 真实连接成功、Spark Chat Completions 真实可用；人工 @机器人 后 20 字回复成功。

## 2. 凭据冒烟（真实平台）

| 项 | 请求 | 结果 |
| --- | --- | --- |
| DingTalk Stream | `POST https://api.dingtalk.com/v1.0/gateway/connections/open` | **200**，返回 `endpoint=wss://wss-open-connection.dingtalk.com:443/connect` + `ticket` |
| Spark 模型列表 | `GET https://maas-api.cn-huabei-1.xf-yun.com/v2/models` | **200**，`spark-x2.5-1.7b`（唯一模型，`owned_by=xunfei`） |
| Spark Chat | `POST .../v2/chat/completions`（`thinking.type=disabled`、`max_completion_tokens=64`） | **200**，0.89s，`content="spark-live-ok"`，usage `prompt=33 completion=6 total=39`，`reasoning_content=null` |

## 3. 插件与部署验证

```bash
# staging：samples/Natter.Sample.Plugin.DingTalk + src/Natter.Feature.Ai.Spark 的 lib/manifest/schema
# 配置目录：artifacts/live/plugins-dt/{dingtalk,spark}（config.json 仅含 ${secret:...}）
dotnet build Natter.slnx
natter --plugin-dir artifacts/live/plugins-dt plugins list --detail
natter --plugin-dir artifacts/live/plugins-dt upstreams --json
natter --plugin-dir artifacts/live/plugins-dt dingtalk status --json
natter --plugin-dir artifacts/live/plugins-dt spark status --json
```

| 检查 | 结果 |
| --- | --- |
| `plugins list` | `natter.transport.dingtalk`、`natter.feature.ai.spark` 均 `Activated`（trust=local） |
| `upstreams --json` | `source=dingtalk:dingtalk:dingeojh87nyyna3lqpg`、`connected=true`、capabilities：group/direct、mentions、max 2000 |
| `dingtalk status` | `connected=true`、topic `/v1.0/im/bot/messages/get`、`requireMention=true` |
| `spark status` | `provider=iflytek-spark`、`model=spark-x2.5-1.7b`、`thinking=disabled`、`requests=0`（尚未调用） |

长驻实例（15:37 启动，窗口 3600s）：

```text
[debug] ChatIngressLifecycle: NLMP ingress subscribed to upstream dingtalk:dingtalk:dingeojh87nyyna3lqpg.
[information] NatterHost: Natter host started with 4 plugin(s) and 3 lifecycle hook(s).
Watching domain events for 3600s (Ctrl+C to stop)...
```

日志：`artifacts/live/serve/dingtalk-spark-1537.log`（首轮）、`dingtalk-spark-1542.log`（重启，等待后续人工试用）

## 4. 配置要点

| 插件 | 配置 |
| --- | --- |
| dingtalk | `clientId=dingeojh87nyyna3lqpg`、`botId=dingeojh87nyyna3lqpg`、`robotCode=dingeojh87nyyna3lqpg`、`clientSecret=${secret:DINGTALK_CLIENT_SECRET}`、`requireMention=true`、`maxMessageLength=2000` |
| spark | `apiKey=${secret:SPARK_API_KEY}`、`baseUrl=https://maas-api.cn-huabei-1.xf-yun.com/v2`、`model=spark-x2.5-1.7b`、`thinking=disabled`、`historyMaxMessages=20`、`maxOutputTokens=400` |

凭据：`artifacts/live/.env`（git-ignored，`chmod 600`）；仓库与日志中无明文。

## 5. 人工入站（通过）

操作者在钉钉群「NDM」内 @大肥鱼酱 发送「你好」，机器人回复「你好，我是 Natter，随时为您效劳。」

| 证据 | 内容 |
| --- | --- |
| 群内截图（15:39） | 用户 `@大肥鱼酱 你好` → 机器人「你好，我是 Natter，随时为您效劳。」 |
| 实例日志 | `[information] spark: natter.feature.ai.spark: ai replied conversation=… cidl/MRR8Fp0IM2FzKeP4PjHQ==, 20` |
| 字符数对照 | 回复正文 20 个字符（`你好，我是 Natter，随时为您效劳。`），与日志 `replyChars=20` 一致 |
| 重复投递 | 日志仅 1 次 `ai replied`，群内仅 1 条回复 → Stream ack 正常工作，未触发重推 |

链路确认：Stream 回调 → `DingTalkCallbackParser` → `DingTalkChatUpstream`（补 self mention 使 orchestrator 的 mention 门控通过）→ Spark `spark-x2.5-1.7b` → `sessionWebhook` 回复。

## 6. 自动化补充（15:41）

| 套件 | 数量 | 结果 |
| --- | --- | --- |
| `SparkLiveTests` | 7 | **7/7 通过**：`/models`；thinking disabled 483ms `44/6/50`、`text=spark-live-ok`；thinking enabled `hasReasoning=True`、`text=5`；假 key→401 `Authentication`（不泄漏）；未知模型→400 `InvalidRequest`（`No Available Source`）；不可达→`Unavailable`；3 并发全部成功 |
| `DingTalkStreamLiveTests` | 2 | **2/2 通过**：真实 open-connection + WebSocket attach（`connected=True`）、`DisconnectAsync` 后 `False`；错误 secret→`HTTP 401` 且不泄漏 secret |

运行：

```bash
NATTER_LIVE=1 dotnet test tests/Natter.Live.Tests   --filter "FullyQualifiedName~SparkLiveTests|FullyQualifiedName~DingTalkStreamLiveTests"
```

## 7. 已知限制与后续

1. **人工入站仍是唯一人工项**：钉钉机器人无法用自身身份产生「人类 @」，自动化覆盖到「连接 + 回调解析 + ack + 回复链路」为止。
2. **未覆盖**：
   - 共享 responder 交叉矩阵真实组合（Discord+Spark、DingTalk+DeepSeek）；
   - 钉钉卡片/图片/语音（插件明确不支持）；
   - Spark 流式/tools（v1 不支持）；
   - 断线重连的真实网络中断演练（仅有退避重连代码与单元测试）。
3. **能力降级**：钉钉 `reply=false`、`edit/delete=false`，回复走 sessionWebhook 纯文本，需确认群内展示与 @ 语义。
4. **凭据轮换**：`DINGTALK_CLIENT_SECRET` / `SPARK_API_KEY` 已进入本机 `.env`，如需更换请同步更新该文件。

## 8. 结论

- 钉钉 Stream：真实连接、回调、ack、回复**全部通过**；
- Spark MaaS：真实 `models` / `chat/completions` 通过，`spark-x2.5-1.7b` 可用，thinking disabled 下无 reasoning 泄漏；
- 插件加载与上游连接通过（两个插件 Activated、`connected=true`）；
- **端到端（钉钉群 @机器人 → Spark → 群回复）通过**，20 字回复与日志 `replyChars=20` 一致；
- 自动化 9/9 通过（Spark 7 + DingTalk 连接 2），后续可直接回归。
- 本轮未修改产品代码。
