# P6.9 记忆召回评测结果

> **状态**：P6.9 第一轮测量完成
> **日期**：2026-11-23
> **定位**：用固定夹具量化 tokenizer / decay / candidates 三个参数，回答「生产默认值该取什么」。
> **依据**：[记忆系统设计](P6_MEMORY_SYSTEM.md) §5、§6、§9.1.1、§12
> **不复用**：[P6 Memory 缺口清单](P6_MEMORY_OPEN_QUESTIONS.md) 中 Q4 的度量缺口（本文是第一份实测数据）

---

## 0. 一句话结论

> **tokenizer 有明确证据（预分词显著优于 trigram）；decay 与 candidates 这一轮没有证据——不是「确认无害」，而是这个夹具测不出来。** 所以生产默认值只改有证据的那一项，其余保持不动，并记下让下一轮能测出差异的前置条件。

---

## 1. 方法

```text
夹具      tests/fixtures/memory-retrieval-cases.json
          12 条 corpus（五类 + 噪声 + 一对衰减对照）、10 个 case
入口      natter memory eval --cases <fixture> [--compare <dim>=<v1>,<v2>]
固定时钟  MemoryEvalRunner.EvalNow = 2026-11-23T00:00:00Z
          写入与打分都用它，所以衰减计算不随运行时刻漂移
隔离      每个配置一个独立临时库（用完删除），两条索引互不污染，可各自计时
口径      pass = 期望的每个 id 都在返回列表里；expectEmpty = 返回必须为空
          rank = 期望 id 在列表中的 1-based 最好位置（0 = 未命中）
          MRR  = mean(1/rank)，未命中记 0
门槛      §12：命中率 ≥ 85%、MRR ≥ 0.8
```

**可复现性已验证**：`MemoryEvalTests.Two_runs_of_the_same_configuration_produce_identical_decisions` 断言两次运行的 `Fingerprint`（case→rank→pass→命中列表）逐字节相同，只有耗时列不同。诊断里的 `retention` 用**该次运行的实际配置**重算（`decay=off` 打印 `retention=1.0000`），不是默认曲线——否则诊断会骗人。

---

## 2. 结果一：tokenizer（有结论）

```text
config                 recall   MRR     avg ms
tokenizer=pretokenized  90.0%   1.000   2.2
tokenizer=trigram       90.0%   0.806   0.3

per-case rank
case                    pretokenized  trigram
policy-language                1          6
preference-food                1          1
fact-city                      1         11
episodic-migration             1          1
trace-origin                   1          1
english-backend                1          1
decay-recency                  1          1
noise-weather                  1          1
unrelated                      x          x
emoji-policy                   1          1
```

**结论**：预分词索引 MRR **1.000 vs 0.806**，差距全部来自两字词锚点：

- `policy-language`（"你该用什么语言回答"）：预分词第 1，trigram 第 **6**
- `fact-city`（"我住哪"，3 字）：预分词第 1，trigram 第 **11**

这与 Q8 的机制完全一致：trigram 只能匹配 ≥3 字符的滑窗，中文两字词（回答/我住/喜欢/苹果/迁移）无法作为锚点。

**注意 recall 都是 90% 并不代表 trigram 可用**：recall 只问「期望 id 在不在返回列表里」，而列表被 recency 回退填满（见 §5），所以 trigram 靠兜底也能「找到」。**MRR 与 rank 才是判别指标。** 真实差距比 MRR 显示的更大：0.806 已被兜底抬高。

**建议：生产默认保持预分词**（已是当前实现）。trigram 仅作诊断开关，不进 profile（`MemoryEntryStoreOptions` 标 `[EditorBrowsable(Never)]`，不在 `config.schema.json`）。

---

## 3. 结果二：decay（无结论）

```text
config           recall   MRR     avg ms
decay=default     90.0%   1.000   2.5
decay=long        90.0%   1.000   0.5
decay=off         90.0%   1.000   0.4

每个 case 的 rank 在三档下完全相同
```

三档（默认 1×、3× 稳定度、完全关闭衰减）**没有改变任何一条排序**。

### 为什么测不出来（这是真实缺陷，不是被修好了）

