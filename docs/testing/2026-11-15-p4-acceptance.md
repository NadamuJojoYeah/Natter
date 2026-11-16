# 2026-11-15 · P4 Tier 1 产品化验收（No-Go）

> **开发侧处置**：[P4 验收处置记录](2026-11-15-p4-acceptance-response.md)（临时配对文档，High/Medium 关闭并复测后归档）。

## 1. 概要

- **测试时间**：2026-11-15 20:28–（进行中）
- **基线**：`HEAD=73123a4`（P4.0–P4.1 foundation 已提交）
- **被测对象**：`serve`、示例 memory.store 插件、Discord 命令 UX（`/ask` `/remember` `/forget` `/memory` `/reset`）、memory scope、备份/恢复、Tier 1 真实链路
- **环境**：Linux 7.2.11-arch1-1；.NET SDK 10.0.114；凭据 `~/.config/natter/live.env`（600）
- **证据目录**：`artifacts/p4/2026-11-15-2028/`
- **结论**：**执行中**；1 个 High（schema 阻塞 slash 命令）+ 2 个 Medium，真实多轮 summary 与 Tier 2 smoke 待人工。

## 2. 基础

| 项 | 结果 |
| --- | --- |
| 构建 | **通过** |
| 框架回归 | **236/236 通过** |
| 五插件加载 | **通过**：7 activated / 0 failed（5 被测 + 2 内置示例），双 upstream connected |
| health | `healthy`，providers 均 available |

## 3. P3.1 残留

### 3.1 示例 memory.store 插件

| 用例 | 结果 |
| --- | --- |
| 插件加载/contribution | **通过**：`plugins contributions` 显示 `natter.sample.memory#file`（memory.store） |
| profile 选择（AI context 路径） | **通过**：`HostContextPipeline` 按 `profile.Memory.Store` 选择 store |
| profile 选择（CLI `remember`/`memory list`） | **失败**：CLI 直接注入宿主 `IMemoryStore`（SQLite），不按 profile；跨进程 list 仍可见 → 写入的是 sqlite（`BUG-P4-PROFILE-STORE`） |
| profile 选择（Discord 命令路由） | **待验证**（slash 被 schema 阻塞；`ChatCommandRouter.ActiveMemory` 依赖 `ProfileContext`，命令路由早于 responder 时可能回落 host store） |
| 回滚（停用插件、profile 仍引用） | **行为明确但不是文档要求**：启动失败 `references a missing prompt/context/memory component`，不是「回滚到 host#sqlite」（`OBS-P4-STORE-FALLBACK`） |

### 3.2 真实多轮 summary

- **通过（真实）**：`context.maxRecentMessages=2`，Discord 连续 7 轮 @bot 后 `summaries` 生成 2 条并增量更新（version 4 / 5），内容包含历史消息；重启持久化待补。

## 4. P4 命令 UX（已复测通过）

修复前（`34fc7ef` 之前）配置 `slashCommands=true` + `commands:{...}` 时 Discord 插件启动失败：

```text
plugin failed natter.transport.discord phase=config code=invalid-config:
/commands/ask: Additional properties are not allowed.
/commands/remember: ...
/commands/forget: ...
/commands/memory: ...
/commands/reset: ...
```

`plugins config validate natter.transport.discord` 同样报错。

**根因**：`config.schema.json` 的 `commands` 使用 `patternProperties`，而 NPS 的 schema 校验器**不支持 patternProperties**（源码无相关实现），只按 `properties` + `additionalProperties` 判定 → 所有命令名都被当成额外属性拒绝。

### BUG-P4-SCHEMA-PATTERN（High，已修复复测通过）

- **现象**：`commands` 配置被 schema 拒绝，slash 无法注册。
- **修复**：schema 改为显式 `properties`（ask/remember/forget/memory/reset）；`plugins config validate` → `valid:true`，guild 命令注册 5 个。
- **证据**：`p4-profile-store.json`、命令列表（REST）。

### 4.1 真实命令交互（21:02–21:05，用户截图）

| 命令 | 结果 |
| --- | --- |
| `/ask prompt:用一句话介绍你自己` | **通过**：「我是 Natter，一个简洁、直接的 AI 助手。」 |
| `/remember text:我喜欢乌龙茶` | **通过**：「Remembered mem_fd8fd6d0d976.」 |
| `/memory` | **通过**：`mem_fd8fd6d0d976 [user] 我喜欢乌龙茶` |
| `/forget target:我喜欢乌龙茶` | **通过**：「Forget successful.」 |
| `/reset` | **通过**：「Conversation reset.」；随后 `summaries=0`、Discord session 被清空 |
| `/remember scope:conversation` | **通过（拒绝）**：「Shared memory scopes require an administrator permission model; use scope:user for now.」（ephemeral） |

## 5. 运维

| 项 | 结果 |
| --- | --- |
| 备份 | **通过**：`scripts/natter-backup.sh` → 0600、`PRAGMA integrity_check=ok`、含 1 条 memory |
| 恢复 | **通过**：备份复制到新 state → serve exit 0，数据保留（1 memory） |
| systemd/docker 示例 | 未执行（`deploy/` 存在，待静态检查） |
| 24h soak | **未执行**（P4 计划中列为长跑项，非阻塞本次功能签收） |

## 5.1 真实多轮发现（20:32）

用户连续 5 轮 @bot 后观察到：

