# Natter 真实 API 联调测试方案（Discord + DeepSeek）

> **状态**：Accepted · L1–L4 已在本机对真实 DeepSeek / Discord API 执行通过；L5 人工闸门待人工触发  
> **日期**：2026-11-15  
> **被测对象**：`natter.transport.discord`（`Natter.Sample.Plugin.Discord` + `Natter.Chat.Discord`）、`natter.feature.ai.deepseek`（`Natter.Feature.Ai.DeepSeek`）  
> **入口**：`tests/Natter.Live.Tests`、`scripts/live-test.sh`  
> **关联**：[Discord 传输设计](DISCORD_PLUGIN_DESIGN.md)、[AI Provider 设计](AI_PROVIDER_DESIGN.md)、[NPS v1](PLUGIN_SPEC.md)

---

## 0. 一句话结论

两个插件在 **真实 Discord 网关 + 真实 DeepSeek Chat Completions** 上完成了「入站 → NLMP → responder → 出站」的完整链路，基线轮 `Category=Live` 的 **26 项真实测试全部通过**（另有 1 项人工闸门默认跳过）、**172 项框架单测回归通过**；开发修复后本轮已重跑 L1 8/8、L4 5/5、L3/L3b 2/2，框架回归 **175/175**。过程中记录了 2 个会直接导致 CLI 无法加载插件的真实缺陷（见 §7，已由开发修复，待全量 live 复测）。

---

## 1. 测试目标

| 目标 | 判定方式 |
| --- | --- |
| 适配器契约与官方 API 一致 | 真实 HTTP 断言（模型列表、usage 字段、401/400 映射） |
| Discord 网关可用 | `Ready` 事件给出 guild/channel/latency，`upstreams` 报 `connected: true` |
| 出站能力真实生效 | 发送/编辑/删除/分片/reply 引用由 REST oracle 复核 |
| 入站映射正确 | 网关 `MESSAGE_CREATE` → NLMP 事件的 conversation/sender/mention 清理 |
| 端到端可用 | 一条 NLMP 输入 → 真实 DeepSeek 文本 → 真实 Discord 回复 |
| 部署形态可用 | 独立 CLI 进程从文件目录加载插件、从环境变量取 secret |
| 失败可诊断 | 缺 secret、错误 key、未知模型都给出结构化错误而非崩溃/泄漏 |
| 不产生副作用 | 全部测试消息自建自删；同一事件重放不重复发送 |

**非目标**：slash 命令交互（需要真实用户点击）、附件上传、sharding、多 bot、长时间 soak、并发压测上限（仅做 3 并发烟测）。

---

## 2. 分层矩阵

| 层 | 项目/文件 | 真实依赖 | 覆盖 | 数量 |
| --- | --- | --- | --- | --- |
| L1 provider | `DeepSeekLiveTests` | DeepSeek HTTP | 模型列表、thinking 开关、`reasoning_content` 隔离、usage/缓存、401/400/不可达、3 并发 | 8 |
| L2 transport | `DiscordProbeLiveTests`、`DiscordUpstreamLiveTests` | Discord Gateway + REST | 身份、READY 指标、typing、reply、upstream 发送/编辑/删除、分片、source 校验、自发消息可见性探针 | 9 |
| L2b 入站 | `GatewayIngressLiveTests` | Discord Gateway + REST | 真实入站映射、mention 清理、conversation 映射、`ignoreSelf` 过滤 | 2 |
| L3 编排 E2E | `OrchestrationLiveTests` | Gateway + DeepSeek + REST | 双插件激活、真实网关连接、注入一条 NLMP → 真实 AI 回复 → REST 复核、幂等重放、`ai status` | 1 |
| L3b 文件插件 E2E | `FilePluginE2ELiveTests` | 文件插件目录 + Gateway + DeepSeek + REST | NPS 从 `lib/` 包加载（私有依赖、secret 引用）、真实网关连接、真实 AI 回复、`discord status` | 1 |
| L4 部署 | `DeploymentCliLiveTests` | 独立 CLI 进程 + 文件插件 + 环境 secret | 插件布局加载、`upstreams`/`discord status`/`ai status`、缺 secret 诊断 | 5 |
| L5 人工闸门 | `ManualGateLiveTests` | 真人 + 全链路 | 人类消息触发 AI 回复（`NATTER_LIVE_MANUAL=1`） | 1（默认 skip） |

