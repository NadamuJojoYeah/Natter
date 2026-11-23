# 测试记录规范与索引
> **id 归一化说明（2026-11-23）**：插件 id 已统一为 `natter.<kind>.<name>`（`9abd38e`）。本目录历史记录中的 id 已按新命名同步替换以保持可 grep；当时的原始 id 见 git 历史。记录中的结论、计数与判定未改动。

> 目的：**每次测试必须留下可追溯的书面记录**，至少说明三件事——测试时间、测试内容、发现的具体问题。
> 测试的职责是**发现问题并留下证据**，不是修复产品代码。

---

## 1. 强制字段

每份测试记录（`docs/testing/YYYY-MM-DD-<scope>.md`）必须包含：

| 字段 | 要求 |
| --- | --- |
| **测试时间** | 起止时间（`YYYY-MM-DD HH:mm–HH:mm`，`Asia/Shanghai`），以及各阶段完成时间 |
| **基线** | git `HEAD`、工作区是否干净、涉及的构建产物版本 |
| **环境** | OS、.NET SDK、目标服务与账号标识（不含密钥）、网络条件 |
| **测试范围** | 被测模块、用例清单或分层表、明确不测什么 |
| **执行命令** | 可复制粘贴的命令行，含凭据来源（只写路径，不写值） |
| **结果** | 通过/失败/跳过的数量与清单；关键真实数据（延迟、token、消息 id 等） |
| **具体问题** | 每个问题独立编号，含现象、复现、证据、影响、建议 |
| **临时改动** | 为解除测试阻塞而对产品代码做的改动（如有），必须登记并可回退 |
| **结论与后续** | 当前结论、待开发确认项、下一轮建议 |

## 2. 问题编号

| 前缀 | 含义 |
| --- | --- |
| `BUG-xxx` | 产品缺陷（可复现、影响功能） |
| `OBS-xxx` | 真实环境观测（行为事实，不一定是缺陷） |
| `ENV-xxx` | 环境/权限/账号限制（阻碍测试覆盖，不是产品问题） |
| `FLAKE-xxx` | 不稳定现象（需要更多样本才能定性） |

问题条目模板：

```markdown
### BUG-001 · 一句话标题

- **发现时间**：2026-11-15 12:20
- **严重度**：阻塞 / 高 / 中 / 低
- **影响**：谁能感知到什么
- **复现**：命令或步骤（可直接复制）
- **证据**：原始输出摘录
- **测试侧处置**：仅为解除阻塞做了什么（若有），并声明不是修复
- **建议**：给开发的处置方向（不代替开发决策）
```

## 3. 纪律

1. **只测试、不修复**：发现问题后记录并按约定上报；产品代码的改动属于开发职责。
2. **临时改动必须显式**：若某个问题会完全阻塞测试，且必须临时改动才能继续，须在记录中单列「临时改动」并给出回退方式；该改动视为待评审，不视为测试成果。
3. **证据优先**：记录原始命令与输出，不写「应该是」「可能」；无法复现的现象归入 `FLAKE`。
4. **不写密钥**：只写凭据来源路径（如 `artifacts/live/.env`），任何日志/文档不得出现 key/token 明文。
5. **时间必须真实**：使用系统时间，不估算；跨时区统一标注 `Asia/Shanghai`。

## 4. 记录索引

