# 2026-11-15 · 真实 API 联调：Discord + DeepSeek

> **开发侧处置**：[开发侧处置记录](2026-11-15-live-deepseek-discord-response.md)（临时配对文档，BUG-001/002 关闭并复测后归档）。

## 1. 概要

- **测试时间**：2026-11-15 12:17–12:48（Asia/Shanghai）；已采样的时间点：12:27（L4 首跑通过）、12:37（L3b 文件插件 E2E 通过）、12:48（记录归档）。其余阶段按命令运行时长推算，标为「约」。
- **被测对象**：`natter.transport.discord`（`Natter.Sample.Plugin.Discord` + `Natter.Chat.Discord`）、`natter.feature.ai.deepseek`（`Natter.Feature.Ai.DeepSeek`）
- **基线**：`git HEAD=201e4ff`（2026-11-15 11:45:03 +0800，*test: 校验 DeepSeek reasoning token 映射*）；工作区 **11 项未提交变更**（含新增测试资产与 §6 的临时改动）
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；DeepSeek `deepseek-flash` / `deepseek-v4-pro`；Discord bot `大肥鱼酱#4237`（id `1571154023493410836`，`bot=true`）；guild `nadamu的服务器` `1123031326354841661`；测试频道 `常规` `1123031326354841665`
- **结论**：`Category=Live` **26/26 通过**、框架回归 **172/172 通过**、无凭据时 **27/27 skip**；记录 **2 个产品缺陷（BUG-001/002）**、**2 个真实环境观测（OBS-001/002）**、**2 个环境限制（ENV-001/002）**。

## 2. 范围

| 层 | 用例文件 | 数量 | 覆盖 |
| --- | --- | --- | --- |
| L1 provider | `DeepSeekLiveTests` | 8 | `/models`、thinking 开关、`reasoning_content` 隔离、usage/缓存、401/400/不可达、3 并发 |
| L2 transport | `DiscordProbeLiveTests`、`DiscordUpstreamLiveTests` | 9 | 身份、READY 指标、typing、reply、upstream 发送/编辑/删除、分片、source 校验、自发消息探针 |
| L2b 入站 | `GatewayIngressLiveTests` | 2 | 网关 `MESSAGE_CREATE` → NLMP、mention 剥离、conversation 映射、`ignoreSelf` 过滤 |
| L3 编排 E2E | `OrchestrationLiveTests` | 1 | 双插件激活、真实网关、注入 NLMP → 真实 AI 回复 → REST 复核、幂等重放、`ai status` |
| L3b 文件插件 E2E | `FilePluginE2ELiveTests` | 1 | NPS 从 `lib/` 加载、私有依赖、真实一轮、`discord status` |
| L4 部署 | `DeploymentCliLiveTests` | 5 | 独立 CLI 进程加载、`upstreams`/`discord status`/`ai status`、缺 secret 诊断 |
| L5 人工闸门 | `ManualGateLiveTests` | 1 | 真人在频道 `@bot` 触发回复（默认 skip） |

**不测**：slash 交互点击、附件上传、streaming（v1 不支持）、429/5xx 主动注入、长时间断线重连、多 guild/多频道、压测。

## 3. 执行

```bash
cp tests/Natter.Live.Tests/.env.example artifacts/live/.env && chmod 600 artifacts/live/.env
# 填入 DEEPSEEK_API_KEY / DISCORD_BOT_TOKEN / DISCORD_TEST_GUILD_ID /
#      DISCORD_TEST_CHANNEL_ID / DISCORD_BOT_USER_ID

scripts/live-test.sh all          # 26 项，约 2.3 分钟
scripts/live-test.sh deepseek     # 8 项，约 5 秒
scripts/live-test.sh deploy       # 5 项，约 26 秒

dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj   # 172 项回归
env -u NATTER_LIVE dotnet test tests/Natter.Live.Tests/Natter.Live.Tests.csproj  # 27 skip
```

凭据来源：`artifacts/live/.env`（`chmod 600`，`artifacts/` 已被 `.gitignore` 忽略）。仓库与测试输出均不含密钥明文（已扫描 `sk-…`、bot token 前缀，0 命中）。

## 4. 结果

