# P6.3 RAG 插件设计（memory-rag）

> **状态**：设计（未实现，不含代码）
> **日期**：2026-11-22
> **基线**：`b4e91f8`（P6.1 轻量检索 + P6.2 embedding/rerank 端口均已提交）
> **定位**：在 FTS5 轻量线上叠加向量与 rerank，作为 `IMemoryStore` 的可选超集实现。
> **关联**：[P6 设计](P6_MEMORY_RAG.md)、[设计缺口](P6_MEMORY_OPEN_QUESTIONS.md)、[Harness 责任边界](HARNESS_RESPONSIBILITY.md)

---

## 0. 本文要回答的六个问题

```text
1. MEVD 是否提供官方维护的 SQLite 连接器？
2. 默认后端选 sqlite-vec 还是 Qdrant，部署代价各是什么？
3. 向量表结构与 schema 版本怎么定？
4. 混合检索的 RRF 参数与候选池多大？
5. embedding 模型/维度变更时旧向量怎么办？（P6 文档 §5.3 的空白）
6. rerank 接在哪一步，失败怎么降级？
```

§1 是**核实结论**（有据可查），§2–§7 是设计决定，§8 是风险，§9 是验收。

---

## 1. MEVD 核实结论（NuGet 实测，2026-11-22）

### 1.1 抽象包：官方维护，存在

| 项 | 核实结果 |
| --- | --- |
| 包名 | `Microsoft.Extensions.VectorData.Abstractions` |
| 最新版 | **11.0.1** |
| 与项目对齐的版本 | **10.11.1**（项目 `Microsoft.Extensions.AI` = 10.11.0） |
| 作者 / 仓库 | Microsoft · `github.com/dotnet/extensions` · MIT |
| 依赖 | `Microsoft.Extensions.AI.Abstractions`，10.11.1 要求 10.11.1；11.0.1 要求 11.0.1 |
| 支持 TFM | net10.0 / net8.0 / netstandard2.0 / net462（11.0.1 另有 net11.0） |

**版本硬约束**：选 11.0.1 会把 `Microsoft.Extensions.AI.Abstractions` 从 10.11.0 拉到 11.0.1。本项目 `ManagePackageVersionsCentrally` + `CentralPackageTransitivePinningEnabled` 均为 true，这是一次全解决方案级版本变动，不是插件局部改动。**若要引入 MEVD，必须选 10.11.1 线。**

### 1.2 官方 SQLite 连接器：**不存在**

这是本设计最重要的一条核实结论，且与 P6 文档 §5.2 的猜测不同：

```text
Microsoft 官方（author=Microsoft / owner=SemanticKernel 或 dotnetframework）发布的
MEVD 相关包只有两个：
  Microsoft.Extensions.VectorData.Abstractions     ← 抽象
  Microsoft.Extensions.VectorData.ConformanceTests  ← provider 一致性测试
没有任何 SQLite provider。
```

微软官方文档（`learn.microsoft.com/dotnet/ai/vector-stores/overview`）的措辞是：provider 是**独立包**（"separate provider packages provide implementations for specific vector databases"），示例直接用 `CommunityToolkit.VectorData.InMemory`。文档提到的目标数据库是 Azure SQL/SQL Server、Cosmos DB、PostgreSQL 和专业向量库——**SQLite 不在其中**。

MEVD 生态的 provider 实测清单：

| 包 | 版本 | 归属 | 备注 |
| --- | --- | --- | --- |
| `CommunityToolkit.VectorData.Qdrant` | 1.0.1 | .NET Community Toolkit（dotnetfoundation + Microsoft.Toolkit） | 社区维护，非 Microsoft 官方 |
| `CommunityToolkit.VectorData.PgVector` | 1.0.2 | 同上 | |
| `CommunityToolkit.VectorData.AzureAISearch` | 1.0.1 | 同上 | |
| `CommunityToolkit.VectorData.Redis` | 1.0.0 | 同上 | |
| `CommunityToolkit.VectorData.SqlServer` | 1.0.1 | 同上 | 走 SQL Server 原生 vector 类型 |
| `CommunityToolkit.VectorData.AzureCosmosDB` | 1.0.2 | 同上 | |
| `CommunityToolkit.VectorData.InMemory` | 1.0.1 | 同上 | 原型用 |
| **`CommunityToolkit.VectorData.Sqlite`** | — | — | **不存在（NuGet 404）** |
| `Qavren.Edge.VectorData` | 1.0.1 | Qavren Solutions LLC（第三方厂商） | **基于 sqlite-vec + FTS5，自带 RRF 混合检索** |