| 日期 | 记录 | 范围 | 结果 |
| --- | --- | --- | --- |
| 2026-11-15 | [真实 API 联调：Discord + DeepSeek](2026-11-15-live-deepseek-discord.md) | `natter.transport.messaging.discord`、`natter.feature.ai.deepseek` 的分层真实测试（L1–L5） | 26/26 live 通过，172/172 框架回归通过；2 个缺陷、2 个观测、2 个环境限制 |
| 2026-11-15 | [开发侧处置记录](2026-11-15-live-deepseek-discord-response.md) | BUG/OBS 处置、临时改动、发布闸门与归档规则 | BUG-001/002 已修复；26/26 live 复测通过；L5 待复测 |
| 2026-11-15 | [人工闸门（真人 @bot）](2026-11-15-live-manual-gate.md) | 真实频道内人工 @bot → AI 回复（L5） | 基线未通过：`BUG-003`；修复提交 `687f27f` |
| 2026-11-15 | [人工闸门处置记录](2026-11-15-live-manual-gate-response.md) | BUG-003 根因、修复决策、回归闸门与归档规则 | BUG-003 已修复；177/177 回归、ingress 2/2；L5 待真人复测 |
| 2026-11-15 | [人工闸门复测（通过）](2026-11-15-live-manual-gate-rerun.md) | 真实频道内人工 @bot → AI 回复（L5 复测） | **通过**：约 3s 回复、reply 引用正确；`BUG-003` 关闭 |
| 2026-11-15 | [真实联调：DingTalk Stream + Spark](2026-11-15-live-dingtalk-spark.md) | 钉钉 Stream 连接、Spark MaaS、插件加载与端到端 | **端到端通过**：群内 @机器人 收到 20 字回复；自动化 Spark 7/7 + DingTalk 2/2 |
| 2026-11-15 | [测试资产收口](2026-11-15-asset-closure.md) | 凭据移出工作区、明文扫描、临时/正式目录分离、资产清单 | 完成：仓库无明文、历史 0 命中、测试改从仓库外读取；平台轮换待人工 |
| 2026-11-15 | [P2 验收测试（归档）](archive/2026-11-15-p2-acceptance.md) | serve、profiles、SQLite、Tier 1/2、并发与安全 | **Closed · Go**：4 个 High 复测通过；真实 DM/频道/钉钉往返；214/214 |
| 2026-11-15 | [P2 验收处置记录（归档）](archive/2026-11-15-p2-acceptance-response.md) | 4 个 High 的根因、修复方向、复测闸门与归档规则 | **Closed · Go**（Archived） |
| 2026-11-15 | [P3 Context & Memory 验收（归档）](archive/2026-11-15-p3-acceptance.md) | prompt/context/memory、SQLite memory、remember/forget、真实召回 | **Closed · Go**：1 High + 2 Medium 修复复测；232/232；真实召回/删除/重启通过 |
| 2026-11-15 | [P3 验收处置记录（归档）](archive/2026-11-15-p3-acceptance-response.md) | 共享删除、host contribution、export CLI、DM private、prompt injection、summary 更新 | **Closed · Go**（Archived） |
| 2026-11-15 | [P4 Tier 1 产品化验收](2026-11-15-p4-acceptance.md) | 命令 UX、memory.store、备份/恢复、真实多轮、单实例 | **Go**：命令 5/5 真实通过、Tier2 smoke、239/239；24h soak 未执行 |
| 2026-11-17 | [P4.5 修复验证](2026-11-17-p4-5-test-report.md) | 代理、health offline/时间戳、Tier1 诊断、P4 回归 | **环境受限**：245/245、代理单测 5/5、health 通过；Discord 网络阻断，真实链路未完成；1 Medium（DingTalk 时间戳） |
| 2026-11-17 | [P4.5 联合处置记录](2026-11-17-p4-5-joint-response.md) | 测试/运维结果冲突、代理 gateway、DingTalk 时间戳、OBS-009~014 | **Conditional No-Go**：代理 WS 结果冲突待复现；DingTalk 时间戳/诊断/退出延迟待修 |
| 2026-11-17 | [P4.5 联合复测计划](2026-11-17-p4-5-retest-plan.md) | 代理 90s 受控复现、duration、DingTalk health、sendFailures、systemd/Docker/24h | 复测后 Go/No-Go |
| 2026-11-17 | [P4.5 复测报告（测试）](2026-11-17-p4-5-retest-report.md) | 代理 gateway、duration、DingTalk health、Tier1/offline | **Conditional Pass**：代理/duration/DingTalk connected 通过；P4 真人回归待补 |
| 2026-11-17 | [P4.5 复测处置记录](2026-11-17-p4-5-retest-response.md) | phase/lastError/canceled/sendFailures 稳定性、P4 回归、运维收口 | **Conditional Pass**：代理冲突关闭；真人回归/运维项待完成 |
| 2026-11-15 | [P4 验收处置记录](2026-11-15-p4-acceptance-response.md) | Schema、profile store、fail-closed、单实例、clipped 输出 | 待修复：1 High + 2 Medium + 2 观测 |
| 2026-11-16 | [首次部署测试（Tier 1）](2026-11-16-first-deployment-test.md) | `OPERATIONS_RUNBOOK.md` §1–§11：发布、配置/secret、health、单实例、备份/恢复、优雅停止、采样、T+13h 巡检 | **通过（有条件）**：18 用例 17 通过；2 个代理阻塞缺陷 + 5 个观测 + 2 个环境限制；T+13h 断连经重启恢复 |
| 2026-11-17 | [首次部署处置记录](2026-11-16-first-ops-deploy-response.md) | 代理/超时、health 语义、CLI 巡检、静默断连、uptime、systemd/soak | 待修复：2 阻塞 + 5 OBS + 2 环境项 |
| 2026-11-18 | [WebConsole Phase 1 实现与验证](2026-11-18-webconsole-p1.md) | 插件加载/认证/health 桥接/审计/UI/staging | **通过**：68/68 单测、真实宿主与 HTTP 矩阵通过；G1–G9 显式降级；1 BUG 已修复 |
| 2026-11-18 | [WebConsole Phase 2/3 就绪与硬化](2026-11-18-webconsole-p2-ready.md) | 配置校验/ETag/diff、命令 allowlist/截断、审计权限、HEAD/转发协议、external 模式、CI/E2E | **通过**：123/123（Debug+Release）、真实宿主矩阵、`webconsole-e2e.sh` PASS；写/执行仍待 G5/G6 |
| 2026-11-18 | [P5 验收报告](2026-11-18-p5-acceptance-report.md) | /task 主链路、组件、工具契约、health、安全矩阵 | **Go（scope：/task）**：264/264；真实 `/task` 通过；`/status`/`/cancel` 待补 |
| 2026-11-18 | [WebConsole 测试报告](2026-11-18-webconsole-test-report.md) | 认证/CSRF/静态/CSP/snapshot/配置校验/命令策略 | **Phase 1 Go**；Phase 2/3 仍 501；3 Medium 已关闭 |
| 2026-11-21 | [P5 + WebConsole 修复复测](2026-11-18-p5-webconsole-retest.md) | schema、router wiring、真实 `/task`、WebConsole 3 Medium | **P5 Go（/task）**；**WebConsole Phase 1 Go**；Phase 2/3 仍 501 |
| 2026-11-21 | [P5 遗留缺口回归](2026-11-21-p5-followup-regression.md) | `/status` `/tasks` `/cancel`、lease 续租/过期、重启幂等与结果只发一次、工具安全矩阵、Tier 2 降级、metrics | **Go（自动化部分）**；真实 Discord 命令面与人机回归待人工 |
| 2026-11-21 | [WebConsole Phase 2/3 独立验收](2026-11-21-webconsole-phase23-acceptance.md) | `/api/config/*` 保存/备份/回滚、ETag 与并发写、符号链接、`/api/commands` 受限执行、审计与脱敏、Phase 1 回归 | **Go**：真实落盘与受限执行通过，Phase 2/3 全路径无 501，Phase 1 无回归 |
| 2026-11-22 | [最终 6 项 S1 独立验收](2026-11-22-final-s1-acceptance.md) | S1-01…S1-10 修复合集的独立复核 + 网关看门狗只读核验 | **通过**：新增验收连跑全绿，Framework 全量无回归（数字见记录） |
| 2026-11-22 | [P5.3 task UX 验收](2026-11-22-p5-task-ux-acceptance.md) | `ITaskStore.AppendEventAsync`、`TaskTracePayload` 白名单、`/status` `/tasks` `/cancel`、`TaskMetrics` 新字段 | **通过**；另记录 1 个**既有**重放缺陷（非 P5.3 引入），建议单独立项 |
| 2026-11-22 | [审查修复独立验收](2026-11-22-review-acceptance.md) | S1-01/02/04/08/09/10 + task-29 的独立复核（含隔离端到端现场） | **通过**：Framework 全量无回归（数字见记录） |
| 2026-11-22 | [S2 第一批独立验收](2026-11-22-s2-batch1-acceptance.md) | 插件包与 store、任务运行时、Satori + AI、钉钉 + Discord、CLI + 运维、签名规范（全部反向/边界断言） | **通过**；注意 Release 必须先重建再 `--no-build` 执行 |
| 2026-11-22 | [S2 第二批独立验收](2026-11-22-s2-batch2-acceptance.md) | task-45/46/47/48 的 23 项反向验证（并发预留、判重窗口、分片续传、权限治理、安装后完整性等） | **通过**；3 处断言按产品实际语义调整，2 处覆盖缺口见记录 §0.3 |
| 2026-11-22 | [S2 测试与文档质量](2026-11-22-s2-test-quality-acceptance.md) | F1 harness ETag 对拍、F2 预算断言、F3 串行化、F5 超时/blame-hang、F6 覆盖、F7 三文件合并、F8/F9 文档口径 | **已修**（F4 归 WebConsole）；明细见记录 |