设计要点：

1. **真实端点、受控输入**。E2E 在 NLMP 边界注入恰好一条事件，而不是让 bot 在频道里自问自答；入站方向由 L2b 用 REST 真实触发。
2. **REST 作为 oracle**。回复内容、编辑结果、分片数量、reply 引用一律用 `/channels/{id}/messages` 复核，不信任插件自报。
3. **独立进程验证部署**。L4 用 `dotnet <cli>/natter.dll --plugin-dir ...` 运行，让程序集加载上下文、`lib/` 布局、`deps.json`、环境变量 secret 全部进入被测范围。
4. **零残留**。每个用例在 `finally` 中 `DELETE` 自己创建的消息（输入、回复、分片、父消息）。

---

## 3. 凭据与配置

### 3.1 凭据来源

```text
优先级：进程环境变量 > artifacts/live/.env（git-ignored）
```

- `DEEPSEEK_API_KEY`、`DISCORD_BOT_TOKEN`、`DISCORD_TEST_GUILD_ID`、`DISCORD_TEST_CHANNEL_ID`、`DISCORD_BOT_USER_ID`
- 模板：`tests/Natter.Live.Tests/.env.example`；实际文件：`artifacts/live/.env`（`chmod 600`，`.gitignore` 已忽略 `artifacts/`）
- 插件侧仍坚持 **secret reference**：`config.json` 只写 `${secret:DEEPSEEK_API_KEY}` / `${secret:DISCORD_BOT_TOKEN}`，真实值只经 `ISecretProvider` 注入
- 断言层面做了防泄漏：所有异常消息、CLI 输出、`ai status`、测试日志都断言不含 key/token

### 3.2 目标环境

| 项 | 值 |
| --- | --- |
| Bot | `大肥鱼酱#4237`，id `1571154023493410836`，已验证 `bot: true` |
| Guild | `nadamu的服务器` `1123031326354841661` |
| 测试频道 | `常规` `1123031326354841665`（文字频道） |
| Bot 在该频道的权限 | View Channel、Send Messages、Read Message History、Embed Links、Attach Files、Add Reactions；**无** Manage Channels / Manage Webhooks |
| Intents | Guilds、GuildMessages、DirectMessages、MessageContent（已在 Developer Portal 开启） |
| 模型 | `deepseek-flash`（1,048,576 ctx / 393,216 max output）；`deepseek-v4-pro` 可用 |

> 由于没有 Manage Channels，测试**复用**既有频道而不新建临时频道；因此测试消息必须自删，且并发执行会互相干扰（见 §9）。live 套件通过 `CollectionBehavior(DisableTestParallelization = true)` 强制串行：单 bot 多 gateway session 会互相竞争，同频道消息也会互相污染。

---

## 4. 执行方式

```bash
# 一次性
cp tests/Natter.Live.Tests/.env.example artifacts/live/.env && chmod 600 artifacts/live/.env
$EDITOR artifacts/live/.env

# 全部真实测试（默认不含人工闸门）
scripts/live-test.sh all

# 分层
scripts/live-test.sh deepseek   # 最快，只打 DeepSeek
scripts/live-test.sh discord    # 网关 + REST + upstream
scripts/live-test.sh ingress    # 真实入站映射
scripts/live-test.sh e2e        # 端到端（真实 AI + 真实发送）
scripts/live-test.sh deploy     # 独立 CLI 进程
scripts/live-test.sh manual     # 人工闸门，需人在频道 @bot
```

未设置 `NATTER_LIVE=1` 时，全部 live 用例以 `Skipped` 出现在报告中，离线 `dotnet test` 不受影响。CI 不配置凭据即可保持绿色。

---

## 5. 本轮真实执行结果

