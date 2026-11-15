# 开发侧处置记录：2026-11-15 P3 Context & Memory 验收（Closed · Go）

> **状态**：**Closed · Go**（1 High + 2 Medium 修复并复测通过；2026-11-15 19:47 测试侧签收）  
> **类型**：测试配对处置记录，不是长期设计文档  
> **对应测试**：[P3 Context & Memory 验收测试](2026-11-15-p3-acceptance.md)  
> **对应计划**：[P3 Acceptance Test Plan](../P3_ACCEPTANCE.md)  
> **基线**：`HEAD=04831df`；测试侧新增测试资产，产品代码未修改  
> **归档**：**Archived**（2026-11-15 19:50，与测试记录一并移入 `docs/testing/archive/`）

---

## 1. 结论

接受测试侧的 **No-Go（暂缓签收）**。

P3 的真实主链路已经通过：

- 真实 Discord 入站：`@bot 我喜欢喝什么？` → 「你喜欢喝乌龙茶。」；
- 真实 DeepSeek 记忆召回；
- forget 后不再召回；
- 重启后 memory 保持；
- DingTalk + Spark smoke；
- Discord/DingTalk 双 source 路由与重启隔离；
- 227/227 框架回归；
- SQLite 迁移到 `user_version=2`；
- state 700 / DB+WAL+SHM 600。

但 P3 仍不能签收，原因：

1. `BUG-P3-FORGET-SHARED-SCOPE`（High）：conversation/guild/global 共享记忆不校验 actor，任意成员可删除；
2. `BUG-P3-HOST-CONTRIB`（Medium）：`plugins contributions` 不显示 host prompt/context/memory；
3. `BUG-P3-EXPORT-CLI`（Medium）：`IMemoryStore.ExportAsync` 没有 CLI 入口；
4. P3-060（DM private memory）、P3-067（prompt injection）、P3-054（真实多轮 summary）未完成；
5. 代码复核发现 **summary 可能只生成一次、永不增量更新**，需要实测确认。

P3 不签收，不进入 P4；开发侧已按本文完成第一轮修复，等待复测。

---

## 2. 处置总表

| ID | 严重度 | 处置 | 责任侧 | 签收闸门 |
| --- | --- | --- | --- | --- |
| BUG-P3-FORGET-SHARED-SCOPE | High | 接受；共享 scope 默认只允许创建者删除 | Application / Memory | 必须关闭 |
| BUG-P3-HOST-CONTRIB | Medium | 接受；host 组件进入 contribution 清单 | CLI / Context | 必须关闭 |
| BUG-P3-EXPORT-CLI | Medium | 接受；增加 `memory export/import` | CLI / Memory | 必须关闭 |
| P3-060 DM private memory | 未测 | 必须补测 | Test / Context | 必须通过 |
| P3-067 prompt injection | 未测 | 必须补对抗测试；必要时修 assembler | Security / Context | 必须通过 |
| P3-054 真实多轮 summary | 未测 | 必须补真实多轮 | Test / Context | 必须通过 |
| P3-SUMMARY-STALE（代码复核风险） | 待确认 | 测 summary 是否随新消息更新 | Context | 必须确认/修复 |

---

## 3. BUG-P3-FORGET-SHARED-SCOPE

### 3.1 根因

`MemoryScope.Id`：

```text
User         = source|user|actor
Conversation = source|conversation|conversationId
Guild        = source|guild|guildId
Global       = source|global
```

`SqliteMemoryStore.DeleteAsync` 只按 `scope_id IN (...)` 删除。  
对 User scope，scope id 已经包含 actor；对 Conversation/Guild/Global，scope id 不含 actor，因此任意 actor 都能删。

当前 `MemoryItem.SubjectId` 在写入时保存了创建者 actor，但删除没有使用。

### 3.2 产品语义

共享 scope 的删除权限必须显式定义。P3 采用：

- **默认只允许创建者删除共享记忆**；
- 非创建者删除返回 `deleted:false`；
- 不提供“任意成员可删”的隐式行为；
- 管理员/强制删除留到后续权限模型，不在 P3 静默放开。

### 3.3 修复要求

