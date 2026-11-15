# P2 Runtime & Profile Foundation — Development Spec

> **状态**：P2 foundation implemented（profiles / profile-aware routing / SQLite host store / serve / health + loopback health port / config+state dirs；多实例与部分 provider model 细节仍未实现）
> **日期**：2026-11-15
> **目标**：把 Natter 从“能跑的一次性联调宿主”推进到“可长驻、可配置、可路由、可恢复”的产品底座。
> **范围**：RuntimeProfile、provider 路由、`natter serve`、插件配置目录、SQLite 会话持久化、健康/指标、一等/二等公民支持策略。
> **不包含**：Context/Memory 插件化（P3）、Task Worker、第三方插件市场、Android 动态宿主、跨平台身份自动合并。
> **验收计划**：[P2 Acceptance Test Plan](testing/P2_ACCEPTANCE.md)。
> **当前结论**：No-Go；4 个 High 待修，见 [P2 验收处置](testing/2026-11-15-p2-acceptance-response.md)。

---

## 0. 一句话目标

> **P2 完成后，一个进程可以同时连接 Discord 和钉钉，按 profile 把不同 source 路由到 DeepSeek 或 Spark，重启后会话不丢，并且不依赖 `chat.responder` 的 priority 来决定 provider。**

P1（显式 provider 选择）合并进 P2 验收，不单独作为阶段。

---

## 1. 为什么要做

当前已具备：

- Discord/DeepSeek 一等链路；
- DingTalk Stream/Spark 二等价链路；
- NLMP、NPS、`AiChatResponder`、`CompositeChatResponder`；
- 多 upstream 并发注册能力；
- fake/live 测试基础。

当前缺口：

1. **没有长驻模式**：CLI 仍以一次性命令为主，真实测试靠 `events watch`/脚本撑住。
2. **没有显式 provider 路由**：DeepSeek priority=10、Spark priority=20；同时加载时 DeepSeek 先应答。
3. **没有持久会话**：`AiConversationStore` 在插件内存中，重启丢失；不同 provider 各有一份历史。
4. **没有统一配置目录/状态目录**：CLI 没有 `--plugin-config-dir` / `--state-dir`。
5. **没有健康/指标**：长驻后无法判断 upstream、插件、请求是否正常。
6. **一等/二等公民没有运行时边界**：Tier 2 插件容易影响 Tier 1 默认体验。

---

## 2. 设计原则

1. **Transport 与 provider 正交**：upstream 可以同时在线；provider 由 profile 路由，不按平台硬编码。
2. **Profile 是产品配置，不是插件内部逻辑**：不把 “Discord 用 DeepSeek、钉钉用 Spark” 写进 core。
3. **Host 拥有状态**：会话、profile、路由、健康由 host 管；插件不各自持久化。
4. **Tier 1 优先**：Discord/DeepSeek 进入发布闸门；DingTalk/Spark 只保证核心契约。
5. **渐进迁移**：保留 `CompositeChatResponder` 作为 fallback/测试路径，新增 profile-aware router。
6. **不引入第二套插件系统**：继续使用 NPS contribution；不新增平台特有 contribution。
7. **先 SQLite，后 Postgres**：P2 单机可恢复；多实例留到后续。
8. **不做 Context/Memory 插件**：P2 只做持久会话；P3 再做 `IPromptProvider` / `IContextAssembler` / `IMemoryStore`。

---

## 3. 目标架构

```text
CLI / Android / Test Host
        │
        ▼
NatterHost
  ├── RuntimeProfileStore
  ├── ProfileResolver
  ├── ProfileAwareChatResponder
  ├── SqliteConversationStore
  ├── HealthService
  └── NPS plugins
        ├── Discord transport
        ├── DingTalk transport
        ├── DeepSeek responder
        └── Spark responder

NLMP event
  → ChatOrchestrator
  → ProfileAwareChatResponder
       ├── resolve profile(source, conversation, actor)
       ├── select responder(providerRef)
       └── fallback / error policy
  → ChatCommandDispatcher
  → transport
```

