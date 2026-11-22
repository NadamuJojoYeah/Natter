# P6 Memory：设计缺口与验收核对

> **状态**：设计审阅（只读）
> **日期**：2026-11-22
> **核对基线**：`b4e91f8`（`b4e91f85415e72a9920af7fc9f12de9200602534`）
> **定位**：`docs/P6_MEMORY_RAG.md` 尚未定清的问题清单 + §11 验收标准的实现状态核对。
> **关联**：[P6 设计](P6_MEMORY_RAG.md)、[P6.3 RAG 插件方案](P6_RAG_PLUGIN_DESIGN.md)、[Prompt Compiler](PROMPT_COMPILER.md)

---

## 0. 本文的边界

```text
只读审阅。不修改 src/、plugins/、tests/、scripts/、deploy/。
不部署、不重启、不 commit/push。
一切代码判定取自 git show b4e91f8:<path>，不采信工作区未提交改动。
```

### 0.1 基线变动说明（重要）

本文动笔时 HEAD 为 `a98ec29`（只有设计文档）。审阅过程中 P6.1、P6.2 已由并行任务提交，HEAD 前进到 `b4e91f8`：

```text
8a982f8  feat: 记忆作用域、FTS5 轻量检索与片段化注入     ← P6.1
e48a784  feat: 记忆默认写入按 actor，检索保持可见性并集
b4e91f8  feat: 增加 embedding 与 rerank 可扩展端口        ← P6.2
```

因此本文的 §8 验收表是**对已提交代码**的核对，不是对空实现的计划核对。这也是为什么本轮的「缺口」集中在**设计语义**而不是「有没有做」。

### 0.2 实测环境（本文所有运行时结论的来源）

| 项 | 值 | 获取方式 |
| --- | --- | --- |
| `Microsoft.Data.Sqlite` | 10.0.14 | `Directory.Packages.props` |
| 捆绑原生库 | `SQLitePCLRaw.lib.e_sqlite3` 2.1.13 | `~/.nuget/packages` |
| 实际 SQLite | **3.53.3** | `SELECT sqlite_version()` |
| FTS5 编译 | **是**（`ENABLE_FTS5`） | `sqlite_compileoption_used('ENABLE_FTS5')` |
| 扩展加载 | 函数存在但**默认禁用**（`not authorized`） | `SELECT load_extension(...)` |
| `Microsoft.Extensions.AI` | 10.11.0 | `Directory.Packages.props` |

> 本机 `sqlite3` CLI **未**编译 FTS5（`no such module: fts5`）。任何 FTS5 验证必须走 .NET，用 CLI 验会得到假阴性。

---

## 1. 缺口总览

| # | 缺口 | 严重度 | 需要谁决策 | 阻塞什么 |
| --- | --- | --- | --- | --- |
| Q1 | 记忆写入路径未定义（谁写/何时写/显式 vs 自动） | **高** | 产品 + 架构 | P6.1 已交付检索，但写入仍靠外部命令 |
| Q2 | 去重与冲突未定义（同一事实说两次） | **高** | 架构 | 长跑后记忆库会积累矛盾事实 |
| Q3 | 容量上限与淘汰未定义（per-actor 无限增长） | **高** | 架构 + 运维 | 生产长跑必然撞上 |
| Q4 | 检索质量无度量 | **高** | 架构（需先定口径） | 无法调 trigram/衰减/RRF，RAG 无法验收 |
| Q5 | embedding 模型/维度变更后的旧向量处理 | 中 | 架构 | P6.3 上线即遇到 |
| Q6 | 作用域术语三套并存（actor / User / Global） | 中 | 架构 | 文档与代码互相看不懂 |
| Q7 | Global/Guild 作用域与「跨 actor 隔离」断言冲突 | **高** | 架构 | 隐私承诺有洞 |
| Q8 | 中文 trigram 两字词零命中（已实测） | **高** | 架构 | 中文检索实际不可用 |
| Q9 | 衰减只作用于候选池内（两段式排序） | 中 | 架构 | 老而高度相关的记忆永远进不来 |
| Q10 | SQLite 与 InMemory 两实现检索语义不一致 | 中 | 架构 | 测试通过、生产行为不同 |
| Q11 | FTS 命中为空时静默回退 recency | 中 | 架构 | 相关性检索失败不可观测 |
| Q12 | 导出/导入格式与 FTS 索引的关系未定义 | 低 | 架构 | 导入后检索不到 |