### 1.3 SK 侧 SQLite 连接器：只有 preview，从未 GA

| 项 | 核实结果 |
| --- | --- |
| 包名 | `Microsoft.SemanticKernel.Connectors.SqliteVec` |
| 可用版本 | **全部是 pre-release**：`1.52.0-preview` … `1.74.0-preview`（26 个版本，无一个 GA） |
| 结论 | 不能作为生产依赖 |

> 这条很关键：记忆里"SK 有 SQLite 连接器"是真的，但**它从未发布稳定版**。把它写进设计并当作既有能力，会在实现时卡住。

### 1.4 对 P6 文档 §5.2 的修正建议

```text
原文：| Microsoft.Extensions.VectorData（MEVD）| NuGet | 统一接口、后端可换 | 抽象层选它 |
      必须先核实：当前版本 MEVD 是否提供官方维护的 SQLite 连接器

核实后：
- 抽象层可用，但**官方 SQLite 连接器不存在**。
- 选 sqlite-vec 作后端时，MEVD 只是「未来换 Qdrant 的接口预留」，
  第一个 provider 必须自己写（或引第三方 Qavren）。
- 因此 P6.3 v1 建议**不引入 MEVD**，理由见 §3.4。
```

---

## 2. 运行时能力核实（决定后端可行性）

在 `Microsoft.Data.Sqlite 10.0.14` + `SQLitePCLRaw.lib.e_sqlite3 2.1.13` 上实测：

| 能力 | 结果 | 影响 |
| --- | --- | --- |
| `sqlite_version()` | **3.53.3** | 远超 trigram 所需的 3.34 |
| `ENABLE_FTS5` | **1（已编译）** | 轻量线的 FTS5 可靠 |
| `load_extension(...)` | **`not authorized`** | 扩展加载**存在但默认关闭**，必须先 enable |
| 原生符号 `sqlite3_enable_load_extension` / `sqlite3_load_extension` | **已导出** | 可通过 SQLitePCLRaw 调用打开 |
| sqlite-vec 是否内建 | **否**（`vec0` 符号 0 命中） | 必须作为外部扩展加载 |

**结论**：sqlite-vec 在本项目的 SQLite 上**可行，但不是纯 SQL 可得**：

```text
必须先 sqlite3_enable_load_extension(db, 1)
再 sqlite3_load_extension(db, "<path>/vec0.so", null, &err)
两者都要通过 SQLitePCL.raw 的原生调用，Microsoft.Data.Sqlite 没有公开托管 API
→ 需要一处 P/Invoke 包装（约 30 行），且要处理 native 库路径解析
```

这是我建议把 sqlite-vec 的**加载器做成插件内的一个小模块**（而非散落在 store 里）的原因。

---

## 3. 默认后端选型

### 3.1 候选对比

| 维度 | sqlite-vec | Qdrant |
| --- | --- | --- |
| 外部服务 | **无**（进程内扩展） | 需要独立进程 / 容器 |
| 新增端口 | 无 | 6333（HTTP）/ 6334（gRPC） |
| 部署代价 | 一个 `.so` + 一处原生加载 | 一个 systemd unit 或容器 + 数据目录 + 备份 + 健康检查 + 升级流程 |
| 内存 | 随进程 | 独立进程，默认需预留几百 MB |
| 索引 | **暴力搜索（chunk-based），无 ANN** | HNSW（真 ANN） |
| 数据量舒适区 | 万级～十万级（需实测） | 百万级 |
| 多实例共享 | 不能（每实例一份 db） | 能 |
| 运维可见性 | 一个文件 | 一个服务（要好，也要养） |
| 崩溃影响面 | 影响宿主进程 | 独立，可重启 |

### 3.2 选型决定：默认 sqlite-vec，Qdrant 作为 opt-in

理由按优先级：

```text
1. P6 §0 的硬约束是「默认必须轻量」。引入 Qdrant 就违反了这条，
   而这条是整个 P6 设计的起点（默认零外部依赖）。
2. 记忆的规模上限由 Q3（容量上限）决定。若 per-actor 上限 500 条、
   guild 20000 条，单机向量总量在十万级，sqlite-vec 的暴力搜索可接受。
3. 单机部署是当前唯一形态（deploy/ 只有 systemd + docker 单体）。
   多实例共享向量库是没有被验证过的需求。
4. Qdrant 的运维成本不是「装一下」：备份、升级、监控、证书、
   与宿主的启动顺序依赖 —— 这些会持续消耗维护预算。
```

反向条件（什么时候必须换 Qdrant）：

