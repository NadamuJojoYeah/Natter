# P4 Tier 1 Productization — Development Spec

> **⚠️ 归档文档 · 历史记录，勿据此实现。**
> 本文件描述的阶段已完成或已被取代，仅用于追溯当时的决策与证据。
> 现行文档见 [docs/README.md](../README.md)。

> **状态**：P4.0–P4.1 foundation implemented；P4 验收 No-Go（schema/命令 UX 阻塞，profile store/单实例/输出异常待收敛）。
> **日期**：2026-11-15
> **前置**：P3 已 Go（Context/Memory foundation 已归档；232/232 回归）
> **目标**：把 Discord + DeepSeek 从“架构验证”推进到“真实用户可长期使用、可运维、可恢复”的一等公民产品。
> **关联**：[P3 Context & Memory](P3-CONTEXT-MEMORY.md)、[P2 Runtime & Profile](../P2_RUNTIME_PROFILE.md)、[Harness 架构](../HARNESS_ARCHITECTURE.md)、[Discord transport](../DISCORD_PLUGIN_DESIGN.md)
> **验收处置**：[P4 验收 No-Go](../testing/2026-11-15-p4-acceptance-response.md)

---

## 0. 一句话目标

> **P4 完成后，真实 Discord 用户可以在聊天里 `/ask`、`/remember`、`/forget`、`/reset`，记忆可召回/删除/重启保持；`serve` 能作为服务连续运行 24h，配置/state 可备份恢复，日志/指标可排障。**

P4 不接第三个平台，不做 Task Worker，不做 Android/市场，不上向量库。

---

## 1. 为什么是 P4

P2 解决了：

- serve / profiles / provider 路由；
- SQLite 会话；
- health / state 权限；
- Tier 1/Tier 2 分层。

P3 解决了：

- prompt/context/memory 契约；
- SQLite summary/memory；
- 显式 remember/forget；
- 真实记忆召回/删除/重启。

但当前还缺：

1. **用户入口**：memory 管理只有 CLI，真实 Discord 用户用不到；
2. **slash 命令**：`/ask`、`/remember`、`/forget`、`/reset` 没有完整 UX；
3. **运维闭环**：备份/恢复/升级/24h soak 未验证；
4. **可观测性**：health 有，但请求/成本/错误指标不足；
5. **P3 残留**：没有第三方 `memory.store` 示例插件；真实多轮 summary 未验证；
6. **Tier 1 UX**：streaming/attachments/reconnect/限流仍不完整。

P4 的目标是把这些收口，不是继续扩平台。

---

## 2. 范围

### 2.1 In scope

- P3.1 残留关闭：
  - 最小 `memory.store` 示例插件；
  - 真实多轮 summary；
- Discord 聊天内命令：
  - `/ask`；
  - `/remember`；
  - `/forget`；
  - `/memory`；
  - `/reset`；
- Memory 用户 UX：
  - 默认 user scope；
  - 共享 scope 的创建者/管理员权限；
  - list/forget 的用户可见反馈；
- Discord Tier 1：
  - slash 注册/更新；
  - 多 guild / 频道 allowlist；
  - reconnect / 429 / 权限错误降级；
  - 长文本分片/reply 回归；
  - streaming/attachments 按能力评估；
- DeepSeek Tier 1：
  - usage/成本统计；
  - 超时/错误降级；
  - thinking 关闭状态稳定；
  - provider 参数配置化；
- 运维：
  - systemd/container 部署示例；
  - 配置/secret/state 目录；
  - SQLite 备份/恢复；
  - 升级/回滚；
  - health/metrics/结构化日志；
  - 24h soak。
- Tier 2 smoke：
  - DingTalk + Spark 只保证文本链路不回归。

### 2.2 Out of scope

- Task Worker / AgentTask / tools；
- 第三平台/Android；
- 插件市场；
- 向量/embedding/GraphRAG；
- 自动模型记忆提取；
- semantic response cache；
- 多实例集群；
- 钉钉卡片/审批/语音；
- Spark streaming/tools。

---

## 3. P3.1 残留关闭

### 3.1 示例 memory.store 插件

新增：

```text
samples/Natter.Sample.Plugin.Memory/
├── plugin.json
├── config.schema.json
├── MemoryStorePlugin.cs
└── FileMemoryStore.cs
```

要求：