### 5.1 L1 DeepSeek（8/8 通过，总耗时 5.3s）

| 用例 | 真实证据 |
| --- | --- |
| `/models` | `deepseek-flash`、`deepseek-v4-pro`；flash `context=1048576`、`max_output=393216` |
| thinking `disabled` | 944ms；`in=34 out=7 total=41 cached=0 reasoning=0`；content=`natter-live-ok` |
| thinking `enabled` | `reasoning_content` 57 字符（`We need answer only total. 3+5=8...`）、`content=8`；适配器 `Text=8`，`ReasoningTokenCount=18`，reasoning 未进入 `ChatResponse.Text` |
| 缓存 | 同一 4000 字符 prompt 第二次 `prompt_cache_hit_tokens=256 / miss=252` |
| 3 并发 | 全部成功，wall=562ms，输出 `ok1/ok2/ok3` |
| 无效 key | `Authentication` + `401`，消息 `DeepSeek authentication failed.`，不含 key |
| 未知模型 | `InvalidRequest` + `400`，错误消息明确列出支持模型 |
| 不可达端点 | `Unavailable`（retryable） |

### 5.2 L2 Discord 传输

| 用例 | 真实证据 |
| --- | --- |
| 身份 | `/users/@me` → id `1571154023493410836`，`bot=true` |
| READY | `guilds=1 channels=4 latency=448ms`，`selfId` 与配置一致 |
| typing | 真实 `POST /channels/{id}/typing` 成功 |
| reply 引用 | `referenced_message.id` 等于父消息 id |
| upstream 往返 | 发送 → REST 读到 `natter-live-send` → 编辑读到 `natter-live-updated` → 删除后 404 |
| 分片 | 649 字符 + `maxMessageLength=200` → 4 条真实消息，每条 ≤200 |
| source 校验 | 不匹配 source 返回 `InvalidRequest`，无网络调用 |
| 自发消息可见性（探针） | **bot 会收到自己发送的 `MESSAGE_CREATE`**（`selfMessageVisible=True`） |
| `ignoreSelf` | 同样自发消息在 `ignoreSelf=true` 下产生 0 个 NLMP 事件 |

### 5.3 L2b 真实入站

```text
mapped id=1571381192731791442 conversation=1123031326354841665 kind=Guild
guild=1123031326354841661 sender=1571154023493410836
text=natter-live-ingress-56e5d1e0764545189242e70504615fa3
```

`<@!botId>` 被 `CleanContent` 剥离，conversation/guild/sender 全部正确。

### 5.4 L3 端到端（1/1 通过，12s）

```text
plugin natter.transport.discord      state=Activated trust=bundled
plugin natter.feature.ai.deepseek    state=Activated trust=bundled
upstream discord:discord:1571154023493410836 connected=True
reply author=1571154023493410836 chars=14 text=17 + 25 is 42.
repliesAfterReplay=1
ai status: requests=1 errors=0 p50LatencyMs=678 inputTokens=35 outputTokens=8
```

判据全部满足：回复由 bot 账号发出、内容非空且 ≤1900、不含 `reasoning_content`、不含 key、同 event id 重放不产生第二条消息。

### 5.4b L3b 文件插件端到端（1/1 通过，12s）

```text
staged .../discord from samples/Natter.Sample.Plugin.Discord/bin/Debug/net10.0
staged .../deepseek from src/Natter.Feature.Ai.DeepSeek/bin/Debug/net10.0
plugin natter.feature.ai.deepseek  state=Activated trust=local
plugin natter.transport.discord    state=Activated trust=local
file-plugin reply chars=11 text=6 × 7 = 42.
discord status: connected=true guildCount=1 channelCount=4 latencyMs=497
```

与 L3 的差别：插件不在进程内 embedded，而是由 NPS 从 `lib/` 目录加载，因此 `PluginLoadContext` 的共享/私有策略、`Discord.Net` 与 `Microsoft.Extensions.AI` 的私有解析、`config.json` 的 `${secret:...}` 引用都被真实执行。

### 5.5 L4 独立 CLI 进程（5/5 通过）

