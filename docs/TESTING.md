# 测试指南

> **状态**：现行 · 2026-11-23
> **本文定位**：从零开始跑测试的入口。命令细节（超时层级、blame-hang、Android 人工步骤）见 [testing/TEST_EXECUTION.md](testing/TEST_EXECUTION.md)。
> **相关**：[LIVE_TEST_PLAN.md](LIVE_TEST_PLAN.md)（真人验收清单）、[testing/README.md](testing/README.md)（验收报告索引）

---

## 0. 三十秒上手

```bash
cd ~/Projects/csharp/Natter

# 1. 构建（必须先做，见 §5.1 增量构建陷阱）
dotnet build Natter.slnx -c Release

# 2. 跑全部单元测试
dotnet test Natter.slnx -c Release --no-build

# 3. 只跑框架主体
dotnet test tests/Natter.Framework.Tests -c Release --no-build
```

预期：全绿，约 15 秒。当前规模见 §1。

> **单测不需要任何凭据、网络或数据库**。它们全部在临时目录里自建 SQLite，用假实现替代外部服务。凡是需要真实 API 的都在 §3 的 live 层，且默认跳过。

---

## 1. 测试分层

| 层 | 工程 | 用例数* | 需要什么 | 何时跑 |
| --- | --- | --- | --- | --- |
| **框架主体** | `tests/Natter.Framework.Tests` | **1051** | 无 | 每次改动 |
| **WebConsole 插件** | `tests/Natter.Plugin.WebConsole.Tests` | 344 | 无（HttpListener 走 loopback） | 改 WebConsole |
| **会话控制台** | `tests/Natter.Plugin.ConversationConsole.Tests` | 67 | 无 | 改该插件 |
| **Prompt Library** | `tests/Natter.Plugin.PromptLibrary.Tests` | 36 | 无 | 改 Prompt |
| **StateVitals** | `tests/Natter.Plugin.StateVitals.Tests` | 17 | 无 | 改状态数值 |
| **AI Embedding** | `tests/Natter.Plugin.AiEmbedding.OpenAi.Tests` | 11 | 无（fake HTTP handler） | 改 embedding |
| **Live 集成** | `tests/Natter.Live.Tests` | 39 个方法（按 `[Live*Fact]` 计） | **真实 API 凭据** | 发布前，手动 |
| **E2E 脚本** | `scripts/webconsole-e2e.sh` | — | 本机可起进程 | 改 WebConsole / 发布前 |
| **Soak** | `scripts/soak-*.{sh,py}` | — | 长跑环境 | 24h 长稳 |

\* 数字随改动变化，**以 CI 实时输出为准**，本文数字仅供判断量级。历史教训：把用例数写死在文档里，几轮之后必然过期（见 [S3-39](../docs/CODE_REVIEW_S3_TRIAGE.md)）。

**合计单测约 1526 个**，其中框架 1051 占 69%。

---

## 2. 常用跑法

### 2.1 全量

```bash
# 整个解决方案（会跳过 Live 层的 [LiveFact]）
dotnet build Natter.slnx -c Release
dotnet test Natter.slnx -c Release --no-build
```

`--no-build` 不是优化技巧，是**正确性要求**：混用 Debug/Release 程序集会导致 `manifest-not-nps` 之类的假失败。见 §5.2。

### 2.2 跑单个套件

```bash
dotnet test tests/Natter.Framework.Tests -c Release --no-build
dotnet test tests/Natter.Plugin.WebConsole.Tests -c Release --no-build
```

### 2.3 跑单个用例 / 一类用例

```bash
# 精确到方法
dotnet test tests/Natter.Framework.Tests -c Release --no-build \
  --filter "FullyQualifiedName~ChatTextChunkerTests"

# 一类（按类名）
dotnet test tests/Natter.Framework.Tests -c Release --no-build \
  --filter "FullyQualifiedName~Memory"

# 排除某类（并行开发时常用：排除别人正在改的区域）
dotnet test tests/Natter.Framework.Tests -c Release --no-build \
  --filter "FullyQualifiedName!~StateVitalsTests"
```

### 2.4 看失败细节

```bash
dotnet test tests/Natter.Framework.Tests -c Release --no-build -v normal
```

`-v minimal`（默认）只给统计，`-v normal` 才打出断言信息与堆栈。

### 2.5 连跑确认无 flake

定时/并发相关的用例偶发失败时，连跑 3 次：