- NPS `kind=memory`；
- contribution `memory.store` id `file`；
- `storage` 权限；
- 最小文件存储或内存存储；
- 支持 `Write/Search/Delete/Export/Import`；
- 可被 `profiles.json` 选择：
  ```json
  "memory": { "store": "natter.sample.memory#file" }
  ```
- 停用插件前必须把 profile 切回 `host#sqlite`；profile 仍引用缺失组件时启动失败，不静默回滚；
- 不实现向量/外部服务。

测试：

- `plugins contributions` 显示该插件；
- profile 选择后 `remember`/`memory list` 走该插件；
- 停用/回滚后不崩溃，落到默认 store；
- 第三方 store 的 scope 行为与 host 一致。

### 3.2 真实多轮 summary

测试：

- `context.maxRecentMessages = 2`；
- 真实 Discord 连续 10+ 轮；
- `summaries` 表出现并随新消息更新；
- 回复使用 summary；
- 重启后 summary 仍在；
- summary 不无限增长；
- `forget` 后相关 summary 失效。

通过后，P3 的“真实多轮 summary”从组件级变为真实闭环。

---

## 4. Discord 命令 UX

### 4.1 命令清单

| 命令 | 作用 | 默认权限 |
| --- | --- | --- |
| `/ask prompt:<text>` | AI 回答 | 所有人 |
| `/remember text:<text> [scope]` | 写入显式记忆 | 所有人（user scope） |
| `/forget target:<id\|text>` | 删除记忆 | 创建者 |
| `/memory` | 列出当前用户可见记忆 | 所有人 |
| `/reset` | 清空当前会话历史/摘要 | 所有人（当前会话） |

共享 scope：

- `/remember scope:conversation` / `guild` 需要管理员或显式权限；
- `/forget` 共享记忆默认只允许创建者；
- `/memory` 在 guild 默认只显示 user scope；管理员可列出共享。

### 4.2 命令路由

当前 `ChatOrchestrator` 把交互交给 `IChatResponder`；AI responder 不应处理 `/remember`。

建议新增：

```csharp
public interface IChatCommandHandler
{
    string Command { get; }
    ValueTask<ChatCommand?> HandleAsync(
        Interaction interaction,
        CancellationToken ct = default);
}
```

或复用 `chat.responder` 但加 **command router**：

```text
InteractionCreatedEvent
  → ChatCommandRouter（优先）
      ├── /remember
      ├── /forget
      ├── /memory
      ├── /reset
      └── /ask → AI responder
  → 未命中 → AI responder
```

要求：

- 命令处理优先于 AI；
- 未知命令返回 ephemeral help；
- 不把命令文本送进 AI；
- 命令 handler 不直接调 Discord API，返回 `InteractionRespondCommand`；
- 所有命令有 3 秒内 defer（transport 已处理）；
- 错误文案不含内部异常/密钥。

### 4.3 Discord 注册

- Discord transport 支持按配置注册命令；
- 命令定义来自 host command catalog，不硬编码在 transport；
- `devGuildId` 用于开发；
- 全局命令传播慢，测试 guild 先用 guild command；
- 命令更新/删除要幂等；
- 不可用命令（Tier 2 平台）通过 capability 隐藏。

### 4.4 Memory scope

`/remember` 默认：

```text
scope=user
source=当前 interaction.Source
conversation=当前 channel
actor=interaction.User.Id
sensitivity=normal
```

- 不默认写 guild/global；
- 共享 scope 需要管理员权限；
- 用户可 `forget` 自己的 memory；
- `/memory` 默认 user scope；
- 不在 guild 展示 private memory。

---

## 5. Discord Tier 1 产品化

### 5.1 Slash/命令

- `/ask` 支持空输入提示；
- `/remember` 参数校验；
- `/forget` id/text 二选一；
- `/memory` 分页/limit；
- `/reset` 二次确认或明确提示；
- 命令回复 ephemeral 选择：
  - `/remember`、`/forget`、`/memory` 默认 ephemeral；
  - `/ask` 公开回复；
  - `/reset` ephemeral。

### 5.2 多 guild / 频道

- profile 可按 guild/channel 路由；
- allowlist 与 profile 匹配一致；
- 新 guild 不自动开放；
- guild 配置变更无需重启（可选）；
- 频道删除/权限丢失有诊断。