关键：

- **upstream 全部保持连接**；
- 一个事件只走一个 profile；
- profile 决定 provider，不决定 transport；
- 所有 provider 共享同一个 `IConversationStore`；
- 插件不感知其他 transport。

---

## 4. RuntimeProfile

### 4.1 文件位置

```text
<config-dir>/profiles.json
```

- `--config-dir` / `--plugin-config-dir` 指向同一目录；
- 默认 `<app base>/config`（可由 host 覆盖）；
- 测试使用临时目录；
- 不把 profile 放进插件包。

### 4.2 Schema

```json
{
  "schemaVersion": 1,
  "defaultProfile": "tier1-default",
  "profiles": [
    {
      "id": "tier1-default",
      "supportTier": 1,
      "match": {
        "protocols": ["discord"],
        "platforms": [],
        "guilds": [],
        "conversations": [],
        "actors": []
      },
      "ai": {
        "provider": "natter.feature.ai.deepseek#deepseek",
        "model": "deepseek-flash"
      },
      "history": {
        "scope": "conversation",
        "maxMessages": 20,
        "maxCharacters": 24000
      },
      "policy": {
        "enabled": true
      }
    },
    {
      "id": "tier2-dingtalk-spark",
      "supportTier": 2,
      "match": {
        "protocols": ["dingtalk"],
        "platforms": ["dingtalk"],
        "guilds": [],
        "conversations": [],
        "actors": []
      },
      "ai": {
        "provider": "natter.feature.ai.spark#spark",
        "model": "spark-x2.5-1.7b"
      },
      "history": {
        "scope": "conversation",
        "maxMessages": 20,
        "maxCharacters": 24000
      },
      "policy": {
        "enabled": true
      }
    }
  ]
}
```

### 4.3 字段语义

| 字段 | 说明 |
| --- | --- |
| `schemaVersion` | 固定 1；未知版本拒绝启动 |
| `defaultProfile` | 无匹配时使用；必须存在 |
| `supportTier` | 1/2，仅诊断与默认启用策略，不改变协议 |
| `match.protocols` | 匹配 `Source.Protocol`，支持 `*` |
| `match.platforms` | 匹配 `Source.Platform` |
| `match.guilds` | 匹配 `Conversation.GuildId` |
| `match.conversations` | 匹配 `Conversation.Id` |
| `match.actors` | 匹配 sender id；空表示不限 |
| `ai.provider` | `pluginId#responderId`；必须唯一 |
| `ai.model` | 可选覆盖 provider 默认模型 |
| `history.scope` | `conversation` / `user` |
| `history.maxMessages` | 最近消息上限 |
| `history.maxCharacters` | 历史字符上限 |
| `policy.enabled` | false 时该 profile 不处理事件 |

### 4.4 匹配优先级

从具体到宽泛：

```text
conversation + actor
conversation
guild + actor
guild
protocol + platform + actor
protocol + platform
protocol
defaultProfile
```

规则：

- 同一优先级多个 profile 匹配时按 `profiles` 数组顺序取第一个；
- 启动时校验歧义并给出 warning；
- 无匹配使用 `defaultProfile`；
- `defaultProfile` 不得 `enabled=false`；
- `supportTier=2` profile 默认不阻塞启动；如果 provider 插件缺失，标记 disabled + warning。

### 4.5 Profile 校验

启动时：

- JSON schema / 手写验证；
- `defaultProfile` 存在；
- `ai.provider` 引用存在（至少 responder registry 中存在）；
- `supportTier` 只允许 1/2；
- match 列表不为 null；
- 不允许重复 `id`；
- 不允许 profile 内含 secret；
- Tier 1 profile 的 provider 缺失 → 启动失败；
- Tier 2 profile 的 provider 缺失 → 禁用该 profile，其他 profile 继续。

---

## 5. Provider 路由

### 5.1 目标

替代：

```text
CompositeChatResponder: priority 升序，第一个非空获胜
```

