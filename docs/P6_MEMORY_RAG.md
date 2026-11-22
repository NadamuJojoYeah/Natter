# P6 Memory：轻量检索与 RAG 两条线

> **状态**：设计已定 · 分阶段实现
> **日期**：2026-11-22
> **定位**：把 memory 从「精确匹配 + 最近窗口」升级为「相关度检索」，并在其上叠加可选 RAG。默认必须轻量。
> **关联**：[Prompt Compiler](PROMPT_COMPILER.md)、[Harness 责任边界](HARNESS_RESPONSIBILITY.md)、[State & Vitals](STATE_VITALS_DESIGN.md)

---

## 0. 一句话

> **一个契约，两条实现线**：轻量线用 SQLite FTS5 做相关度检索；RAG 线在此基础上叠加向量与 rerank。默认只装轻量线。

---

## 1. 两条线

```text
轻量线  memory-lite      FTS5/BM25 + 时间衰减 + 作用域过滤       零新增外部依赖
RAG 线  memory-rag       BM25 + 向量混合检索 + 可选 rerank       需要 embedding 提供方
```

**RAG 是轻量线的超集，不是替代。** 没有向量时退回 BM25，有向量时混排。两条线共用同一份导出格式，换实现不丢数据。

---

## 2. 一个契约，两个实现

不新增概念，复用既有机制：

```text
memory.store             已是 NPS 贡献类型
ContextComponentCatalog  按 owner#id 注册
profile.memory.store     按 profile 选实现
```

因此：

```text
IMemoryStore（唯一契约）
├── host#sqlite-fts            轻量线，宿主内置
└── natter.plugin.memory-rag   RAG 线，独立插件
```

**不做成两套契约**：否则导出/导入格式不兼容，换实现就丢记忆——这是最难挽回的一类问题。

---

## 3. 作用域：默认 per-actor

这是 P6 最有价值的一项，也是最需要早定的一项。

```text
conversation   当前会话（现状，等于「短期记忆」）
actor          同一用户的跨会话长期记忆   ← 默认
guild          整个服务器共享
```

### 3.1 为什么默认 actor

「我记得你上次说过」是长期记忆的真正用例。会话级记忆在开新会话后就断了，等于没有长期记忆。

### 3.2 键设计

复用刚加的 `ConversationScope`（`PromptFragmentConstants` 那边已就位）：

```text
SourceKey   protocol:platform:selfId      结构化，不反解析
ActorId     平台用户 id                     跨会话的锚点
```

存储键：

```text
(scopeKind, scopeKey, memoryId)
   scopeKind = conversation | actor | guild
   scopeKey  = 对应 id
```

### 3.3 跨平台身份：不是 bug

同一人在 Discord 与钉钉是不同 `ActorId`。默认应视为两个不同的人——真人也不知道这两个身份是同一人。

身份关联是独立 opt-in 功能，不在 P6 范围。

### 3.4 隐私边界

per-actor 记忆会**进入所有该用户的会话**，但**绝不能进入其他用户**的 prompt。

必须有一条硬断言：actor A 的记忆在 actor B 的检索里命中数为 0。

---

## 4. 轻量线：FTS5

### 4.1 检索公式

```text
score = BM25(text) × decay(age) × scopeMatch
```

- **BM25**：SQLite FTS5 内置，无需扩展
- **decay**：`exp(-age / halfLife)`，半衰期可配（默认 30 天）；只降权不删除
- **scopeMatch**：先按作用域过滤再加分，不是过滤后补分

### 4.2 schema 归属

FTS5 虚表进 **`SqliteSchema`**，因为它是宿主 memory 的检索能力，不是插件内容。

这符合我们已立的规矩：**单一 DDL owner**。RAG 的向量表则相反，归插件自己的库（见 §5.4）。

### 4.3 中文分词

FTS5 默认 `unicode61` 分词器对中文按**单字**切分，效果差。

必须先核实可用方案，按优先级：

```text
1. FTS5 trigram tokenizer（SQLite 3.34+，对中文按三字滑窗，无需扩展）
2. 预分词写入（应用层切词后存空格分隔副本列）
3. 社区分词器扩展（需加载 native extension，systemd 下要额外处理）
```

**推荐 trigram**：无需扩展、无部署负担，代价是索引体积与短查询精度。若实测不满意再退化到方案 2。

---

## 5. RAG 线

### 5.1 混合检索

```text
候选池 = BM25 top-K ∪ 向量 top-K
重排   = RRF（Reciprocal Rank Fusion）或加权分数
可选   = rerank 模型精排
```