| 命令 | 结果 |
| --- | --- |
| `plugins list --json` | 两插件 `state=activated`，`error=null` |
| `upstreams --json` | `platform=discord`、`selfId` 正确、`connected=true` |
| `discord status --json` | `connected=true guilds=1 channels=4 latency=477`，capabilities `edit/delete=true` |
| `ai status --json` | `provider=deepseek model=deepseek-flash thinking=disabled`，无 key |
| 缺 secret（清空 env） | `plugins list --detail` → 两插件 `Failed` + `missing-secret` + `Required secret '...' is missing.`，退出码 0（不崩溃） |

### 5.6 真实环境观测

- **一次瞬时 550**：全量跑中出现 `kind=Unavailable status=550 message=DeepSeek is temporarily unavailable.`，适配器把服务端瞬时不可用正确归类为 `Unavailable`（可重试）。DeepSeek 真实调用因此加了**有限重试**（最多 3 次，仅对 `Retryable`/`HttpRequestException`），错误映射本身仍有独立断言。
- **Gateway 延迟**：本轮 448–497ms，稳定可用。
- **Discord 写操作**：全部成功，未见 429；分片 4 条连发也未触发限流。

### 5.7 回归

```text
Natter.Framework.Tests: Failed 0, Passed 175, Skipped 0
Live re-run after fixes: L1 8/8, L4 5/5, L3/L3b 2/2 (15/26; discord/ingress pending)
```

---

## 6. 隔离与安全策略

1. **三层自问自答防护**：`DiscordOptions.IgnoreBots`、`DiscordOptions.IgnoreSelf`、`ChatOrchestrationOptions.IgnoreSelfMessages`。探针证明 Discord **确实会把 bot 自己的 `MESSAGE_CREATE` 推给同一个 bot**，所以 `ignoreSelf=true` 是硬性前提；L2b 验证了该过滤（0 个事件），E2E 则用 human sender + `ignoreSelf=true` 保证回复不会被当成新输入。
2. **输入注入而非伪造网关**：L3/L3b 只在 NLMP 边界注入一条事件，不伪造 transport 事件、不绕过 mapper；真实入站方向由 L2b 用 REST 触发网关事件单独覆盖。
3. **消息自清理**：所有创建的消息 ID 都在 `finally` 中删除；回复消息通过 `referenced_message` 精确定位后删除。
4. **凭据不落盘**：仓库只存 `${secret:...}`；真实值在 `artifacts/live/.env`；测试输出统一做 `***` 脱敏。
5. **上限保护**：单次回复 `maxOutputTokens=200`、`timeoutSeconds=60`，单用例硬超时 120s；不跑压测以免触发 Discord 写限流。

---

## 7. 测试发现的问题（待开发确认）