```text
- 单机向量量级持续 > 50 万，且检索 p95 超过预算
- 出现多实例共享记忆的真实需求（当前不存在）
- 需要向量库级别的过滤/聚合能力（如按时间范围 + 标签的复合检索）
```

这些条件写进文档，将来有据可依，而不是"感觉该换了"。

### 3.3 sqlite-vec 的部署代价（systemd 细节）

这是本设计最容易被低估的部分。三件事必须做对：

**(a) native 库随插件分发**

```text
<state>/plugins/natter.plugin.memory-rag/
  vectors.db
  lib/linux-x64/vec0.so          ← 随插件包分发，或首次启动下载（离线部署不能下载）
  lib/linux-arm64/vec0.so
```

- 权限：目录 `0700`、`.so` `0600`（只需属主可读；**不需要** 可执行位，dlopen 不要求 x 位）
- 与 `MemoryScope`/StateVitals 一致：插件状态目录 0700，符合既有惯例
- **不要**放 `/tmp`：`PrivateTmp=yes` 下路径不可预期

**(b) systemd 加固与 dlopen 的冲突**

`deploy/systemd/natter.service` 当前用了 `ProtectSystem`、`SystemCallFilter=@system-service` 等。加载扩展时要注意：

| 指令 | 与 dlopen 的关系 | 处理 |
| --- | --- | --- |
| `MemoryDenyWriteExecute=yes` | **会阻止 dlopen**（需要可写可执行映射） | 若有此指令，必须设为 `no`；或者接受 sqlite-vec 不可用并降级 |
| `ProtectSystem=strict` | `.so` 只需**读**，不受影响 | 无需改（保证 `ReadOnlyPaths` 覆盖插件目录即可） |
| `PrivateTmp=yes` | 只要不把 `.so` 放 /tmp 就无影响 | 无需改 |
| `NoNewPrivileges=yes` | 无影响 | 无需改 |
| `SystemCallFilter=@system-service` | `mmap`/`openat` 在集合内 | 通常无需改，需验证 |

**必须做**：在 `natter doctor` 或 `memory-rag status` 里输出一次探测结果（`sqlite-vec: loaded 0.1.9 / unavailable: <reason>`），而不是等到第一次检索才发现加载失败。这与 P4.5 的"HEALTHCHECK 只是 liveness"教训一致。

**(c) 加载失败必须降级，不是启动失败**

```text
扩展加载失败 → 插件以 BM25-only 模式运行（等价轻量线）
              → 计数 + warning 日志（code=memory-rag.vec_unavailable）
              → 不阻止宿主启动、不阻止该回合
```

理由：§8.1「记忆是增强，不是前置条件」。

### 3.4 要不要引入 MEVD（v1 建议：不引入）

这是一个必须交代清楚的判断，因为 P6 文档 §5.2 已经写了「抽象层选 MEVD」。

**核实后的三个事实**：

```text
1. 官方没有 SQLite provider → 第一个 provider 得自己写。
   自己写 provider 时，MEVD 提供的只是「接口形状」，
   而非「现成的后端可换」，收益大幅缩水。

2. 引入 MEVD 10.11.1 会把 Microsoft.Extensions.AI.Abstractions
   从 10.11.0 拉到 10.11.1，属全解决方案级版本变动
   （CentralPackageTransitivePinningEnabled=true，需显式 PackageVersion）。

3. 插件在 ALC 隔离下运行。MEVD 的 VectorStore 类型若由插件自带，
   与宿主侧的 VectorData 类型不是同一个 Type，
   跨 ALC 边界无法传递 —— 除非宿主也引入并把抽象放进共享契约层。
   那又回到事实 2 的版本变动。
```

**v1 建议**：插件内部定义一个薄接口承担「可换后端」的职责，不引入 MEVD。

```csharp
// 插件内部，不进 host 契约
internal interface IVectorIndex
{
    ValueTask UpsertAsync(IReadOnlyList<VectorRecord> records, CancellationToken ct);
    ValueTask<IReadOnlyList<VectorHit>> SearchAsync(
        ReadOnlyMemory<float> query, VectorFilter filter, int topK, CancellationToken ct);
    ValueTask<int> DeleteAsync(IReadOnlyList<string> memoryIds, CancellationToken ct);
    ValueTask<VectorIndexStats> GetStatsAsync(CancellationToken ct);
}
```

两个实现：`SqliteVecIndex`（v1 唯一）、`QdrantIndex`（将来）。

**什么时候重新评估 MEVD**：当 Qdrant 后端真的要做时（也就是 §3.2 的反向条件触发时）。到那时 MEVD 若已有官方 Qdrant provider，或社区 provider 已经稳定，再引入不迟；而且那时引入的收益（真的能换后端）才对得起版本变动的代价。