改为：

```text
ProfileAwareChatResponder:
  event → profile → providerRef → 对应 IChatResponder
```

`CompositeChatResponder` 保留：

- 测试；
- 没有 profile 的嵌入式/legacy host；
- fallback。

### 5.2 接口

建议新增（命名可调整）：

```csharp
public interface IRuntimeProfileStore
{
    RuntimeProfileSet Current { get; }
    ValueTask ReloadAsync(CancellationToken ct = default);
}

public interface IProfileResolver
{
    RuntimeProfile Resolve(Source source, Conversation conversation, User sender);
}

public interface IProviderResponderRouter
{
    ValueTask<ChatCommand?> RespondAsync(
        ChatEvent chatEvent,
        RuntimeProfile profile,
        CancellationToken ct = default);
}
```

### 5.3 Registry 查询

`IChatResponderRegistry` 增加：

```csharp
bool TryGet(string providerRef, out ChatResponderRegistration registration);
```

`providerRef` 格式：

```text
<pluginId>#<contributionId>
```

例如：

```text
natter.feature.ai.deepseek#deepseek
natter.feature.ai.spark#spark
```

要求：

- 启动时校验唯一；
- 如果重复，Tier 1 启动失败；
- 如果 profile 引用的 provider 未加载，按 Tier 处理（1 失败，2 禁用）。

### 5.4 路由流程

```csharp
var profile = _resolver.Resolve(event.Source, conversation, sender);
if (!profile.Policy.Enabled)
    return null;

if (!_registry.TryGet(profile.Ai.Provider, out var responder))
    return profile.SupportTier == 1
        ? throw new InvalidOperationException(...)
        : null;

return await responder.Factory().RespondAsync(chatEvent, ct);
```

例外：

- provider 内部异常不能阻止其他 source 的独立请求；
- 日志记录 `profileId`、`providerRef`、`eventId`；
- 不记录正文/密钥。

### 5.5 默认 Profile

- `tier1-default`：Discord → DeepSeek；
- `tier2-dingtalk-spark`：DingTalk → Spark；
- 没有匹配的 source → `tier1-default`；
- 不按 priority 自动选 provider。

---

## 6. 持久化会话

### 6.1 目标

- 重启不丢；
- 多 provider 共享；
- 按 source/conversation 隔离；
- 不把 history 存在插件内部。

### 6.2 Host 服务

新增 host-owned：

```csharp
public interface IConversationStore
{
    ValueTask<ConversationState> GetOrCreateAsync(
        ConversationKey key,
        CancellationToken ct = default);

    ValueTask<IReadOnlyList<StoredMessage>> GetRecentAsync(
        ConversationKey key,
        int maxMessages,
        int maxCharacters,
        CancellationToken ct = default);

    ValueTask AppendTurnAsync(
        ConversationKey key,
        StoredMessage userMessage,
        StoredMessage assistantMessage,
        CancellationToken ct = default);

    ValueTask ClearAsync(
        ConversationKey key,
        CancellationToken ct = default);
}
```

`ConversationKey`：

```csharp
public readonly record struct ConversationKey(
    string SourceKey,
    string ConversationId,
    string? GuildId,
    string? ActorId);
```

要求：

- `SourceKey` 必须包含 protocol/platform/selfId；
- `history.scope=user` 时 key 包含 actor；
- `conversation` 时不包含 actor；
- 不能用 Discord channel id 直接当全局 key。

### 6.3 SQLite

建议使用 `Microsoft.Data.Sqlite`（不用 EF，保持轻量）。

路径：

```text
<state-dir>/natter.db
```

CLI：

```text
--state-dir <dir>
NATTER_STATE_DIR
```

Schema：