> 测试只负责发现问题、留下证据；本节改动是测试侧为解除阻塞做的**临时处置**，不是修复。完整时间线与回退方式见[本轮测试记录](testing/2026-11-15-live-deepseek-discord.md#6-临时改动)。

### BUG-001 · 插件加载上下文把所有 `Natter.*` / `Microsoft.Extensions.*` 当作宿主共享

- **现象**：把插件目录放进 CLI 后，`plugins list` 显示
  - `natter.transport.discord` → `load-failed: Could not load file or assembly 'Natter.Chat.Discord'`
  - `natter.feature.ai.deepseek` → `load-failed: Could not load file or assembly 'Microsoft.Extensions.AI.Abstractions, Version=10.11.0.0'`
- **根因**：`PluginLoadContext.SharedPrefixes` 用 `Natter.` / `Microsoft.Extensions.` 前缀判定为「宿主共享」，返回 `null` 交给默认 ALC；但宿主（`Natter.Cli`）并不引用这两个程序集，默认 ALC 里没有，于是加载失败。插件目录里明明带着这两个 DLL。
- **测试侧临时处置**（不是修复，待开发评审）：
  - 共享策略改为 **精确的契约程序集白名单**（`Natter.Abstractions`、`Natter.Chat`、`Natter.Plugin.Abstractions`、`Natter.Infrastructure` 等 10 个）；
  - 新增 `PrivateOverrides`（`Microsoft.Extensions.AI*`）优先私有加载；
  - `Load` 在 `AssemblyDependencyResolver` 返回 null 时，再按 `插件入口目录/<name>.dll` 兜底——符合 `docs/PLUGINS.md` 的「程序集与依赖位于同一插件目录」约定；
  - `Microsoft.Extensions.*` 不再无条件共享：先向默认 ALC 询问能否解析，能解析才共享，否则用插件自带副本（否则 `Microsoft.Extensions.Caching.*` 这类宿主未引用的扩展包会在首次真实调用时 `FileNotFoundException`）。
- **守护测试**：`tests/Natter.Framework.Tests/PluginLoadContextTests.cs` 3 项（无 deps.json 的目录加载、`Microsoft.Extensions.AI` 私有加载、契约程序集留在默认上下文）+ L4 `Cli: both file-system plugins load from the staged layout`。

### BUG-002 · 插件 staging 没有把 `deps.json` 放进 `lib/`

- **现象**：即使 BUG-001 已临时解除，`AssemblyDependencyResolver` 也解析不到 `Discord.Net.*` / `Newtonsoft.Json` 等私有依赖，因为它要求 `lib/<assembly>.deps.json` 与入口程序集同目录。
- **根因**：两个 `Stage*Plugin` target 只复制 `$(TargetPath)` 与 `@(ReferenceCopyLocalPaths)`，漏了 `$(AssemblyName).deps.json`。
- **测试侧临时处置**（不是修复）：`samples/Natter.Sample.Plugin.Discord.csproj`、`src/Natter.Feature.Ai.DeepSeek.csproj` 的 staging target 追加 `$(TargetDir)$(AssemblyName).deps.json`。
- **守护测试**：同上 L4 用例 + BUG-001 的目录兜底（即使漏 staging 也能工作）。

> 这两个问题在进程内单测中不可见（测试项目自己引用了这两个程序集，默认 ALC 里恰好存在），只有「独立 CLI 进程 + 文件插件目录」才暴露。

---

## 8. 已知限制

1. **单 bot 身份**：没有第二个可发言身份（只有 bot token，无用户 token、无 webhook 管理权限），因此无法用另一个身份自动产生「人类」入站消息。入站方向用「bot 自发消息 + `ignoreSelf=false`」验证映射，回复方向用 NLMP 注入验证，真正的人类触发留给 L5。
2. **L5 需人工**：`scripts/live-test.sh manual` 会打印提示并等待 180s，需要有人在测试频道 `@bot` 发言。
3. **频道复用**：无 Manage Channels 权限，不能建独立临时频道；并行跑多个 live 测试可能互相看到消息，建议串行。
4. **覆盖缺口**：slash 交互（需要真人点击）、附件上传/下载、streaming（v1 明确不支持）、rate limit 429 主动触发、长时间断线重连、多 guild/多频道。
5. **计费**：L1 每轮约 10 次短请求，L3 每轮 1 次；按 `deepseek-flash` 价格可忽略，但仍属于真实计费调用。

---

## 9. 后续扩展建议

| 优先级 | 项 | 方式 |
| --- | --- | --- |
| 高 | slash 命令真实交互 | 用真人点击 + interaction token 回放，或第二个 bot 触发 |
| 高 | 附件链路 | 用 REST 上传 PNG → 验证 mapper 的 `AttachmentKind` 与 `maxAttachmentBytes` |
| 中 | 429 / 5xx 降级 | 用 WAF 或本地反向代理注入 429，验证 `DeepSeekErrorKind.RateLimited` 与 fallback 文本 |
| 中 | 断线重连 | 断开网络/强制 `StopAsync`，验证 `UpstreamStatusEvent` 与重连后的 `ready` |
| 低 | thinking E2E | 在 L3 用 `thinking=enabled` 验证 `reasoning_content` 永不进入 Discord 消息 |
| 低 | 长上下文 | 1M context 边界、`maxInputCharacters` 截断行为 |