---

## 2. Q1：写入路径（谁写、何时写、显式 vs 自动）

### 为什么重要

§10 非目标写了「不做自动记忆提取（LLM 抽取）」——这个决定是对的。但文档**没有定义显式写入的入口**：
`IMemoryStore.WriteAsync` 存在，可是「什么时候被谁调用」在整份文档里只有一句「先只做显式记忆 + 会话原文索引」。

后果：P6.1 交付了检索，但**没有生产者在写**。检索一条不存在的记忆永远是空的，而验收用例用的是测试里直接 `WriteAsync` 塞的数据。这会制造一个危险的假象——「检索功能完成」，而真实部署里记忆条数恒为 0。

### 建议方案

明确三档写入，并写进文档：

```text
1. 显式命令（唯一的默认路径）
   /remember <text>            → scope=actor（默认）
   /remember --guild <text>    → scope=guild
   /remember --private <text>  → sensitivity=private
   /forget <id|text>           → DeleteAsync
   这一档不依赖任何外部决策，P6.1 收尾即可做。

2. 会话原文索引（不改记忆表）
   不做「从对话抽取记忆」，但允许 FTS 索引既有 messages 表，
   检索时与 memory_items 结果合并。这满足「会话原文索引」且不引入抽取成本。

3. 自动抽取（明确不做）
   保持 §10 非目标。若将来要做，必须先有 Q4 的度量，否则无法判断抽取是否净收益为正。
```

关键区分：**写入的默认作用域必须是 actor，但「写入」这个动作必须是人显式触发的**。让 bot 自己决定"什么值得记住"是产品决策，不是 harness 决策（对齐 [责任边界](HARNESS_RESPONSIBILITY.md) §2）。

### 需要谁决策

产品（要不要 `/remember` 这个命令形态）+ 架构（会话原文索引是否算 P6.1 范围）。

---

## 3. Q2：去重与冲突

### 为什么重要

同一个事实被说两次，现在的实现是**按 `(scope_id, kind, lower(text))` 精确匹配就返回旧记录**：

```csharp
// SqliteMemoryStore.WriteAsync @ b4e91f8
SELECT ... FROM memory_items WHERE scope_id = $scope AND kind = $kind AND lower(text) = lower($text) LIMIT 1;
... if (reader.Read()) return Read(reader);   // 命中即返回，不再写
```

这套逻辑只处理了「一字不差的重复」，而且**命中后连 `updated_at` 都不更新**。于是：

```text
用户在 1 月说「我住杭州」 → 写入，updated_at=1月
用户在 6 月又说「我住杭州」 → 命中旧记录，updated_at 仍是 1 月
衰减按 updated_at 算 → 这条记忆持续被降权，尽管用户刚刚重申过
```

更糟的是**冲突**没有被处理：

```text
1 月「我住杭州」 → 一条记忆
6 月「我搬到上海了」 → 不是重复，新写一条
结果：两条并存，检索时都可能命中，prompt 里出现互相矛盾的记忆
```

这是长期记忆类系统最典型的失败模式，比"检索不准"更伤人——用户会直接看到 bot 自相矛盾。

### 建议方案

分三层，前两层不依赖任何外部决策：

