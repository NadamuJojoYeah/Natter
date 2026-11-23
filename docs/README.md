# Natter 文档索引

> 本文件是 `docs/` 的**唯一入口**。新增文档请在此登记一行；归档文档在文末单独列出。
> 最后整理：2026-11-23（基线 `99b99cc`）

---

## 0. 两条阅读约定（重要）

### 0.1 插件 id 命名

自 2026-11-23（`9abd38e`）起，NPS 插件 id 统一为 **`natter.<kind>.<name>`**，`kind` 与 manifest 的 `kind` 字段一致，并有测试强制（`PluginNaming.IdMatchesKind`）。

| kind | 示例 id | 目录 |
| --- | --- | --- |
| `transport` | `natter.transport.messaging.discord` | `samples/Natter.Sample.Plugin.Discord` |
| `tool` | `natter.tool.admin.webconsole` | `plugins/Natter.Plugin.WebConsole` |
| `feature` | `natter.feature.prompt.library` | `plugins/Natter.Plugin.PromptLibrary` |
| `sample` | `natter.sample.port.greetings` | `samples/Natter.Sample.Plugin.Greetings` |

> **目录名 ≠ 插件 id**：NPS 按目录扫描 + 读 `plugin.json`，唯一标识是 `manifest.id`。
> **历史记录例外**：`docs/testing/`、`docs/operations/` 下的历史记录为保持可读性已按新 id 同步替换，其原始 id 见 git 历史。

### 0.2 文档里的命令必须可核实

> **规矩**：文档中出现的命令，要么**实测过**（标注 ✓ + 输出摘要），要么**明确标注「未验证 + 原因」**（需 root / 需凭据 / 需真机 / 需运行中宿主 / 需网络）。**不允许介于两者之间。**