### 5.3 Reconnect / 限流 / 错误

- Gateway 断线重连；
- 429/5xx 退避；
- 权限不足时给管理员可读日志，不刷用户屏；
- 消息分片/reply 回归；
- typing 不阻塞；
- `discord status` 暴露 send_failures/last_error。

### 5.4 Streaming / Attachments（按能力）

- 先评估，不默认全开；
- streaming 需要：
  - provider 支持流式；
  - Discord 编辑节流；
  - 失败回退整段；
  - `Capabilities.StreamEdit=true` 才能启用；
- attachments/vision 需要：
  - provider 支持；
  - 下载/大小/MIME/私网 guard；
  - capability 声明；
- 没有实现就保持 `false`，不要“声明支持”。

---

## 6. DeepSeek Tier 1 产品化

- 模型/参数配置化；
- thinking disabled 默认；
- usage：请求数、输入/输出 token、成本估算；
- 超时/429/5xx 降级；
- 错误不泄漏 key/prompt；
- `/ask` 的 latency p50/p95；
- 429/超时用户文案；
- 长上下文预算与 memory 裁剪联动；
- `deepseek status`/`ai status` 显示 provider/model/usage；
- 不做 tools/streaming（后续）。

---

## 7. 运维与部署

### 7.1 Serve 部署

提供：

```text
deploy/
├── systemd/natter.service
├── docker/Dockerfile
├── docker/compose.yaml
└── README.md
```

要求：

- 配置文件外置；
- secret 从环境/secret provider；
- state 目录持久化；
- 非 root 运行；
- 只读插件目录；
- graceful stop；
- `Restart=on-failure`；
- health check 使用 `/health` 或 `natter health`。

### 7.2 SQLite 备份/恢复

- 备份前 WAL checkpoint；
- 使用 SQLite backup API 或 `.backup`；
- 备份文件 600；
- 恢复流程文档化；
- migration 前自动备份；
- 恢复后 health/会话检查；
- 不在备份中输出 secret。

### 7.3 升级/回滚

- 插件版本固定；
- profile 版本固定；
- DB migration 只向前；
- 升级前备份；
- 回滚需要恢复 DB 备份或兼容 migration；
- 失败自动回到上一版插件目录（可选）。

### 7.4 可观测性

指标：

- ingress/命令/回复；
- profile/provider 路由；
- AI latency/error/usage；
- context chars、memory count、summary count；
- Discord reconnect/429/send failure；
- SQLite size/WAL；
- plugin failed count。

日志：

- eventId/sessionId/profileId/providerRef；
- 不记录 prompt/memory 正文/密钥；
- 结构化，可被 journald/容器采集。

---

## 8. 安全

- `/remember` 用户显式触发；
- 共享 scope 创建者/管理员权限；
- `/forget` 创建者校验（P3 已修）；
- DM private 不进入 guild；
- prompt injection 已由 P3 测试覆盖，P4 继续回归；
- memory export/import 只导出当前 scope；
- state 700/DB 600；
- 日志脱敏；
- 插件权限最小化；
- `serve` 不以 root 运行。

---

## 9. 测试与验收

### 9.1 P3.1 回归

- 示例 memory.store 插件加载/选择/回滚；
- 真实多轮 summary；
- P3 232/232 不回归。

### 9.2 P4 命令 UX

- `/ask` 正常；
- `/remember` 写入 user scope；
- `/forget` 删除；
- `/memory` 只显示可见项；
- `/reset` 清空当前会话；
- 共享 scope 权限拒绝；
- 未知命令 help；
- ephemeral 行为正确。

### 9.3 真实 Discord

- 两个 guild；
- DM；
- 长文本分片；
- reply 引用；
- 断线重连；
- 限流/权限错误；
- 重启后 memory/session 恢复。

### 9.4 运维

- systemd/container 启动；
- 备份/恢复；
- 升级/回滚；
- health/metrics；
- 24h soak：无重复回复、无内存/连接失控、无密钥泄漏。

### 9.5 Tier 2

- DingTalk + Spark 文本 smoke；
- 不要求新命令/memory UX；
- 不阻塞 Tier 1 发布。

### 9.6 签收标准

