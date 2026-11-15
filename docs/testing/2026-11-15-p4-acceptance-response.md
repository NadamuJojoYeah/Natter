# 开发侧处置记录：2026-11-15 P4 Tier 1 产品化验收（No-Go）

> **状态**：Fixed · Ready for test-side retest（1 High + 2 Medium + 2 观测已修复/收敛）  
> **类型**：测试配对处置记录，不是长期设计文档  
> **对应测试**：[P4 Tier 1 产品化验收](2026-11-15-p4-acceptance.md)  
> **对应计划**：[P4 Tier 1 Productization](../P4_TIER1_PRODUCTIZATION.md)  
> **基线**：`HEAD=73123a4`；测试侧新增测试资产，产品代码未修改  
> **归档**：1 个 High、2 个 Medium、2 个观测关闭并复测后，与测试记录一并归档

---

## 1. 结论

接受测试侧的 **No-Go（暂缓签收）**。

已通过的部分：

- 构建成功；
- 框架回归 **236/236**；
- 五插件加载：7 activated / 0 failed；
- 双 upstream connected，health healthy；
- 示例 `memory.store` 插件能被 `plugins contributions` 发现；
- AI context 路径能按 profile 选择 memory store；
- 真实多轮 summary 已生成并增量更新（version 4 / 5）；
- 备份/恢复通过：0600、`integrity_check=ok`、数据保留；
- P3 记忆真实召回/删除/重启基线未回归。

但不能签收，原因：

1. `BUG-P4-SCHEMA-PATTERN`（High）：Discord `commands` schema 使用 `patternProperties`，NPS 不支持，slash 命令完全无法配置；
2. `BUG-P4-PROFILE-STORE`（Medium）：CLI `remember`/`memory list` 固定使用 host SQLite，不遵循 profile 的 memory.store；
3. `OBS-P4-STORE-FALLBACK`（Medium）：停用 memory 插件但 profile 仍引用时启动失败，与文档“回滚 host#sqlite”不一致；
4. `OBS-P4-DUP-CONTRIB`（Low）：`plugins contributions` 中示例 memory contribution 重复；
5. `OBS-P4-DUAL-INSTANCE`（Medium）：同一 bot 两个 host 同时在线会双回复；
6. `OBS-P4-CLIPPED`（Medium）：首轮回复出现 `<response clipped>`，已进入 SQLite 与 summary；未复现，需清理与观察。

开发侧已完成第一轮修复：显式 Discord commands schema、profile-aware memory store、fail-closed、去重、单实例锁与 clipped 输出保护；等待测试侧复跑 P4 用户入口与 soak。

---

## 2. 处置总表

| ID | 严重度 | 处置 | 责任侧 | 签收闸门 |
| --- | --- | --- | --- | --- |
| BUG-P4-SCHEMA-PATTERN | High | 接受；改显式 schema 或补校验器能力 | Discord plugin / NPS | 必须关闭 |
| BUG-P4-PROFILE-STORE | Medium | 接受；CLI/命令路由统一 profile-aware store resolver | CLI / Application | 必须关闭 |
| OBS-P4-STORE-FALLBACK | Medium | 接受；改为 fail-closed 并修文档 | Context / Docs | 必须收敛 |
| OBS-P4-DUP-CONTRIB | Low | 接受；contribution 列表去重 | CLI / NPS | 必须收敛 |
| OBS-P4-DUAL-INSTANCE | Medium | 接受；增加单实例锁/文档 | Host / Ops | 必须收敛 |
| OBS-P4-CLIPPED | Medium | 观察；输出校验/清理 summary | AI / Context | 必须处理 |

---

## 3. BUG-P4-SCHEMA-PATTERN

### 3.1 根因

`samples/Natter.Sample.Plugin.Discord/config.schema.json` 的 `commands` 使用：

```json
"patternProperties": {
  "^[a-z0-9_-]{1,32}$": { "type": "boolean" }
}
```