| 时间 | 用例/命令 | 结果 | 关键数据 |
| --- | --- | --- | --- |
| 12:18 | `GET https://api.deepseek.com/models` | 200 | `deepseek-flash` ctx `1048576`、max out `393216`；`deepseek-v4-pro` 可用 |
| 12:18 | `GET https://discord.com/api/v10/users/@me` | 200 | id `1571154023493410836`，`bot=true` |
| 12:19 | `GET /users/@me/guilds`、`GET /guilds/{id}/channels` | 200 | 1 guild / 4 channels；文字频道 `1123031326354841665` |
| 12:21–12:26 | L1 + L2 用例搭建与首跑 | 通过 | thinking disabled 944ms / 34→7 tokens / reasoning 0；thinking enabled `reasoning_content` 57 字符，`ChatResponse.Text="8"`，reasoning tokens 18 |
| 12:26 | 缓存复跑 | 通过 | 第二次 `prompt_cache_hit=256`、`miss=252` |
| 12:27 | L4 首次全绿（时间采样） | 5/5 | `plugins list` 两插件 `activated`；`upstreams connected=true`；`discord status guilds=1 channels=4 latency=477` |
| 12:29 | 强制串行（`DisableTestParallelization`） | 生效 | 单 bot 多 gateway session 不再竞争 |
| 约 12:34–12:36 | 首次全量（无 filter，运行 2.49 分钟） | 24/25，1 失败 | 见 [OBS-001](#obs-001--deepseek-瞬时-550) |
| 12:37 | L3b 文件插件 E2E（时间采样） | 通过 | `state=Activated trust=local`；回复 `6 × 7 = 42.`；`latencyMs=497` |
| 约 12:38–12:41 | `scripts/live-test.sh all` | **26/26 通过** | 耗时 2.32 分钟 |
| 约 12:42 | `dotnet test Natter.Framework.Tests` | **172/172 通过** | 含 3 项新增加载上下文守护测试 |
| 约 12:45 | 无凭据回归 | **27 skip / 0 failed** | `NATTER_LIVE` 未设置时全跳过，CI 安全 |
| 约 12:47 | 频道残留检查 | 通过 | 最近 10 条无测试消息（`natter-live-*` 0 条） |

**关键真实数据**

- 端到端（embedded）：回复 `17 + 25 is 42.`（14 字符）、`p50=678ms`、`in=35 out=8`、`requests=1 errors=0`；同 event id 重放后仍只有 1 条回复。
- 端到端（文件插件）：回复 `6 × 7 = 42.`（11 字符）、`trust=local`。
- 分片：649 字符 + `maxMessageLength=200` → 4 条真实消息，每条 ≤200。
- 错误映射：401 → `Authentication`（消息不含 key）；未知模型 → 400 `InvalidRequest`；`127.0.0.1:1` → `Unavailable`。
- 入站：`<@!botId>` 被剥离，`conversation=1123031326354841665 kind=Guild guild=1123031326354841661`。

## 5. 具体问题

### BUG-001 · 插件加载上下文把 `Natter.*` / `Microsoft.Extensions.*` 全部当宿主共享，导致 CLI 无法加载这两个插件

- **发现时间**：约 12:20（首次把插件目录接入 CLI）
- **严重度**：**阻塞**（生产形态完全不可用）
- **影响**：`natter` 独立进程启动时，两个插件均为 `Failed`，任何 `discord status` / `ai status` / 上游连接都不可用。进程内单测完全看不到（测试项目自身引用了这些程序集，默认 ALC 里恰好存在）。
- **复现**：
  ```bash
  dotnet build src/Natter.Cli/Natter.Cli.csproj
  dotnet build samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj
  dotnet build src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj
  # 把 <bin>/lib + plugin.json + config.schema.json + config.json 拷到 plugins/{discord,deepseek}/
  ./src/Natter.Cli/bin/Debug/net10.0/natter --plugin-dir artifacts/live/plugins plugins list --detail
  ```
- **证据**：
  ```text
  [natter.feature.ai.deepseek] load-failed: Could not load file or assembly
    'Microsoft.Extensions.AI.Abstractions, Version=10.11.0.0, Culture=neutral, PublicKeyToken=31bf3856ad364e35'.
  [natter.transport.discord]   load-failed: Could not load file or assembly
    'Natter.Chat.Discord, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null'.
  ```
- **测试侧处置**：为继续执行 L3b/L4，临时改动 `PluginLoadContext`（见 §6），**不视为修复**。
- **建议**（待开发决策）：共享策略应按「契约程序集白名单」而不是命名前缀；`Microsoft.Extensions.*` 应先向默认 ALC 询问可解析性，解析不到时使用插件自带副本；同时保留「插件目录内按文件名兜底」以匹配 `docs/PLUGINS.md` 的目录约定。

### BUG-002 · 插件 staging 未把 `<AssemblyName>.deps.json` 放进 `lib/`

- **发现时间**：约 12:22
- **严重度**：**高**（会退化为 BUG-001 的同类症状）
- **影响**：`AssemblyDependencyResolver` 取不到依赖图，`Discord.Net.*`、`Newtonsoft.Json` 等私有依赖无法解析；即使共享策略正确也会加载失败。
- **复现**：构建后 `ls samples/Natter.Sample.Plugin.Discord/bin/Debug/net10.0/lib` → 无 `Natter.Sample.Plugin.Discord.deps.json`；`src/Natter.Feature.Ai.DeepSeek/bin/Debug/net10.0/lib` 同理。
- **证据**：`lib/` 仅含 `*.dll` / `*.pdb`；`deps.json` 只留在插件包根目录。
- **测试侧处置**：临时在两个 csproj 的 staging target 追加 deps.json（见 §6），**不视为修复**。
- **建议**：明确「插件包是否允许只靠目录加载」；若允许，宿主需保留目录兜底；若不允许，打包流程必须强制带上 deps.json 并在 `plugins inspect` 中校验。

### OBS-001 · DeepSeek 瞬时 550

- **发现时间**：约 12:34–12:36（首次全量跑）
- **严重度**：低（服务端瞬时）
- **影响**：单项用例失败一次（`Assert.All` 中一条响应抛异常），重跑通过。
- **证据**：`kind=Unavailable status=550 code= message=DeepSeek is temporarily unavailable.`
- **测试侧处置**：真实调用加**有限重试**（最多 3 次，仅对 `Retryable` / `HttpRequestException`），错误映射本身保留独立断言。
- **建议**：确认 550 的语义（是否等价 503）；评估 responder 层是否需要退避重试，以及 Discord 用户在降级期间看到的文案。

### OBS-002 · bot 会收到自己发送的 `MESSAGE_CREATE`

- **发现时间**：约 12:25
- **严重度**：信息（设计前提）
- **影响**：若 `ignoreSelf=false`，AI 回复会被当作新输入，形成自问自答。
- **证据**：探针输出 `selfMessageVisible=True messageId=1571379952815964231`；同场景 `ignoreSelf=true` 时 `selfEchoEvents=0`。
- **测试侧处置**：E2E 固定使用 `ignoreSelf=true` + human sender 注入，避免回环。
- **建议**：在 Discord 插件文档中显式声明该事实与三层过滤（`IgnoreBots` / `IgnoreSelf` / `IgnoreSelfMessages`）的关系。

### ENV-001 · 测试频道缺少 Manage Channels / Manage Webhooks

- **影响**：无法创建临时频道，只能复用 `常规`；无法用 webhook 制造「人类」入站消息。测试必须串行且自删消息。
- **建议**：若需并行的稳定回归，为测试 bot 单独建 guild/频道并授予 Manage Channels。

### ENV-002 · 只有单个 bot 身份

- **影响**：无法自动产生「人类」入站消息；真正的人类触发只能人工完成（L5）。
- **建议**：引入一个专用测试用户账号或第二个 bot 作为「用户侧」输入源。

## 6. 临时改动

> 以下改动由测试侧为解除阻塞而做，**待开发评审/合并或替换**。回退后 L3b/L4 会重新失败，BUG-001/002 依然成立。

| 文件 | 改动 | 原因 | 回退 |
| --- | --- | --- | --- |
| `src/Natter.Infrastructure/Plugins/PluginLoadContext.cs` | 共享策略改为精确白名单（10 个契约程序集）+ `Microsoft.Extensions.AI*` 私有覆盖 + `Microsoft.Extensions.*` 动态可解析判定 + 目录按文件名兜底（+80/−10） | BUG-001 | `git checkout -- src/Natter.Infrastructure/Plugins/PluginLoadContext.cs` |
| `samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj` | staging target 追加 `$(TargetDir)$(AssemblyName).deps.json`（+3） | BUG-002 | `git checkout -- samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj` |
| `src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj` | 同上（+3） | BUG-002 | `git checkout -- src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj` |

一并回退：

```bash
git checkout -- src/Natter.Infrastructure/Plugins/PluginLoadContext.cs \
  samples/Natter.Sample.Plugin.Discord/Natter.Sample.Plugin.Discord.csproj \
  src/Natter.Feature.Ai.DeepSeek/Natter.Feature.Ai.DeepSeek.csproj
```

非产品改动（测试资产，可直接保留）：`tests/Natter.Live.Tests/`、`tests/Natter.Framework.Tests/PluginLoadContextTests.cs`、`scripts/live-test.sh`、`docs/LIVE_TEST_PLAN.md`、本记录。

## 7. 结论与后续

- **待开发确认**：BUG-001 的共享策略归属；BUG-002 的打包契约；OBS-001 的 550 语义与重试策略。
- **下一轮建议**：slash 交互（需真人或第二 bot）、附件链路、429/5xx 注入降级、断线重连、`thinking=enabled` 的 E2E。
- **测试纪律**：后续每轮按 [记录规范](README.md) 留档，只记录问题与证据，不修改产品代码。