**这个修正需要 lead 确认**，因为它改变了 P6 文档 §5.2 的既定结论。

---

## 4. 向量表结构与 schema 版本

### 4.1 库的位置

```text
<state>/plugins/natter.plugin.memory-rag/vectors.db
```

理由沿用 P6 §5.4：向量是插件内容，不是宿主能力；进 `natter.db` 会破坏单一 DDL owner（与 StateVitals 用 `state.db` 同一条规则）。

### 4.2 v1 表结构

```sql
PRAGMA journal_mode = WAL;
PRAGMA user_version = 1;          -- 插件库自己的版本，与 natter.db 的 user_version 无关

-- 元数据表：与 memory_items 一一对应的权威记录
CREATE TABLE vector_meta (
    memory_id     TEXT PRIMARY KEY,
    scope_kind    TEXT NOT NULL,          -- 'user' | 'conversation' | 'guild' | 'global'
    scope_key     TEXT NOT NULL,          -- MemoryScope.Id 的结果，原样存，不反解析
    model         TEXT NOT NULL,          -- embedding 模型标识（如 qwen3.7-text-embedding）
    model_rev     TEXT NOT NULL,          -- 模型版本/日期后缀，见 §6.2
    dimensions    INTEGER NOT NULL,
    dtype         TEXT NOT NULL,          -- 'float32' | 'int8' | 'bit'
    content_hash  TEXT NOT NULL,          -- sha256(规范化后的 memory text)
    state         TEXT NOT NULL,          -- 'ready' | 'pending' | 'stale'   见 §6.3
    source_updated_at TEXT NOT NULL,      -- 生成向量时 memory_items.updated_at 的副本
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX ix_vector_meta_scope  ON vector_meta(scope_kind, scope_key, state);
CREATE INDEX ix_vector_meta_model  ON vector_meta(model, model_rev, dimensions);
CREATE INDEX ix_vector_meta_state  ON vector_meta(state, updated_at);

-- 向量表：sqlite-vec vec0 虚表，维度和 dtype 在建表时固定
CREATE VIRTUAL TABLE vec_memory USING vec0(
    memory_id  TEXT PRIMARY KEY,
    embedding  float[1024]
);

-- embedding 缓存：§6.5 的落地位置
CREATE TABLE embedding_cache (
    cache_key   TEXT PRIMARY KEY,   -- sha256(model + '|' + dimensions + '|' + text)
    model       TEXT NOT NULL,
    dimensions  INTEGER NOT NULL,
    vector      BLOB NOT NULL,
    created_at  TEXT NOT NULL
);
CREATE INDEX ix_embedding_cache_model ON embedding_cache(model, dimensions);
```

### 4.3 为什么元数据表与向量表分开

```text
vec0 虚表能存 metadata 列，但它的列类型与索引行为受扩展版本约束，
且**改维度必须重建整表**。把 model/dimensions/state 放在普通表里：
  - 迁移只需重建 vec 表，元数据不丢
  - 可以按 model 一键查出"哪些向量需要重建"（普通表能建索引，vec 虚表不保证）
  - 换后端（Qdrant）时，元数据表可以整表复用
```

### 4.4 schema 版本与迁移

```text
vector_meta.user_version 是唯一的版本来源（与 natter.db 的 SqliteSchema 无关）

v1 → v2（维度变更，见 §6.4）：
  1. 新建 vec_memory_v2（新维度）
  2. vector_meta 全部置 state='pending'
  3. 后台逐条重建 → 写入 vec_memory_v2 + state='ready'
  4. 全部 ready 后原子切换：DROP vec_memory; ALTER vec_memory_v2 RENAME TO vec_memory
  5. user_version = 2
  中间态可服务（BM25 兜底 + 部分向量），可回滚（vec_memory 仍在）
```

关键点：**向量表的维度变更不能原地改**，必须新表 + 切换。这是 sqlite-vec（以及所有固定维度向量表）的硬约束，P6 文档 §5.3 没有提到。

---

## 5. 混合检索：候选池与 RRF

### 5.1 数据流

```text
query
 ├─[BM25]   memory_fts（宿主已有的 FTS5）      → topK_bm25
 └─[Vector] vec_memory（插件库）              → topK_vec
                    │
                    ▼
              RRF 融合（按名次，不按分数）
                    │
                    ▼
              可选 rerank（§7）
                    │
                    ▼
              预算裁剪（maxInjected / maxTokens）
                    │
                    ▼
              prompt.fragment（宿主注入路径，P6.1 已就绪）
```

