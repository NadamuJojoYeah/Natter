# P3 Context & Memory Acceptance Test Plan

> **状态**：Ready for test  
> **日期**：2026-11-15  
> **基线**：`HEAD=04831df`（P2 已归档；P3 foundation 已提交）  
> **被测对象**：Prompt/Context/Memory 契约、profile 组件选择、SQLite summary/memory、显式 remember/forget、context pipeline、NPS contribution  
> **开发规格**：[P3 Context & Memory](../P3_CONTEXT_MEMORY.md)  
> **目标**：验证 P3 是否真正可用：能选择策略、能召回显式记忆、能删除、能不串 scope、能在重启后保持。

---

## 0. 通过口径

P3 Go 需要同时满足：

1. 旧 P2 `profiles.json` 兼容；
2. 新 profile 能选择 prompt/context/memory；
3. SQLite 从 P2 迁移到 P3 成功；
4. `remember` / `memory list` / `forget` 可用；
5. 真实 Tier 1 DeepSeek 能召回显式记忆；
6. `forget` 后不再召回；
7. DM/guild/source/actor 隔离；
8. summary/memory 失败降级，不阻塞回复；
9. Tier 2 DingTalk+Spark smoke 不回归；
10. 无密钥/prompt/memory 正文泄漏。

P3 不通过，不进入 P4。

---

## 1. 范围

### 1.1 In scope

- `IPromptProvider` / `IContextAssembler` / `IMemoryStore` / `ISummaryStore`；
- `profiles.json` 的 `prompt` / `context` / `memory`；
- `host#static` / `host#recent-window` / `host#sqlite` / `host#inline`；
- SQLite `summaries` / `memory_items`；
- `remember` / `memory list` / `forget`；
- 显式记忆；
- context 预算/裁剪；
- memory/summary 失败降级；
- NPS `prompt.provider` / `context.assembler` / `memory.store`；
- Tier 1 真实记忆召回；
- Tier 2 smoke。

### 1.2 Out of scope

- 向量检索/embedding；
- 自动模型记忆提取；
- LLM 摘要质量评估；
- semantic response cache；
- 多实例 memory；
- Context/Memory UI；
- 第三方 memory SaaS；
- Task Worker；
- 第三个平台/Android。

---

## 2. 环境

| 项 | 要求 |
| --- | --- |
| OS | Linux 优先 |
| .NET | SDK 10.0.114+ |
| CLI | `src/Natter.Cli/bin/Debug/net10.0/natter` |
| 配置目录 | `artifacts/p3/<run>/config` |
| 状态目录 | `artifacts/p3/<run>/state` |
| 插件目录 | `artifacts/p3/<run>/plugins` |
| 凭据 | `~/.config/natter/live.env`（600，仓库外） |
| 证据 | `artifacts/p3/<run>/evidence/` |

凭据变量：

```text
DEEPSEEK_API_KEY
DISCORD_BOT_TOKEN
DISCORD_BOT_USER_ID
DISCORD_TEST_GUILD_ID
DISCORD_TEST_CHANNEL_ID
SPARK_API_KEY
DINGTALK_CLIENT_ID
DINGTALK_CLIENT_SECRET
DINGTALK_BOT_ID
```

不要写入 `profiles.json`、插件 `config.json`、截图或测试记录。

---

## 3. 测试前准备

### 3.1 构建与回归

```bash
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

预期：

- build 成功；
- 框架测试 **227/227**（或当前 HEAD 等价全绿）。

### 3.2 无凭据 live

```bash
env -u NATTER_LIVE \
    -u DEEPSEEK_API_KEY \
    -u DISCORD_BOT_TOKEN \
    -u SPARK_API_KEY \
    -u DINGTALK_CLIENT_SECRET \
  dotnet test tests/Natter.Live.Tests/Natter.Live.Tests.csproj
```

预期：

- 全 skip；
- 0 failed；
- 不因缺凭据崩溃。

### 3.3 P2 旧 profile 兼容

用 P2 的 `profiles.json`（不含 prompt/context/memory）启动 `serve`：

```bash
natter --config-dir <p2-config> --state-dir <p3-state> serve --duration 3 --json
```

预期：

- 正常启动；
- 使用 host 默认 prompt/context/memory；
- 不要求用户手写新字段。

### 3.4 P3 profile

用 `config/profiles.json` 示例，包含：

```json
"prompt": { "provider": "host#static", "systemPrompt": "..." },
"context": {
  "assembler": "host#recent-window",
  "maxInputCharacters": 12000,
  "maxMemoryItems": 8,
  "maxSummaryCharacters": 1200
},
"memory": {
  "store": "host#sqlite",
  "retriever": "host#recent",
  "extractor": "host#explicit"
}
```

---

## 4. 测试用例

### Gate A：静态 / Profile 兼容

| ID | 用例 | 命令/方法 | 预期 |
| --- | --- | --- | --- |
| P3-001 | 构建 | `dotnet build` | 成功 |
| P3-002 | 框架回归 | `dotnet test` | 227/227 |
| P3-003 | 无凭据 live | 见 §3.2 | 全 skip，0 failed |
| P3-004 | 旧 P2 profile | 无新字段启动 | 成功，默认 host 组件 |
| P3-005 | 新 profile | 完整字段启动 | 成功 |
| P3-006 | 非法 ref | `prompt.provider=hoststatic` | 启动失败，提示 `owner#id` |
| P3-007 | 缺 Tier 1 ref | 引用不存在 prompt/context/memory | 启动失败 |
| P3-008 | 缺 Tier 2 ref | Tier 2 profile 引用不存在组件 | 禁用/明确失败，不能 fallback 到 Tier 1 |
| P3-009 | context 预算非法 | `maxInputCharacters=0` | 启动失败 |