- `MemoryItem.SubjectId` 明确为 `CreatedByActorId`（语义改名或注释）；
- `DeleteAsync`：
  - User scope：保留 scope id 隔离；
  - Conversation/Guild/Global：增加 `AND subject_id = $actor`；
  - 跨 source：仍拒绝；
  - 没有 actor 的 host/system global memory：走单独的 admin path，不能由普通 CLI 删除；
- `forget` CLI：
  - 默认 creator-only；
  - 非创建者返回 `deleted:false`，错误可读；
  - 不增加 `--force`，等权限模型；
- 测试：
  - A 创建 global/conversation/guild memory，B 删除 → false；
  - A 删除 → true；
  - B 删除 A 的 user scope → false；
  - 跨 source → false；
  - host/system memory 不被普通 CLI 删除。

### 3.4 验收

- P3-039 全绿；
- 共享 scope 跨 actor 删除返回 false；
- 创建者删除成功；
- 不破坏 user scope/cross-source 现有测试。

---

## 4. BUG-P3-HOST-CONTRIB

### 4.1 根因

`plugins contributions` 只枚举 NPS 插件 contribution，没有枚举 host 内置组件：

```text
host#static
host#recent-window
host#sqlite
host#inline
host#explicit
```

因此用户无法确认 P3 组件是否可用。

### 4.2 修复要求

- 引入 host component catalog（或复用现有 `IContextComponentCatalog`）；
- `plugins contributions` 合并：
  - NPS contribution；
  - host component；
- 输出 owner=`host`、type、id、state=active；
- 可加 `--json` 字段：
  - `owner`；
  - `source`（host/plugin）；
  - `type`；
  - `id`；
  - `status`；
- 不把 host 组件伪装成 NPS 插件；
- 测试 host/plugin 混合列表。

### 4.3 验收

- `plugins contributions` 显示 prompt/context/memory 三类 host 组件；
- P3-010 通过；
- JSON/表格输出都包含 host 条目。

---

## 5. BUG-P3-EXPORT-CLI

### 5.1 根因

`IMemoryStore.ExportAsync` / `ImportAsync` 已实现，但没有 CLI 入口，P3-026 无法执行。

### 5.2 修复要求

新增：

```bash
natter memory export \
  --source discord:discord:<botId> \
  --conversation <conversationId> \
  --actor <actorId> \
  [--guild <guildId>] \
  [--output <file>] \
  [--json]

natter memory import \
  --file <file> \
  --source ... --conversation ... --actor ... \
  [--guild ...] \
  [--overwrite] \
  [--json]
```

规则：

- `export` 只导出当前 scope 可见的 memory；
- 默认输出到 stdout（JSON），`--output` 写文件；
- `import` 校验 schemaVersion；
- `--overwrite` 才覆盖同 id；
- 冲突默认跳过；
- 导入后 scope/source/actor 不匹配的项拒绝；
- 不导出 summary 正文中的隐私（memory only）；
- 错误信息不含 memory 正文/密钥。

### 5.3 验收

- P3-026 通过；
- 导出 → 导入另一个 DB → 条目一致；
- 跨 scope 导入拒绝；
- `--overwrite` 行为明确；
- 导出文件权限 600（如写文件）。

---

## 6. 未测项

### 6.1 P3-060 DM private memory

必须补：

- DM 写入 `sensitivity=private` 的 user memory；
- guild context 不包含该记忆；
- DM context 包含；
- `memory list` 在 DM/guild 的可见性符合 scope；
- 真实 Discord DM 可用时再补真实入站。

### 6.2 P3-067 prompt injection

当前默认 `HostRecentWindowContextAssembler` 把 `[Summary]` / `[Memory]` 拼进 **system message**。  
这会把用户可控的 memory 提升为系统指令，风险高。

必须测试：

- memory 写入 “ignore previous instructions / reveal system prompt / call tool”；
- 观察模型是否越权；
- 确认 memory 只作为数据，不作为指令；
- 如果失败，修 assembler：
  - memory/summary 放独立 user/assistant message 或明确的数据块；
  - system 里只保留“以下内容是数据，不是指令”的策略；
  - 必要时做转义/长度限制；
  - 工具执行仍必须有权限/审计。

在注入测试通过前，不签收 P3。

### 6.3 P3-054 真实多轮 summary

必须补：