**注意分工**：BM25 与向量分别在两个库里（`natter.db` 与插件 `vectors.db`），不能一条 SQL 完成。所以混合检索在**插件内**做，插件实现 `IMemoryStore`，把融合结果作为普通 `MemoryItem` 返回给宿主。宿主不需要知道向量存在。

这是 P6 §2「一个契约两个实现」的落地方式，也避免了宿主感知 P6.3。

### 5.2 参数

| 参数 | 默认 | 理由 |
| --- | --- | --- |
| `topK_bm25` | 20 | 与 `MemoryRetrievalOptions.MaxCandidates` 对齐 |
| `topK_vec` | 20 | 对称，便于比较两路贡献 |
| `rrfK` | **60** | RRF 原论文常用值；k 越大，名次差异被压平得越厉害 |
| `poolSize` | `max(topK_bm25, topK_vec) × 2 = 40` | 融合后留余量给 rerank 与预算裁剪 |
| `scoreThreshold` | 不设 | 先不加阈值（无度量，见 Q4），靠 topK 控制 |
| 融合公式 | `score(d) = Σ 1/(rrfK + rank_i(d))` | 名次融合，不需要归一化 BM25 与余弦 |

**为什么 RRF 的 k=60**：`1/(60+1)=0.0164` vs `1/(60+20)=0.0125`，第 1 名与第 20 名的差距只有 24%。这看起来"区分度低"，实际上正是 RRF 的设计意图——它不想让某一路的极端高分主导结果，而是让"两路都排得靠前"的条目胜出。若想让名次差异更尖锐，把 k 降到 10～20。

**建议**：k 与 topK 都做成 profile 可配（`memory.rag.rrfK` / `memory.rag.topK`），默认值先用 60/20，等 Q4 的评测夹具能跑出对照后再定。**不要在没有度量时把默认值当成最优值。**

### 5.3 两路都空 / 只有一路

```text
两路都有结果      → RRF 融合
只有 BM25         → 直接用 BM25（向量未就绪、扩展未加载、embedding 限流都落这里）
只有向量          → 直接用向量，并在诊断标记 bm25_miss
两路都空          → 回退 recency（复用 P6.1 既有行为），并计数
```

呼应 P6 §8.3：「检索时若某条记忆还没有向量，用 BM25 结果兜底——混合检索天然容忍这种情况」。这里把它做成显式分支，而不是靠"某一路恰好返回空"。

### 5.4 与宿主既有缺陷的关系