### Gate B：组件与 NPS

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P3-010 | `plugins contributions` | 四个插件 + P3 组件 | 显示 prompt/context/memory contribution |
| P3-011 | 组件选择 | profile 切换 `host#recent-window` | 生效 |
| P3-012 | plugin 组件 | 装一个 memory.store 插件 | 可选择、可回滚 |
| P3-013 | 缺失组件 | profile 引用未加载插件 | 按 Tier 策略处理 |
| P3-014 | 组件回滚 | 停用插件后重启 | profile 不静默使用错误实现 |

### Gate C：SQLite 迁移与存储

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P3-020 | P2 DB 迁移 | 用 P2 `natter.db` 启动 | `PRAGMA user_version=2` |
| P3-021 | 表存在 | 查询 | `summaries` / `memory_items` 存在 |
| P3-022 | 重复迁移 | 重启两次 | 幂等，不丢数据 |
| P3-023 | 权限 | `stat` | state 700、DB/WAL/SHM 600 |
| P3-024 | summary 持久化 | 写入/重启/读取 | 内容一致 |
| P3-025 | memory 持久化 | 写入/重启/读取 | 内容一致 |
| P3-026 | export/import | 导出到另一个 DB | 往返一致 |
| P3-027 | delete | 删除 memory | 记录消失，summary 失效 |
| P3-028 | 过期 | `expires_at` 过去 | 不再召回 |

### Gate D：显式记忆 CLI

命令模板：

```bash
natter --config-dir artifacts/p3/<run>/config \
       --state-dir artifacts/p3/<run>/state \
  remember "用户喜欢乌龙茶" \
  --source discord:discord:<botId> \
  --conversation <channelId> \
  --actor <humanUserId> \
  --scope user --kind preference --json

natter ... memory list \
  --source discord:discord:<botId> \
  --conversation <channelId> \
  --actor <humanUserId> --json

natter ... forget <memory-id> \
  --source discord:discord:<botId> \
  --conversation <channelId> \
  --actor <humanUserId> --json
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| P3-030 | remember | 返回 `mem_...`，`memory list` 可见 |
| P3-031 | 重复 remember | 不重复插入，返回已有项 |
| P3-032 | scope=user | 同 actor 其他会话可见 |
| P3-033 | scope=conversation | 只在该 conversation 可见 |
| P3-034 | scope=guild | 同 guild 可见，其他 guild 不可见 |
| P3-035 | scope=global | 同 source 可见 |
| P3-036 | forget by id | 删除成功 |
| P3-037 | forget by text | 删除成功 |
| P3-038 | forget 后 list | 不再出现 |
| P3-039 | source/actor 不匹配 | 看不到/删不掉 |

> 关键：CLI 的 `--source` / `--conversation` / `--actor` 必须与真实 Discord 消息的 `Source`/`Conversation`/`Sender` 一致，否则真实回复不会召回。

### Gate E：Context 组装与预算

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P3-040 | recent window | 多轮消息 | 最近消息按序进入 |
| P3-041 | summary | 超过 window | system 中包含 `[Summary]` |
| P3-042 | memory | 写入 memory | system 中包含 `[Memory]` |
| P3-043 | 顺序 | 固定输入 | system + summary + memory + recent + current |
| P3-044 | 预算 | `maxInputCharacters=80` | `EstimatedCharacters <= 80` |
| P3-045 | 裁剪顺序 | 超预算 | 先 memory，再 summary，再 recent |
| P3-046 | 当前用户消息 | 超长输入 | 不丢失；截断并记录 diagnostics |
| P3-047 | memory store 抛异常 | fake/throwing | recent-only fallback，仍回复 |
| P3-048 | summary store 抛异常 | fake/throwing | recent fallback |
| P3-049 | assembler 抛异常 | fake/throwing | recent-only fallback |

### Gate F：真实 Tier 1 记忆召回

先跑自动化：

```bash
NATTER_LIVE=1 dotnet test tests/Natter.Live.Tests \
  --filter "FullyQualifiedName~MemoryRecallLiveTests"
