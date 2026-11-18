# P5 Agent Task Runtime 验收测试计划

> **状态**：Ready for test  
> **日期**：2026-11-18  
> **基线**：`HEAD=2a13b8b`（P5 foundation 已提交；框架 254/254）  
> **被测对象**：`AgentTask`、`TaskDispatcher`、`SqliteTaskStore`、`/task` `/status` `/tasks` `/cancel`、`ITool`/`IToolPolicy`、task health/metrics  
> **开发规格**：[P5 Agent Task Runtime](../P5_AGENT_TASK_RUNTIME.md)  
> **关联**：[P4.5 复测处置](2026-11-17-p4-5-retest-response.md)、[运维手册](../OPERATIONS_RUNBOOK.md)

---

## 0. 先定交付口径

当前 P5 是：

- **P5.0–P5.2 foundation**：
  - AgentTask + SQLite/InMemory store；
  - TaskDispatcher；
  - `/task` `/status` `/tasks` `/cancel`；
  - lease/cancel/timeout/recovery；
  - clock/text-transform/http-fetch 工具；
  - AI worker + deterministic fallback；
  - health task metrics。

不是：

- 多步 agent loop；
- 多工具 planner；
- shell/文件写入；
- 多 Agent；
- 第三平台。

测试不要用“有工具有命令”推断“完整 agent 已交付”。

---

## 1. 前置

