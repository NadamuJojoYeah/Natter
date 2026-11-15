# 2026-11-15 · P2 验收测试（Closed · Go）

> **开发侧处置**：[P2 验收处置记录](2026-11-15-p2-acceptance-response.md)（临时配对文档，4 个 High 关闭并复测后归档）。

## 1. 概要

- **测试时间**：2026-11-15 16:47–17:05（Asia/Shanghai），本轮只执行到 Gate A–C 与 Gate D/E 的自动化部分
- **被测对象**：`serve`、RuntimeProfile、profile 路由、SQLite 会话、health、Tier 1/Tier 2 策略
- **基线**：`HEAD=f114f7e`（工作区有测试侧新增文件，无产品代码改动）
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；凭据来自 `~/.config/natter/live.env`
- **证据目录**：`artifacts/p2/2026-11-15-1647/`
- **修复复测**：2026-11-15 18:02–18:05，基线 `HEAD=cbc0901`（含修复 `1fdbd2c`），自动化 + 真实 DM 复测见 §12。
- **当前判定**：**Go**（4 个 High 已修复并复测通过）。

## 2. Gate A：CLI / Host 基础

| ID | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| P2-001 | `dotnet build Natter.slnx` | **通过** | `evidence/build.log` |
| P2-002 | 框架回归 | **通过**：执行时 203/203；补测后 208/208（新增 ProfileRouting 3、SQLite 隔离 1、Spark 429 1） | 终端输出 |
| P2-003 | 无凭据 live | **通过：37 skip / 0 failed** | `evidence/live-nocreds.log` |
| P2-004 | `serve --duration 3 --json` | **通过**：exit 0，`mode=serve`、`profiles=2`、`defaultProfile=tier1-default` | `evidence/p2-004-serve.json` |
| P2-005 | `health --json` | **通过（有语义问题）**：exit 0，输出 profiles/providers；无 upstream 时仍报 `status=healthy`（见 OBS-P2-HEALTH） | `evidence/p2-005-health.json` |
| P2-006 | loopback `/health` | **通过**：HTTP 200，JSON 可解析 | `evidence/p2-006-health.json` |
| P2-007 | 未知 profile | **通过**：exit 1，`Profile override 'missing' does not exist.` | `evidence/p2-007-unknown-profile.log` |
| P2-008 | `SIGINT` 优雅停止 | **通过（修复后）**：`scripts/p2-signal-smoke.sh` → `PASS: INT exit=0`、`PASS: TERM exit=0`（均 5s 内）；修复 `1fdbd2c`（`PosixSignalRegistration` + 重置继承信号处置） | `evidence/p2-008-serve-int.log`、`p2-008-sigterm.log`、`p2-008-diag.log`、`p2-008-sigint-detail.log`、`p2-008-tty2.log` |

### BUG-P2-SIGINT（High，已修复复测通过）

- **现象**：`natter serve`（duration=0）收到 SIGINT 不退出；`SIGTERM` 会直接终止（未走优雅关闭）。
- **影响**：Ctrl+C / 容器 stop（SIGINT）会挂住，无法优雅停止；P2-008 阻塞签收。
- **复现**：`natter --no-plugins serve &` → `kill -INT $!` → 25s 后仍存活（`ps STAT=Sl`）。
- **根因取证（16:57）**：SIGINT 后 12 秒进程仍存活，日志**没有** `cancelled.`、也没有 `Host shutdown exceeded 10 seconds.`。说明 `Console.CancelKeyPress` 处理器根本没有执行（若执行，`CliRunner` 会打印 `cancelled.`，或 `StopAsync` 超时兜底打印）。`CliRunner`/`ServeCommand` 的 token 传递链本身是正确的（`RunAsync` → `runner.RunAsync` → `handler.ExecuteAsync(invocation, token)`）。
- **TTY 对照（17:01）**：用 `script -qec "<natter serve>" /dev/null` 分配 pty，定位真正的 natter pid 后 `kill -INT`，进程立即退出（`ps` 为空）。同一命令在 stdout 重定向到文件时不退出。
- **判断**：`Console.CancelKeyPress` 只在控制台/终端场景注册；重定向场景下 SIGINT 既没有触发取消，也没有走默认终止（被 .NET 接管但无回调）。容器/CI/`nohup` 正是非 TTY 场景，因此这是生产相关的 High 缺陷。SIGTERM 未注册，直接默认终止。
- **建议**：改用 `PosixSignalRegistration.Create(PosixSignal.SIGINT | PosixSignal.SIGTERM, ...)`（或等价）显式注册，取消同一个 `CancellationTokenSource`；补一个进程级测试（启动 serve → 发 SIGINT → 断言 5s 内退出）。

