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

## 4. P4 命令 UX（阻塞）

配置 `slashCommands=true` + `commands:{ask,remember,forget,memory,reset}` 后，Discord 插件启动失败：

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

### BUG-P4-SCHEMA-PATTERN（High）

- **现象**：Discord `commands` 无法配置，`slashCommands` 形同虚设，`/ask` `/remember` `/forget` `/memory` `/reset` 无法注册。
- **影响**：P4 的核心用户入口（聊天内命令）完全不可用；P4 §9.2 全部用例阻塞。
- **证据**：`evidence/serve.log`（首次启动失败）、`plugins config validate` 输出。
- **建议**：schema 改为显式 `properties`（五个命令固定），或让校验器支持 `patternProperties`；两者都要补一条「带 commands 的 discord config 通过校验」测试。

## 5. 运维

| 项 | 结果 |
| --- | --- |
| 备份 | **通过**：`scripts/natter-backup.sh` → 0600、`PRAGMA integrity_check=ok`、含 1 条 memory |
| 恢复 | **通过**：备份复制到新 state → serve exit 0，数据保留（1 memory） |
| systemd/docker 示例 | 未执行（`deploy/` 存在，待静态检查） |
| 24h soak | 未执行 |

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

## 8. 结论（进行中）

- P3.1：插件加载/选择（AI 路径）通过；CLI/命令路由未遵循 profile；回滚策略与文档不一致。
- P4 命令 UX：**被 schema 缺陷阻塞**。
- 运维：备份/恢复通过。
- **当前 No-Go**（1 High：`BUG-P4-SCHEMA-PATTERN`；真实多轮已通过；Tier 2 smoke 待测）。