NPS schema 校验器不支持 `patternProperties`，只按 `properties` + `additionalProperties` 判定，因此：

```text
/commands/ask: Additional properties are not allowed.
```

`slashCommands=true` + `commands` 配置会导致 Discord 插件 config 校验失败，插件无法激活。

### 3.2 修复选择

**P4 采用显式 properties，先解除阻塞。**

```json
"commands": {
  "type": "object",
  "additionalProperties": false,
  "properties": {
    "ask": { "type": "boolean" },
    "remember": { "type": "boolean" },
    "forget": { "type": "boolean" },
    "memory": { "type": "boolean" },
    "reset": { "type": "boolean" }
  }
}
```

规则：

- 新增命令必须同时改 schema/provider；
- 配置示例只使用这五个 key；
- 未知命令仍然拒绝；
- 不建议为了动态命令名临时放宽 `additionalProperties=true`。

后续可选：

- 让 NPS schema 校验器支持 `patternProperties`；
- 但 P4 不依赖它，避免再次阻塞。

### 3.3 测试

- 带五个 commands 的 Discord config 通过 `plugins config validate`；
- 插件启动成功；
- slash 命令注册成功；
- 未知 command key 被拒绝；
- 空 commands / 单项 commands 均可；
- `slashCommands=false` 不注册。

参考文件：

- `samples/Natter.Sample.Plugin.Discord/config.schema.json`
- `samples/Natter.Sample.Plugin.Discord/plugin.json`
- `src/Natter.Infrastructure/Plugins/Nps/NpsConfigSchemaValidator.cs`（或实际校验器路径）

---

## 4. BUG-P4-PROFILE-STORE

### 4.1 根因

`MemoryCommands`（CLI）直接注入 host `IMemoryStore`：

```text
remember -> IMemoryStore (host SQLite)
memory list -> IMemoryStore (host SQLite)
```

没有根据 `profiles.json` 的 `memory.store` 解析。

`ChatCommandRouter.ActiveMemory` 依赖 `ProfileContext.Current`，但命令路由可能早于 `ProfileAwareChatResponder` 的 `ProfileContext.Push`，因此也可能回落 host store。

结果：

- profile 选择 `natter.sample.memory#file`，CLI 仍写 SQLite；
- 跨进程 list 能看到 SQLite 内容；
- Discord 命令可能写错 store。

### 4.2 修复要求

引入统一的 profile-aware resolver，例如：

```csharp
public interface IProfileMemoryStoreResolver
{
    IMemoryStore Resolve(Source source, Conversation conversation, User user);
}
```

行为：

1. 用 `IProfileResolver.Resolve(source, conversation, user)` 找 profile；
2. 若 profile 指定 `memory.store`，从 `IContextComponentCatalog` 取；
3. 取不到时：
   - Tier 1：抛错/启动失败；
   - Tier 2：禁用/明确失败；
   - 不允许静默写 host SQLite；
4. 没有 profile/`host#inline` 时才使用 host default；
5. CLI 和 `ChatCommandRouter` 都使用同一个 resolver。

`ChatCommandRouter` 不再依赖 ambient `ProfileContext`：

- 在 `TryHandleAsync` 开头解析 profile；
- 或用 resolver 直接选 store；
- 需要时临时 `ProfileContext.Push(profile)`；
- 命令路由和 AI responder 看到同一个 profile。

### 4.3 测试

- profile 选择 `natter.sample.memory#file`：
  - CLI `remember` 写入 file store；
  - 重启后 `memory list` 可见；
  - host SQLite 不出现该条；
- profile 选择 `host#sqlite`：CLI 写 SQLite；
- Discord `/remember` 使用同一 store；
- `/memory`、`/forget` 同样走 profile store；
- 缺失 store：
  - Tier 1 启动失败；
  - Tier 2 禁用/明确错误；
- 跨 source/actor 隔离不变。

---

## 5. OBS-P4-STORE-FALLBACK

### 5.1 决策