P6.1 的 `SearchFtsAsync` 存在「候选池先按纯 BM25 截断，decay 只在池内重排」的问题（见 [缺口 Q9](P6_MEMORY_OPEN_QUESTIONS.md#q9-衰减只作用于候选池之内)）。混合检索会**放大**它：两路各取 topK 后池子更窄。

建议：P6.3 实现时，把 decay 应用在**融合之后、裁剪之前**，且 BM25 路的 SQL 取 `topK × 4`。否则 RAG 线会继承并加深同一个缺陷。

### 5.5 过滤（作用域与隔离）

必须在**两路都做**，且在**取 topK 之前**：

```text
scope 过滤：scope_kind + scope_key（走 ix_vector_meta_scope）
过期过滤：  expires_at IS NULL OR > now
隐私过滤：  sensitivity <> 'private'（当 IncludePrivate=false）
```

sqlite-vec 的 vec0 是否支持「先过滤再 KNN」非常关键：如果只支持"KNN 后过滤"，那么 topK=20 取出后被过滤掉一半，结果会变少甚至为空。**这一点必须在选型验证时实测**（见 §9 的 P0 验证项）。

若 sqlite-vec 不支持行级预过滤，退路是：把 `scope_key` 做成 vec0 的 partition key（若版本支持），或按 scope 分表，或扩大 topK 后过滤。**不要把这条留到实现时才发现。**

---

## 6. embedding 模型 / 维度变更的重建流程

这是 P6 文档 §5.3 的空白。设计如下。

### 6.1 要解决的三个问题

```text
1. 维度变了 → 向量表列宽变了 → 必须建新表（§4.4）
2. 模型变了但维度相同 → 旧向量与新向量语义空间不同 → 不可混算
3. 重建期间 → 检索不能停，不能返回垃圾
```

### 6.2 模型身份

```text
model      = 提供方返回的模型标识（如 qwen3.7-text-embedding）
model_rev  = 提供方返回的版本/日期后缀，没有就用 "default"
fingerprint = sha256(model + '|' + model_rev + '|' + dimensions + '|' + dtype)
```

**用 fingerprint 而不是只用 model**：同一模型名在不同时间可能返回不同向量（提供方静默升级）。只比 model 会漏检这类漂移。P6 §5.3 说"存 model 与 dimensions"是不够的。

### 6.3 每条向量的状态机

```text
pending    向量未生成（新写入、或重建队列中）
ready      可用（fingerprint 与当前配置一致）
stale      fingerprint 与当前配置不一致（模型变了，还没重建）

检索只用 state='ready' 且 fingerprint 匹配的行
其余行让 BM25 兜底
```

`state` 与 `model/model_rev/dimensions` 落在 `vector_meta`（普通表），所以"哪些要重建"是一条索引查询，不需要扫 vec 表。

### 6.4 重建流程（离线重建 + 原子切换）

```text
触发条件（任一）
  - 启动时检测到 fingerprint 与库内不一致
  - 运维执行 natter memory-rag reindex --from-scratch
  - 提供方返回维度与配置不符（硬错误，立刻停止写入）

阶段 0  预检
  用一条短文本探测提供方实际返回的维度与 model 标识
  与配置不符 → 拒绝启动重建，报 memory-rag.dimension_mismatch
  （不要相信配置，要相信实际返回值 —— 这是唯一的真相）

阶段 1  标记
  若维度变化：建 vec_memory_new(float[新维度])
  若维度不变：原地保留 vec 表，只把 vector_meta.state 全置 'pending'
  两者都不删旧数据（可回滚）

阶段 2  重建（后台、限速、可中断）
  游标按 memory_id 排序，每批 N 条（N = provider.MaxBatchSize，通常 ≤ 10）
  走 P6.2 的分批与截断工具，遵守 MaxInputTokens
  每批成功写 vec + state='ready' + fingerprint
  失败：重试 ≤3 次，仍失败则 state='pending' + 计数，继续下一批（不整体失败）
  限速：每批后 sleep（默认 200ms），避免把 embedding 配额打满
  进程重启后从 state='pending' 继续（幂等，不需额外断点文件）

阶段 3  切换
  当 state != 'ready' 的数量为 0：
    DROP TABLE vec_memory; ALTER TABLE vec_memory_new RENAME TO vec_memory;
    user_version += 1
  切换是单事务、毫秒级

阶段 4  收尾
  旧向量已在阶段 1 备份或已删（按配置）
  输出统计：total / rebuilt / failed / elapsed
```

**为什么选离线重建而不是在线双写**：

```text
在线双写（新查询同时用新旧两套向量）的问题：
  - 两套向量要分别做 KNN 再合并，等于每次检索翻倍开销
  - 两套结果的分数不可比，融合逻辑复杂
  - 回滚困难（部分条目已双写）
离线重建的代价：
  - 重建窗口内新写入的记忆没有向量（退化为 BM25）—— 可接受，§8.1 已定这个降级
  - 需要一次全量 embedding 成本 —— 一次性，且有缓存可复用（content_hash 未变的条目
    若 model 没变，理论上可跳过；但 model 变了就无法复用，这是必然成本）
```

**成本估算**（供决策参考）：

```text
假设 per-actor 上限 500 条、活跃 actor 100 人 → 5 万条记忆
每条平均 60 token → 300 万 token
若 embedding 单价按量计费，这是一次可见但有限的成本
→ 因此 Q3（容量上限）必须与本节一起定，否则重建成本失控
```

### 6.5 只重建变化的部分

```text
content_hash 未变 且 fingerprint 未变  → 跳过（向量仍有效）
content_hash 变   （记忆被编辑）       → 必须重建
fingerprint 变    （模型/维度变了）     → 全部重建
```

这三行是重建的全部判据，不需要额外字段。

---

## 7. rerank 的接入位置与降级

### 7.1 位置：检索之后、注入之前（具体到函数）

宿主 P6.1 的调用链（`b4e91f8`）：

```text
HostContextPipeline.AssembleAsync
  → memoryStore.SearchAsync(...)          ← ① 检索（插件实现 IMemoryStore 时在这里内部做融合）
  → BuildMemoryFragments(summary, items)  ← ② 按 MaxInjected / MaxTokens 裁剪并生成片段
  → prompt provider / composer            ← ③ 编译进 PromptPlan
```

rerank 必须插在 **① 与 ② 之间**：

```text
rerank 在 ② 之前：
  融合后的 poolSize(40) 条候选 → rerank → 取 MaxInjected(5) → 注入
  优点：rerank 能真正改变"哪 5 条进 prompt"，这是它全部价值所在
rerank 在 ② 之后：
  只有 5 条进入 rerank → 重排也只在这 5 条里换顺序
  而且 MaxTokens 截断已经发生，重排无法挽回被截掉的条目
  → rerank 基本无意义
```

实现落点：RAG 插件实现 `IMemoryStore.SearchAsync` 时，在**返回之前**完成 rerank。宿主完全无感。

```text
IMemoryStore.SearchAsync(query, limit=MaxCandidates=20)
  ├─ BM25 topK 20 + Vector topK 20
  ├─ RRF → 40 条
  ├─ （可选）rerank → 重排 40 条
  ├─ Take(limit=20)                       ← 返回给宿主
  └─ 宿主再做 MaxInjected=5 / MaxTokens=800 裁剪
```

注意 `SearchAsync` 的 `limit` 语义在此变为"融合后返回条数"，仍等于 `MaxCandidates`，与宿主契约一致。

### 7.2 候选集大小与成本

| 项 | 值 | 说明 |
| --- | --- | --- |
| rerank 输入条数 | `min(poolSize, provider.MaxCandidates)` | 默认 40，受 provider 上限约束 |
| 单条文本长度 | 截断到 512 token | 记忆条目本身很短，不需要长上下文 |
| 超时 | **150ms** | 检索总预算 300ms，rerank 不能吃掉全部 |
| 失败/超时 | **保留 RRF 顺序**，计数 + 可选 warning | 不重试、不阻塞 |

关键：**rerank 的预算必须小于检索总预算**。300ms 是宿主给的整段预算（`MemoryRetrievalOptions.RetrievalTimeout`），BM25+向量+融合已经要花掉一半，rerank 只剩 150ms 量级。

### 7.3 降级链（四级，逐级退化）

```text
L0  BM25 + Vector + RRF + rerank        理想路径
L1  BM25 + Vector + RRF                  rerank 超时/报错/provider 未配置
L2  BM25 only                            向量不可用（扩展没加载、embedding 限流、向量未就绪）
L3  recency only                         BM25 也失败（FTS 表缺失、库损坏）
     → 与 P6.1 既有回退一致，并计数
```

每一级都要：

```text
- 在 ContextDiagnostics 里留下明确标记（哪个级别、什么原因）
- 计数（不记正文）
- 回合成功，不抛
```

这是 P6 §8.1 的具体化。**四级都必须有测试**，而不是只测理想路径。

### 7.4 rerank 与衰减的关系

要明确一个容易打架的顺序问题：

```text
decay 是宿主语义（时间衰减），rerank 是模型语义（相关性）
两者都反映"相关"，但方向不同：decay 让旧条目下降，rerank 只看语义匹配度

建议顺序：RRF → decay 加权 → rerank → 裁剪
理由：
  - decay 是确定性的、便宜的，先做
  - rerank 是外部调用、有成本和不确定性，后做，且只对已经过 decay 的候选做
  - 若反过来（rerank 后再 decay），rerank 的分数会被 decay 破坏，
    "rerank 说第 1 但很旧"与"第 2 很新"的取舍就没有统一口径
```

这条必须写进文档，否则实现时两种顺序都"看起来对"，行为会不一致。

---

## 8. 风险

| # | 风险 | 影响 | 缓解 |
| --- | --- | --- | --- |
| R1 | sqlite-vec 不支持「先过滤后 KNN」 | 作用域隔离在向量路上失效，可能越权召回 | **选型验证 P0 必测**（§9）；不支持则用 partition key 或分表 |
| R2 | `MemoryDenyWriteExecute=yes` 导致 dlopen 失败 | RAG 线整体不可用 | 启动探测 + 明确降级 + 部署文档写明该指令必须为 no |
| R3 | 维度变更重建成本失控 | 一次全量 embedding 账单 | 与 Q3 容量上限一起定；重建限速 + 可中断 + 缓存复用 |
| R4 | 两套检索路径（BM25 在宿主库、向量在插件库）放大 Q9 缺陷 | 老而高度相关的记忆更进不来 | §5.4：BM25 路取 topK×4，decay 在融合后应用 |
| R5 | 引入 MEVD 触发全解决方案 M.E.AI 版本变动 | 影响 15+ 工程 | §3.4：v1 不引入；Qdrant 需求出现时再评估 |
| R6 | 第三方 sqlite-vec NuGet（`HiraokaHyperTools.sqlite-vec` 0.1.9）供应链 | 一个非官方镜像包进生产 | 固定版本 + 校验 sha256 + 随插件分发（不在运行时下载）；或自建构建 |
| R7 | 向量与记忆不同步（记忆删除后向量残留） | 检索返回已删记忆 | `IMemoryStore.DeleteAsync` 必须在插件内同步删向量；启动时做一次孤儿清理（`vector_meta` 内 memory_id 不在宿主库的） |
| R8 | 插件库损坏 | 整条 RAG 线不可用 | `vectors.db` 可完全重建（元数据可从宿主库重建）→ 设计上把 vectors.db 当**可丢弃缓存**，这是它用独立库的另一个理由 |

R8 值得强调：**vectors.db 必须能删了重建**。凡是能从 `natter.db` 重建的数据，都不该成为单点。

---

## 9. 实施前的 P0 验证清单

这些不是"实现的一部分"，是**动手前必须先验的假设**。全部可在 1 天内用一个临时控制台程序验完（不碰 src/）：

```text
V1  sqlite-vec 能否在本项目的 Microsoft.Data.Sqlite 上加载成功
    （已知：load_extension 默认 not authorized，需先 enable —— 实测过）
V2  vec0 虚表能否建表、插入、KNN 查询，返回的距离度量与顺序正确
V3  vec0 是否支持在 KNN 前做行级过滤（scope_key）；若不支持，能否用 partition key
V4  1 万 / 5 万 / 10 万条 × 1024 维的 KNN 延迟，验证"暴力搜索可接受"的结论
V5  写入 5 万条后 vectors.db 的体积，确认磁盘预算
V6  systemd 加固下（MemoryDenyWriteExecute=yes/no 两种）加载行为差异
```

V3 与 V4 是**否决性**的：若 V3 不成立且没有 partition key 退路，默认后端必须换成 Qdrant，整个 §3.2 要重写。若 V4 显示 5 万条就要几秒，同样要换。

**建议**：这六项做成 `docs/P6_RAG_SPIKE.md` 并附实测数据，再决定是否动 P6.3 代码。这也符合本轮"先想清楚地基"的定位。

---

## 10. 分期

```text
P6.3a  索引与检索（无 rerank）
  IVectorIndex + SqliteVecIndex + 加载器 + 探测
  向量表 v1 + 元数据表 + 缓存表
  BM25 + Vector + RRF
  IMemoryStore 插件实现（替换 host#sqlite）
  降级链 L0..L3（L0 此时无 rerank = L1）
  验收：混合检索优于纯 BM25（用 Q4 的夹具证明）

P6.3b  rerank
  IRerankProvider 接入（P6.2 端口已就绪）
  150ms 预算 + 降级到 RRF 顺序
  验收：rerank 开启后 mention@5 不下降

P6.3c  重建与迁移
  fingerprint 检测 + 重建状态机 + 原子切换
  natter memory-rag reindex / status
  验收：维度变更后全量重建可用，中断可续，切换可回滚
```

不要一次做完。P6.3a 就能验证"RAG 是否真的更好"，如果 RRF 混合检索打不过纯 BM25，rerank 和重建流程都不用做。

---

## 11. 验收标准

```text
索引
[ ] vectors.db 可删除并从 natter.db 完整重建（R8）
[ ] 扩展加载失败时降级为 BM25-only，宿主启动不受影响，有探测输出
[ ] 每 provider 的 MaxBatchSize / MaxInputTokens 被真正用于分批与截断

检索
[ ] 作用域过滤在向量路上同样生效（actor A 的向量不进 B 的结果）—— 需 V3 支撑
[ ] 两路都空 → recency 回退，且诊断可区分（fsl / vector / fallback）
[ ] 降级链 L0..L3 每一级都有用例
[ ] decay 在融合后应用，候选池不因两路 topK 而收窄（§5.4）

一致性
[ ] 同一查询，装/不装 RAG 插件的结果差异可解释（诊断里能看出用了哪条路）
[ ] 未装插件时行为与 P6.1 完全一致（逐字节基线）
[ ] 导出/导入不丢数据；换到 RAG 后向量可重建而非静默缺失

重建
[ ] 模型变更 → fingerprint 不匹配 → 触发重建，旧向量不被混用
[ ] 维度变更 → 新表 + 原子切换，中断可续，可回滚
[ ] 重建期间检索持续可用（BM25 兜底）

rerank
[ ] 超时/报错 → 保留 RRF 顺序、计数、不阻塞
[ ] rerank 在 MaxInjected 裁剪之前生效（能改变"哪 5 条进 prompt"）
```

---

## 12. 一句话总结

> **MEVD 的抽象是官方的，但官方没有 SQLite provider；SK 的 SQLite 连接器只有 preview。所以 P6.3 的默认后端选 sqlite-vec（零外部服务，实测可加载），第一个 provider 自己写，v1 不引入 MEVD；代价是一个 `.so` 的部署与 dlopen 加固细节，和一套必须在上代码前先用六项验证钉死的假设（尤其是"能否先过滤后 KNN"）。**