```bash
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

预期：

- build 成功；
- 框架 **254/254**；
- WebConsole 123/123 不回归；
- live 无凭据 skip。

Discord 插件 config 必须包含：

```json
"commands": {
  "ask": true,
  "remember": true,
  "forget": true,
  "memory": true,
  "reset": true,
  "task": true,
  "status": true,
  "tasks": true,
  "cancel": true
}
```

---

## 2. 测试矩阵

### Gate A：构建/回归

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-001 | build | 成功 |
| P5-002 | 框架回归 | 254/254 |
| P5-003 | WebConsole 回归 | 123/123 |
| P5-004 | 无凭据 live | 全 skip |

### Gate B：命令与任务创建

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| P5-010 | `/task prompt:<text>` | Discord slash | 3s 内 ephemeral ACK：`Task accepted: task_...` |
| P5-011 | 空 prompt | `/task` | usage 提示，不创建 |
| P5-012 | task 不可用 | 关闭 task runtime | `Task runtime is not available.` |
| P5-013 | `/status task:<id>` | 创建后查询 | 状态、version、结果摘要 |
| P5-014 | `/tasks` | 当前 session | 只列 active，不列别人 |
| P5-015 | `/cancel task:<id>` | 创建者 | `Cancel requested.` |
| P5-016 | 非创建者 cancel | 另一 actor | 拒绝 |
| P5-017 | 未知 task | `/status`/`/cancel` | not found，不崩溃 |
| P5-018 | 普通 `/ask` | 普通聊天 | 不创建 task，行为不变 |

### Gate C：生命周期

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-020 | Pending → Running → Completed | 状态正确，version 递增 |
| P5-021 | Worker 失败 | Failed + errorCode/message |
| P5-022 | cancel before terminal | Cancelled，结果只发一次 |
| P5-023 | cancel after terminal | false，不改状态 |
| P5-024 | 超时 | Cancelled/Expired，不永久 Running |
| P5-025 | lease 抢占 | 第二个 owner 失败 |
| P5-026 | 乐观锁 | 旧 version 更新失败 |
| P5-027 | lease 过期 | watchdog 清扫 |
| P5-028 | 重启 Pending | 重新入队 |
| P5-029 | 重启 Running expired | Expired/重排，不重复结果 |
| P5-030 | 结果只发一次 | 重放/重启无重复 |

> 当前 TaskDispatcher 使用固定 `LeaseTtl=2m`、`TaskTimeout=5m`、30s watchdog；测试可通过 fake worker 缩短等待，不要真的等 5 分钟。

### Gate D：工具与策略

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-040 | clock 工具 | 返回 UTC 时间 |
| P5-041 | text-transform | 大写 + 字符数 |
| P5-042 | http-fetch 允许 host | 200 + 截断 |
| P5-043 | http-fetch 非 allowlist | `host_not_allowed` |
| P5-044 | http 非 HTTPS | `invalid_url` |
| P5-045 | http 非 2xx | `http_error` |
| P5-046 | http 超时 | 取消/超时 |
| P5-047 | 网络工具默认关闭 | `AllowNetworkTools=false` 不选 network tool |
| P5-048 | shell/file write | 没有工具，policy 拒绝 |
| P5-049 | 工具输出上限 | 64 KiB / 可配置 |

### Gate E：Worker / AI fallback

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-050 | AI responder 返回文本 | task Completed，结果回原会话 |
| P5-051 | AI responder 返回 null | fallback 到 read-only tool |
| P5-052 | AI responder 抛异常 | fallback，不直接 Failed |
| P5-053 | fallback 无工具 | `no_tool` |
| P5-054 | task context 隔离 | 不污染用户聊天 history/summary |
| P5-055 | 结果不是 Worker 直接发送 | 通过 dispatcher/command |

### Gate F：持久化 / health

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-060 | SQLite task 表 | 迁移/写入/读取 |
| P5-061 | 重启恢复 | Pending/Running/Completed 正确 |
| P5-062 | state 权限 | 0700/0600 |
| P5-063 | health task metrics | created/completed/failed/cancelled/active |
| P5-064 | active 数 | 完成/失败/取消后递减 |
| P5-065 | 结果 idempotency | 重放只发一次 |
| P5-066 | task 事件 | created/started/result/failed/cancelled |

### Gate G：安全

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-070 | 非创建者 cancel | 拒绝 |
| P5-071 | 跨 source task | 不可见/不可取消 |
| P5-072 | prompt 注入工具 | 不能绕过 allowlist |
| P5-073 | 私有 IP/元数据 URL | allowlist 之外拒绝 |
| P5-074 | shell 注入 | 不启动子进程 |
| P5-075 | 日志 | 不含 prompt/工具正文/secret |
| P5-076 | 结果长度 | 截断，不撑爆平台 |

### Gate H：真实 Tier 1 / Tier 2

Tier 1：

- Discord `/task prompt:...`；
- ACK；结果回原频道；
- `/status`；
- `/cancel`；
- 重启后状态；
- 普通 `/ask` 不受影响；
- 真实 DeepSeek 可用时任务结果正常。

Tier 2：

- DingTalk 不支持 slash `/task` 时 capability 降级；
- 普通文本回复不回归；
- 不影响 Tier 1。

| ID | 用例 | 预期 |
| --- | --- | --- |
| P5-080 | 真实 `/task` | 结果回 Discord |
| P5-081 | 真实 cancel | 取消成功 |
| P5-082 | 重启恢复 | task 状态/结果不丢 |
| P5-083 | `/ask` 回归 | 正常 |
| P5-084 | DingTalk smoke | 文本正常 |

---

## 3. 重点风险

1. **lease 不续租**：`LeaseTtl=2m`、TaskTimeout=5m，长任务可能被 watchdog 判过期；必须测。
2. **结果只发一次**：`_resultsSent` 在内存，重启后可能重复发送；必须测。
3. **AI worker fallback**：AI 失败会落到第一个 read-only tool，可能返回 clock 等无关结果；测试要确认产品语义。
4. **多工具 loop 未实现**：不要按“完整 agent”验收。
5. **HTTP 工具 SSRF**：当前只有 host allowlist + HTTPS，没有私网 IP/DNS rebinding 防护；测试 allowlist，生产需补。
6. **Tier 2 不支持 slash**：`/task` 只对 Discord 等支持 interaction 的平台；钉钉应明确不可用。

---

## 4. 证据

```text
artifacts/p5/<run>/
├── build.log
├── framework.trx
├── task-commands.json
├── task-status.json
├── task-cancel.json
├── sqlite-task.txt
├── health-task.json
├── logs/
└── screenshots/
```

不提交 state、prompt、task 结果正文、secret。

---

## 5. 签收

- [ ] P5-001 ~ P5-004；
- [ ] P5-010 ~ P5-018；
- [ ] P5-020 ~ P5-030；
- [ ] P5-040 ~ P5-049；
- [ ] P5-050 ~ P5-055；
- [ ] P5-060 ~ P5-066；
- [ ] P5-070 ~ P5-076；
- [ ] P5-080 ~ P5-084；
- [ ] 无 Blocker/High；
- [ ] lease/重启/结果幂等有证据；
- [ ] Tier 2 明确降级；
- [ ] 文档/证据归档。

---

## 6. 一句话

**P5 现在能签的是“任务运行时 foundation”，不是“完整多步 Agent”；先验 `/task`、lease、重启幂等、工具 allowlist，再谈真实 Tier1 和多工具 loop。**