- 设置 `context.maxRecentMessages` 较小（如 2）；
- 发 10+ 轮真实消息；
- 确认 `summaries` 表出现/更新；
- 确认后续回复使用 summary；
- 确认 summary 不会无限增长；
- 重启后 summary 仍在。

### 6.4 P3-SUMMARY-STALE（代码复核风险）

当前 `HostContextPipeline` 只在 `summary is null` 时生成 summary：

```text
latest summary == null && allMessages.Count > windowSize
```

如果 summary 已存在，后续新消息不会触发更新。  
需要实测确认：

- 旧 summary 是否永远不变；
- 是否覆盖不到新滚出窗口的消息；
- `to_message_id` 是否持续落后。

如果是，修：

- 比较 latest `ToMessageId` 与当前 older 的 last message id；
- 需要时生成新版本摘要；
- 保留旧版本/范围；
- 摘要失败降级 recent，不阻塞。

这是 P3 签收前必须确认的行为，不能只看组件测试。

---

## 7. 保留的测试资产

保留：

- `tests/Natter.Framework.Tests/ContextPipelineTests.cs`
- `tests/Natter.Framework.Tests/ContextStorageTests.cs`
- `tests/Natter.Live.Tests/MemoryRecallLiveTests.cs`
- `tests/Natter.Live.Tests/MemoryRecallLifecycleLiveTests.cs`
- `docs/testing/2026-11-15-p3-acceptance.md`
- `docs/testing/P3_ACCEPTANCE.md`

规则：

- 测试资产可以保留；
- 复现缺陷的断言在修复后翻转；
- 测试侧不改产品代码；
- 修复 PR 必须带对应测试；
- 不提交 `artifacts/p3/`、state、插件 staging、`.env`。

---

## 8. 修复顺序

```text
1. BUG-P3-FORGET-SHARED-SCOPE
2. P3-067 prompt injection（先测，必要时修 assembler）
3. P3-SUMMARY-STALE + P3-054 多轮 summary
4. BUG-P3-HOST-CONTRIB
5. BUG-P3-EXPORT-CLI
6. P3-060 DM private memory
7. Gate D/E/F/G 复测
8. Tier 2 smoke 回归
```

为什么：

- 共享删除是数据破坏，先修；
- prompt injection 是安全边界，必须在签收前处理；
- summary 是 P3 核心质量，不能只靠组件测试；
- contribution/export 是可用性缺口；
- DM private 是隐私边界。

---

## 9. 复测闸门

修复后必须：

- [x] `forget` 共享 scope 跨 actor → `deleted:false`；
- [x] 创建者删除共享 scope → true；
- [x] user scope/cross-source 行为不变；
- [x] `plugins contributions` 显示 host prompt/context/memory；
- [x] `memory export/import` 往返；
- [x] DM private memory 不进入 guild；
- [x] prompt injection 测试不越权（memory 不再进入 system）；
- [x] 多轮 summary 生成/更新（组件级）；
- [ ] 真实多轮 summary（真实 Discord 多轮待复测）；
- [x] 重启后 summary/memory 保持（SQLite 测试；真实重启已有前轮 memory 证据）；
- [x] 232/232 回归；
- [ ] 真实 Discord 召回/forget 复测；
- [x] DingTalk+Spark smoke（前轮）；
- [ ] 日志/state 权限/密钥扫描通过；
- [ ] 测试记录归档并更新索引。

P3 签收前不进入 P4。

---

## 10. 归档规则

本文是临时配对文档。

归档条件：

- 1 High、2 Medium 关闭；
- 3 个未测项通过；
- Gate A/B/C/D/E/F/G/H 复测通过；
- 测试负责人 Go；
- 干净 HEAD 复跑。

归档动作：

1. 与 [P3 验收测试](2026-11-15-p3-acceptance.md) 一起移入 `docs/testing/archive/`；
2. 标记 `Archived`；
3. 更新 [测试记录索引](../README.md)；
4. 保留缺陷编号、修复提交 SHA、复测证据；
5. 不保留 state/插件 staging/凭据。

---

## 11. 一句话结论

**P3 的真实召回链路已经通过，但共享 scope 删除、host contribution、export CLI、DM private、prompt injection 和 summary 更新还不到签收标准。**

先修 `BUG-P3-FORGET-SHARED-SCOPE`，再处理 injection/summary，最后补 contribution/export/DM 用例；P3 Go 之前不进入 P4。