## 3. Gate B：Profile / 路由

| ID | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| P2-010 | 默认 profile | **通过**：无 `profiles.json` 时 `profiles=2`、`defaultProfile=tier1-default` | `p2-004-serve.json` |
| P2-011 | Discord → DeepSeek | **解析层通过**：`ProfileRoutingTests.Default_set_routes_discord_to_deepseek_...` | 测试 |
| P2-012 | DingTalk → Spark | **解析层通过**（同上） | 测试 |
| P2-013 | `--profile` 强制覆盖 | **通过（修复后）**：`ProfileResolver` 优先返回 `OverrideProfile`；`ProfileRoutingTests` 断言已翻转并 8/8 通过 | `ProfileRoutingTests` |
| P2-014 | Tier 1 provider 缺失 | **通过**：exit 1，`requires Tier 1 provider ... but it is not loaded.` | `evidence/p2-014-tier1missing.log` |
| P2-015 | Tier 2 provider 缺失 | **通过**：只装 Discord+DeepSeek 时启动成功，`providers=1`、Discord `connected=true`、Spark `available=false` | `evidence/p2-015-tier1-only.log`、`p2-015-health.json` |
| P2-016 | 未知 profile | **通过**（同 P2-007） | 同上 |
| P2-017 | 重复 profile id | **通过**：exit 1，`contain duplicate ids.` | `evidence/p2-b-case-dup.log` |
| P2-018 | 非法 provider ref | **通过**：exit 1，`ai.provider must be 'pluginId#responderId'.` | `evidence/p2-b-case-badref.log` |

### BUG-P2-OVERRIDE（High，已修复复测通过）

- **现象**：`ProfileResolver.Resolve` 先按 match 打分，只在**无任何 match** 时回退到 `OverrideProfile ?? DefaultProfile`。因此 `--profile tier2-dingtalk-spark` 对 Discord 消息仍然解析为 `tier1-default`。
- **证据**：`ProfileRoutingTests`（3 项）：
  - `Override_profile_does_not_force_when_a_match_exists` → 实测 `tier1-default`（期望 `tier2-dingtalk-spark`）；
  - `Override_profile_is_used_when_nothing_matches` → satori 无 match 时 override 生效；
  - `Default_set_routes_discord_to_deepseek_and_dingtalk_to_spark` → 默认集路由正确。
- **影响**：运维无法用 `--profile` 强制切换 provider；P2-013 阻塞签收。
- **建议**：override 存在时应跳过 match（或仅对 match 做“是否允许”校验），并在 `ReloadAsync` 后保留 override。

## 4. Gate C：SQLite 会话

| ID | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| P2-020 | 建库 | **通过**：`serve --state-dir` 生成 `state/natter.db`（sessions/messages 表，WAL） | `artifacts/p2/.../state/` |
| P2-021 | 写入 | **组件级通过**；serve 进程级待真实消息 | `SqliteConversationStoreTests` |
| P2-022 | 重启恢复 | **通过**：新 store 实例读回历史 | 同上 |
| P2-023 | source 隔离 | **通过**：同 conversationId、不同 source_key 不共享 | `Sources_and_actors_are_isolated...` |
| P2-024 | history scope | **通过**：actor-a / actor-b 生成不同 session | 同上 |
| P2-025 | 重复 event id | **通过**：重放同 event 不重复写 | 两种测试均覆盖 |
| P2-026 | provider 切换 | **通过**：同 session 继续写，历史不丢 | 同上 |
| P2-027 | 清理 | **通过**：`ClearAsync` 删除 session 并级联消息 | 同上 |

> 说明：测试中发现 assistant 消息若也带 `source_event_id`，会被 `UNIQUE(session_id, source_event_id)` 丢弃——这是约定（只有 user 消息带 event id），已在测试注释中标注。