```bash
for i in 1 2 3; do
  printf "round %d: " $i
  dotnet test tests/Natter.Framework.Tests -c Release --no-build --nologo \
    2>&1 | grep -oE "Passed: *[0-9]+, Skipped: *[0-9]+, Total: *[0-9]+" | tail -1
done
```

**规矩：改动涉及并发、时序、文件系统时，必须连跑 3 次全绿才算通过。** 只跑一次全绿不算证据——我们踩过多次「单跑绿、全量红」。

---

## 3. Live 集成测试（需要真实凭据）

默认全部跳过（`[LiveFact]` / `[DeepSeekLiveFact]` / `[DiscordLiveFact]` / `[LiveE2EFact]` 等）。只有显式指定才跑。

```bash
# 凭据放在这里（git-ignored）
cp tests/Natter.Live.Tests/.env.example ~/.config/natter/live.env
$EDITOR ~/.config/natter/live.env

# 分层跑
scripts/live-test.sh deepseek     # 只测 provider 适配（快）
scripts/live-test.sh discord      # 网关 / REST / upstream
scripts/live-test.sh ingress      # 真实网关入站
scripts/live-test.sh e2e          # 端到端：插件 → DeepSeek → Discord
scripts/live-test.sh deploy       # 独立进程 + 文件插件 + secret
scripts/live-test.sh spark        # 讯飞星火
scripts/live-test.sh dingtalk     # 钉钉 Stream 握手
scripts/live-test.sh manual       # 需要真人在 Discord 发消息
```

**注意**：`live-test.sh` 目前没有 memory 目标，P6 的记忆 live 用例只能用 `all` 或手写 `--filter`。这是已知缺口（见 [S3 复核](../docs/CODE_REVIEW_S3_TRIAGE.md) 批次 C）。

---

## 4. E2E 与长跑

### 4.1 WebConsole E2E

起真实进程、真实 HTTP、隔离 config/state，验证配置编辑与受限命令：

```bash
./scripts/webconsole-e2e.sh
# 预期末行：PASS: WebConsole E2E matrix
```

覆盖：建/读/409 冲突/带 ETag 保存/备份落盘/rollback/命令真执行/模块 MIME/诊断包 ZIP。

### 4.2 Soak（24h 长稳）

```bash
# 采样（systemd timer 或手动）
sudo systemctl status natter-soak.service

# 出报告
python3 scripts/soak-report.py <样本.jsonl> --interval 120
```

判定口径是**累计有效运行 24h**（跨天/跨重启/跨断电都算），不是自然时间。硬门槛：

```text
保证字段   bootId（区分重启）、rssBytes（内存漂移）
护栏       min segment ≥6h、gap 归因完整、未归因 ≤2%
           RSS 漂移 ≤20%、crash=0、sendFailures=0、断连归因 100%
状态值     healthy 与 plugins-failed 都算有效；degraded/unknown 算异常
```

> 部署必然打断 soak 窗口。重启后旧样本不再适用，需重置并记 `host-restart` 归因。

---

## 5. 五个坑（都是实际踩过的）

### 5.1 增量构建会跳过你的改动

容器/虚拟机时钟跳变时，源文件 mtime 可能 ≤ 产物 mtime，MSBuild 判定「无需重建」。表现为**删掉的用例还在跑**。

```bash
# 改了测试但行为没变，先强制重建
dotnet build -t:Rebuild Natter.slnx -c Release
```

### 5.2 混用 Debug/Release 会假失败

插件测试报 `manifest-not-nps` 但代码没问题 → 通常是宿主是 Release、插件是 Debug。

```bash
# 永远成对使用
dotnet build Natter.slnx -c Release
dotnet test  <项目> -c Release --no-build
```

### 5.3 并行开发时全量红不一定是你的错

多人/多 agent 同时改工作区时，失败会「跟着谁在写漂移」。判断方法：

```bash
# 排除正在改的区域，看剩下的是否全绿
dotnet test tests/Natter.Framework.Tests -c Release --no-build \
  --filter "FullyQualifiedName!~<别人正在改的类>"
```

汇报时说清「排除 X 后 N/N 全绿」，比含糊说「有失败但不是我」可信。

### 5.4 只跑一次全绿不算证据

时序敏感的改动（并发、锁、文件系统、定时器）单次通过可能是运气。连跑 3 次。

### 5.5 NuGet 缓存只读时无法新建工程

沙箱里 `~/.nuget/packages` 只读时，`dotnet new` / 临时探针工程无法 restore。用仓库内已有工程加临时测试文件代替，或直接在现有测试工程里写断言。

---