```

| ID | 用例 | 预期 |
| --- | --- | --- |
| P3-050 | 显式记忆召回 | 写入“用户喜欢乌龙茶”，真实 DeepSeek 回复包含 oolong |
| P3-051 | 删除后不召回 | forget 后再问，不含 oolong |
| P3-052 | 重启后召回 | 重启 host，记忆仍在 |
| P3-053 | 真实入站 | 真人 Discord 消息使用同 source/actor 记忆 |
| P3-054 | summary + memory | 多轮后 summary/memory 同时存在，回复正常 |

注意：`MemoryRecallLiveTests` 是通过 NLMP 注入消息 + 真实 DeepSeek/Discord REST 发送，不是“真人入站”。  
P3-053 需要真人在频道 @bot，且 memory 的 source/conversation/actor 与真人一致。

### Gate G：隐私 / 隔离 / 安全

| ID | 用例 | 预期 |
| --- | --- | --- |
| P3-060 | DM private memory | 不进入 guild context |
| P3-061 | 用户 A/B | A 的 user memory 不被 B 召回 |
| P3-062 | Discord/DingTalk | 同 conversationId 不跨 source |
| P3-063 | guild scope | 其他 guild 不可见 |
| P3-064 | export scope | 只导出当前 scope |
| P3-065 | 日志 | 不含 prompt/memory 正文、密钥 |
| P3-066 | state 权限 | 700/600 |
| P3-067 | prompt injection | memory 文本不作为 system 指令执行/不越权 |

### Gate H：Tier 2 smoke

| ID | 用例 | 预期 |
| --- | --- | --- |
| P3-070 | DingTalk+Spark 启动 | 两个 upstream connected |
| P3-071 | 钉钉群 @ | Spark 回复，纯文本 |
| P3-072 | 默认 memory/context | 不报错，不要求召回 |
| P3-073 | 双 source 同时在线 | Discord→DeepSeek，DingTalk→Spark，不串 |
| P3-074 | 重启 | 会话/记忆隔离保持 |

---

## 5. 重点风险

测试时要重点确认：

1. **CLI memory 的 source/actor 对齐**  
   `remember` 默认是 `discord:discord:cli`，与真实 Discord 消息不匹配；必须用真实 botId/channelId/userId。

2. **Summary 是否会长期不更新**  
   当前实现第一次生成 summary 后，后续是否增量更新/替换，需要专门测；如果一直使用旧 summary，记为缺陷。

3. **Memory 与 system prompt 注入**  
   当前默认 assembler 把 `[Summary]` / `[Memory]` 拼进 system message；要验证敏感/跨 scope 不泄漏，且 memory 不被当作指令。

4. **`forget` 的 summary invalidation**  
   删除 memory 后，旧 summary 里如果还有该事实，必须失效/重建；否则用户以为删了但模型仍知道。

5. **真实入站与注入的区别**  
   `MemoryRecallLiveTests` 是注入 NLMP；必须补一条真人入站。

6. **多 source 隔离**  
   Discord/DingTalk 同 conversation id 不能共享 memory。

7. **Tier 2 fallback**  
   Tier 2 组件缺失不能悄悄回退到 Tier 1 memory/provider。

---

## 6. 缺陷分级

| 级别 | 定义 | 例子 |
| --- | --- | --- |
| Blocker | 启动失败、数据损坏、Tier 1 无法回复 | P2 DB 迁移失败、memory 写入崩溃 |
| High | 召回错误、跨 scope 泄漏、删除无效 | DM memory 进 guild、forget 后仍召回 |
| Medium | 预算/降级/诊断问题 | context 超预算、summary 不更新 |
| Low | 文档/字段/格式 | health 字段不一致 |

Tier 2 缺陷可降一级，但必须记录；不能静默忽略。

---

## 7. 证据

每轮保存：

```text
artifacts/p3/<YYYY-MM-DD-HHmm>/
├── config/
├── state/
├── plugins/
└── evidence/
    ├── build.log
    ├── framework-tests.log
    ├── live-nocreds.log
    ├── sqlite.txt
    ├── memory-cli.json
    ├── serve.log
    ├── recall.txt
    └── screenshots/
```

`state/` 含真实会话/记忆，测试结束按需要清理，不进入 git。

---

## 8. 签收标准

- [ ] P3-001 ~ P3-009 全绿；
- [ ] P3-010 ~ P3-014 全绿；
- [ ] P3-020 ~ P3-028 全绿；
- [ ] P3-030 ~ P3-039 全绿；
- [ ] P3-040 ~ P3-049 全绿；
- [ ] P3-050 ~ P3-054 真实 Tier 1 召回/删除/重启通过；
- [ ] P3-060 ~ P3-067 隐私/安全通过；
- [ ] P3-070 ~ P3-074 Tier 2 smoke 通过；
- [ ] 无 Blocker/High；
- [ ] P2 227/227 不回归；
- [ ] 测试负责人 Go/No-Go。

---

## 9. 测试记录

新增：

```text
docs/testing/YYYY-MM-DD-p3-acceptance.md
```

必须包含：

- 基线 SHA；
- 每个 Gate 的命令/结果；
- 真实 memory recall 证据；
- forget 后行为；
- scope 隔离证据；
- 缺陷编号；
- 临时改动/回退；
- 最终 Go/No-Go。

---

## 10. 结论

P3 的验收重点不是“表建好了”，而是：

> **真实回复能召回显式记忆，删除后不再召回，且不跨 DM/guild/source/actor 泄漏。**

先做 Gate A→E，再做真实 Tier 1；Tier 2 只做 smoke。Gate F/G 通过前，不签收 P3。