本仓库曾因文档写错 3 处路径、1 个不存在的命令（`natter prompt list`）浪费实测时间。当前的校验结果见
[架构与一致性审查 §19](REVIEW_2026-11-23_ARCHITECTURE.md#19-第三部分执行结果命令实跑校验)。

---

## 1. 现行设计 · 架构与规范

| 文档 | 一句话定位 |
| --- | --- |
| [ARCHITECTURE.md](ARCHITECTURE.md) | **当前代码**的六边形映射、组合根顺序、Rx 语义、插件加载、CLI 内核与宿主 |
| [HARNESS_ARCHITECTURE.md](HARNESS_ARCHITECTURE.md) | **目标态** harness：控制面/执行面、Session/Task、上下文缓存、记忆持久化与实施阶段 |
| [HARNESS_RESPONSIBILITY.md](HARNESS_RESPONSIBILITY.md) | host 与插件的责任边界：机制归 host、内容归插件，默认值≠强制 |
| [NLMP.md](NLMP.md) | 轻量消息协议模型：source/会话/消息/事件/命令/能力，以及 Satori 映射 |
| [PLUGIN_SPEC.md](PLUGIN_SPEC.md) | **NPS v1 规范**（权威）：manifest、contribution、依赖图、Host API、配置/秘密、权限、激活回滚 |
| [PROMPT_COMPILER.md](PROMPT_COMPILER.md) | Prompt 编译：片段、预设、来源、排序、关闭/启用、版本与安全边界 |
| [DEPENDENCY_EVOLUTION_POLICY.md](DEPENDENCY_EVOLUTION_POLICY.md) | 依赖检查/升级周期、net10→net12 LTS、插件 ABI、C# 语言特性边界 |

## 2. 记忆与检索

| 文档 | 一句话定位 |
| --- | --- |
| [P6_MEMORY_SYSTEM.md](P6_MEMORY_SYSTEM.md) | **记忆现行设计**：五类分层、生命周期、写入/召回、打分预算、Schema、评测；§17 检索与 RAG、§18 Core/Plugin 边界 |
| [P6_MEMORY_GAPS.md](P6_MEMORY_GAPS.md) | **未决策项**：缺口 + 理由 + 触发条件（已决项已并入上一份） |
| [P6_MEMORY_EVAL_RESULTS.md](P6_MEMORY_EVAL_RESULTS.md) | 检索评测数据：tokenizer / 衰减 / 候选数三组对照的实测结论 |
| [P6_RAG_PLUGIN_DESIGN.md](P6_RAG_PLUGIN_DESIGN.md) | RAG **插件**（外部服务、向量库）的方案；与内核记忆能力的边界见其 §0 |

> **口径**：v2（五类分层）是**目标态、正在切换**；v1（`IMemoryStore`）仍是生产召回路径并标 Obsolete 待退役。不要把任何一份读成「已全部切到 v2」。

## 3. 子域设计

| 文档 | 一句话定位 |
| --- | --- |
| [DISCORD_PLUGIN_DESIGN.md](DISCORD_PLUGIN_DESIGN.md) | Discord 上游：manifest、config、transport 边界、NLMP 映射、slash 前置与测试 |
| [AI_PROVIDER_DESIGN.md](AI_PROVIDER_DESIGN.md) | Microsoft.Extensions.AI、IChatClient、DeepSeek/Spark 适配、thinking/reasoning 与 responder |
| [STATE_VITALS_DESIGN.md](STATE_VITALS_DESIGN.md) | State & Vitals：断言态/vital/effects/trigger、pack 体系、定点持久化与 CLI |
| [CONVERSATION_CONSOLE.md](CONVERSATION_CONSOLE.md) | 会话控制台插件：只读回放的查询端口、认证、审计与 UI |
| [WEBCONSOLE_PLUGIN_DESIGN.md](WEBCONSOLE_PLUGIN_DESIGN.md) | WebConsole 插件设计：只读诊断、配置编辑、受限命令、认证/反代与加载风险 |
| [WEBCONSOLE_PLUGIN.md](WEBCONSOLE_PLUGIN.md) | WebConsole 使用说明：staging、配置、反向代理、排障与已知限制 |
| [WEBCONSOLE_UI_DIRECTION.md](WEBCONSOLE_UI_DIRECTION.md) | WebConsole 前端方向：时间轴、折叠、三态、诊断包（零构建 ES modules） |

## 4. 指南（开发 · 插件 · 发布）

| 文档 | 一句话定位 |
| --- | --- |
| [DEVELOPMENT.md](DEVELOPMENT.md) | 日常开发循环、扩展 recipe、DI 约定、测试与调试速查 |
| [PLUGINS.md](PLUGINS.md) | **legacy 插件模型**教程（`IPluginModule` + `ICliCommandContributor`）；NPS 插件见 PLUGIN_SPEC |
| [plugin.schema.json](plugin.schema.json) | NPS v1 `plugin.json` 的机器可读 JSON Schema |
| [PUBLISHING.md](PUBLISHING.md) | RID 矩阵、单文件发布、Android 签名与裁剪 |
| [LIVE_TEST_PLAN.md](LIVE_TEST_PLAN.md) | 真实 API 联调：L1–L8 分层矩阵、判据、执行入口与计数口径 |

> 两套插件模型**并存且都有效**：legacy（`Natter.Abstractions` + `Natter.Cli.Abstractions`）与 NPS v1（`Natter.Plugin.Abstractions`）。新插件一律写 NPS。

## 5. 运维与部署

| 文档 | 一句话定位 |
| --- | --- |
| [OPERATIONS_RUNBOOK.md](OPERATIONS_RUNBOOK.md) | 部署、secret/state、health、备份恢复、升级回滚与故障处理 |
| [operations/](operations/) | 运维执行记录（soak 采样、P4.5 收口、网关停顿、出口探测、secret 轮换） |
| [../deploy/README.md](../deploy/README.md) | 部署产物与 staging 布局 |
| [../deploy/DEPLOY-2026-11-23.md](../deploy/DEPLOY-2026-11-23.md) | 本次实机部署步骤 |
| [../deploy/VERIFY-2026-11-23.md](../deploy/VERIFY-2026-11-23.md) | 本次部署的验证矩阵 |

## 6. 阶段记录与审查

| 文档 | 一句话定位 |
| --- | --- |
| [P2_RUNTIME_PROFILE.md](P2_RUNTIME_PROFILE.md) | P2 底座：profile 路由、serve、SQLite 会话、health（结论 Closed·Go） |
| [P5_AGENT_TASK_RUNTIME.md](P5_AGENT_TASK_RUNTIME.md) | AgentTask：租约、Worker、工具、取消/超时/重启恢复与安全边界 |
| [REVIEW_2026-11-23_ARCHITECTURE.md](REVIEW_2026-11-23_ARCHITECTURE.md) | 架构与一致性审查（23 项）+ 文档整理与**命令实跑校验**结果 |

## 7. 测试记录

| 文档 | 一句话定位 |
| --- | --- |
| [testing/README.md](testing/README.md) | 测试记录规范 + **完整索引**（每条记录的日期/范围/结果） |
| [testing/TEST_EXECUTION.md](testing/TEST_EXECUTION.md) | 标准测试命令、超时层级、`--blame-hang` 排查顺序 |
| [testing/TEMPLATE.md](testing/TEMPLATE.md) | 新记录模板 |

---

## 8. 归档（历史记录，勿据此实现）

`docs/archive/` 下的文件描述的是**已完成或已被取代的阶段**，仅用于追溯当时的决策与证据。文件头均带归档横幅。

| 文档 | 原用途 |
| --- | --- |
| [archive/P3-CONTEXT-MEMORY.md](archive/P3-CONTEXT-MEMORY.md) | P3 的 prompt/context/memory 实现规格 |
| [archive/P4-TIER1-PRODUCTIZATION.md](archive/P4-TIER1-PRODUCTIZATION.md) | P4 Tier 1 产品化（命令 UX、soak、发布闸门） |
| [archive/P5-SECOND-ADAPTER-VALIDATION.md](archive/P5-SECOND-ADAPTER-VALIDATION.md) | 钉钉 + 星火的接入验证矩阵 |
| [archive/REVIEW-2026-11-22-CODE-REVIEW.md](archive/REVIEW-2026-11-22-CODE-REVIEW.md) | 2026-11-22 全仓库代码审查（S1/S2/S3 来源） |
| [archive/REVIEW-2026-11-22-S3-TRIAGE.md](archive/REVIEW-2026-11-22-S3-TRIAGE.md) | 上述审查的 S3 45 项复核与分级 |
| [archive/WEBCONSOLE-HOST-API-GAP.md](archive/WEBCONSOLE-HOST-API-GAP.md) | WebConsole 宿主契约缺口 G1–G10（已由 Phase 2/3 关闭） |

---

## 9. 维护规则

1. **新增顶层文档必须在此登记**，并归入 1–6 组之一；归档的进 §8。
2. **合并**：同作用域的两份文档合并后删除其一，并在删除处留一行「已并入 X」——原文永远可从 git 历史取回。
3. **归档**：阶段完成且不再维护时移入 `docs/archive/`，文件名加阶段前缀，文件头加归档横幅，并把相对链接补一层 `../`。
4. **不允许**：写死会过期的计数（插件数、用例数、文档数）；用「以 X 为准」或「当前 N（来源：Y）」。
5. **命令**：见 §0.2。新增命令段落时必须当场跑一次并把结果写进同一份文档。