测试观察到的行为：

```text
停用 memory 插件但 profile 仍引用 -> 启动失败（missing component）
```

原 P4 文档写“回滚到 host#sqlite”。  
**开发侧决定改为 fail-closed，不静默回滚。**

理由：

- 静默回滚会造成 memory 分裂：一部分在插件 store，一部分在 host SQLite；
- 用户以为还在用原 store，实际数据写到了另一处；
- Tier 1 配置错误应该显式暴露；
- 回滚应该是“先改 profile，再重启”，不是运行时自动切换。

### 5.2 文档修正

- P4 文档已改为：
  - 停用插件前必须把 profile 切回 `host#sqlite`；
  - profile 仍引用缺失组件时启动失败；
  - 不静默回滚。
- 错误信息应包含：
  - profile id；
  - 缺失 component ref；
  - 修复建议（切回 host#sqlite 或启用插件）。

### 5.3 测试

- 停用插件、profile 仍引用 → 启动失败，错误可读；
- 改 profile 为 `host#sqlite` → 启动成功；
- 原插件 store 数据不被自动迁移；
- Tier 2 同样按策略处理，不悄悄切 Tier 1 store。

---

## 6. OBS-P4-DUP-CONTRIB

### 6.1 现象

`plugins contributions` 中 `natter.sample.memory#file` 出现两次。

### 6.2 修复

- contribution 列表按 `(owner, type, id)` 去重；
- 同时检查 NPS runtime 与 host catalog 是否重复注册；
- 保留 active 状态优先，不要显示两个相同条目；
- 加测试：host + plugin 混合列表无重复。

### 6.3 验收

- `plugins contributions` 每个 `(owner,type,id)` 只一条；
- JSON/表格一致；
- memory sample 只出现一次。

---

## 7. OBS-P4-DUAL-INSTANCE

### 7.1 现象

同一 bot 两个 host 同时在线（测试侧遗留 P3 实例 + P4 实例），每条消息被回复两次。  
停掉 P3 后单回复。

### 7.2 判断

不是 P4 业务逻辑 bug，但生产风险真实存在：  
同一 Discord bot token 多实例会各自接收 gateway 事件并回复。

### 7.3 修复要求

P4 至少做一项：

1. **state-dir 单实例锁**  
   `serve` 启动时对 `<state-dir>/.lock` 获取独占文件锁；已有实例则启动失败，错误说明；
2. **启动诊断**  
   health/serve 输出 instance id + state dir；检测同 state 多实例；
3. **部署文档**  
   明确“一个 bot token 只跑一个 serve”；systemd/container 不要水平扩容同一 token。

推荐 1 + 3。

### 7.4 测试

- 两个 `serve` 同 state-dir：第二个失败，错误可读；
- 第一个停止后第二个可启动；
- 不同 state-dir（测试）仍可能双回复，文档明确不支持；
- 锁文件不包含 secret；
- 异常退出后锁可恢复（OS 文件锁释放）。

---

## 8. OBS-P4-CLIPPED

### 8.1 现象

首轮回复包含 `<response clipped>`，已发送 Discord、写入 SQLite、进入 summary；第六/七轮未复现；源码 grep 0 命中。

### 8.2 风险

- 如果来自模型/网关截断，用户看到不完整回复；
- 异常串进入 summary 后会被后续上下文继承；
- 当前无输出质量校验。

### 8.3 处理

1. 清理测试 state 中受影响 session/summary（仅测试数据）；
2. 增加输出校验：
   - 检测 `<response clipped>`、`<response truncated>` 等明显截断标记；
   - 命中时视为 provider 输出异常，重试一次或返回降级文案；
   - 不把异常串写入 summary；
3. 记录 provider/model/request id/latency，便于定位；
4. 如果再次出现，抓取原始 provider response（不含 key），判断是模型输出还是网关截断；
5. 加回归测试：fake client 返回含标记的内容，pipeline/responder 拒绝持久化。