## 6. 加测试的规矩

### 6.1 断言要能真的抓到问题

反面教材（我们修过的）：

```csharp
// 和实现自己比 —— 永远通过
Assert.Equal(ComputeEtag(input), harness.EtagOf(input));
// 改成对拍产品实现 + 固定期望值，才能抓到算法漂移
Assert.Equal(AdminETag.Compute(input), harness.EtagOf(input));
```

同类问题：

```text
弱   Assert.True(plan.Degraded)              只验一个没意义的布尔
强   Assert.Equal("plugins-failed", status)  验具体状态值
弱   Assert.Contains("--no-host", note)      验字符串片段
强   四种状态下逐一断言
```

**「这个断言在什么情况下会红」如果答不出来，它就不是测试。**

### 6.2 已知限制要写成断言，不要只写文档

```csharp
// 固化为可执行事实：trigram 对中文双字词零命中，这是已知限制不是 bug
Assert.Equal(0, TrigramMatches("苹果"));
Assert.True(TrigramMatches("吃苹果") >= 1);
```

文档会过期、会没人读；断言不会。想改回去的人会先撞上它。

### 6.3 不确定性要注入，不要 sleep

```csharp
// 错：靠时长赌
await Task.Delay(50); Assert.Equal(1, calls);
// 对：用可控时钟 / TaskCompletionSource 门
var gate = new TaskCompletionSource();
// ... 断言 gate 关闭期间 calls == 1，开门后 == 2
```

框架里已有：`ControllableClock`（时间）、`TimeProvider` 注入（衰减曲线）、`TaskCompletionSource`（并发门）。

### 6.4 并行开发下限定作用域

新增测试文件优先，避免多人改同一个文件。若必须改公共文件，先 `git status` 确认没人在动。

---

## 7. 提交前自查

```text
[ ] dotnet build Natter.slnx -c Release       → 0 error
[ ] 相关套件 --no-build 全绿
[ ] 涉及并发的改动连跑 3 次
[ ] 没有把别人的未提交文件带进 commit（按文件名 add，不要 git add -A）
[ ] 新增的「已知限制」有对应断言
```

第 4 条是实际教训：并行开发时 `git add -A` 会把别人半成品一起提交，导致提交历史里出现编译不过的状态。

---

## 8. 我要验证某个具体功能，从哪开始

| 想验证 | 跑什么 |
| --- | --- |
| 消息分片、围栏、emoji | `--filter "FullyQualifiedName~ChatTextChunker"` |
| 幂等、去重窗口 | `--filter "FullyQualifiedName~ChatIdempotency|~ChatEventDeduplicator"` |
| 记忆作用域与检索 | `--filter "FullyQualifiedName~Memory"` |
| 遗忘曲线数值 | `--filter "FullyQualifiedName~MemoryDecay"` |
| Prompt 组装与预算 | `--filter "FullyQualifiedName~Prompt"` |
| 插件装载、权限、信任 | `--filter "FullyQualifiedName~Nps|~Plugin"` |
| 任务运行时 | `--filter "FullyQualifiedName~TaskRuntime|~TaskDispatcher"` |
| 健康与状态判定 | `--filter "FullyQualifiedName~CliOpsHardening"` |
| WebConsole 全部 | `dotnet test tests/Natter.Plugin.WebConsole.Tests` |
| 真实平台收发 | `scripts/live-test.sh e2e`（需凭据） |
| 部署后是否真的装了插件 | `sudo bash -c 'set -a; . /etc/natter/live.env; set +a; /opt/natter/natter plugins list'` |

最后一条有坑：**不带环境变量跑 `plugins list` 会显示大量 `missing-secret` 假失败**，因为 secret 在 `live.env` 里，只有 systemd 启动的进程才有。判断运行中宿主的真实状态请用 `curl /health`，不要用 CLI 列表。

---

## 9. 相关文档

| 文档 | 内容 |
| --- | --- |
| [testing/TEST_EXECUTION.md](testing/TEST_EXECUTION.md) | 超时层级、`--blame-hang` 取证、禁止写法、Android 人工步骤 |
| [LIVE_TEST_PLAN.md](LIVE_TEST_PLAN.md) | 真人验收清单（含未执行的 24h soak） |
| [testing/README.md](testing/README.md) | 历次验收报告索引 |
| [DEVELOPMENT.md](DEVELOPMENT.md) | 构建布局、工程结构 |
| [PLUGIN_SPEC.md](PLUGIN_SPEC.md) | 写插件时需要满足的契约 |