```sql
PRAGMA journal_mode = WAL;
PRAGMA foreign_keys = ON;

CREATE TABLE IF NOT EXISTS sessions (
    session_id      TEXT PRIMARY KEY,
    source_key      TEXT NOT NULL,
    protocol        TEXT NOT NULL,
    platform        TEXT NOT NULL,
    conversation_id TEXT NOT NULL,
    guild_id        TEXT NULL,
    actor_id        TEXT NULL,
    created_at      TEXT NOT NULL,
    updated_at      TEXT NOT NULL,
    version         INTEGER NOT NULL DEFAULT 0,
    UNIQUE(source_key, conversation_id, guild_id, actor_id)
);

CREATE TABLE IF NOT EXISTS messages (
    message_id      INTEGER PRIMARY KEY AUTOINCREMENT,
    session_id      TEXT NOT NULL REFERENCES sessions(session_id) ON DELETE CASCADE,
    role            TEXT NOT NULL,
    text            TEXT NOT NULL,
    source_event_id TEXT NULL,
    created_at      TEXT NOT NULL,
    UNIQUE(session_id, source_event_id)
);

CREATE INDEX IF NOT EXISTS ix_messages_session
    ON messages(session_id, message_id);
```

### 6.4 事务与幂等

- append user+assistant 在一个事务；
- `source_event_id` 唯一，防重复；
- 如果 assistant 回复失败，不写入半条；
- `ClearAsync` 删除 session/messages；
- 读取按 `message_id` 倒序取 N，再正序返回；
- 字符上限从最旧开始裁。

### 6.5 NPS 注入

`NpsPluginRuntime.BuildPluginProvider` 增加：

```csharp
if (candidate.Permissions?.Granted.Contains(PluginPermissions.Storage) == true)
    services.AddSingleton(_hostProvider.GetRequiredService<IConversationStore>());
```

DeepSeek/Spark manifest 增加 `storage` 权限。

插件改用 host store：

- 删除插件内部 `AiConversationStore` 单例注册；
- `AiChatResponder` 注入 `IConversationStore`；
- `historyScope` 由 profile 决定，不由插件 config 决定；
- 保留内存实现用于单元测试。

### 6.6 迁移

当前内存历史无需迁移；首次启动空库。
不要尝试导入 `AiConversationStore` 的进程内字典。

---

## 7. `natter serve`

### 7.1 命令

```bash
natter serve \
  --config-dir ./config \
  --state-dir ./state \
  --profile tier1-default
```

参数：

| 参数 | 说明 |
| --- | --- |
| `--config-dir` / `--plugin-config-dir` | profiles + 插件 config |
| `--state-dir` | SQLite/状态目录 |
| `--profile` | 覆盖默认 profile（可选） |
| `--duration <seconds>` | 测试用；到时优雅停止 |
| `--health-port <port>` | 可选 loopback HTTP health |
| `--json` | 启动摘要/健康输出 |

环境变量：

```text
NATTER_CONFIG_DIR
NATTER_STATE_DIR
NATTER_PROFILE
```

### 7.2 行为

```text
build host
→ load profiles
→ validate providers
→ start host (plugins/upstreams)
→ wait for cancellation / duration
→ graceful stop
→ dispose
```

要求：

- Ctrl+C 后 10 秒内退出；
- upstream 断开、插件停止顺序正确；
- 启动失败返回非 0；
- Tier 1 provider 缺失直接失败；
- Tier 2 provider 缺失只禁用对应 profile；
- 启动日志包含 profile 数、upstream 数、provider 数；
- 不输出 secret。

### 7.3 Health

最小实现：

```bash
natter health --json
```

返回：

```json
{
  "status": "healthy",
  "uptimeSeconds": 123,
  "profiles": 2,
  "upstreams": [
    { "source": "discord:discord:...", "connected": true },
    { "source": "dingtalk:dingtalk:...", "connected": true }
  ],
  "providers": [
    { "id": "natter.feature.ai.deepseek#deepseek", "available": true },
    { "id": "natter.feature.ai.spark#spark", "available": false }
  ],
  "plugins": { "activated": 4, "failed": 0 }
}
```

`--health-port` 可选提供：

```text
GET http://127.0.0.1:<port>/health
```

只绑定 loopback；默认关闭。

---