- [ ] P3.1 两项关闭；
- [ ] `/ask` `/remember` `/forget` `/memory` `/reset` 真实可用；
- [ ] 共享 scope 权限正确；
- [ ] 真实多 guild/DM；
- [ ] 24h soak；
- [ ] 备份/恢复；
- [ ] 232/232 + P4 测试；
- [ ] 日志/state/secret 扫描通过；
- [ ] Tier 2 smoke；
- [ ] 测试记录归档。

---

## 9.7 本轮实现状态

| 项 | 状态 | 文件 |
| --- | --- | --- |
| 示例 memory.store 插件 | 已实现（JSON 文件持久化、`kind=memory`、NPS 激活/回滚） | `samples/Natter.Sample.Plugin.Memory` |
| Discord 命令路由 | `/ask` `/remember` `/forget` `/memory` `/reset` | `ChatCommandRouter.cs` |
| Discord 命令注册 | transport 按命令名生成参数 schema | `DiscordSocketTransport.cs` |
| memory UX | user scope 默认、共享 scope 拒绝、创作者删除、ephemeral 反馈 | 同上 |
| P3 shared delete | 共享 scope 创建者校验 | `SqliteMemoryStore` / `InMemoryMemoryStore` |
| host contributions | 合并 host prompt/context/memory | `plugins contributions` |
| export/import CLI | `memory export/import` + 0600 文件 | `MemoryCommands.cs` |
| prompt injection | memory/summary 移出 system，作为 untrusted data | `HostRecentWindowContextAssembler` |
| summary 增量 | `ToMessageId` 比较 + 新版本 | `HostContextPipeline` |
| deploy | systemd/docker/backup 示例 | `deploy/`、`scripts/natter-backup.sh` |
| profile-aware memory resolver | CLI/Discord 命令统一按 profile 选择 store，缺失 fail-closed | `ProfileMemoryStoreResolver.cs` |
| 单实例锁 | `<state-dir>/.lock` 独占锁 | `ServeCommand` |
| clipped 输出 | 检测并重试一次，仍异常则降级；不写 clipped 内容 | `AiChatResponder` |
| 代理/超时 | Discord REST+Gateway 统一代理、ConnectionTimeout≥READY 窗口 | `DiscordEgressProxy` |
| health uptime/Tier1 | 首次 uptime >0、startedAt/sampledAt、Tier2 缺失不降级 | `HostRuntimeClock` / `RuntimeHealth` |
| health offline | `health --offline` 不启动 gateway | `NatterCli` |
| upstream 诊断 | lastEvent/connected/disconnect/reconnect count | `IChatUpstreamHealth` |
| 24h soak | **未完成**：按 [passive-soak-24h](../operations/passive-soak-24h.md) §3.1 的「累计有效运行」口径，需 Σ有效运行 ≥24h 且满足单段 ≥6h／缺口归因／RSS 漂移三项护栏；截至 2026-11-22 无合格记录（现有 soak-once 0.3%、toolcheck 83.3%、p2 旧格式 0 样本均判 FAIL），不得按 PASS 计 | — |
| 真实双 guild/DM | 未执行（测试侧） | — |

测试：`Natter.Framework.Tests` **245/245**；P3 232 基线未回归。

## 10. 实施顺序

```text
P4.0：P3.1 残留
  ├── 示例 memory.store 插件
  └── 真实多轮 summary

P4.1：命令路由 + memory UX
  ├── /ask /remember /forget /memory /reset
  ├── 权限/scope/ephemeral
  └── Discord 注册

P4.2：Discord Tier1
  ├── 多 guild/频道
  ├── reconnect/429/权限
  ├── 分片/reply
  └── streaming/attachments 评估

P4.3：Ops
  ├── systemd/container
  ├── backup/restore
  ├── metrics/log
  └── 24h soak

P4.4：验收
```

---

## 11. 后续路线

- **P5：Task Worker / Tools**
  AgentTask、worker 隔离、工具白名单、checkpoint/watchdog。
- **P6：检索增强**
  embedding、hybrid retrieval、rerank、记忆冲突/衰减。
- **P7：生态**
  第三平台、Android、插件市场、out-of-process host。

P4 不提前做 P5/P6/P7。

---

## 12. 一句话结论

**P3 证明“有记忆”；P4 要证明“真实用户能用、能运维、能长期跑”。**

先补 P3.1 的两个证据缺口，再做 Discord 命令 UX、Tier 1 稳定性和运维闭环；Task Worker 和第三平台都往后放。