RRF 的好处：不需要把 BM25 分数和余弦相似度归一化到同一量纲，按排名融合，稳健。

### 5.2 框架选型

| 方案 | 依赖 | 适用 | 结论 |
| --- | --- | --- | --- |
| **`Microsoft.Extensions.VectorData`（MEVD）** | NuGet | 统一接口、后端可换 | **抽象层选它** |
| sqlite-vec | SQLite 扩展 | 单机零外部服务 | **首选默认后端** |
| Qdrant | 独立服务 | 多实例、向量量大 | 可选后端 |
| pgvector | 已有 Postgres | 已用 PG 的部署 | 可选后端 |
| Semantic Kernel 全家桶 | 很大 | — | **不用**，只取 VectorData |

**为什么不用 SK**：我们只需要「存向量 + 近似检索」。SK 的 Kernel/Planner/Plugin 体系与我们的插件模型重复。为了一个向量 API 引入整套框架，是拿架构换便利。

**必须先核实**：当前版本 MEVD 是否提供官方维护的 SQLite 连接器（记忆里 Qdrant/Redis/Azure 是官方的，SQLite 侧可能是社区包或 SK 连接器）。**先去 NuGet 核实再定，不要按印象写。**

### 5.3 向量表结构（插件自己的库）

```text
<state>/plugins/natter.plugin.memory-rag/vectors.db
  memory_id      TEXT PRIMARY KEY   -- 与 IMemoryStore 的 memoryId 一致
  scope_kind     TEXT
  scope_key      TEXT
  model          TEXT               -- embedding 模型标识
  dimensions     INTEGER
  vector         BLOB               -- 量化后的 float32/int8
  updated_at     TEXT
```

**必须存 `model` 与 `dimensions`**：换 embedding 模型后旧向量不可比，要能识别并重建，而不是静默混用。

### 5.4 为什么 RAG 用插件的库

向量是 RAG 插件的内容，不是宿主能力。进 `natter.db` 会破坏单一 DDL owner——这与 StateVitals 用自己库是同一条规则。

---

## 6. 端口设计：embedding 与 rerank 必须可扩展

你的要求是对的：**这部分要像 DeepSeek provider 一样可扩展**。

### 6.1 两个新贡献类型

```text
embedding.provider    文本 → 向量
rerank.provider       查询 + 候选 → 重排分数
```

参照 `chat.responder` 的既有形态：插件实现接口，宿主持有目录，profile 按 `owner#id` 选。

### 6.2 契约草案

```csharp
public sealed record EmbeddingRequest(
    IReadOnlyList<string> Inputs,
    string? Model = null,
    int? Dimensions = null);

public sealed record EmbeddingResult(
    IReadOnlyList<ReadOnlyMemory<float>> Vectors,
    string Model,
    int Dimensions,
    int? PromptTokens);

public interface IEmbeddingProvider
{
    string Id { get; }
    int MaxBatchSize { get; }
    int MaxInputTokens { get; }

    ValueTask<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);
}

public sealed record RerankCandidate(string Id, string Text);

public sealed record RerankResult(IReadOnlyList<(string Id, double Score)> Ranked);

public interface IRerankProvider
{
    string Id { get; }
    int MaxCandidates { get; }

    ValueTask<RerankResult> RerankAsync(string query, IReadOnlyList<RerankCandidate> candidates, CancellationToken ct = default);
}
```

### 6.3 必须暴露的能力元数据

`MaxBatchSize` / `MaxInputTokens` / `MaxCandidates` 不是装饰——**调用方必须据此分批**，否则 DashScope 这类接口会直接报错。

### 6.4 与 DashScope（qwen-embedding-v4）对齐时需要核实的参数

据我对 DashScope 的了解，以下需要在接入前对着官方文档核实，**不要按印象写死**：

```text
模型标识        qwen-embedding-v4 的确切 API 名
维度            是否支持自定义（1024/1536/2048…），默认是多少
单批条数上限     超出需分批
单条 token 上限  超出需截断
是否支持 truncate 策略
rerank 模型      对应模型名与返回格式
```

这些参数直接决定 `MaxBatchSize` / `MaxInputTokens` 的取值和分批逻辑。

### 6.5 embedding 缓存

同一文本不应重复 embedding（成本 + 延迟）。必须有一层按 `(model, dimensions, sha256(text))` 的缓存，落在插件自己的库里。

---

## 7. 注入路径：统一到 `prompt.fragment`

这是 P6 的一笔技术债。

现状：

