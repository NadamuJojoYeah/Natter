# 测试记录规范与索引

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
| 2026-11-15 | [真实 API 联调：Discord + DeepSeek](2026-11-15-live-deepseek-discord.md) | `natter.transport.discord`、`natter.feature.ai.deepseek` 的分层真实测试（L1–L5） | 26/26 live 通过，172/172 框架回归通过；2 个缺陷、2 个观测、2 个环境限制 |
| 2026-11-15 | [开发侧处置记录](2026-11-15-live-deepseek-discord-response.md) | BUG/OBS 处置、临时改动、发布闸门与归档规则 | BUG-001/002 已修复；26/26 live 复测通过；L5 待复测 |
| 2026-11-15 | [人工闸门（真人 @bot）](2026-11-15-live-manual-gate.md) | 真实频道内人工 @bot → AI 回复（L5） | 基线未通过：`BUG-003`；修复提交 `687f27f` |
| 2026-11-15 | [人工闸门处置记录](2026-11-15-live-manual-gate-response.md) | BUG-003 根因、修复决策、回归闸门与归档规则 | BUG-003 已修复；177/177 回归、ingress 2/2；L5 待真人复测 |
| 2026-11-15 | [人工闸门复测（通过）](2026-11-15-live-manual-gate-rerun.md) | 真实频道内人工 @bot → AI 回复（L5 复测） | **通过**：约 3s 回复、reply 引用正确；`BUG-003` 关闭 |
| 2026-11-15 | [真实联调：DingTalk Stream + Spark](2026-11-15-live-dingtalk-spark.md) | 钉钉 Stream 连接、Spark MaaS、插件加载与端到端 | **端到端通过**：群内 @机器人 收到 20 字回复；自动化 Spark 7/7 + DingTalk 2/2 |
| 2026-11-15 | [测试资产收口](2026-11-15-asset-closure.md) | 凭据移出工作区、明文扫描、临时/正式目录分离、资产清单 | 完成：仓库无明文、历史 0 命中、测试改从仓库外读取；平台轮换待人工 |
| 2026-11-15 | [P2 验收测试（归档）](archive/2026-11-15-p2-acceptance.md) | serve、profiles、SQLite、Tier 1/2、并发与安全 | **Closed · Go**：4 个 High 复测通过；真实 DM/频道/钉钉往返；214/214 |
| 2026-11-15 | [P2 验收处置记录（归档）](archive/2026-11-15-p2-acceptance-response.md) | 4 个 High 的根因、修复方向、复测闸门与归档规则 | **Closed · Go**（Archived） |
| 2026-11-15 | [P3 Context & Memory 验收](2026-11-15-p3-acceptance.md) | prompt/context/memory、SQLite memory、remember/forget、真实召回 | **No-Go**：真实链路全部通过；1 High（共享 scope 删除）+ 2 Medium（contribution/export） |
| 2026-11-15 | [P3 验收处置记录](2026-11-15-p3-acceptance-response.md) | 共享删除、host contribution、export CLI、DM private、prompt injection、summary 更新 | 待修复：1 High + 2 Medium + 3 未测项 |

## 5. 相关文档

- [P3 Context & Memory 验收测试计划](P3_ACCEPTANCE.md)：prompt/context/memory、SQLite 迁移、remember/forget、真实召回与隔离
- [P2 Runtime & Profile 验收测试计划](P2_ACCEPTANCE.md)：serve、profiles、SQLite 会话、health、Tier 1/Tier 2 与并发验收
- [测试资产清单与归档规则](ASSETS.md)：代码/脚本/文档/凭据的位置，正式与临时目录边界，清理与轮换清单
- [真实 API 联调测试方案](../LIVE_TEST_PLAN.md)：分层矩阵、判据与执行入口
- [模板](TEMPLATE.md)：新记录直接复制