## 8. 一等/二等公民

### 8.1 支持等级

| Tier | 平台/provider | 发布闸门 | 测试 | 功能 |
| --- | --- | --- | --- | --- |
| 1 | Discord + DeepSeek | 必须通过 | 单元 + live + 长跑 | 优先完整 |
| 2 | DingTalk + Spark | 不阻塞 Tier 1 | 契约 + smoke/manual | 核心文本链路 |

### 8.2 运行时策略

- 默认 profile 只启用 Tier 1；
- Tier 2 profile 存在但可选；
- Tier 2 插件缺失不阻止启动；
- Tier 1 provider 缺失阻止启动；
- Tier 2 最小能力：连接、文本、mention、reply、错误、source 隔离；
- Tier 2 不要求 slash/cards/streaming/tools；
- 不为 Tier 2 在 core 加平台特判。

### 8.3 测试策略

- Tier 1：每次合并跑框架 + fake live；
- Tier 2：契约测试保留；真实凭据 smoke 手动/ nightly；
- Tier 2 失败不阻塞 Tier 1 发布，但必须有 issue；
- Tier 2 不允许破坏 NPS/NLMP 契约。

---

## 8.4 本轮实现状态

| 项 | 状态 | 文件 |
| --- | --- | --- |
| RuntimeProfile 模型/校验 | 已实现 | `src/Natter.Application/Chat/Profiles/RuntimeProfiles.cs` |
| ProfileResolver 优先级 | 已实现 | 同上 |
| `IChatResponderRegistry.TryGet` | 已实现 | `ChatResponderRegistry.cs` |
| ProfileAwareChatResponder | 已实现 | `ProfileAwareChatResponder.cs` |
| Tier 1/Tier 2 启动策略 | 已实现（Tier 2 缺失时路由返回 null） | `RuntimeProfileValidator.cs` |
| SQLite IConversationStore | 已实现 | `SqliteConversationStore.cs` |
| Host store 注入插件 | 已实现（`storage` 权限） | `NpsPluginRuntime.cs` + `HostConversationStoreAdapter.cs` |
| `--config-dir` / `--state-dir` / `--profile` | 已实现 | `NatterCliOptions.cs` |
| `serve` / `health` | 已实现 | `RuntimeCommands.cs` |
| profile history scope/limits | 已实现（AsyncLocal profile context） | `ProfileContext.cs` + adapter |
| profile `ai.model` | 已实现（部分 provider 支持 ModelId） | `AiChatResponder.cs` |
| HTTP health port | 已实现（loopback `/health`） | `HealthHttpServer.cs` |
| 多实例/Postgres | 未实现 | — |
| Android 动态宿主 | 不在范围 | — |

测试：`Natter.Framework.Tests` **214/214**；新增 profile/resolver/validation、SQLite、health/serve/state 测试。

## 9. 实施顺序

### Step 1：Profile 模型 + 校验

- `RuntimeProfile` / `RuntimeProfileSet`；
- JSON load + schema validation；
- `ProfileResolver` + precedence tests；
- `profiles.json` 示例。

### Step 2：Provider 路由

- `IChatResponderRegistry.TryGet(providerRef)`；
- `ProfileAwareChatResponder`；
- host 注册 profile-aware responder；
- DeepSeek/Spark priority 保留仅 fallback；
- tests：Discord→DeepSeek、DingTalk→Spark、缺失 provider、Tier 1/2 策略。

### Step 3：SQLite 会话

- add `Microsoft.Data.Sqlite`；
- `IConversationStore` + SQLite implementation；
- schema/migrations/transaction/idempotency；
- NPS `BuildPluginProvider` 注入；
- DeepSeek/Spark 改用 host store；
- tests：重启恢复、source 隔离、scope、重复事件。

### Step 4：CLI

- `--config-dir` / `--state-dir`；
- `serve`；
- `health`；
- `NATTER_*` env；
- graceful shutdown；
- tests：启动、Ctrl+C、duration、失败返回码。

### Step 5：并发/隔离验证

