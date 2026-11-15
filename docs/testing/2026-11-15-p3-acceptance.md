# 2026-11-15 · P3 Context & Memory 验收测试（No-Go）

> **开发侧处置**：[P3 验收处置记录](2026-11-15-p3-acceptance-response.md)（临时配对文档，High/Medium 关闭并复测后归档）。

## 1. 概要

- **测试时间**：2026-11-15 19:18–（进行中）
- **基线**：`HEAD=04831df`（`P3 foundation` 已提交；P2 已归档）
- **被测对象**：`IPromptProvider` / `IContextAssembler` / `IMemoryStore` / `ISummaryStore`、profile 组件选择、SQLite summary/memory、`remember`/`memory list`/`forget`、context pipeline、NPS contribution
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；凭据 `~/.config/natter/live.env`（600）
- **证据目录**：`artifacts/p3/2026-11-15-1918/`
- **结论**：**执行中**；已发现 1 High、2 Medium（见 §6），Gate F 真人入站与 Gate H 待复测。

## 2. Gate A：静态 / Profile 兼容

| ID | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| P3-001 | 构建 | **通过** | `evidence/build.log` |
| P3-002 | 框架回归 | **通过 227/227** | `evidence/framework-tests.log` |
| P3-003 | 无凭据 live | **通过：38 skip / 0 failed** | `evidence/live-nocreds.log` |
| P3-004 | 旧 P2 profile | **通过**：exit 0，2 profiles / 2 providers / 双 upstream connected | `evidence/p3-a-p2-legacy.log` |
| P3-005 | 新 profile（prompt/context/memory） | **通过**：exit 0，同上 | `evidence/p3-a-p3-full.log` |
| P3-006 | 非法 ref `hoststatic` | **通过**：exit 1，`refs must use 'owner#id'` | `evidence/p3-a-case-badref.log` |
| P3-007 | 缺 Tier 1 组件 | **通过**：exit 1，`references a missing prompt/context/memory component` | `evidence/p3-a-case-missing-tier1.log` |
| P3-008 | 缺 Tier 2 组件 | **通过（启动不阻塞）**：Tier2 memory.store 引用不存在组件时仍 exit 0，Tier1 正常；运行时策略待 P3-073 | `evidence/p3-a-case-missing-tier2.log` |
| P3-009 | 预算非法 `maxInputCharacters=0` | **通过**：exit 1，`context budgets are invalid` | `evidence/p3-a-case-budget.log` |

## 3. Gate B：组件与 NPS

| ID | 用例 | 结果 |
| --- | --- | --- |
| P3-010 | `plugins contributions` | **失败**：只显示 chat.responder / chat.upstream / cli.command，**没有 prompt.provider / context.assembler / memory.store**（`BUG-P3-HOST-CONTRIB`） |
| P3-011 | 组件选择 | **部分**：默认 `host#recent-window` 启动成功；profile 切换未单独验证（无差异可观测） |
| P3-012 | plugin 组件 | **未实现/无法测**：仓库没有提供 memory.store 示例插件 |
| P3-013 | 缺失组件 | **通过（启动层面）**：P3-007/008 |
| P3-014 | 组件回滚 | **未测**：无插件可停用 |

## 4. Gate C：SQLite 迁移与存储

| ID | 用例 | 结果 | 证据 |
| --- | --- | --- | --- |
| P3-020 | P2 DB 迁移 | **通过**：`user_version` 0 → **2** | `evidence/sqlite-before.txt` / `sqlite-after.txt` |
| P3-021 | 表存在 | **通过**：`memory_items`、`summaries` 已创建 | 同上 |
| P3-022 | 重复迁移 | **通过**：二次启动 exit 0，`user_version=2`，4 sessions / 16 messages 不丢 | `evidence/p3-c-migrate2.log` |
| P3-023 | 权限 | **通过**：state 700、`natter.db` 600 | `p3-023` 输出 |
| P3-024 | summary 持久化 | **组件级通过**（`ContextStorageTests`）；CLI 无 summary 命令，需通过 Gate E 间接验证 | `ContextStorageTests` |
| P3-025 | memory 持久化 | **通过**：CLI 重启后 `memory list` 仍返回 | `memory-cli.json` |
| P3-026 | export/import | **未实现 CLI**：`IMemoryStore.ExportAsync` 存在，但无 `memory export/import` 命令（`BUG-P3-EXPORT-CLI`） | 代码 |
| P3-027 | delete | **通过**：`forget` by id / by text 均 `deleted:true`，list 消失 | `memory-forget.json` |
| P3-028 | 过期 | **通过**：SQL 把 `expires_at` 改为过去时间后，`memory list` 不再返回该条 | 终端输出 |