## 5. Gate D / E：真实链路（自动化部分）

| ID | 用例 | 结果 |
| --- | --- | --- |
| P2-030 | 插件加载（Tier 1） | **通过**（Tier1-only 实例：activated=4，含 2 示例） |
| P2-031 | upstream 连接 | **通过**：`discord ... connected=true` |
| P2-032 | DM 回复 | **通过（修复后真实）**：18:01 私信「你好」→ 18:04 bot 回复「你好！有什么我可以帮你的吗？」（14 字，reply 引用）；日志 `ai replied conversation=1571451809187500033, 14`，无 `not_found`；SQLite DM session version=2 | `evidence/serve-all.log`、截图 |
| P2-033 | guild @mention | **通过（真实）**：17:05:13 用户 `@bot 你好，晚上好` → 17:05:15 bot 回复「晚上好！有什么我可以帮你的吗？」（2s，`referenced_message` 正确） | `evidence/collect-170520/`、`discord-tail.json` |
| P2-034 | 重启历史 | **通过（真实）**：真实双边会话后停止实例，用同一 `state` 重启 → `conversations=2`、`messages=4` 完整、双 upstream 重连；另在独立 `state-hist` 上做过注入历史对照 | `evidence/p2-034-real-health.json`、`evidence/natter-before-restart.db` |
| P2-035 | 无 Tier 2 | **通过**：不装钉钉/Spark 正常启动 |
| P2-036 | 日志 | **通过**：`Routed event discord:message:1571450997341954091:created to profile tier1-default provider natter.feature.ai.deepseek#deepseek`；`ai replied ... replyChars=15`；无密钥/无正文泄漏 | `evidence/serve-all.log` |
| P2-040 | DingTalk Stream 连接 | **通过**：`dingtalk ... connected=true` |
| P2-041 | 钉钉群 @bot | **通过（真实）**：`dingtalk:message:msggUeb4LNJROZQFD2C6DOp5A==` → `tier2-dingtalk-spark` / `spark`，回复「下午好，随时为您服务。」（11 字） | `evidence/serve-all.log`、`sqlite` |
| P2-042 | 四插件加载 | **通过**：`activated=6, failed=0`（4 被测 + 2 内置示例） |
| P2-043 | 能力降级 | **通过**：钉钉回复为纯文本、无引用/无编辑删除/无 typing，符合 `capabilities`（reply/edit/delete/typing=false） | 同上 |
| P2-044 | health providers | **通过**：DeepSeek/Spark 均 `available=true` |
| P2-050 | 双 upstream 在线 | **通过**：Discord + DingTalk 同时 `connected=true` |
| P2-054 | 双 source 重启恢复 | **通过（真实 + 注入对照）**：真实 discord/dingtalk 各一个 session，重启后 `conversations=2`、`messages=4`，source_key 各自隔离；注入历史对照同样通过 | `evidence/p2-034-real-health.json`、`p2-054-health.json` |
| P2-052 | provider 故障降级 | **组件级通过**：共享 responder 已有 `Timeout → fallback`、`Authentication → 不回复`、重复事件幂等（`DeepSeekResponderTests.AiChatResponderTests`）；Spark 侧新增 429 → `RateLimited` 且不泄漏 key。真实消息降级待验 | 测试 |
| P2-053 | upstream 不可达 | **通过（不阻塞）**：DingTalk `streamGatewayUrl` 指向 `127.0.0.1:1` 时启动成功（exit 0），该插件 `Failed` + `upstream-connect-failed: Connection refused`，Spark 照常 Activated；但 health 仍报 `healthy` 且 `upstreams=[]`（见 OBS-P2-HEALTH / OBS-P2-DIAG） | `evidence/p2-053-offline.log`、`p2-053-health.json` |
| P2-056 | 长跑（部分） | **通过（16:57–17:02，10/10 次采样）**：`status=healthy`、双 upstream `connected=true`、`plugins=6`、`conversations=0` 无漂移（`evidence/soak-health.jsonl`）；完整 30–60 分钟待续 |

真实链路（17:05）：

```text
[information] deepseek: ... ai replied conversation=1123031326354841665, 15
[debug] ProfileAwareChatResponder: Routed event discord:message:1571450997341954091:created
        to profile tier1-default provider natter.feature.ai.deepseek#deepseek
```