1. **每轮出现两条回复** —— 根因是测试侧遗留的 **P3 实例（health 5097）** 与 P4 实例（5098）同时在线，同一 bot 双 gateway 连接，各自回复一次。**已停止 P3 实例**；这不是产品缺陷，但暴露运维事实：同一 bot 多实例会双回复，产品应提供单实例约束/告警。
2. **第一轮回复含 `<response clipped>`** —— P4 实例首轮回复落库为 `收到，第一\n\n第一轮测试已就<response clipped><response clipped>`（51 字符），并进入 `summaries`；源码中无该标记（grep 0 命中），后续 4 轮正常。待复测确认是否复现。
3. `summaries` 已生成（`maxRecentMessages=2` 生效），summary 文本包含首轮异常串。

### 5.2 停 P3 后复测（20:36）

| 轮次 | 结果 |
| --- | --- |
| 第六轮 | **单条回复**「第六轮测试收到，运行正常。」13 字，日志 1 次 `ai replied` |
| 第七轮 | **单条回复**「第七轮测试收到，运行正常。」13 字，日志 1 次 `ai replied` |

- 双实例根因确认：停掉 P3 实例后不再重复回复。
- `<response clipped>` **未复现**（第六/七轮正常）。
- `summaries` 增量更新生效：现有 2 条，`version=5`（755 字符）与 `version=4`（584 字符），内容包含首轮异常串（历史消息被摘要继承）。

### OBS-P4-DUAL-INSTANCE（Medium，已定位）

同一 bot 两个 host 同时在线时，每条消息被回复两次。**根因是测试侧遗留的 P3 实例（5097）**，不是 P4 代码；停掉后第六/七轮均为单回复。仍建议部署文档明确「一个 bot token 只跑一个 serve」或增加 startup 检测。

### OBS-P4-CLIPPED（Medium，未复现）

首轮回复出现 `<response clipped>`（已发送到 Discord、写入 SQLite、进入 summary）；停 P3 后的第六/七轮**未复现**。源码 grep 0 命中，判断为一次性模型输出或环境残留。风险：该异常串已进入 summary（`version=5`），会被后续上下文继承；建议清理该会话 summary 或观察是否再次出现。

## 5.3 修复复测（20:58–21:00，基线 `HEAD=47c2c49`）

修复提交 `34fc7ef fix: 收敛 P4 slash schema、profile memory、单实例与 clipped 输出`。

| 项 | 结果 |
| --- | --- |
| 构建 + 框架回归 | **239/239 通过** |
| `BUG-P4-SCHEMA-PATTERN` | **通过**：`plugins config validate` 返回 `valid:true`；启动后 guild 命令注册 5 个：`ask(prompt)` / `remember(text,scope)` / `forget(target)` / `memory(limit)` / `reset` |
| `BUG-P4-PROFILE-STORE` | **通过**：CLI `remember`/`memory list` 经 `IProfileMemoryStoreResolver` 选择 `natter.sample.memory#file`；`NATTER_SAMPLE_MEMORY_PATH` 指向 state 时生成 0600 文件（517B），跨进程 list 可见 |
| `OBS-P4-DUAL-INSTANCE` | **通过**：同 state 启动第二个 serve → exit 1，`Another Natter serve instance is using state directory ...` |
| `OBS-P4-CLIPPED` | **通过（组件级）**：`AiChatResponder` 增加 clipped 检测 + 重试一次 + fallback；框架测试新增覆盖 |
| 命令 UX 真实交互 | **待用户**：`/ask` `/remember` `/forget` `/memory` `/reset`、共享 scope 拒绝、ephemeral |

## 6. 缺陷与观察

| 编号 | 级别 | 现象 |
| --- | --- | --- |
| `BUG-P4-SCHEMA-PATTERN` | **High** | schema `patternProperties` 不被 NPS 支持 → slash 命令无法配置 |
| `BUG-P4-PROFILE-STORE` | **Medium** | CLI `remember`/`memory list` 固定用 host sqlite，不遵循 profile.memory.store |
| `OBS-P4-STORE-FALLBACK` | **Medium** | 停用 memory 插件但 profile 仍引用 → 启动失败（文档要求回滚 host#sqlite，需明确策略） |
| `OBS-P4-DUP-CONTRIB` | **Low** | `plugins contributions` 中 `natter.sample.memory#file` 重复出现两次 |

## 7. 待人工

1. **真实多轮 summary**：Discord `#常规` 连续 @bot 4–5 轮（`maxRecentMessages=2` 已配置），预期 `summaries` 表出现并随消息更新；
2. **Tier 2 smoke**：钉钉群 @机器人 一条；
3. 命令 UX：等 `BUG-P4-SCHEMA-PATTERN` 修复后，再验证 `/ask` `/remember` `/forget` `/memory` `/reset`、共享 scope 拒绝、ephemeral。

## 8. 结论（复测后）

- P3.1：memory 插件加载/选择/回滚（显式失败策略）通过；CLI 与命令路由均按 profile 选择 store（文件持久化）。
- P4 命令 UX：`/ask` `/remember` `/forget` `/memory` `/reset` 与共享 scope 拒绝真实通过；命令注册 5 个。
- 真实多轮 summary：通过（7 轮，summary 增量 version 4/5）。
- Tier 2 smoke：通过（钉钉「第二次测试」→ Spark 27 字）。
- 运维：备份/恢复通过；单实例锁通过；24h soak 未执行。
- 安全：日志无密钥；state 700、DB/.lock 600。
- **当前判定：Go**（24h soak 作为后续长跑项，不阻塞本次功能签收）。
- **实例状态**：`serve --duration 3600` 于 21:59 正常到期退出（exit 0，无 error），5098 已无监听；DB 保留 1 session / 2 messages / 1 旧 memory / 0 summaries。