## 5. Gate D：显式记忆 CLI

| ID | 用例 | 结果 |
| --- | --- | --- |
| P3-030 | remember | **通过**：`mem_524de3c39307440e`，list 可见 |
| P3-031 | 重复 remember | **通过**：返回同一 `memoryId`，不重复插入 |
| P3-032 | scope=user | **通过**：同 actor 其他 conversation 可见 |
| P3-033 | scope=conversation | **通过**：同 conversation 可见，其他 conversation 不可见 |
| P3-034 | scope=guild | **通过**：同 guild 其他 conversation 可见，其他 guild 不可见 |
| P3-035 | scope=global | **通过**：同 source 其他 conversation / 其他 actor 可见 |
| P3-036 | forget by id | **通过** `deleted:true` |
| P3-037 | forget by text | **通过** `deleted:true` |
| P3-038 | forget 后 list | **通过**：不再出现 |
| P3-039 | source/actor 不匹配 | **失败**：user scope 跨 actor 拒绝（`deleted:false`）✅；但 **global scope 用其他 actor 删除返回 `deleted:true`**❌；跨 source 删除拒绝 ✅ |

### BUG-P3-FORGET-SHARED-SCOPE（High）

- **现象**：`forget <mem_id>` 对 conversation / guild / global 三类共享 scope 不校验 actor。同一 source 下的任意 actor 都能删除他人的共享记忆。
- **证据**：
  - `forget mem_40146ccb40894986 --actor 999` → `{"deleted":true}`，global 记忆消失（`memory-forget.json`）；
  - 对照：`forget mem_f413a478c0b545c0 --actor 999`（user scope）→ `{"deleted":false}`（`memory-user-cross-actor-delete.json`）；
  - 跨 source 删除 → `{"deleted":false}`（`memory-cross-source-delete.json`）。
- **根因**：`SqliteMemoryStore.DeleteAsync` 的 `scope_id IN ($s0..$s3)` 中，Conversation/Guild/Global 的 scope id 不含 actor（`MemoryScope.Id` 只含 source + scope 维度），只有 User scope 含 actor。
- **影响**：P3-039 失败；任意成员可删除共享记忆（跨用户数据破坏）。若产品语义是「共享 scope 允许删除」，需要在计划/文档中明确并加权限模型，而不是静默通过。
- **建议**：删除共享 scope 需要显式授权（管理员/创建者），或 CLI 增加 `--force` 并默认拒绝非创建者；至少 user scope 与共享 scope 的策略要一致且有测试。

## 6. Gate E：Context 组装与预算（组件级）

`ContextPipelineTests` + `ContextStorageTests`：**10/10 通过**，覆盖 recent window、summary/memory 注入、顺序、预算、裁剪、当前消息保留、store/assembler 抛异常降级、summary 持久化/失效。

> 真实回复中 `[Memory]` / `[Summary]` 的注入证据见 Gate F。

## 7. Gate F：真实 Tier 1 记忆召回

| ID | 用例 | 结果 |
| --- | --- | --- |
| P3-050 | 显式记忆召回 | **通过**：`MemoryRecallLiveTests` 10s，真实 DeepSeek 回复包含 `oolong` |
| P3-051 | 删除后不召回 | **通过**：`MemoryRecallLifecycleLiveTests`（16s）删除前回复 `Oolong.`，`DeleteAsync` 后回复 `unknown` |
| P3-052 | 重启后召回 | **通过**：同一 state 重启 host 后 memory 仍在（1 条），回复 `Oolong.`（`MemoryRecallLifecycleLiveTests`，17s） |
| P3-053 | 真实入站 | **通过**：19:27:19 真人 `@bot 我喜欢喝什么？` → 19:27:23 回复「你喜欢喝乌龙茶。」（source/conversation/actor 与 memory 一致） |
| P3-054 | summary + memory | **组件级通过**（`ContextPipelineTests` 覆盖 `[Summary]`/`[Memory]` 注入与顺序）；真实两条单轮消息未触发 summary（`summaries` 表为空，符合「未超窗口」），多轮待补 |

## 8. Gate G / H