SQLite：`sessions=1`、`messages=2`（user `你好，晚上好` / assistant `晚上好！有什么我可以帮你的吗？`，`source_event_id=discord:message:1571450997341954091:created`）。

当前实例（四插件，`serve --duration 3600 --health-port 5096`），16:56 连续 3 次采样均稳定（双 upstream `connected=true`）：

```json
{"status":"healthy","profiles":2,"providers":[{"id":"...deepseek#deepseek","available":true},{"id":"...spark#spark","available":true}],
 "upstreams":[{"source":"dingtalk:dingtalk:dingeojh87nyyna3lqpg","connected":true},
              {"source":"discord:discord:1571154023493410836","connected":true}],
 "plugins":{"activated":6,"failed":0}}
```

## 5.1 Gate G：安全（自动部分）

| ID | 用例 | 结果 |
| --- | --- | --- |
| P2-060 | 密钥不落日志 | **通过（当前样本）**：`serve-all.log`（6 行）已按凭据模式扫描，0 命中；真实消息产生更多日志后需复扫。本记录不保留任何密钥片段。 |
| P2-061 | profiles 无明文 | **通过**：`config/*/profiles.json` 只含 provider ref |
| P2-062 | 插件 config 只含 secret 引用 | **通过**：`config/all/*.json` grep 字面密钥 0 命中 |
| P2-063 | state 权限 | **不通过**：`state/` 755、`natter.db` 644（其他用户可读）→ OBS-P2-STATE-PERMS |
| P2-064 | 错误脱敏 | **通过（组件级）**：DeepSeek 401/429/5xx 参数化映射；Spark 401/429/5xx 重试与 `RateLimited`（新增 `SparkChatClientTests.Rate_limit_is_retryable...`），断言错误串不含 key |

### BUG-P2-DM-ALLOWLIST（High，已修复复测通过）

- **现象**：配置了 `guilds`/`channels` allowlist 时，Discord 私信（DM）被 `DiscordEventMapper` 静默丢弃。
- **代码路径**：`MapMessage` 先判 `IsGuildAllowed(message.Guild?.Id)`；DM 的 `Guild` 为 null，而 `Guilds` 非空 → 返回 false；即使放开 guild，`IsChannelAllowed(dmChannelId)` 也因频道 id 不在 allowlist 中而失败。
- **测试**：`tests/Natter.Framework.Tests/DiscordDmAllowlistTests.cs`（2 项）
  - `Dm_is_dropped_when_a_guild_allowlist_is_configured` → 实测 `null`（复现）；
  - `Dm_maps_when_no_allowlist_is_configured` → 无 allowlist 时 `ConversationKind.Direct` 正常。