```text
L1 精确重复（已有）
    同 (scope, kind, lower(text)) → 返回旧记录
    追加：更新 updated_at 与 Confidence（重申即加固）
    理由：语义上「再次说同一句话」= 加强了这条记忆

L2 同键覆盖（需要 kind 带键语义）
    给 WriteAsync 增加可选的 dedupeKey（如 profile.city）
    同 (scope, dedupeKey) 新值 → 覆盖旧值，旧值进 history 或标记 superseded
    理由：结构化事实天然有主键，覆盖是对的语义

L3 自然语言冲突（不自动处理）
    同一 kind 下多条文本相似但内容相反 → 不自动合并
    只在检索注入时按「新者优先 + 全部注入并标注时间」处理
    理由：判定"两条自然语言是否矛盾"需要语义理解，这属于插件内容层，不是 host 机制层
```

**明确不做**：向量相似度去重（会引入 pending 的 embedding 依赖，且阈值无法在没有 Q4 度量的情况下定）。

### 需要谁决策

架构。L2 的 `dedupeKey` 是契约变更，需要在 P6.1 收口前定，否则之后再加会动 schema。

---

## 4. Q3：容量上限与淘汰

### 为什么重要

per-actor 记忆是**只增不减**的：

```text
每个用户每天都在产生记忆（如果 Q1 的写入路径打开）
没有上限、没有 TTL 默认值（expires_at 默认 null）
没有 per-actor 配额
```

三个具体后果：

1. `memory_items` 与 `memory_fts` 各存一份 text（见 Q11），磁盘按 2 倍增长；
2. `SearchAsync` 的 `scope_id IN (...)` 走 `ix_memory_scope`，但命中条目数随用户活跃度线性增长，300ms 预算迟早不够；
3. 没有淘汰 = 旧的错误记忆永久留在库里，且仍可能被检索命中。

文档 §4.1 只说了「只降权不删除」，这对**单条**记忆的排序是对的，但**不等于不做容量管理**。这两件事被混淆了。

### 建议方案

分开处理「软遗忘」与「硬上限」：

```text
软遗忘（已有，不改）
  decay 只降权，条目保留。这是对的：用户可能很久以后又问起旧事。

硬上限（缺，必须补）
  per-actor 条数上限       默认 500
  单条 text 长度上限       默认 4000 字符（超出截断并计数）
  per-scope 总量上限       默认 20000（防一个 guild 撑爆）
  触顶策略：按 (score = 未衰减相关性 × decay) 保留高者
            —— 注意必须用**衰减后**的分数选，否则会淘汰掉用户最近关心的事
  被淘汰条目不静默消失：写 audit 计数 + 原因，用户可 /memory export 后自行留档

TTL
  expires_at 保持 null 默认（不主动遗忘）
  但 /remember 支持 --ttl，由用户显式决定
```

关键设计点：**淘汰必须发生在写入路径，不能在检索路径**。检索路径有 300ms 预算，不允许做全表扫描和重排。

### 需要谁决策

架构（默认数值）+ 运维（是否需要按部署调低上限）。数值本身可以先给默认值，不需要产品拍板。

---

## 5. Q4：检索质量如何度量

### 为什么重要

这是本文最该被先解决的一条，因为它**阻塞所有调参**：

```text
trigram vs 预分词 vs unicode61       —— 没有对照组无法选
衰减半衰期 30 天是否合适               —— 没有数据
maxCandidates=20 / maxInjected=5     —— 拍的数字
RRF 的 k 和候选池大小（P6.3）         —— 更没有依据
```

文档 §9 把「命中率统计」放在 P6.4（可选）。这个排序是错的：**没有度量就无法判断 P6.1 的中文检索是否可用**，而我们已经实测到它不可用（Q8）。

我现在能给的唯一"度量"是定性的：`trigram` 让中文两字词检索为 0。这已经说明问题，但它无法回答"改成预分词后好了多少"。

### 建议方案

不要做成产品级指标，做成**可复现的离线评测夹具**（成本低、收益立即）：