```text
memory      走 HostContextPipeline，直接拼进消息
prompt 片段  走 PromptComposer，有 id/来源/顺序/hash/配额
```

两套并存的直接后果：**记忆在 prompt 诊断里看不见**。我们花大力气让 `PromptPlan` 输出片段元数据，但「这条记忆为什么进了 prompt」查不到。

P6 要求：memory 也变成 fragment —— 可关闭、可排序、进 planHash、受 provider 配额约束。

顺带的好处：memory 与 PromptLibrary、StateVitals 三者机制一致，用户可以用同一个「关掉某段」的操作管理它们。

---

## 8. 三个硬约束

### 8.1 检索超时降级

```text
检索预算 300ms
超时 → 返回空记忆 + 计数，不阻塞回合
```

**记忆是增强，不是前置条件。** 向量库挂了、embedding 限流了，bot 还得能说话。

### 8.2 候选与 token 预算

```text
maxCandidates    默认 20（检索）
maxInjected      默认 5（进 prompt）
maxTokens        默认 800
```

超出截断并**在诊断里标记**，不要静默丢弃。

### 8.3 写路径不阻塞

embedding 在写路径上会加延迟。做法：

```text
先落原文（同步，快）
再异步入向量索引（后台）
```

检索时若某条记忆还没有向量，用 BM25 结果兜底——混合检索天然容忍这种情况。

---

## 9. 分期

### P6.1 契约与轻量检索（不依赖任何外部决策）

- `IMemoryStore` 增加作用域（conversation / actor / guild）
- `SqliteSchema` 加 FTS5 虚表（trigram 分词）
- BM25 + 时间衰减 + 作用域过滤
- 候选/token 预算与 300ms 超时降级
- **memory 改走 `prompt.fragment`**
- 跨 actor 隔离的硬断言

### P6.2 embedding / rerank 端口

- 两个新贡献类型 + 契约 + 目录注册
- 能力元数据（batch/token/candidate 上限）
- embedding 缓存（按 model+dimensions+text hash）
- 一个参考实现（OpenAI 兼容，覆盖 DashScope 兼容模式）
- **不做真实联网调用**，用 fake 提供方测分批与超时

### P6.3 RAG 插件

- MEVD 抽象 + 默认后端（sqlite-vec 或 Qdrant，按核实结果）
- 混合检索 RRF
- 可选 rerank
- 自己的向量库
- 模型/维度变更时的重建路径

### P6.4 观感与调优（可选）

- 命中率统计（哪些检索真被模型用上）
- 衰减半衰期与候选数的可观测调整
- 记忆时间轴视图（放会话控制台，不放进 WebConsole）

---

## 10. 非目标

- 不做跨平台身份关联
- 不做全局（per-bot）记忆——那是 StateVitals 的领域
- 不做自动记忆提取（LLM 抽取）：成本高、易出错，先只做显式记忆 + 会话原文索引
- 不做内存数据库/图数据库
- 不接管 StateVitals 的职责

---

## 11. 验收标准

**契约与作用域**
- [ ] `IMemoryStore` 支持三种作用域，默认 actor
- [ ] actor A 的记忆在 actor B 的检索结果中命中数为 0（硬断言）
- [ ] 导出/导入格式两条线通用，换实现不丢数据

**轻量检索**
- [ ] FTS5 走 `SqliteSchema`（单一 DDL owner 未破）
- [ ] 中文检索质量有可复现的对照用例（同一查询，trigram vs 单字）
- [ ] 时间衰减按半衰期降权，只降不删
- [ ] 300ms 超时降级为空结果并计数，回合不失败

**注入**
- [ ] memory 出现在 `PromptPlan` 片段列表里（id/来源/顺序/hash）
- [ ] 可通过 disabledFragments 关掉记忆片段
- [ ] 受 provider 配额约束，不挤占其他片段

**端口**
- [ ] 两个贡献类型注册/注销/权限门控与既有形态一致
- [ ] 能力元数据被调用方真正用于分批
- [ ] embedding 缓存命中时不重复调用
- [ ] 提供方抛错/超时时检索降级为 BM25，不阻塞

**回归**
- [ ] 未装 RAG 插件时行为与现在完全一致
- [ ] 既有全部用例不回归

---

## 12. 一句话总结

> **默认轻量（FTS5 + 衰减 + per-actor）不给部署加任何外部依赖；RAG 作为可选超集叠加，embedding/rerank 走插件贡献点保持可扩展；记忆统一成 prompt 片段，这样它才在诊断里看得见。**