- **影响**：P2-032 无法通过；频道可用但 DM 永远无响应，且没有任何日志。
- **与 BUG-P2-DM-SEND 的关系**：临时清空 allowlist 后 DM 能到达宿主，说明 allowlist 是第一道拦截；发送阶段的 `not_found` 是第二道。两个缺陷都要修。
- **Discord 侧事实（17:17，已按官方/社区文档复核）**：`GET /applications/@me` → `bot_public=false`（只影响邀请）、`bot_require_code_grant=false`、`install_params=null`、`integration_types_config={"0":{},"1":{}}`；`GET /users/@me/channels`=0。
  - **更正**：`integration_types_config` 被认为总是回显两个 integration context（[Pycord #3437](https://github.com/Pycord-Development/pycord/issues/3437)），不能据此判断 User Install 是否保存。
  - **更正**：User Install 的默认 scope 只支持 `applications.commands`（见 [D++ Creating User Apps](https://www.dpp.dev/user-applications.html)），**不支持 `bot` scope**；它是给 slash command 在 DM/群聊里用的，不是「普通消息 DM」的前提。截图中的 `bot` scope 属于 Guild Install / OAuth2 URL Generator。
  - 因此 DM 不走 User Install；与 bot 共享 guild 的用户本来就可以私信 bot。`/users/@me/channels`=0 说明 DM 会话未被创建（消息没发出/未建立），需要在客户端侧确认；产品侧仍会因 allowlist 静默丢弃（本缺陷）。
- **建议**：allowlist 只应作用于 guild/thread 场景；`ConversationKind.Direct`/`Group` 或 `Guild is null` 的 DM 不应被 guild/channel allowlist 过滤（用户 allowlist 可保留）。同时建议 mapper 过滤时输出 debug 日志。
- **Discord 侧证据（17:33）**：`GET /users/@me/channels` 始终返回 `[]`（bot 无任何 DM channel）；应用信息 `bot_public=false`、flags `565248`（`GATEWAY_MESSAGE_CONTENT`=false、`GATEWAY_MESSAGE_CONTENT_LIMITED`=true）、`install_params=null`。说明 DM 目前在 Discord 侧就未交给 bot，与 allowlist 叠加。
- **临时验证（17:36）**：备份原配置（`evidence/discord-config-before-dm-probe.json`）后把 `guilds`/`channels` 置空并重启，等待操作者再发一次 DM，以区分「Discord 未推送」与「allowlist 过滤」。验证结束后恢复原配置。

### BUG-P2-DM-SEND（High，已修复复测通过）

- **现象**：清空 allowlist 后，DM 消息可以到达宿主、正确路由到 `tier1-default/deepseek` 并生成回复，但**发送失败**，用户侧无消息。
- **证据**：
  ```text
  [information] deepseek: ... ai replied conversation=1571451809187500033, 17
  [debug] ProfileAwareChatResponder: Routed event discord:message:1571458959229849601:created
          to profile tier1-default provider natter.feature.ai.deepseek#deepseek
  [warning] ChatOrchestrator: NLMP reply for discord:discord:1571154023493410836 failed:
          not_found Discord channel '1571451809187500033' was not found.
  ```
  SQLite 已写入 user「你好」+ assistant「你好！有什么我可以帮…帮你的吗？」（session `76540180FEDDBA607F5BFD2E`）。
- **根因方向**：`DiscordSocketTransport.GetMessageChannelAsync` 只查 `_client.GetChannel(id)`（本地缓存）；DM channel 未进入该缓存，而 REST `GET /users/@me/channels` 对 bot 也返回 `[]`，于是发送直接 `not_found`。
- **建议**：DM 发送路径改用「收到消息时的 `IMessageChannel` 缓存」或 `GetDMChannelAsync(recipientId)`；NLMP 需要保留 DM 的 recipient 信息（或 transport 内部按 channelId 缓存 channel 对象）。同时 `not_found` 目前只打 warning，用户无感知，建议回复失败时向用户可见地降级或在 `discord status` 暴露计数。

## 6. 其他观测

### OBS-P2-HEALTH（Medium，已修复）

无 upstream 时 `/health` 返回 `status=healthy`（`All(...)` 对空集合为真），providers 也全 `available=false`。健康语义应为 `degraded` 或 `unknown`，否则监控无法发现「没有连接任何平台」。

### OBS-P2-STATE-PERMS（Medium，已修复）

`--state-dir` 创建的目录与 `natter.db` 使用默认 umask（755/644），不符合 §4 P2-063「state 目录不可被其他用户读取」。建议在 host 创建 state 目录时显式 `0700`、数据库文件 `0600`（或文档明确由部署侧收紧并给出示例）。

### OBS-P2-DIAG（Medium，已修复）

`serve` 模式（`--verbose`）下插件 `Failed` 不打印失败原因：日志只有 `plugins=3 failed=1`，真实错误（`upstream-connect-failed: Connection refused (127.0.0.1:1)`）只有在另跑 `plugins list --detail` 时才可见。长驻实例排查困难，建议启动摘要同时输出失败插件的 id + error。

### OBS-P2-HEALTH-FIELDS（Low）

`natter health --json` 含 `uptimeSeconds`，而 HTTP `/health` 端点没有该字段；两者 schema 不一致，监控与运维脚本难以复用同一解析逻辑。

### OBS-P2-PLUGINCOUNT（Low）

`--no-plugins` 会连显式 `--plugin-dir` 一起禁用，因此 Gate B/D/E 必须去掉它，代价是 CLI 自带的 greetings/metrics 也被加载（`activated=6` 而非 4）。建议文档写明该交互，或提供「只禁用默认目录」的选项。

## 7. 待人工与后续

复现入口（测试资产，不改产品代码）：

```bash
scripts/p2-serve.sh --prepare            # staging 四插件 + profiles/配置
scripts/p2-serve.sh --foreground 3600    # 长驻 serve（配合后台 job/终端）
scripts/p2-serve.sh --status             # health + 日志尾
scripts/p2-collect.sh artifacts/p2/2026-11-15-1647 5096   # 消息后一键取证
```


1. **Gate D/E 真实消息**：在 Discord 频道 @大肥鱼酱、在钉钉群 @机器人，各发一条；预期 Discord→DeepSeek、钉钉→Spark，互不串（P2-051）。
2. **P2-052/053/054/055**：错误 provider 降级的真实消息观察、upstream 断线、双 source 重启恢复、重复投递；P2-056 长跑继续采样。
3. **P2-060~064**：日志/配置/state 权限/错误脱敏扫描。
4. **修复后复测**：`BUG-P2-SIGINT`、`BUG-P2-OVERRIDE` 修好后重跑 Gate A/B。

## 8. 临时改动

| 文件 | 改动 | 原因 | 回退 |
| --- | --- | --- | --- |
| `tests/Natter.Framework.Tests/ProfileRoutingTests.cs` | 新增 3 个用例（override 语义与默认路由） | P2-013 证据 | 删除文件 |
| `tests/Natter.Framework.Tests/SqliteConversationStoreTests.cs` | 追加 1 个用例（source/actor 隔离、provider 切换、Clear） | Gate C 覆盖 | 删除该 `[Fact]` |

未修改产品代码；`git diff src samples` 为空。

## 9. 签收标准对照（复测后）

| 标准 | 状态 | 说明 |
| --- | --- | --- |
| P2-001 ~ P2-008 全绿 | **是** | P2-008 由 `p2-signal-smoke.sh` 验证（INT/TERM 均 pass） |
| P2-010 ~ P2-018 全绿 | **是** | P2-013 由翻转后的 `ProfileRoutingTests` 验证 |
| P2-020 ~ P2-027 全绿 | **是** | 组件级 8/8 + serve 建库 + 真实双边重启恢复 |
| P2-030 ~ P2-036 Tier 1 真实通过 | **是** | 033 频道 2s 回复；**032 DM 真实回复 14 字**；034 真实重启；036 日志含 profile/provider/eventId |
| P2-040 ~ P2-044 Tier 2 smoke | **是** | 040/041/042/043/044 全部通过（041 真实 Spark 回复） |
| P2-050 ~ P2-056 并发/隔离 | **是** | 050/051/053/054 真实通过；052/055 组件级；056 10/10 采样 |
| P2-060 ~ P2-064 安全 | **是** | 063 复测：state 目录 700、`natter.db` 600；其余同前 |
| 无 Blocker | **是** | 4 个 High 已修复并复测通过 |
| 无跨 source 路由/记忆泄漏 | **是** | 默认集路由 + SQLite source/actor 隔离 + 真实双 source 不串 |
| 重启可恢复 | **是** | 真实会话重启（4 sessions / 12 messages 保留） |
| 无密钥进入仓库/日志/截图 | **是** | 扫描 0 命中；`RuntimeHealth.Sanitize` 额外脱敏 |
| 测试记录归档 | **是** | 本记录 + 索引 |
| 测试负责人 Go/No-Go | **Go** | 见 §12 |

## 10. 证据索引

```text
artifacts/p2/2026-11-15-1647/
├── config/{all,tier1,sparkonly,offline,case-*}   # profiles 与插件配置（仅 ${secret:...}）
├── plugins/{all,tier1,sparkonly,offline}         # 临时 staging（可删）
├── state/                                        # 真实会话 natter.db（4 sessions / 12 messages）
├── state-hist/  state-fixcheck/                  # 注入历史 / 修复后权限检查
└── evidence/
    ├── build.log  head.txt
    ├── p2-004-serve.json  p2-005-health.json  p2-006-health.json
    ├── p2-007-unknown-profile.log  p2-008-{serve-int,sigterm,diag,sigint-detail,tty2}.log
    ├── p2-b-case-{dup,badref}.log  p2-014-tier1missing.log  p2-015-*.{log,json}
    ├── p2-034-{health,real-health,restart}.log  p2-054-{health,restart}.log
    ├── p2-053-offline.log  p2-053-health.json
    ├── soak-health.jsonl（10 次采样）
    ├── serve-all.log（真实双边 + DM 路由与回复）
    ├── natter-before-restart.db
    └── collect-170520/（health / serve.log.tail / sqlite.txt / discord-tail.json）
artifacts/p2-signal/                               # 修复后 INT/TERM 烟测日志
```

## 11. 修复复测（2026-11-15 18:02–18:05）

基线 `HEAD=cbc0901`，修复提交 `1fdbd2c fix: 修复 P2 验收的 SIGINT、profile override、DM 与 health/state 缺陷`。

| 复测项 | 结果 |
| --- | --- |
| 构建 | **成功**（允许既有 Android warning） |
| 框架回归 | **214/214 通过** |
| 非 TTY SIGINT / SIGTERM | `scripts/p2-signal-smoke.sh` → **PASS: INT exit=0 / PASS: TERM exit=0** |
| override | `ProfileRoutingTests` + `DiscordDmAllowlistTests` → **8/8 通过**（断言已翻转） |
| `/health` 空 upstream | `status=unknown`（不再 healthy），含 `uptimeSeconds` / `failures` |
| state 权限 | 新目录 **700**、`natter.db` **600** |
| DM 真实回复 | 18:01 私信「你好」→ 18:04 回复「你好！有什么我可以帮你的吗？」（14 字，引用正确）；`ai replied conversation=1571451809187500033, 14`，**无 `not_found`**；DM session version=2 |
| 修复后实例 | `healthy`、`conversations=4`、双 upstream `connected=true`、providers 均 available |
| 修复后 **guild @ 复测** | 18:22:47 `@bot 最后测试` → 18:22:49 回复「测试收到，一切正常。有什么需要帮忙的…」（2s，`referenced_message` 正确） |
| 修复后 **钉钉 @ 复测** | `dingtalk:message:msgshApUsv2yWwDh4OMx3I4FA==` → Spark 回复 28 字；SQLite 新 state 下两轮完整（2 sessions / 4 messages） |
| 30 分钟 soak | **30/30 采样全部 `healthy`**，双 upstream 持续 `connected=true`，`plugins=6` 无漂移（`evidence/soak-30min.jsonl`） |

## 10.1 收口记录（2026-11-15 18:20–18:50）

| 项 | 结果 |
| --- | --- |
| 处置记录状态 | `Fixed · Ready for test-side retest` → **`Closed · Go`**，复测闸门 14 项全勾 |
| 归档 | 测试记录 + 处置记录移入 `docs/testing/archive/`，链接修正 |
| 索引 | `docs/testing/README.md` 更新为「（归档）」+ **Closed · Go** |
| artifacts 清理 | 删除 `plugins/`（31MB staging）与 `state*`（含 fixcheck/hist/offline/sigint/tty）；保留 `config/`（68K）+ `evidence/`（236K，含 `natter-final.db`、`soak-30min.jsonl`） |
| 残留测试消息 | 18:48 一条 `natter-live-file-e2e ...`（bot 自发测试输入）已删除（204） |
| 本地凭据 | **已清理**（19:06 删除 `~/.config/natter/live.env`）；平台侧轮换需账号持有人按 [ASSETS §4](../ASSETS.md#4-轮换清单需账号持有人操作) 执行 |

## 12. 结论

- **Go**：P2 的 4 个 High（SIGINT、override、DM allowlist、DM send）已修复并复测通过；
- 真实链路：Discord 频道 @bot、Discord DM、DingTalk 群 @ 均往返成功；双 upstream 同时在线且路由不串（Discord→DeepSeek、DingTalk→Spark）；
- 持久化：真实会话重启后 4 sessions / 12 messages 完整恢复；
- 安全：日志/配置/profiles 无密钥，429/401 脱敏，state 700/600；
- 仍为组件级而非真实端到端的项：P2-052（provider 故障降级）、P2-055（同 msgId 重放），均有确定性测试覆盖；
- **修复后 guild 与钉钉真实消息已复测**（18:22，新 state）；30 分钟 soak 30/30 全绿；不再存在「修复前验证」的遗留项。