```text
1. 固化一个用例集（YAML，进仓库，随代码评审）
   tests/fixtures/memory-retrieval-cases.yaml
   每例：query / 期望命中 id 列表 / 期望不命中理由
   覆盖：中文两字词、中文三字词、中英混、同义改写、时间衰减、跨 actor 隔离

2. 一个可跑的评测命令（不联网、不调模型）
   natter memory eval --cases <file> --json
   输出：recall@k / mrr / 逐例失败明细

3. 用它做三组对照，结果写回 P6 文档
   tokenizer:     trigram | 预分词+unicode61
   decay:         30d | 90d | off
   candidate:     20 | 50
```

这三组对照做完，Q8/Q9 的取舍就有据了，P6.3 的 RRF 参数也不用再拍。

注意度量口径要先说清：「命中」是"检索返回了期望条目"，不是"模型用了它"。后者需要线上归因（prompt 片段 → 回复是否引用），那是 P6.4，不要在 P6.1 做。

### 需要谁决策

架构（定义用例格式与指标口径）。用例内容可以由实现者填。

---

## 6. Q5：embedding 模型/维度变更后的旧向量

### 为什么重要

§5.3 只说「必须存 `model` 与 `dimensions`，换模型后旧向量不可比，要能识别并重建」，**但没有定义重建流程**。而实际上还有三个更硬的问题文档没提：

```text
1. 维度变了，向量表列宽就得变（sqlite-vec 的 vec0 虚表维度在 CREATE 时固定）
   → 不是「更新一行」，是「建新表 + 搬迁」
2. 同一时刻会有两批向量共存（部分条目已重建、部分未重建）
   → 检索必须能识别并跳过不兼容向量，而不是混算
3. 重建期间检索不能停
   → 降级到 BM25（§8.1 已有这条原则，但没说这是重建期的默认行为）
```