> **索引以文件为准**：本表是导读，不是清单的唯一来源。核对命令：`ls docs/testing/*.md`（归档记录在 `docs/testing/archive/`）。新增记录时同步本表一行。

## 5. 相关文档

- [P5 + WebConsole 联合处置记录](2026-11-18-p5-webconsole-response.md)：P5 schema 阻塞、WebConsole 3 Medium 与复测闸门
- [P5 Agent Task Runtime 验收测试计划](2026-11-18-p5-acceptance.md)：/task、租约、取消、重启恢复、工具 allowlist、健康指标
- [WebConsole 插件验收测试计划](2026-11-18-webconsole-acceptance.md)：Phase 1 只读控制台、Phase 2/3 策略与 501 降级、认证/审计/代理
- [P4.5 修复验证（测试交接）](2026-11-17-p4-5-test-verification.md)：代理、health/offline、Tier1-only、OBS-007 边界
- [P3 Context & Memory 验收测试计划](P3_ACCEPTANCE.md)：prompt/context/memory、SQLite 迁移、remember/forget、真实召回与隔离
- [P2 Runtime & Profile 验收测试计划](P2_ACCEPTANCE.md)：serve、profiles、SQLite 会话、health、Tier 1/Tier 2 与并发验收
- [测试资产清单与归档规则](ASSETS.md)：代码/脚本/文档/凭据的位置，正式与临时目录边界，清理与轮换清单
- [真实 API 联调测试方案](../LIVE_TEST_PLAN.md)：分层矩阵、判据与执行入口
- [模板](TEMPLATE.md)：新记录直接复制