```text
1. 夹具里没有「近似竞速」对
   唯一为衰减设计的是 decay-recency：decay_new(1 天) vs decay_old(400 天)。
   但旧的条目内容更长（"…开始发布窗口"），FTS5 的 BM25 长度归一化直接把它压下去，
   于是无论是否开衰减它都排第 2。
2. 衰减要影响排序，必须先有一个「BM25 接近」的候选对
   decay_old 的 retention=0.627 vs decay_new 的 0.993，是 1.58× 的乘数；
   只要 BM25 差距小于 1.58×，开关衰减就会翻转名次。当前这对的 BM25 差距远大于此。
3. 我无法工程化出「接近」的一对，因为 store 不暴露 bm25
   这是契约缺口（见 §6），不是可以靠调夹具绕过的。
```

我刻意**没有**反复调夹具直到出现差异——那是「调到变绿」，会用假证据支持一个真实参数。

**建议：decay 保持现值（default 1×）。** 现有数据既不支持改成 3×，也不支持关掉。它仍是未经测量的参数，这一点必须留在文档里，而不是因为「测试全绿」就当作已验证。

---

## 4. 结果三：candidates（无结论）

```text
config              recall   MRR     avg ms
candidates=5         90.0%   1.000   2.4
candidates=20        90.0%   1.000   0.5
candidates=50        90.0%   1.000   0.4
```

三档排序完全相同。原因是**语料只有 12 条，全部能装进 top-5**，候选池上限根本没被触及。这个夹具在设计上就无法测 candidates。

**建议：candidates 保持 20。** 要测它需要一个 ≥100 条、且包含大量「同一话题多个近似条目」的语料，让正确条目落在 5~20 名之间。这是下一轮的前置工作。

---

## 5. 失败的 case：`unrelated`（唯一失败，且是设计问题）

```text
unrelated  query="量子色动力学渐近自由" expected=[] got=12 条全量
  #1 decay_new       fact    age=1.0d   S=20.0 retention=0.9926
  #2 trace_origin    trace   age=1.0d   S=2.3  retention=0.9536
  ...
  #12 decay_old      fact    age=400.0d S=20.0 retention=0.6272
```

期望「无词面重叠 → 返回空」，实际返回**全部 12 条**。

原因在 store 里（P6.1 遗留行为）：FTS 无命中时 `SearchAsync` 回退到 `ORDER BY updated_at DESC` 的 recency 列表。

```csharp
var fts = await SearchFtsAsync(...);
if (fts.Count > 0) return fts;
// No relevant hit: fall back to recent entries (legacy behaviour)
```

**对主动注入可以接受**（回合有记忆比没有好，§8.1 的降级原则）；**对 `memory_search` 工具是错误的**——用户问「量子色动力学」，工具会把「今天天气不错」和「我喜欢吃苹果」塞给模型。

已用 `MemoryEvalTests.Recall_path_cannot_return_nothing_so_expect_empty_fails` 把这条围栏化：断言它**当前失败**，等回退行为可配置时该测试会红，从而逼出一次显式决策，而不是悄悄改掉基线。

**建议**：P6.8 接 `memory_search` 时，工具路径必须能返回空（`AllowRecencyFallback=false`）。这条应作为 P6.8 的验收项，不是 P6.9 的调参项。

---

## 6. 这一轮暴露的两个契约缺口

| # | 缺口 | 影响 | 建议 |
| --- | --- | --- | --- |
| E1 | store 不暴露 bm25 / 融合分 | 无法构造「近似竞速」夹具，因此无法量化 decay；也无法解释「为什么这条排第 2」 | `MemoryEntryQuery` 加 `IncludeScores`（或返回 `MemoryEntryHit(Entry, Score, Bm25)`）。这是 P6.9 校准的前置条件，不是可选美化 |
| E2 | 无「不回退」开关 | 期望为空的 case 永远无法通过；`memory_search` 行为不可控 | 查询级 `AllowRecencyFallback`（默认 true 保 v1 行为，工具路径设 false） |

两条都不大，但都不做就无法把 decay 从「凭直觉」变成「有数据」。

---

## 7. 耗时数据不可用于性能对比（诚实标注）

```text
pretokenized 2.2ms vs trigram 0.3ms   ← 不要据此得出「trigram 快 7 倍」
```