- **G**：scope 隔离（P3-032~035/039）已测；P3-065 日志无密钥/memory 正文、P3-066 state 700 / DB+WAL+SHM 600 已验证；DM private memory、跨 source、prompt injection 待真实入站与代码复核。
- **H Tier 2 smoke**：
  - P3-070 启动/连接：**通过**（DingTalk/Discord 双 `connected=true`）；
  - P3-071 钉钉群 @：**通过**（19:27 真人「晚上好呀」→ Spark 回复「晚上好。有什么…」14 字纯文本）；
  - P3-072 默认 memory/context：**通过**（钉钉回复无报错，未触发召回）；
  - P3-073 双 source 同时在线：**通过**（Discord→DeepSeek、DingTalk→Spark，SQLite 两个 source 分开、内容不串）；
  - P3-074 重启隔离：**通过**（同一 state 重启后 conversations=2、2 sessions / 4 messages / 1 memory 完整，双 upstream 重连）。

## 9. 临时改动

无产品代码改动；新增测试证据与 run 目录。复用 P2 的 `scripts/p2-serve.sh` 逻辑做 staging（P3 用独立 config/state）。

## 10. 签收对照与结论

| 标准 | 状态 | 说明 |
| --- | --- | --- |
| P3-001 ~ 009 | **是** | 构建/227 回归/38 skip/旧新 profile/非法 ref/缺失组件/预算 |
| P3-010 ~ 014 | **否** | P3-010 无 prompt/context/memory contribution；012/014 无插件可测 |
| P3-020 ~ 028 | **部分** | 除 P3-026（export CLI 缺失）外全通过 |
| P3-030 ~ 039 | **否** | P3-039 共享 scope 跨 actor 删除（High） |
| P3-040 ~ 049 | **是** | 组件级 10/10 |
| P3-050 ~ 054 | **部分** | 050/051/052/053 真实通过；054 summary 真实多轮未触发（组件级通过） |
| P3-060 ~ 067 | **部分** | 061/062/063/065/066 通过；060/067 未测（DM private、prompt injection） |
| P3-070 ~ 074 | **是** | 钉钉 smoke、双 source 路由、重启隔离全部通过 |
| 无 Blocker/High | **否** | 1 个 High（P3-039） |
| P2 227/227 | **是** | 未回归 |
| Go/No-Go | **No-Go** | 见下 |

## 11. 证据索引

```text
artifacts/p3/2026-11-15-1918/
├── config/{p2-legacy,p3-full,case-badref,case-missing-tier1,case-missing-tier2,case-budget}/
├── plugins/all/{discord,deepseek,dingtalk,spark}/     # 临时 staging（可删）
├── state/{cli,migrate,p3serve}/                      # memory/会话 DB
└── evidence/
    ├── head.txt  build.log  framework-tests.log  live-nocreds.log
    ├── p3-a-{p2-legacy,p3-full,case-*}.log
    ├── p3-c-migrate{,2}.log  sqlite-before.txt  sqlite-after.txt
    ├── memory-cli.json  memory-scope.json  memory-scope-guild-global.json
    ├── memory-forget.json  memory-cross-source-delete.json  memory-user-cross-actor-delete.json
    ├── memory-recall-live.log  memory-lifecycle-live.log
    ├── p3-serve.log  p3-serve-restart.log  p3-serve-memory.json
    └── p3-before-restart.db
```

## 12. 结论

- **真实链路全部通过**：Discord 真实入站召回（「你喜欢喝乌龙茶。」）、钉钉 Spark smoke、双 source 路由不串、重启后会话/记忆完整。
- **No-Go 原因**：
  1. `BUG-P3-FORGET-SHARED-SCOPE`（High）：conversation/guild/global 的 `forget` 不校验 actor，同 source 任意 actor 可删共享记忆；
  2. `BUG-P3-HOST-CONTRIB`（Medium）：`plugins contributions` 不显示 host prompt/context/memory；
  3. `BUG-P3-EXPORT-CLI`（Medium）：`ExportAsync` 无 CLI 入口，P3-026 无法执行。
- **未测项**：P3-060 DM private memory（scope 隔离已在 CLI 层覆盖）、P3-067 prompt injection（建议补对抗用例）、P3-054 真实多轮 summary。
- **建议复测顺序**：修复 P3-039 后跑 Gate D 全量 + 真实入站；补 export CLI 后跑 P3-026；补 host contribution 后跑 P3-010；再补一条多轮 summary 与 DM 用例。