- fake Discord + fake DingTalk 同时在线；
- 两个 source 同时发消息；
- 一个 provider 故障不影响另一个；
- 一个 upstream 断开不影响另一个；
- 重启恢复；
- 不按 transport×provider 配对。

### Step 6：Tier 策略与文档

- default profile / optional profile；
- support matrix；
- plugin manifests permissions；
- docs.

---

## 10. 验收标准

### Runtime

- [ ] `natter serve --config-dir ... --state-dir ...` 能长驻；
- [ ] Ctrl+C 10 秒内优雅退出；
- [ ] `--duration` 测试模式可用；
- [ ] Tier 1 provider 缺失启动失败；
- [ ] Tier 2 provider 缺失只禁用对应 profile；
- [ ] 启动日志包含 profile/upstream/provider 数量；
- [ ] 无 secret 泄漏。

### Profile / Routing

- [ ] `profiles.json` schema 校验；
- [ ] 匹配优先级确定；
- [ ] Discord → DeepSeek；
- [ ] DingTalk → Spark；
- [ ] 不依赖 priority；
- [ ] 同一 provider 可服务多个 transport；
- [ ] profile 可覆盖默认 provider；
- [ ] 路由日志包含 profileId/providerRef/eventId。

### Persistence

- [ ] SQLite 自动建库/迁移；
- [ ] 重启后最近历史恢复；
- [ ] 同一 source/conversation 隔离；
- [ ] `history.scope=user` 按 actor 隔离；
- [ ] 重复 event id 不重复写入；
- [ ] provider 切换后历史仍共享；
- [ ] 删除/清理接口可用。

### Health / Ops

- [ ] `natter health --json` 返回 upstream/provider/plugin；
- [ ] 可选 loopback health endpoint；
- [ ] 健康状态不依赖调用真实 AI；
- [ ] 日志结构化、可过滤。

### Concurrency

- [ ] Discord + DingTalk 同时在线；
- [ ] 两个 source 独立路由；
- [ ] 一个 provider 故障不影响另一个；
- [ ] 一个 upstream 断开不影响另一个；
- [ ] 无跨 source 回复/记忆泄漏。

### Tests

- [x] profile resolver tests；
- [x] router tests；
- [x] SQLite store tests；
- [x] serve/health CLI tests；
- [ ] Tier 1 live smoke（本轮未重跑真实 Discord/DeepSeek）；
- [ ] Tier 2 contract/smoke 可选；
- [x] 200 框架测试不回归。

---

## 11. 明确不做

- Context/Memory 插件（P3）；
- Task Worker / AgentTask；
- 多实例 Postgres/Redis；
- Android 动态插件；
- 第三方插件市场；
- 钉钉卡片/图片/语音；
- Spark streaming/tools；
- 跨平台身份自动合并；
- 为 Tier 2 在 core 加平台特判。

---

## 12. 交给工程师的落地清单

1. 先提交 `RuntimeProfile` 模型 + `profiles.schema.json` + 校验测试；
2. 再做 `IChatResponderRegistry.TryGet` + `ProfileAwareChatResponder`；
3. 迁移 DeepSeek/Spark 到 profile 路由，保留 priority fallback；
4. 加 SQLite `IConversationStore`，把插件内存历史替换为 host store；
5. 加 `serve` / `health` / `--config-dir` / `--state-dir`；
6. 最后做并发/隔离测试和 Tier 1/Tier 2 支持矩阵；
7. 每一步保持现有 190+ 测试通过；
8. 不把测试凭据、state 目录、生成数据库提交到仓库。

---

## 13. 结论

P2 的核心不是“多一个 CLI 命令”，而是把三件事定死：

1. **Profile 决定 provider，不决定 transport；**
2. **Host 拥有会话状态，provider 插件共享；**
3. **Tier 1 是发布闸门，Tier 2 是契约探针。**

这样 P1 的 priority 冲突自然消失，P3 的 context/memory 插件也有稳定的 session/profile 基础。