原因：每个配置跑独立进程内首次查询，JIT 预热与首次 SQLite 打开被算进了第一条 case（`policy-language` 单条 18.2ms，其余 0.2~0.6ms）。这不是受控的性能测量。

要测真实延迟，需要：预热 → 多次重复 → 取中位数/分位数。这一轮不做，因为参数决策不依赖它（12 条语料下两者都远低于 300ms 预算）。

---

## 8. 对生产默认值的建议

| 参数 | 当前值 | 建议 | 依据 |
| --- | --- | --- | --- |
| tokenizer | 预分词 + unicode61 | **保持** | MRR 1.000 vs 0.806；两字中文锚点只有预分词能命中 |
| decay scale | 1×（`StabilityScale=1`） | **保持** | 无数据；夹具测不出差异（§3） |
| candidates | 20 | **保持** | 无数据；12 条语料触及不到上限（§4） |
| maxInjected | 5 | **未测** | 本夹具不含注入环节，属 P6.8 |
| maxTokens | 800 | **未测** | 同上 |

**只改有证据的项**，其余保持并把「未测」写在明面上。trigram 那次教训就是「凭直觉改 + 测试全绿」。

---

## 9. 门槛现状

```text
§12 门槛：命中率 ≥ 85% 且 MRR ≥ 0.8
实 测  ：9/10 = 90.0%，MRR 1.000
```

**达到门槛，但要打折看**：

- 90% 的上限被 `unrelated` 卡住，而它在当前实现下**不可能通过**（§5）。也就是说 90% 是这个夹具的天花板，不是 100% 的 90%。
- MRR 1.000 说明「只要词面能对上，正确条目就是第 1 名」。这验证了排序质量，但没有验证语义召回（换个说法还能不能找到）——那需要 RAG/向量，属 P6.3。

---

## 10. 复现方式

```bash
# 基线
natter memory eval --cases tests/fixtures/memory-retrieval-cases.json

# 三组对照
natter memory eval --cases tests/fixtures/memory-retrieval-cases.json --compare tokenizer=pretokenized,trigram
natter memory eval --cases tests/fixtures/memory-retrieval-cases.json --compare decay=default,long,off
natter memory eval --cases tests/fixtures/memory-retrieval-cases.json --compare candidates=5,20,50
```

退出码：达到 §12 门槛返回 0，否则返回 1（CI 可用）。

测试覆盖（`tests/Natter.Framework.Tests/MemoryEvalTests.cs`，12 例）：

```text
夹具解析（真实文件 12 corpus / 10 case、五类齐全）
未知 type 拒绝而非默认、空 case 列表拒绝
两次运行决策逐字节一致（可复现）
预分词 MRR 高于 trigram 且两字 case 排名断言
recency 回退导致 expectEmpty 失败（已围栏）
失败 case 输出候选（age/S/retention）
decay=off 时诊断 retention=1.0000（诊断不骗人）
decay 模式到 store 选项映射
对照渲染每配置一行 + 失败标记
```

---

## 11. 下一步（按价值排序）

```text
1. 补 E1（暴露 bm25/融合分）→ 才能构造近似竞速夹具 → 才能给 decay 下结论
2. 扩充语料到 ≥100 条、含同话题近似条目 → 才能测 candidates
3. P6.8 接 memory_search 时加 AllowRecencyFallback=false → 修 §5 的工具语义
4. 受控性能测量（预热 + 重复 + 分位数）→ 若要验证 300ms 预算
5. 达到 §5.4 的 v1 退役前置条件之一：≥30 个 case。当前 10 个，需补到 30
```

第 5 条关系到 `MemoryDecay.LegacyRetention` 能否删除：§5.4 要求 ≥30 case 且幂律不劣于指数。本轮 10 个 case，**不满足**，所以 v1 继续走指数曲线是正确的，本轮没有推翻该决定的数据。

---

## 12. 一句话

> **tokenizer 有数据、可以定；decay 与 candidates 没有数据、不许定。90% 命中率是这个夹具的天花板（`unrelated` 因 recency 回退必失败），MRR 1.000 只证明「词面对得上时排序正确」，不证明语义召回。**