详细的重建流程见 [P6.3 设计 §6](P6_RAG_PLUGIN_DESIGN.md#6-embedding-模型--维度变更的重建流程)，此处只登记「这是 §5.3 的空白」这个事实。

### 需要谁决策

架构。核心决策是：**重建是"在线双写"还是"离线重建后原子切换"**。我建议后者（简单、可回滚），代价是重建窗口内新记忆没有向量。

---

## 7. Q6 / Q7：作用域语义的三个坑

### Q6 术语三套并存

同一个概念在三处有三套名字：

| 位置 | 名字 | 取值 |
| --- | --- | --- |
| 设计文档 §3 | `actor` / `conversation` / `guild` | 3 种 |
| 代码 `MemoryScopeType` | `User` / `Conversation` / `Guild` / `Global` | **4 种** |
| `MemoryScope.Id` 生成的 key | `source\|user\|{id}` | 前缀与枚举名不一致（`user` vs `User`） |

`MemoryRetrievalOptions.DefaultScope = MemoryScopeType.User` —— 所以「默认 actor」在实现里是 `User`。语义上 `User` scope 的 key 是 `{source.Key}|user|{user.Id}`，确实等价于 per-actor（且 `source.Key` 前缀天然实现了 §3.3 的跨平台隔离）。

**这不是 bug，是术语债。** 但代价是真实的：文档说 3 种、代码有 4 种，`Global` 在设计文档 §3 里根本没有出现过。任何按文档做验收的人都会卡在这里。

建议：文档 §3 补一行映射表，承认 `User` == `actor`、`Global` 是既有实现，并说明 `Global` 的定位（见 Q7）。不要改枚举（会破坏已提交的导出格式与 DB 值）。

### Q7 Global/Guild 与隔离断言的冲突（**这是隐私洞**）

§3.4 的硬断言是无条件的：

```text
actor A 的记忆在 actor B 的检索里命中数为 0
```

但 `ScopePredicate` 在 `query.Scope is null` 时是**四个 scope 的并集**：

```csharp
// b4e91f8
scope_id IN ($s0 /* user      */, $s1 /* conversation */, $s2 /* guild */, $s3 /* global */)
```

而 `Global` 的 key 是 `{source.Key}|global` —— **不含 actor**。`Guild` 同理（不含 actor）。

于是：

```text
任何写入 Global scope 的记忆 → 该 source 下所有 actor 都能检索到
任何写入 Guild  scope 的记忆 → 该 guild 下所有 actor 都能检索到
```

这在语义上是**故意的**（guild 记忆本该共享），但它让 §3.4 那句无限定的断言无法成立。也就是说：**断言的表述是错的，不是实现是错的**。

真正要保证的是：

```text
scope=actor(User) 时：A 的记忆不出现在 B 的结果里     ← 可断言
scope=guild 时：只有同一 guild 的成员可见              ← 可断言，需换判据
scope=global 时：没有 actor 隔离可言                  ← 不应承诺
```

建议：
1. §3.4 改写为三条分作用域的断言，并明确 `Global` 定位；
2. **默认禁止写 Global**（当前 `MemoryWriteRequest` 允许任何调用方传 `ScopeType=Global`）；
3. 验收用例按三条分别断言，不要用一句"命中数为 0"覆盖全部。

### 需要谁决策

架构（改文档措辞 + 是否限制 Global 写入）。这是我认为**最该优先修的一条**——它关系到对外承诺的准确性。

---

## 8. Q8–Q12：实现层面已可复现的问题

### Q8 中文 trigram 两字词零命中（**已实测**）

`memory_fts` 用 `tokenize='trigram'`（`SqliteSchema` v5）。实测（SQLite 3.53.3，Microsoft.Data.Sqlite 10.0.14）：

```text
trigram  MATCH '苹果'(2字)      → 0    ← 中文最常见的两字词完全检不到
trigram  MATCH '吃苹果'(3字)    → 1
trigram  MATCH '杭州'(2字)      → 0
trigram  MATCH '西湖区'(3字)    → 1
trigram  MATCH '苹果今天'(4字)  → 1
unicode61 原表 MATCH '苹果'      → 0    ← 整句被当成单 token
unicode61 + 预分词空格列 '苹果'  → 1    ← 唯一有效的中文方案
```

**trigram 对中文按 3 字符滑窗，短于 3 字符的查询永远不匹配。** 而中文双字词（苹果、杭州、喜欢、加班）是最高频的检索词。

§4.3 把 trigram 列为推荐方案、把预分词列为「退化方案」，这个优先级是反的。实测结论：**中文场景应首选预分词 + unicode61**（方案 2），trigram 只适合英文/混合。

补充代价：预分词需要额外一列（`text_tokens`），写入时切词，检索时对 query 同样切词。切词器不需要词典（按空格优先 + 单字回退即可起步），但**必须与查询侧同一套逻辑**，否则永远匹配不上。

### Q9 衰减只作用于候选池之内

```csharp
// b4e91f8 SearchFtsAsync
ORDER BY rank, m.updated_at DESC
LIMIT $limit;                       // ← 候选池先按纯 BM25 截断
...
scored.Add((item, (-bm25) * decay)); // ← 再在内存里用 decay 重排
```

候选池由**未衰减的 BM25** 决定，decay 只在池内重排。后果：

```text
一条 3 年前但极相关的记忆 → BM25 排第 1 → 进池 → 被 decay 压到末尾（合理）
一条相关度中等但很新的记忆 → BM25 排第 21（limit=20）→ 根本没进池 → 永远看不见
```

也就是说「最近发生的事情也该被优先」这个意图，只在池内生效。修法很简单：SQL 取 `LIMIT $limit * 4`（可配 `CandidatePoolMultiplier`），内存重排后再 `Take(limit)`。这个改动只动一处，无 schema 变更。

### Q10 两个实现语义不一致

```text
SQLite   : FTS5 MATCH + bm25 × decay，中文 2 字词 0 命中（Q8）
InMemory : token 子串包含 + 命中率 × decay，中文 2 字词 1 命中
```

```csharp
// MemoryScoring.Score —— InMemory 用 Contains
var matches = queryTokens.Count(token => haystack.Contains(token, StringComparison.Ordinal));
```

所以同一个查询，`--state-dir` 有和没有会给出不同结果。task-54 的验收要求「InMemory 与 Sqlite 两实现语义一致」，现在**只有英文的简单查询一致**。

这条不一定要"修成完全一致"（SQLite 有 IDL 优势，InMemory 是纯内存不该做索引），但**必须文档化**：哪些查询两者等价、哪些不等价、测试该在哪个实现上跑。否则 CI 绿、生产错。

### Q11 FTS 命中为空时静默回退 recency

```csharp
var fts = await SearchFtsAsync(...);
if (fts.Count > 0) return fts;
// 注释原文：No relevant hit: fall back to recent entries (legacy behaviour)
```

回退本身是对的（保证回合有记忆可用）。问题是**完全不可观测**：

```text
"用户问了 A，检索没命中，返回了 5 条无关的最新记忆"
prompt 里看不出这是回退结果还是真命中
MemoryRetrievalMetrics 只统计 timeout / 三处 truncation，没有 fallback 计数
```

后果：Q4 的度量做不出来（无法区分「检索失败」和「检索成功但结果平庸」），而这正是调 tokenizer 最需要区分的信号。

建议：加 `MemoryRetrievalMetrics.RecencyFallback()` 计数，并在 `ContextDiagnostics` 里给一个 `MemoryRetrievalMode = fts | recency-fallback | timeout | empty` 字段。**只加计数，不改变行为**，成本极低。

### Q12 导出/导入与 FTS 索引

`ExportAsync` 只导 `MemoryItem`，`ImportAsync` 只写 `memory_items`。FTS 靠触发器同步（`memory_fts_ai/au/ad`），所以 import 后索引会自动补上——这点是对的，实测触发器路径可用。

但有一个遗漏：**`ON CONFLICT(memory_id) DO UPDATE` 只更新了部分列**（text/data_json/confidence/sensitivity/updated_at/expires_at/version），触发器 `memory_fts_au` 是 `AFTER UPDATE OF text`，只在 `text` 变化时触发。若导入时 text 相同而其它列变化，索引无需更新（正确）。若 text 变化，触发器更新（正确）。

所以 Q12 实际**没有 bug**，但 `MemoryExport.SchemaVersion` 恒为 `1` 且没有任何版本演进策略。等 P6.1 的 `dedupeKey`（Q2 L2）或 Q3 的软删除标记加进去，导出格式就要变了。建议现在就约定：**schemaVersion 必须随字段变更递增，导入端按版本分支**。这是零成本的预防。

---

## 9. 三条建议的决策顺序

按「阻塞别人」的程度排序：

```text
1. Q7 + Q6     改文档措辞，明确 Global 不承诺隔离，User==actor 映射
                → 零代码，立刻可做，影响对外承诺准确性
2. Q4          评测夹具 + natter memory eval
                → 中等成本，解锁 Q8/Q9 的取舍，也是 P6.3 的参数依据
3. Q1          /remember 显式写入命令
                → 没有它，检索在生产里永远是空的
```

Q2/Q3/Q5 可以稍后，但**必须在 P6.1 宣布完成之前定 `dedupeKey` 与容量上限**，因为它们要动 schema 与导出格式。

---

## 10. §11 验收标准核对表

基线 `b4e91f8`。判定：**已实现**（代码+测试都在）/ **部分实现**（有代码但语义未达成或不可观测）/ **未开始**。

### 10.1 契约与作用域

| # | 验收标准 | 判定 | 证据（`b4e91f8`） |
| --- | --- | --- | --- |
| 1 | `IMemoryStore` 支持三种作用域，默认 actor | ✅ 已实现 | `MemoryQuery.Scope`（可空，null=并集）+ `MemoryWriteRequest.ScopeType`；`MemoryRetrievalOptions.DefaultScope = MemoryScopeType.User`；枚举为 4 值 `User/Conversation/Guild/Global`（见 Q6 术语差异） |
| 2 | actor A 的记忆在 actor B 检索结果中命中数为 0（硬断言） | ⚠️ 部分实现 | `MemoryScope.Id(User)` = `{source.Key}\|user\|{user.Id}` 且 `ScopePredicate` 带 `scope_id IN (...)` → **User scope 隔离成立**；但 Guild/Global 不含 actor（Q7），断言不能覆盖全部作用域，文档表述需修正 |
| 3 | 导出/导入格式两条线通用，换实现不丢数据 | ⚠️ 部分实现 | `MemoryExport(1, ExportedAt, Items)` + `ImportAsync`（`Overwrite` 走 upsert）存在；两线共用 `MemoryItem`。但无版本演进策略（Q12），且 P6.3 向量库不在导出范围内（设计如此，需写明「换到 RAG 需重建向量」） |

### 10.2 轻量检索

| # | 验收标准 | 判定 | 证据 |
| --- | --- | --- | --- |
| 4 | FTS5 走 `SqliteSchema`（单一 DDL owner 未破） | ✅ 已实现 | `SqliteSchema.CurrentVersion = 5`；`CREATE VIRTUAL TABLE memory_fts USING fts5(memory_id UNINDEXED, text, tokenize=...)`；触发器 `memory_fts_ai/au/ad` + 首次回填；无第二处 DDL |
| 5 | 中文检索质量有可复现的对照用例（trigram vs 单字） | ❌ 未开始 | 无对照夹具；`MemoryRetrievalTests.cs` 存在但无 trigram vs 预分词 的量化对照。**实测已证明 trigram 对两字中文词 0 命中（Q8）** |
| 6 | 时间衰减按半衰期降权，只降不删 | ✅ 已实现 | `decay = exp(-ageDays/halfLife)`，`(-bm25) * decay`，`ORDER BY score DESC`；无删除路径。⚠️ 但衰减只作用于候选池内（Q9） |
| 7 | 300ms 超时降级为空结果并计数，回合不失败 | ✅ 已实现 | `MemoryRetrievalOptions.RetrievalTimeout = 300ms`；`HostContextPipeline` 用 `CancelAfter` + `catch` → `memoryItems = []` + `_memoryMetrics.Timeout()` + warning 日志；回合继续 |

### 10.3 注入

| # | 验收标准 | 判定 | 证据 |
| --- | --- | --- | --- |
| 8 | memory 出现在 `PromptPlan` 片段列表（id/来源/顺序/hash） | ✅ 已实现 | `BuildMemoryFragments` → `PromptFragment("host#memory.summary", Order 200)` 与 `host#memory.recall.N`（Order 210+），`Source = PromptFragmentSource.Memory`，经 `PromptRequest.MemoryFragments` 进入 composer（hash 来自 fragment 源文本） |
| 9 | 可通过 `disabledFragments` 关掉记忆片段 | ✅ 已实现 | `profile.Prompt.DisabledFragments` 过滤 `host#memory.summary` 与各 recall 片段（`Where(fragment => !disabledMemory.Contains(fragment.Id))`） |
| 10 | 受 provider 配额约束，不挤占其他片段 | ⚠️ 部分实现 | `MaxInjected=5`、`MaxTokens=800` 生效；片段 `MaxCharacters = PromptCompilerDefaults.FragmentMaxCharacters`。但记忆是 **host 内部片段**，走 `PromptRequest.MemoryFragments` 而非 `prompt.fragment` provider 聚合路径 → **不受每 provider 16 个 / 8000 字符配额约束**，只受全局 64 / 32000 约束。与 §7「受 provider 配额约束」的字面要求有差距 |

> #10 的判定说明：设计意图（不挤占 PromptLibrary / StateVitals）**基本达到**（5 条 × 800 token 远小于全局配额），但实现路径与 §7 描述的「memory 变成 prompt.fragment 的一个来源，走聚合器」不同。建议改文档措辞：memory 是 host 内置片段源，不参与插件配额仲裁——这实际上更合理（host 机制 vs 插件内容的边界），但要说清。

### 10.4 端口

| # | 验收标准 | 判定 | 证据 |
| --- | --- | --- | --- |
| 11 | 两个贡献类型注册/注销/权限门控与既有形态一致 | ✅ 已实现 | `PluginContributionTypes.EmbeddingProvider = "embedding.provider"` / `RerankProvider = "rerank.provider"` 且已进 `All`；`NpsContributionRegistry` / `NpsPluginRuntime` 接线；`EmbeddingPortTests.cs` 存在 |
| 12 | 能力元数据被调用方真正用于分批 | ✅ 已实现 | `IEmbeddingProvider.MaxBatchSize` / `MaxInputTokens` + host 侧分批与截断工具（`EmbeddingContracts.cs` 含 batcher/truncation）；`EmbeddingPortTests.cs` 覆盖 100 条 / batch 25 → 4 批且顺序保持 |
| 13 | embedding 缓存命中时不重复调用 | ✅ 已实现（InMemory 层） | `IEmbeddingCache` + InMemory 实现，键 `(model, dimensions, sha256(text))`；SQLite 实现按设计留给 RAG 插件（P6.3） |
| 14 | 提供方抛错/超时时检索降级为 BM25，不阻塞 | ⚠️ 部分实现 | 端口层：任一批失败 → 整体失败，由调用方降级（测试覆盖）。检索层：**当前无 RAG 插件时不存在向量路径**，因此「降级为 BM25」还没有真实执行点；且 FTS 失败会 `catch (SqliteException)` 回退 recency（Q11），不是真正的 BM25 降级语义。需在 P6.3 接线时补 |

### 10.5 回归

| # | 验收标准 | 判定 | 证据 |
| --- | --- | --- | --- |
| 15 | 未装 RAG 插件时行为与现在完全一致 | ⚠️ 部分实现 | 无 RAG 插件时代码路径不含向量，成立。但 **FTS 已改变检索行为**（从纯 recency 变为 BM25 + 回退），这不是"与现在完全一致"。默认 profile 下 `memory` 片段也是新增的 prompt 内容 → 需单独确认默认输出基线 |
| 16 | 既有全部用例不回归 | ✅ 已实现（待复核） | `MemoryRetrievalTests.cs` / `EmbeddingPortTests.cs` / `OpenAiEmbeddingProviderTests.cs` 均在 HEAD；本轮只读，未执行测试，回归数字需由 task-54/55 的汇报补齐 |

### 10.6 汇总

```text
已实现       10 项  (#1 #4 #6 #7 #8 #9 #11 #12 #13 #16)
部分实现      5 项  (#2 #3 #10 #14 #15)
未开始        1 项  (#5 中文对照用例)
              ─────
              16 项
```

> #6（时间衰减）判"已实现"，但其排序口径另有一个缺陷登记为 Q9，两者不冲突：#6 验的是"降权且不删"，Q9 说的是"衰减不参与候选生成"。

三项最该补：**#5（中文对照，已实测失败）**、**#14（BM25 降级无真实执行点）**、**#10（配额路径与文档不符）**。

---

## 11. 一句话总结

> **P6.1 的机制是完整的（FTS5 进单一 DDL owner、300ms 降级、片段化注入、作用域过滤都在），缺的是设计语义：谁写记忆、重复怎么说、长到多大、中文检索如何证明有效、以及"跨 actor 隔离"这个承诺在 Guild/Global 存在时到底承诺了什么。**