### 8.4 验收

- 截断标记不进入用户消息/summary；
- 有一次重试或明确降级；
- 受影响测试 summary 清理；
- 无密钥/正文日志泄漏。

---

## 9. 已通过项保留

- 示例 memory.store 插件加载/被发现；
- AI context 路径按 profile 选择 store；
- 真实多轮 summary 生成并增量更新；
- 备份/恢复；
- 双 upstream/health；
- P3 记忆召回基线。

这些不需要重做，只需在修复后回归。

---

## 10. 修复顺序

```text
1. BUG-P4-SCHEMA-PATTERN
2. BUG-P4-PROFILE-STORE
3. OBS-P4-STORE-FALLBACK（文档+fail-closed）
4. OBS-P4-DUP-CONTRIB
5. OBS-P4-DUAL-INSTANCE
6. OBS-P4-CLIPPED
7. 命令 UX 全量
8. 备份/恢复/24h soak
9. Tier 2 smoke
```

为什么：

- schema 阻塞所有 slash 命令；
- profile store 决定命令写到哪里，必须先修；
- fallback 策略会影响数据一致性；
- 单实例是上线前必须的运维保护；
- clipped 是输出质量问题，需先清理测试 summary。

---

## 11. 复测闸门

修复后必须：

- [x] 带 `commands` 的 Discord config 校验/启动通过（显式 schema）；
- [ ] `/ask` `/remember` `/forget` `/memory` `/reset` 真实可用（待真实 Discord）；
- [x] CLI memory 遵循 profile.memory.store；
- [x] Discord 命令与 CLI 使用同一 resolver；
- [x] 缺失 store 启动失败，错误可读，不静默回滚；
- [x] `plugins contributions` 无重复；
- [x] 同 state 双 serve 被拒绝；
- [x] clipped 标记不进入回复/summary（单测）；
- [ ] 真实多轮 summary 仍正常（待测试侧）；
- [x] 备份/恢复（脚本/手工验证）；
- [x] 239/239 回归；
- [ ] 24h soak；
- [ ] Tier 2 smoke；
- [ ] 日志/state/secret 扫描通过；
- [ ] 测试记录归档并更新索引。

---

## 12. 测试资产

保留：

- `docs/testing/2026-11-15-p4-acceptance.md`
- `tests/Natter.Framework.Tests/ChatCommandRouterTests.cs`
- `tests/Natter.Framework.Tests/ContextPipelineTests.cs`
- `tests/Natter.Framework.Tests/ContextStorageTests.cs`
- `samples/Natter.Sample.Plugin.Memory`
- `scripts/natter-backup.sh`
- `deploy/` 示例

规则：

- 测试资产保留；
- 复现缺陷的断言修复后翻转；
- 测试侧不改产品代码；
- 不提交 `artifacts/p4/`、state、插件 staging、`.env`。

---

## 13. 归档规则

本文是临时配对文档。

归档条件：

- 1 High、2 Medium、2 观测关闭；
- P4 命令 UX、profile store、单实例、clipped 收敛；
- 24h soak / Tier 2 smoke 通过；
- 测试负责人 Go；
- 干净 HEAD 复跑。

归档动作：

1. 与 [P4 验收测试](2026-11-15-p4-acceptance.md) 一起移入 `docs/testing/archive/`；
2. 标记 `Archived`；
3. 更新 [测试记录索引](README.md)；
4. 保留缺陷编号、修复提交 SHA、复测证据；
5. 不保留 state/插件 staging/凭据。

---

## 14. 一句话结论

**P4 的底座和真实多轮 memory 已经可用，但 slash 命令被 schema 阻塞，CLI/命令路由没有遵循 profile store，单实例与输出质量还缺保护。**

先修 `BUG-P4-SCHEMA-PATTERN` 和 `BUG-P4-PROFILE-STORE`，再做单实例/输出校验，最后跑命令 UX + 24h soak；P4 Go 之前不进入 P5。
