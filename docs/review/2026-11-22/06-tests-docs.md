# 06 · 测试质量与文档一致性审查（只读）

> **审查范围**：`tests/Natter.Framework.Tests`、`tests/Natter.Live.Tests`、`tests/Natter.Plugin.PromptLibrary.Tests`、`tests/Natter.Plugin.WebConsole.Tests`；`README.md` 与 `docs/` 主要文档；`Natter.slnx`、`.github/workflows/*.yml`。
> **方法**：`grep`/`find`/`wc` 全量统计 + 逐文件抽样阅读 + **实跑** `dotnet test`（`--no-build`，未编译、未修改任何源码/测试/文档）+ 文档声称与代码实现逐条对照。
> **约束**：本次审查只读；除本报告外未写入任何文件。
> **环境**：Linux，.NET SDK 10.0.114，工作区 `HEAD` 未变更。

---

## 0. 结论摘要

1. **测试规模被严重低估**：README 与 6 份验收文档仍以「264 个框架测试」作为 Go 判定依据，实测框架套件为 **557** 个用例（0 failed / 0 skipped，4s）。同一批文档里还并存 410/410、356/357/359/360 等互相矛盾的总数。
2. **CI 只覆盖 4 套中的 3 套，且有 2 个产品工程从不构建**：`Natter.Plugin.PromptLibrary.Tests`（36 个用例）既不在 CI restore/build/test 里，也不在 `scripts/test.sh` 与 `docs/DEVELOPMENT.md` 的任何入口；`plugins/Natter.Plugin.PromptLibrary`、`src/Natter.Hosting.Android` 两个工程没有任何 CI 构建路径。这两处编译破坏可以一路绿灯进主干。
3. **Live 套件的跳过逻辑本身可靠**（实测 40/40 全 skip，exit 0），但 CI 只校验退出码、不校验 skip 数；且有一个 **零断言** 的 live 测试以「不抛异常即通过」的形式存在。
4. **存在一处「假验收」结构性风险**：`Phase23Acceptance` 自称验收层，实际跑的是 200 行的手写 `FileAdminConfigStore`，并且把产品 ETag 算法**逐字重抄**进测试 oracle（`AcceptanceHarness.ETagOf`）；产品换算法时测试仍绿。
5. **断言密度整体健康（各套件 2.9–4.5，总体 3.66 asserts/用例）**，但有两处典型弱断言：预算测试只断言「估算字数 ≤ 80」（空 bundle 也能过），串行化测试只断言 `MaxActive == 1`（第二个请求被静默丢弃也能过）。
6. **flake 面**：无 `xunit.runner.json`/`CollectionBehavior`，4 套全部默认并行；`FreePort()` 是典型 TOCTOU；555 个 async 测试只有 32 处 `WaitAsync`，无 `[Fact(Timeout)]`、无 `--blame-hang`，一旦挂死只能等 CI 的 20 分钟 job 超时且无 dump。
7. **可维护性**：`TempDirectory` 被复制 15 份、`FakeLogger` 3 份、WebConsole 有 2 套并行 fake 体系；无共享测试基础设施工程。
8. **文档内部矛盾**：`LIVE_TEST_PLAN.md` 同时写「L5 待人工触发」与「L5 复测通过」、同时写 27/27 与 26/26；README 称钉钉/星火「真实平台凭据未接入」，而测试记录写「群内 @机器人 收到回复、Spark 7/7 真实通过」。

---

## 1. 统计（全部可复核）

### 1.1 项目与文件

| 项 | 数值 | 说明 |
| --- | --- | --- |
| 测试项目 | 4 | Framework / Live / Plugin.PromptLibrary / Plugin.WebConsole |
| `tests/**/*.cs` 全部文件 | 133 | 含 `obj/` 生成文件 24 个 |
| 手写 `.cs` 文件 | **109** | 133 − 24（`obj/`、`bin/`） |
| 测试用例文件 | **94** | 109 − 15 个纯 helper/fake |
| 纯 helper/fake 文件 | 15 | `DeepSeekFakes`、`DingTalkFakes`、`DiscordFakeTransport`、`NlmpTestSupport`、`PluginPackageTestBuilder`、`LiveDeployment`、`LiveDiscordOracle`、`LiveSettings`、`LiveFactAttribute`、`LiveTestAssembly`、`DiscordRestClient`、`PromptLibraryTestSupport`、`Phase23/Fakes`、`Phase23Acceptance/AcceptanceHarness`、`WebConsoleTestSupport` |
| 测试代码总行数 | 27,426 | 含 `obj/` |
| 手写测试代码行数 | **27,152** | `find ... -not -path "*/obj/*" \| xargs cat \| wc -l` |

### 1.2 用例数量（实测，非估算）

用 `dotnet test --no-build` 实跑（源码未改动）：

```
Natter.Framework.Tests         Passed! Failed: 0, Passed: 557, Skipped: 0, Total: 557, Duration: 4 s
Natter.Plugin.WebConsole.Tests Passed! Failed: 0, Passed: 303, Skipped: 0, Total: 303, Duration: 1 s
Natter.Plugin.PromptLibrary.Tests Passed! Failed: 0, Passed: 36, Skipped: 0, Total: 36, Duration: 221 ms
Natter.Live.Tests (NATTER_LIVE="") Skipped! Failed: 0, Passed: 0, Skipped: 40, Total: 40, Duration: 54 ms
```

| 项目 | `[Fact]` 家族 | `[Theory]` | `[InlineData]` | 用例合计 | 声明式 Skip 数 |
| --- | ---: | ---: | ---: | ---: | ---: |
| Framework | 481 | 10 | 76 | **557** | 0 |
| Live | 40（7 种自定义 LiveFact 属性） | 0 | 0 | **40** | 40（无凭据时动态 Skip） |
| Plugin.WebConsole | 205 | 21 | 98 | **303** | 0 |
| Plugin.PromptLibrary | 36 | 0 | 0 | **36** | 0 |
| **合计** | **762** | **31** | **174** | **936** | **40** |

- `[Fact]` 原始计数 722（仅 `[Fact]` 字面量）+ 40 个自定义 `[XxxLiveFact]` = 762。
- `[MemberData]`：0 处；全部参数化走 `[InlineData]`。
- Skip 相关字面量 `Skip =` 共 7 处，**全部是属性定义**（`LiveFactAttribute.cs` 5 处 + `OrchestrationLiveTests.cs:343` + `ManualGateLiveTests.cs:228`），不是用例级跳过；真实跳过由属性构造器注入，无凭据时 40/40 全部 skip。

### 1.3 断言密度

| 项目 | `Assert.*` 次数 | 用例数 | 断言/用例 |
| --- | ---: | ---: | ---: |
| Framework | 2,212 | 557 | 3.97 |
| Plugin.WebConsole | 886 | 303 | 2.92 |
| Plugin.PromptLibrary | 143 | 36 | 3.97 |
| Live | 180 | 40 | 4.50 |
| **合计** | **3,421** | **936** | **3.66** |

单断言（`n ≤ 1`）用例 72 个，占 7.7%；绝大多数是合法的「否定型」用例（`Assert.Null` / `Assert.Empty` / `Assert.Throws`），已逐个抽查，只有 2 个属于真弱断言（见 2.1/2.2）。

### 1.4 README 声称 vs 实测

| 声称位置 | 声称值 | 实测值 | 偏差 |
| --- | --- | --- | --- |
| `README.md:27` | 264 个框架级测试 | **557** | −52.6% |
| `docs/P5_AGENT_TASK_RUNTIME.md:546` | `Natter.Framework.Tests` 264/264 | 557/557 | 过期 |
| `docs/testing/2026-11-18-p5-acceptance-report.md:87`、`2026-11-18-p5-webconsole-response.md:21`、`2026-11-18-p5-webconsole-retest.md:63` | 264/264 | 557/557 | 过期 |
| `docs/operations/2026-11-21-p4-5-nonsoak-status.md:12-13` | 410/410、236/236 | 557/557、303/303 | 过期 |
| `docs/testing/2026-11-21-p5-followup-regression.md:169` | 「264 → 356 → 357 → 359 → 360」 | 557 | 记录时点快照，但与上面几处互斥 |
| `docs/LIVE_TEST_PLAN.md:15`、`:197` | 27/27、26/26（同文档两处不同） | 40 个 live 用例 | 互斥且过期 |
| `README.md:101` 工程结构表 | 只列 2 个测试项目 | 实际 4 个 | 缺 WebConsole / PromptLibrary |

---

## 2. 测试质量发现

### 发现 1 · P1 · CI 与所有测试入口漏掉 1/4 的套件，且不构建 2 个产品工程

- **位置**：`.github/workflows/ci.yml:44-49`（restore）、`:51-85`（build/test 步骤）、`scripts/test.sh:7-10`、`docs/DEVELOPMENT.md:19,52-53,80,118-122`、`README.md:100-101`。
- **证据**：
  - `ci.yml` 只 restore/build/test 三个工程：`Natter.Framework.Tests`、`Natter.Live.Tests`、`Natter.Plugin.WebConsole.Tests`；全仓库检索 `PromptLibrary` 在 `.github/`、`scripts/`、`README.md`、`docs/DEVELOPMENT.md` 中 **0 命中**（只有 `Natter.slnx:24,28` 提到）。
  - `scripts/test.sh` 只跑 `tests/Natter.Framework.Tests/*.csproj`。
  - `ci.yml` 中没有 `dotnet build Natter.slnx` 或对 `plugins/Natter.Plugin.PromptLibrary`、`src/Natter.Hosting.Android` 的任何构建步骤（仅 `deploy-verify.yml` 走 Dockerfile 间接构建 Tier1 插件，且只 `grep` 出 discord/deepseek/greetings/metrics）。
- **影响**：`Natter.Plugin.PromptLibrary` 及其 36 个用例的编译/行为破坏在 CI 上完全不可见；Android 宿主（`Natter.Hosting.Android`）与 PromptLibrary 插件的编译失败也能绿灯合并。文档/脚本对外宣称的「测试全绿」实际只代表 4 套中的 1–3 套。
- **建议**：CI 改为 `dotnet build Natter.slnx -c Release`（或在 Linux job 里显式构建除 Android 外的全部工程）+ 4 个测试工程全部 restore/build/test；`scripts/test.sh` 改为循环 4 个 csproj；测试项目清单写进 `README.md` 与 `docs/DEVELOPMENT.md` 的表格。

### 发现 2 · P1 · 验收/发布文档用过期测试总数当 Go 依据

- **位置**：`README.md:27`；`docs/P5_AGENT_TASK_RUNTIME.md:546`；`docs/testing/README.md:81`；`docs/testing/2026-11-18-p5-acceptance-report.md:87`；`docs/testing/2026-11-18-p5-webconsole-retest.md:63`；`docs/operations/2026-11-21-p4-5-nonsoak-status.md:12-13`。
- **证据**：上述位置分别写 264/264、410/410、236/236、356→360 等；实测 557（Framework）、303（WebConsole）、36（PromptLibrary）。
- **影响**：验收记录不可复核 —— 读到「264/264」的人无法判断它对应哪次构建、覆盖了哪些套件；同一个仓库里存在 4 个不同的「总数」，任何一个都可能是把某一套件的子集当成全量。
- **建议**：验收文档不写绝对总数，改写「4 套件 × 用例数 + trx 产物路径 + commit sha」；README 的测试数字改成「框架 557 / WebConsole 303 / PromptLibrary 36 / Live 40（可选）」，并在 CI 里生成 trx 后由脚本回填。

### 发现 3 · P2 · 存在零断言的 live 测试（不抛异常即通过）

- **位置**：`tests/Natter.Live.Tests/DiscordProbeLiveTests.cs:110-125`。
- **证据**：`Typing_indicator_is_accepted()` 方法体内只有 `ConnectAsync` → `TriggerTypingAsync` → `DisconnectAsync`，**0 个 `Assert`**（全仓库唯一的 0 断言用例）。`TriggerTypingAsync` 若因权限/流控静默 no-op，测试照样通过。
- **影响**：该用例对「typing 指示器被真实 API 接受」这一声称不提供任何证据；live 套件里这类「冒烟即通过」会稀释真实覆盖。
- **建议**：改为断言 `TriggerTypingAsync` 的返回值/HTTP 204，或至少断言 `transport.State == Connected` 且调用后无 `Error` 事件；否则删除该用例并在 `LIVE_TEST_PLAN` 分层表里移除对应行。

### 发现 4 · P2 · 弱断言：预算测试允许空结果通过

- **位置**：`tests/Natter.Framework.Tests/ContextPipelineTests.cs:151-179`（`Context_budget_is_never_exceeded`）。
- **证据**：构造 500 字 user + 500 字 assistant、`MaxInputCharacters = 80`，最后唯一断言是
  `Assert.True(bundle.EstimatedCharacters <= 80, ...)`（`:178`）。没有断言 bundle 非空、没有断言最近窗口/系统提示仍在、没有断言截断标记。
- **影响**：只要装配器返回空 bundle（最严重的回归），测试依然通过；它与测试名「budget is never exceeded」所承诺的「超预算内容被裁剪」不是同一件事。
- **建议**：追加 `Assert.NotEmpty(bundle.Messages)`、断言系统提示片段存在、断言 user 文本被截断且总字数 ≤ 80 的同时 ≥ 一个下界（如 ≥ system 提示长度），并补一条「预算充裕时不裁剪」的对照用例。

### 发现 5 · P2 · 弱断言：串行化测试无法区分「串行」与「第二个请求被丢弃」

- **位置**：`tests/Natter.Framework.Tests/DeepSeekResponderTests.cs:116-133`（`Same_conversation_is_serialized_by_the_gate`）。
- **证据**：两个 `RespondAsync` 并发，唯一断言 `Assert.Equal(1, chat.MaxActive)`（`:133`）；延迟靠 `await Task.Delay(50)`（`:121`）制造重叠窗口。没有断言两个请求都产生了回复，也没有断言 fake 被调用 2 次。
- **影响**：gate 若直接丢弃同会话第二个请求（或死锁），`MaxActive` 仍为 1，测试通过；同时 50 ms 的窗口在负载下也可能不重叠 → 反向假阴性（理论值）。相邻的 `Idempotency` 用例反而做了「两个事件 → 一个回复」的断言，说明团队有正确写法。
- **建议**：改用 `TaskCompletionSource` 手动放行第一个请求（`gate.Entered.Task` → 启动第二个 → 断言第二个**未**进入 → 释放 → 断言 `chat.Calls == 2` 且两个 `RespondAsync` 都返回非空），彻底去掉 `Task.Delay`。

### 发现 6 · P2 · FreePort() TOCTOU + 固定回环端口，默认并行下可撞车

- **位置**：`tests/Natter.Plugin.WebConsole.Tests/WebConsoleServerTests.cs:118-125`（`FreePort()`）、`:12`、`:70`；`tests/Natter.Plugin.WebConsole.Tests/WebConsoleRuntimeTests.cs:273-278`、`:75`、`:245`。
- **证据**：`FreePort()` 绑定 `port 0` 拿到端口后**立即释放**，随后 `WebConsoleServer.StartAsync` 再绑定同一端口 —— 两次绑定之间的窗口内，并行执行的另一个测试（或同机其他进程）可能抢占。`WebConsoleRuntimeTests:75` 还用 `FreePort()` 的端口 `new TcpListener(...)` 做「端口被占用」的阻塞器，同样依赖这一窗口。
- **影响**：`dotnet test` 默认按 collection 并行；4 套件均无 `xunit.runner.json` 或 `[assembly: CollectionBehavior]`（全仓库仅 `tests/Natter.Live.Tests/LiveTestAssembly.cs:6` 关掉了 Live 的并行）。这是 CI 上最难复现的一类 flake。
- **建议**：进程级绑定一次（`TcpListener` 保持 `Start()` 直到被测服务器接管，或被测服务器支持 `Port = 0` 并回读实际端口）；或给 WebConsole 测试加 `[Collection]` 串行化，并在 4 个测试工程都加 `xunit.runner.json`（`parallelizeTestCollections` 明确声明）。

### 发现 7 · P2 · 时序型断言：sleep + 轮询，无确定性同步点

- **位置**：`tests/Natter.Framework.Tests/TaskRuntimeTests.cs:127-129`（`await Task.Delay(150); Assert.Single(dispatcher.Commands)`）、`P5ToolLoopTests.cs:470-489`（10s 轮询 + `Task.Delay(20)`）、`P5FollowupTests.cs:240,255,323,380,425,443,627,729,877-900`、`P5TaskUxAcceptanceTests.cs:630-652`、`ChatCommandRouterTests.cs:106`、`DeepSeekPluginTests.cs:162`、`DingTalkTests.cs:278,384`。
- **证据**：`Task.Delay` 在测试中 69 处（`grep -rho` 统计）；`WaitUntilAsync` 模式用墙钟 deadline 轮询（`P5ToolLoopTests.cs:470-478`）。
- **影响**：`Delay(150)` 类断言在 CI 高负载或 `dotnet test` 并行时会偶发失败；`WaitUntil` 在 10s 后抛 `TimeoutException`（无诊断信息），失败时只能看到「超时」。
- **建议**：把「等待」换成被测对象暴露的确定性信号（`TaskCompletionSource`/`ManualResetEventSlim`/`Channel`）；必须轮询时把超时值写成 `[Fact(Timeout=...)]` 并在异常里带 `output.WriteLine` 的最后状态快照。

### 发现 8 · P2 · 假验收：Acceptance 层自造 host 并重抄产品算法

- **位置**：`tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/AcceptanceHarness.cs:24`（`FileAdminConfigStore`）、`:214-215`（`ETagOf`）、`:279`（`ScriptedCommandRunner`）、`:337`（`AcceptanceRedactor`）、`:342`（`AcceptanceLogReader`）；对照 `tests/Natter.Plugin.WebConsole.Tests/Phase23/Fakes.cs:11`（`FakeAdminConfigStore`）。
- **证据**：
  - `AcceptanceHarness.ETagOf` 与产品 `WebConsoleConfigEdit.cs:131-133`、`AdminETag.Compute` 是**逐字相同**的 SHA256 实现，测试用「自己算的期望值」比对「插件算的值」，产品算法漂移时两边不同步 → 要么双绿（若同时改了 oracle）要么假红。
  - `Phase23Acceptance` 声称是验收层，但被测宿主 `IAdminConfigStore` 是 200 行手写文件 store，真实实现 `src/Natter.Infrastructure/Admin/HostAdminConfigStore.cs:16` 只在 Framework 的 `AdminHostContractTests` 里被覆盖 → 两套被测对象之间没有契约联调。
  - 同一项目里 `FakeAdminConfigStore`（内存）与 `FileAdminConfigStore`（文件）并存，等于 2 套宿主语义。
- **影响**：「验收通过」不能证明产品真实链路可用；ETag/大小限制/白名单等契约靠人工抄写保持一致，正是 `AdminContractParityTests` 想防的漂移，却在验收层重新引入。
- **建议**：验收 harness 复用 `src` 里的 `HostAdminConfigStore`（可注入临时 `config` 目录），只在「宿主缺失」用例里使用 fake；ETag 断言改为契约断言（`StartsWith("sha256:")`、`AdminETag.Matches(...)`）而非复制算法。

### 发现 9 · P2 · 555 个 async 测试只有 32 处超时保护，CI 无挂死诊断

- **位置**：全仓库；`.github/workflows/ci.yml:58-64`。
- **证据**：`public async Task` 共 555 个；`WaitAsync(` 仅 32 处、分布在 12 个文件；`[Fact(Timeout = ...)]` 0 处（`Timeout =` 命中全部是业务对象属性，如 `P5ToolLoopTests.cs:144`）；CI 无 `--blame-hang`、无 `--blame-hang-timeout`。
- **影响**：任一 `await` 挂死（gate 死锁、`Task.Delay(Infinite)` 未被取消、`HttpListener` 循环未停）会让 job 卡到 `timeout-minutes: 20` 被杀，且没有 hang dump；排查成本极高。
- **建议**：CI 加 `--blame-hang --blame-hang-timeout 90s`；关键并发用例加 `[Fact(Timeout = 15000)]`；`tests/**` 增加统一 `xunit.runner.json` 配置 `longRunningTestSeconds`。

### 发现 10 · P2 · 测试基础设施重复：15 份 `TempDirectory`、3 份 `FakeLogger`、2 套 WebConsole fake

- **位置**：
  - `TempDirectory` 15 份：`PluginStoreTests.cs:391`、`DiscordNpsTests.cs:148`、`DiscordConfigTests.cs:105`、`DeepSeekPluginTests.cs:186`、`PluginLoadContextTests.cs:110`、`RuntimeProfileTests.cs:226`、`ProfileRoutingTests.cs:88`、`SqliteConversationStoreTests.cs:140`、`ContextStorageTests.cs:82`、`DingTalkTests.cs:556`、`RuntimeCliTests.cs:394`、`OrchestrationLiveTests.cs:314`、`ManualGateLiveTests.cs:194`、`RealInboundMentionLiveTests.cs:232`、`PromptLibraryTestSupport.cs:218`。
  - `FakeLogger`（同形 `ILogger`）3 份：`DeepSeekFakes.cs:71`、`WebConsoleTestSupport.cs:22`、`PromptLibraryTestSupport.cs:14`（都有 `Debug/Information/Warning/Error/Advance`）。
  - 插件上下文 fake 3 份：`NlmpTestSupport.cs:116`（`TestHost`）、`PromptLibraryTestSupport.cs:170`（`TestPluginContext`）、`WebConsoleTestSupport.cs`（`FakeServiceProvider`）。
- **证据**：`grep -rn 'class TempDirectory'` → 15 命中；每个 Dispose 都是 `try { Directory.Delete(...) } catch { }`（见发现 12）。
- **影响**：修一个清理/命名缺陷要改 15 处；新测试作者很可能再抄第 16 份。
- **建议**：新增 `tests/Natter.Testing`（`IsPackable=false`，`InternalsVisibleTo` 或 public）承载 `TempDirectory`、`FakeLogger<TCategory>`、`TestClock`、`FakePluginContext`、`WaitUntil` 助手，4 个测试工程引用。

### 发现 11 · P2 · 覆盖盲区（核心路径无测试）

| 盲区 | 证据 | 影响 |
| --- | --- | --- |
| 插件 `AssemblyLoadContext` 卸载 | `PluginLoadContextTests.cs` 只有 3 个 Fact（`:15,:51,:72`），全部关于「私有适配器加载/共享契约解析」；**没有任何用例调用 `Unload()` 或断言可回收**。全仓库 `unload` 关键字 0 命中。 | README:271 明确承认「卸载是 best-effort」，但连「卸载后 ALC 是否 collectible」都没有断言；插件热更新/禁用路径无回归保护。 |
| Android 宿主 | 没有任何测试工程引用 `Natter.Hosting.Android`（`grep -rn 'Natter.Hosting.Android' tests --include='*.csproj'` 空）；`tests/**` 中 Android 关键字 0 命中。 | `AndroidPluginHost`（embedded/dynamic 双发行、`LoadPlugins` 开关、签名安装）完全无自动化覆盖，且不在 CI 构建（发现 1）。 |
| 并发安全（除个别点） | 只有 `Phase23/ConfigPhase2HttpTests.cs` 的 `Concurrent_saves_with_the_same_etag_produce_exactly_one_writer` 等少数用例；`WebConsoleRequestHandler`/`HttpListener` 无并发请求用例；NPS 激活无并发激活用例。 | 配置写锁、命令执行并发、插件激活竞态只能靠人工。 |
| 崩溃恢复 | 有 `TaskRecoveryLifecycle` 相关用例（`P5FollowupTests.cs:338,392,435`），但只覆盖 SQLite task store；`NpsPluginRuntime`、prompt/memory store 无「进程中断后状态」用例。 | 与 docs 中「重启恢复」声称不一致（见发现 20/21 的同类问题）。 |
| PromptLibrary 全部路径 | 36 个用例存在且实测通过，但从不执行（发现 1）。 | 等价于零覆盖。 |
| 覆盖率高但无度量 | 3 个 csproj 引用了 `coverlet.collector`（`Natter.Framework.Tests.csproj:16-19` 等），但 CI 从不加 `--collect:"XPlat Code Coverage"`，也无覆盖率阈值。 | 覆盖率是「引用了但没人知道」，无法回答「P5 Task 是否被覆盖」。 |

### 发现 12 · P3 · 清理逻辑吞异常（15 处同构）

- **位置**：上列 15 个 `TempDirectory.Dispose()`（例：`PluginStoreTests.cs:404-409`、`DiscordNpsTests.cs:160-165`、`RuntimeProfileTests.cs:238`）。
- **证据**：`try { Directory.Delete(_root, recursive: true); } catch { }`。
- **影响**：Windows 上文件句柄未释放导致清理失败会被静默吞掉 → 临时目录堆积、且「测试是否干净」不可判定。
- **建议**：吞异常可以保留，但改用 `Assert.Fail` 不可接受；正确做法是在 `Dispose` 里至少记录（`ITestOutputHelper`）或重试 3 次后忽略，并把重复实现收敛到共享 helper（发现 10）。

### 发现 13 · P3 · 非 Dispose 位置吞异常（掩盖被测代码失败）

- **位置**：`tests/Natter.Plugin.WebConsole.Tests/WebConsoleRuntimeTests.cs:63-66`（`catch (Exception) { }` 包住 `await loop.WaitAsync(TimeSpan.FromSeconds(2))`）、`:259`。
- **证据**：被测的 host loop 若抛异常，`WaitAsync` 立刻完成并把异常抛出，被 `catch (Exception) { }` 吞掉，测试继续断言 `"stopped"` 并成功。
- **影响**：后台循环崩溃与「优雅停止」在测试里不可区分。
- **建议**：只捕获 `TimeoutException`/`OperationCanceledException`，其余重新抛出。

### 发现 14 · P2 · 测试重复：P5 三个文件覆盖面高度重叠

- **位置**：`tests/Natter.Framework.Tests/P5TaskUxTests.cs`（506 行 / 9 Fact）、`P5TaskUxAcceptanceTests.cs`（829 行 / 16 Fact）、`P5FollowupTests.cs`（1015 行 / 31 方法 / 46 用例）。
- **证据**（同名/同义用例三份）：
  - status 字段：`P5TaskUxTests.cs:104`、`P5TaskUxAcceptanceTests.cs:205`、`P5FollowupTests.cs:27`；
  - tasks 列表/限额：`P5TaskUxTests.cs:148`、`P5TaskUxAcceptanceTests.cs:305,331,351`、`P5FollowupTests.cs:86`；
  - cancel 语义：`P5TaskUxTests.cs:190`、`P5TaskUxAcceptanceTests.cs:373`、`P5FollowupTests.cs:116`；
  - trace 不泄漏内容：`P5TaskUxTests.cs:21`、`P5TaskUxAcceptanceTests.cs:101`。
  两个文件各自还带一份私有 `FakeLogger`（`P5TaskUxAcceptanceTests.cs:820`、`P5FollowupTests.cs:1006`）和各自的 `WaitUntilAsync`/host 装配体。
- **影响**：同一行为 3 处维护；改一处语义要同步改 3 处，且三处对 fake 的假设不同（unit 用内存 fake，acceptance 用近似真实 host），容易产生「一处红两处绿」的伪信号。
- **建议**：合并为「契约用例（unit，内存 fake） + 端到端用例（真实 SQLite + 真实 router，各 1 条）」；重复的字段断言收敛为 `[Theory]` 数据驱动。

### 发现 15 · P3 · 命名与分类混乱

- **位置/证据**：
  - 跨文件同名用例：`Caller_cancellation_propagates`（`P5ToolRegistryTests.cs:229` 与 `PromptFragmentTests.cs:246`，互不相关）；`Default_profile_is_unchanged`（`DeepSeekToolCallingTests.cs:192`、`SparkToolCallingTests.cs:79`、`P5TaskUxTests.cs:260`）；`Tool_timeout_returns_an_explicit_error`（`P5ToolLoopTests.cs:130`、`P5NativeToolCallingTests.cs:142`）。
  - 同一项目内两套同构目录：`Phase23/*Tests.cs` 与 `Phase23Acceptance/*AcceptanceTests.cs`（`ConfigPhase2HttpTests` vs `ConfigPhase2AcceptanceTests`，`CommandPhase3HttpTests` vs `CommandPhase3AcceptanceTests`）—— 用例名几乎一一对应（`Rollback_restores_the_previous_content_and_lists_undo_entries` vs `Rollback_restores_the_previous_document_content`、`Rollback_without_a_snapshot_returns_409_and_does_not_write` vs `Rollback_without_snapshot_reports_undo_empty`），读者无法从名字判断哪个是权威。
  - 两个自造属性定义散落在测试文件里：`LiveE2EFactAttribute`（`OrchestrationLiveTests.cs:343`）、`ManualLiveFactAttribute`（`ManualGateLiveTests.cs:228`），与 `LiveFactAttribute.cs` 里的 5 个属性重复同一段 skip 逻辑。
- **建议**：所有 `LiveXxxFact` 收敛到 `LiveFactAttribute.cs` 一个文件（用 `enum LiveCapability` 参数化）；`Phase23` 与 `Phase23Acceptance` 二者保留其一，或改名 `Policy`/`EndToEnd` 明确职责。

### 发现 16 · P3 · 无凭据时的 live 跳过逻辑可靠，但 CI 未验证「确实 skip」

- **位置**：`tests/Natter.Live.Tests/LiveFactAttribute.cs:9-74`、`LiveSettings.cs:40,81-134`、`.github/workflows/ci.yml:66-75`。
- **证据**：
  - `LiveSettings.Current` 是 `static` 单次求值（`:40`），`Enabled` 只认 `NATTER_LIVE == "1"`（`:86`），未启用时直接返回空凭据且 `Has*` 全 false → 属性 `Skip` 全部非 null。实测 `NATTER_LIVE=""` → `Skipped: 40, Total: 40, Failed: 0`，符合预期。
  - 但 CI 步骤名是 "Live tests must skip without credentials"，实现只是 `dotnet test`；**退出码对「skip」和「pass」是一样的**（实测两者都是 0）。若将来 `Enabled` 解析口径变化（例如改成 `bool.TryParse` 接受 `"true"`），CI 仍绿。
  - `LiveSettings.DotEnvCandidates()`（`:140-151`）在没有 `NATTER_LIVE=1` 时也会 `ReadDotEnv`，只是结果被丢弃；一旦有人误把 `enabled` 判断提前，会静默读到真实凭据。
  - 凭据存在性判断只看 `IsNullOrWhiteSpace`（`:56-73`），占位值（如 `changeme`）会被视为有效 → 用例真跑并失败，错误信息指向「API 401」而非「凭据未配置」。
- **影响**：opt-in 机制本身没问题，但缺少防回归断言，「跳过」这一行为没有测试保护。
- **建议**：CI 增加一步解析 trx（`Skipped == 40` 且 `Passed == 0`，或 `dotnet test` 后 grep `Skipped: 40`），不满足即失败；把 `LiveSettings` 的解析与 `Has*` 判定抽成可单测的纯函数，加 `[Theory]` 覆盖 `1/true/yes/空/null` 与占位值。

### 发现 17 · P3 · 时间/随机/文件系统依赖的其余面

- **证据**：`DateTimeOffset.UtcNow` 在测试中仅 1 处（`ContextPipelineTests.cs:140-142` 构造消息时间，不参与断言）；无 `new Random(`（随机源已全部可控）；文件系统依赖集中在 15 处 `Path.GetTempPath()` 派生目录（GUID 命名，冲突风险低）与 3 处 `AppContext.BaseDirectory` 拷贝（`PluginLoadContextTests.cs:107-113` 每次拷贝整个输出目录的所有 dll，I/O 偏重）。
- **影响**：整体可控；主要残余风险是发现 6（端口）与发现 7（sleep）。
- **建议**：`CopyTestBinaries` 改为只拷贝测试需要的 3–5 个 dll，或复用一次静态 fixture。

---

## 3. 文档一致性

### 3.1 数字类（已在 §1.4 汇总）

### 发现 18 · P1 · README 对钉钉/星火「真实凭据未接入」的结论与测试记录相反

- **位置**：`README.md:275`、`docs/LIVE_TEST_PLAN.md:15`（两处都说「第二上游/消费者……真实平台凭据未接入」）；对照 `docs/testing/2026-11-15-live-dingtalk-spark.md:1-16` 与 `docs/testing/README.md:64`。
- **证据**：
  - README:275：「第二上游/消费者（钉钉 Stream + 星火 MaaS）已完成代码、本机 WebSocket 与 fake HTTP 交叉验证，**真实平台凭据未接入**」。
  - `docs/testing/2026-11-15-live-dingtalk-spark.md`：标题「端到端通过」；`§2` 记录 `POST https://api.dingtalk.com/v1.0/gateway/connections/open` → **200** + ticket、`GET .../v2/models` → **200**、`POST .../v2/chat/completions` → **200**（usage `prompt=33 completion=6`）、操作者在真实钉钉群 @机器人 **收到 20 字回复**；`docs/testing/README.md:64` 复述「端到端通过……自动化 Spark 7/7 + DingTalk 2/2」。
  - 代码侧佐证：`tests/Natter.Live.Tests/SparkLiveTests.cs`（7 个 `[SparkLiveFact]`）、`DingTalkStreamLiveTests.cs`（2 个 `[DingTalkLiveFact]`）确实存在并需要真实凭据才会运行。
- **影响**：关于「哪些能力经过真实验证」这一对外声称，两份一级文档给出相反结论；读者无法判断 `natter.transport.dingtalk` / `natter.feature.ai.spark` 是「已验证」还是「未验证」。这是能力边界声称，不是笔误。
- **建议**：二者择一：若 11-15 的真实联调仍有效，README:275 与 LIVE_TEST_PLAN:15 应改为「真实凭据已于 2026-11-15 验证（见记录），此后未回归」；若凭据已失效/未复测，则在测试记录中标注「仅 11-15 一次性有效，已过期」，并统一说法。

### 发现 19 · P2 · `docs/LIVE_TEST_PLAN.md` 内部自相矛盾

- **位置**：`:3` vs `:17`；`:15` vs `:197`；`:242`。
- **证据**：
  - 第 3 行「状态：Accepted · L1–L4 已执行通过；**L5 人工闸门待人工触发**」；第 17 行「修复 `687f27f` 后 13:50 复测**通过**……`BUG-003` 关闭」。同一文档里 L5 既「待触发」又「已通过」。
  - 第 15 行「**27/27 通过**（L1 8、L2 9、L2b 2、L3/L3b/L3c 3、L4 5）」；第 197 行「L1 8/8, L2 9/9, L2b 2/2, L3/L3b 2/2, L4 5/5 = **26/26**」。5+9+3+2+2+2+3+5 = 26 还是 27 取决于 L3c。
  - 第 242 行仍写「真正的人类触发留给 L5」，与第 17 行冲突。
  - 分层矩阵（`:42-49`）只覆盖 28 个用例，漏掉 12 个：矩阵列出 L1 8（`DeepSeekLiveTests` 实际 8 ✓）、L2 9（`DiscordProbeLiveTests` 5 + `DiscordUpstreamLiveTests` 4 ✓）、L2b 2、L3/L3b/L3c 3（`OrchestrationLiveTests`+`FilePluginE2ELiveTests`+`RealInboundMentionLiveTests`）、L4 5、L5 1 = 28；而 `MemoryRecallLiveTests`(1) + `MemoryRecallLifecycleLiveTests`(2) + `SparkLiveTests`(7) + `DingTalkStreamLiveTests`(2) = **12 个用例完全不在矩阵内**。逐文件实测：DeepSeek 8、DiscordProbe 5、DiscordUpstream 4、GatewayIngress 2、Orchestration 1、FilePluginE2E 1、RealInboundMention 1、DeploymentCli 5、ManualGate 1、MemoryRecall 1、MemoryRecallLifecycle 2、Spark 7、DingTalk 2 = **40**。
- **影响**：`docs/LIVE_TEST_PLAN.md` 是 README 指向的「分层测试矩阵」，它漏掉 12 个用例、且对 L5 状态自相矛盾，无法作为覆盖清单使用。
- **建议**：以当前 40 个 `[XxxLiveFact]` 重新生成分层表（脚本可从测试名 `DisplayName` 抽取）；状态段只保留一行机器可更新的结果，删除历史叙述段落（历史留给 `docs/testing/` 记录）。

### 发现 20 · P2 · `docs/P4_TIER1_PRODUCTIZATION.md` 声称 24h soak 完成，状态表与运维文档都说未执行/进行中

- **位置**：`docs/P4_TIER1_PRODUCTIZATION.md:14` vs `:40`、`:457`；`docs/operations/2026-11-21-p4-5-nonsoak-status.md:16`；`docs/testing/README.md:70`。
- **证据**：
  - `P4:14`（文档开篇的完成定义）：「`serve` 能作为服务**连续运行 24h**，配置/state 可备份恢复……」
  - `P4:40`：「运维闭环：备份/恢复/升级/**24h soak 未验证**」；`P4:457` 状态表：「24h soak | **未执行**」。
  - `nonsoak-status:16`：「24h soak | 🔄 **进行中**」；`docs/testing/README.md:70`：「**24h soak 未执行**」。
  - `README.md:246` 把 P4 描述为「serve 部署/备份/**24h soak** 与发布闸门」，读者会理解为已完成。
- **影响**：发布闸门的一项（24h soak）在四份文档里有三种状态（已完成 / 未验证 / 进行中），运维据此判断是否可 Go 时会得到错误结论。
- **建议**：`P4:14` 改为「目标态」并显式标注当前未达成；24h soak 的真实状态只由 `docs/operations/` 的最新一份记录持有，其余文档只引用链接。

### 发现 21 · P2 · `docs/DEVELOPMENT.md` 描述的插件测试机制与 csproj 实际不符

- **位置**：`docs/DEVELOPMENT.md:705`，对照 `tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj:31-38`、`:41-58`。
- **证据**：文档写「测试项目通过 `ProjectReference ... ReferenceOutputAssembly="false"` + MSBuild `Copy` 目标把插件复制到 `$(OutDir)plugins/`」。实际 csproj 的 16 个 `ProjectReference` **都是普通引用**（无 `ReferenceOutputAssembly="false"`，仓库里该属性只出现在 `src/Natter.Cli/Natter.Cli.csproj:23,25`）；`CopyTestPlugins` 目标只复制 **Greetings 一个**插件（dll + plugin.json），Metrics/Discord/DingTalk/Memory 四个示例插件都是普通引用被编译进输出目录，并未以「插件目录」形式 staged。
- **影响**：想按文档新增「插件目录」测试的人会照抄一个不存在的机制；也解释了为什么 `plugins/greetings` 之外的插件加载只能靠 `PluginPackageTestBuilder` 现造包。
- **建议**：文档改写为实际机制（「示例插件作为普通 ProjectReference 编译进测试输出；需要文件系统插件目录时用 `PluginPackageTestBuilder` 或 MSBuild Copy 目标」），或把 csproj 改成文档描述的形态。

### 发现 22 · P3 · README 命令表：列数不一致 + `|` 未转义破坏渲染

- **位置**：`README.md:170-180`（表头 `:149-150` 为 2 列）。
- **证据**：`awk` 统计每个表格行内的 `|` 数量：表头与 `:170,:171` 为 3（2 列），`:172-176` 为 4–5（3–4 列），`:177-180` 为 4（3 列）。即 172–180 行多出一列，Markdown 渲染时被丢弃。更严重的是 `:173` 的 `` `forget <id|text>` `` —— 行内代码里的 `|` 在表格中必须写成 `\|`，否则 `id` 与 `text>` 被拆成两个单元格。
- **影响**：命令表是 README 最常被复制的部分；`forget` 一行渲染错位，读者可能照抄成 `forget <id`。
- **建议**：统一为 2 列（命令 | 说明），把示例移到说明列或用 `\|` 转义；`:172-176` 的「示例」列合并进说明。

### 发现 23 · P3 · README 的 live 凭据路径与脚本/代码的查找顺序不一致

- **位置**：`README.md:122-124`；`scripts/live-test.sh:20-22`；`tests/Natter.Live.Tests/LiveSettings.cs:140-151`。
- **证据**：README 只说 `cp .env.example artifacts/live/.env`；`live-test.sh` 优先 `$HOME/.config/natter/live.env`，回退 `artifacts/live/.env`；`LiveSettings` 优先 `NATTER_LIVE_ENV`，再 `~/.config/natter/live.env`，再仓库内 `artifacts/live/.env`。而 `README.md:123` 的命令会把凭据写进**仓库内目录**，与 `docs/testing/2026-11-15-asset-closure.md`「凭据移出工作区」的收口结论相反。
- **影响**：读者按 README 操作会把密钥落在工作区；三处优先级不同时，运维改了 `~/.config` 的 `.env` 却发现行为没变（因为脚本与代码取值顺序不同）。
- **建议**：README 改为推荐 `~/.config/natter/live.env`（与资产收口结论一致），并注明三处查找顺序；`scripts/live-test.sh` 与 `LiveSettings` 统一成同一顺序（脚本是唯一入口，可直接 export 给测试）。

### 发现 24 · P3 · `docs/testing/README.md` 索引缺 3 份记录，且有一行日期与文件名不符

- **位置**：`docs/testing/README.md:57-83`（索引表）、`:70`、`:83`。
- **证据**：目录中实际存在但索引未收录：`2026-11-21-p5-followup-regression.md`、`2026-11-21-webconsole-phase23-acceptance.md`、`2026-11-22-p5-task-ux-acceptance.md`。索引 `:83` 行首日期写 `2026-11-21`，链接的是 `2026-11-18-p5-webconsole-retest.md`（文件名日期 11-18）；`:82` 行（`2026-11-18 | webconsole-test-report`）与 `:83` 内容高度重叠（都写 Phase 2/3 仍 501），后者又写「Phase 2/3 已 Go」，两行冲突。
- **影响**：`docs/testing/README.md` 自称「每次测试必须留下可追溯的书面记录」的索引，却漏掉最近 3 次（含最新的 P5 task UX 验收），读者会以为工作停在 11-18。
- **建议**：补 3 行；`:83` 的日期与结论按文件名/记录内容修正；把「Phase 2/3 仍 501」与「Phase 2/3 已 GO」的状态列合并为按时间点的一列。

### 发现 25 · P3 · `scripts/test.sh` 与 README 的「测试」入口只覆盖一个套件

- **位置**：`scripts/test.sh:7-10`、`README.md:120`、`docs/DEVELOPMENT.md:19,52,118-122,779`。
- **证据**：脚本与所有文档示例都只跑 `Natter.Framework.Tests`；`dotnet test`（无参数）从未出现在文档里（而 `DEVELOPMENT.md:32` 又提醒不要直接 `dotnet build Natter.slnx`）。
- **影响**：开发者执行「标准测试命令」时，303（WebConsole）+36（PromptLibrary）+40（Live）个用例不会运行；与发现 1 是同一条链路的上下游。
- **建议**：`scripts/test.sh` 支持 `all|framework|webconsole|promptlibrary|live` 五个目标，默认 `all`；文档改用 `scripts/test.sh all`。

---

## 4. 可维护性

| 维度 | 现状 | 证据 | 建议 |
| --- | --- | --- | --- |
| 共享基础设施 | 无。4 个测试工程各自维护 fake/clock/temp-dir | 发现 10（15×`TempDirectory`、3×`FakeLogger`、3×插件上下文 fake） | 建 `tests/Natter.Testing` |
| WebConsole fake 体系 | 两套并存（`Phase23/Fakes.cs` 内存版、`Phase23Acceptance/AcceptanceHarness.cs` 文件版） | 发现 8 | 收敛为一套 + 真实 host 实现 |
| 断言 oracle | 部分用例重抄产品算法（ETag） | `AcceptanceHarness.cs:214` | 改契约断言 |
| 测试注册 | csproj 手工维护 `ProjectReference` 列表（16 条），新增示例插件要手工加 | `Natter.Framework.Tests.csproj:31-38` | 用通配 `<ProjectReference Include="../../samples/**/*.csproj" />` 或 Directory.Build.targets |
| 结果产物 | Ci 上传 trx，但无解析/阈值 | `ci.yml:88-99` | 加 trx 汇总步骤，回填 README 数字（解决发现 2） |
| 覆盖率 | 3 个 csproj 引用 `coverlet.collector`，CI 从不收集 | `Natter.Framework.Tests.csproj:16-19` 等 | `--collect:"XPlat Code Coverage"` + 报告 artifact；或删除引用避免误导 |
| 并行配置 | 4 工程均无 `xunit.runner.json`；只有 Live 用 `CollectionBehavior` 关并行 | `LiveTestAssembly.cs:6` | 4 工程统一配置 |

---

## 5. 逐文件评级

评级口径：
- **A**：行为-断言匹配、有独立 oracle、无时序/端口依赖。
- **B**：可用，但有 sleep/轮询、单断言偏弱、或依赖共享 fake。
- **C**：存在弱断言/吞异常/零断言/重复实现被测逻辑之一。
- **H**：纯 helper/fake（不评用例质量，评其重复度）。

（表格见下节，由脚本从源码统计生成，用例数 = Fact + InlineData。）

| 文件 | 行数 | 用例 | Theory | 断言 | 断言/方法 | 评级 | 备注 |
| --- | ---: | ---: | ---: | ---: | ---: | :---: | --- |
| `tests/Natter.Framework.Tests/AdminHostContractTests.cs` | 1294 | 53 | 2 | 200 | 4.9 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/AggregateRootTests.cs` | 78 | 5 | 0 | 12 | 2.4 | A |  |
| `tests/Natter.Framework.Tests/ApplicationDispatcherTests.cs` | 79 | 2 | 0 | 3 | 1.5 | A |  |
| `tests/Natter.Framework.Tests/ChatCliTests.cs` | 112 | 5 | 0 | 24 | 4.8 | A |  |
| `tests/Natter.Framework.Tests/ChatCommandRouterTests.cs` | 197 | 5 | 0 | 29 | 5.8 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/ChatOrchestratorTests.cs` | 231 | 5 | 0 | 11 | 2.2 | A |  |
| `tests/Natter.Framework.Tests/CliParserTests.cs` | 124 | 8 | 0 | 20 | 2.5 | A |  |
| `tests/Natter.Framework.Tests/CliSmokeTests.cs` | 114 | 6 | 0 | 21 | 3.5 | A |  |
| `tests/Natter.Framework.Tests/ContextPipelineTests.cs` | 312 | 10 | 0 | 31 | 3.1 | B | 预算用例只断言 `EstimatedCharacters`（:178），空 bundle 也通过 |
| `tests/Natter.Framework.Tests/ContextStorageTests.cs` | 97 | 3 | 0 | 13 | 4.3 | A |  |
| `tests/Natter.Framework.Tests/DeepSeekChatClientTests.cs` | 208 | 10 | 1 | 34 | 4.9 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/DeepSeekFakes.cs` | 103 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Framework.Tests/DeepSeekPluginTests.cs` | 209 | 3 | 0 | 24 | 8.0 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/DeepSeekResponderTests.cs` | 375 | 22 | 1 | 48 | 3.4 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/DeepSeekToolCallingTests.cs` | 294 | 8 | 0 | 38 | 4.8 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/DingTalkFakes.cs` | 46 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Framework.Tests/DingTalkTests.cs` | 567 | 9 | 0 | 56 | 6.2 | B | sleep/轮询；FreePort TOCTOU |
| `tests/Natter.Framework.Tests/DiscordConfigTests.cs` | 126 | 3 | 0 | 13 | 4.3 | A |  |
| `tests/Natter.Framework.Tests/DiscordDmAllowlistTests.cs` | 104 | 4 | 0 | 6 | 1.5 | A |  |
| `tests/Natter.Framework.Tests/DiscordEgressProxyTests.cs` | 55 | 5 | 0 | 7 | 1.4 | B | 单方法断言 1.4 条，偏薄 |
| `tests/Natter.Framework.Tests/DiscordFakeTransport.cs` | 113 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Framework.Tests/DiscordMentionGateTests.cs` | 99 | 2 | 0 | 6 | 3.0 | A |  |
| `tests/Natter.Framework.Tests/DiscordNpsTests.cs` | 191 | 2 | 0 | 16 | 8.0 | A |  |
| `tests/Natter.Framework.Tests/DiscordTransportTests.cs` | 473 | 13 | 0 | 91 | 7.0 | A |  |
| `tests/Natter.Framework.Tests/NlmpCommandTests.cs` | 311 | 11 | 0 | 48 | 4.4 | A |  |
| `tests/Natter.Framework.Tests/NlmpEventTests.cs` | 150 | 4 | 0 | 33 | 8.2 | A |  |
| `tests/Natter.Framework.Tests/NlmpJsonTests.cs` | 168 | 6 | 0 | 34 | 5.7 | A |  |
| `tests/Natter.Framework.Tests/NlmpModelTests.cs` | 112 | 6 | 0 | 30 | 5.0 | A |  |
| `tests/Natter.Framework.Tests/NlmpTestSupport.cs` | 183 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Framework.Tests/NpsCliTests.cs` | 195 | 5 | 0 | 30 | 6.0 | A |  |
| `tests/Natter.Framework.Tests/NpsGraphTests.cs` | 132 | 7 | 0 | 17 | 2.4 | A |  |
| `tests/Natter.Framework.Tests/NpsManifestTests.cs` | 173 | 7 | 0 | 38 | 5.4 | A |  |
| `tests/Natter.Framework.Tests/NpsRuntimeTests.cs` | 1130 | 25 | 0 | 97 | 3.9 | A |  |
| `tests/Natter.Framework.Tests/P5FollowupTests.cs` | 1014 | 46 | 2 | 142 | 4.6 | B | sleep/轮询；与另两个 P5 文件覆盖重复 |
| `tests/Natter.Framework.Tests/P5NativeToolCallingTests.cs` | 385 | 10 | 0 | 42 | 4.2 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/P5ReplayAndIdempotencyTests.cs` | 220 | 4 | 0 | 28 | 7.0 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/P5SchemaTests.cs` | 58 | 2 | 0 | 2 | 1.0 | B | 单方法断言 1.0 条，偏薄 |
| `tests/Natter.Framework.Tests/P5TaskUxAcceptanceTests.cs` | 828 | 16 | 0 | 104 | 6.5 | B | sleep/轮询；与 P5TaskUxTests/P5FollowupTests 覆盖重复 |
| `tests/Natter.Framework.Tests/P5TaskUxTests.cs` | 505 | 9 | 0 | 72 | 8.0 | B | sleep/轮询；与另两个 P5 文件覆盖重复 |
| `tests/Natter.Framework.Tests/P5ToolLoopTests.cs` | 617 | 16 | 0 | 66 | 4.1 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/P5ToolRegistryTests.cs` | 407 | 28 | 2 | 68 | 3.8 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/PluginLoadContextTests.cs` | 133 | 3 | 0 | 14 | 4.7 | A |  |
| `tests/Natter.Framework.Tests/PluginLoadingTests.cs` | 95 | 4 | 0 | 20 | 5.0 | A |  |
| `tests/Natter.Framework.Tests/PluginPackageTestBuilder.cs` | 185 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Framework.Tests/PluginStoreTests.cs` | 411 | 13 | 0 | 71 | 5.5 | A |  |
| `tests/Natter.Framework.Tests/ProfileRoutingTests.cs` | 104 | 4 | 0 | 7 | 1.8 | A |  |
| `tests/Natter.Framework.Tests/PromptCompilerTests.cs` | 650 | 37 | 0 | 113 | 3.1 | A |  |
| `tests/Natter.Framework.Tests/PromptFragmentTests.cs` | 492 | 16 | 0 | 75 | 4.7 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/RuntimeCliTests.cs` | 409 | 11 | 0 | 51 | 4.6 | B | sleep/轮询；FreePort TOCTOU |
| `tests/Natter.Framework.Tests/RuntimeProfileTests.cs` | 241 | 7 | 0 | 22 | 3.1 | A |  |
| `tests/Natter.Framework.Tests/RxDomainEventBusTests.cs` | 97 | 4 | 0 | 13 | 3.2 | A |  |
| `tests/Natter.Framework.Tests/SatoriMappingTests.cs` | 337 | 12 | 0 | 91 | 7.6 | A |  |
| `tests/Natter.Framework.Tests/SatoriUpstreamTests.cs` | 190 | 5 | 0 | 22 | 4.4 | A |  |
| `tests/Natter.Framework.Tests/SparkChatClientTests.cs` | 144 | 5 | 0 | 32 | 6.4 | A |  |
| `tests/Natter.Framework.Tests/SparkToolCallingTests.cs` | 192 | 3 | 0 | 15 | 5.0 | A |  |
| `tests/Natter.Framework.Tests/SqliteConversationStoreTests.cs` | 155 | 4 | 0 | 24 | 6.0 | A |  |
| `tests/Natter.Framework.Tests/TaskRuntimeTests.cs` | 386 | 12 | 0 | 53 | 4.4 | B | sleep/轮询 |
| `tests/Natter.Framework.Tests/ToolContractTests.cs` | 275 | 32 | 2 | 35 | 2.5 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/DeepSeekLiveTests.cs` | 301 | 8 | 0 | 35 | 4.4 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/DeploymentCliLiveTests.cs` | 153 | 5 | 0 | 23 | 4.6 | A |  |
| `tests/Natter.Live.Tests/DingTalkStreamLiveTests.cs` | 65 | 2 | 0 | 6 | 3.0 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/DiscordProbeLiveTests.cs` | 166 | 5 | 0 | 9 | 1.8 | C | **含 1 个零断言用例（:110-125）**；sleep/轮询 |
| `tests/Natter.Live.Tests/DiscordRestClient.cs` | 159 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Live.Tests/DiscordUpstreamLiveTests.cs` | 197 | 4 | 0 | 20 | 5.0 | A |  |
| `tests/Natter.Live.Tests/FilePluginE2ELiveTests.cs` | 165 | 1 | 0 | 15 | 15.0 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/GatewayIngressLiveTests.cs` | 145 | 2 | 0 | 10 | 5.0 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/LiveDeployment.cs` | 216 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Live.Tests/LiveDiscordOracle.cs` | 63 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Live.Tests/LiveFactAttribute.cs` | 74 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Live.Tests/LiveSettings.cs` | 191 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Live.Tests/LiveTestAssembly.cs` | 6 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Live.Tests/ManualGateLiveTests.cs` | 236 | 1 | 0 | 8 | 8.0 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/MemoryRecallLifecycleLiveTests.cs` | 192 | 2 | 0 | 6 | 3.0 | B | sleep/轮询；对 LLM 文本断言 |
| `tests/Natter.Live.Tests/MemoryRecallLiveTests.cs` | 105 | 1 | 0 | 1 | 1.0 | B | 单方法断言 1.0 条，偏薄；sleep/轮询；对 LLM 文本断言 |
| `tests/Natter.Live.Tests/OrchestrationLiveTests.cs` | 349 | 1 | 0 | 16 | 16.0 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/RealInboundMentionLiveTests.cs` | 253 | 1 | 0 | 8 | 8.0 | B | sleep/轮询 |
| `tests/Natter.Live.Tests/SparkLiveTests.cs` | 181 | 7 | 0 | 23 | 3.3 | A |  |
| `tests/Natter.Plugin.PromptLibrary.Tests/PromptLibraryLoaderTests.cs` | 378 | 18 | 0 | 44 | 2.4 | A |  |
| `tests/Natter.Plugin.PromptLibrary.Tests/PromptLibraryNpsIntegrationTests.cs` | 125 | 1 | 0 | 11 | 11.0 | A |  |
| `tests/Natter.Plugin.PromptLibrary.Tests/PromptLibraryPluginTests.cs` | 203 | 9 | 0 | 42 | 4.7 | A |  |
| `tests/Natter.Plugin.PromptLibrary.Tests/PromptLibraryProviderTests.cs` | 242 | 8 | 0 | 46 | 5.8 | A |  |
| `tests/Natter.Plugin.PromptLibrary.Tests/PromptLibraryTestSupport.cs` | 252 | 0 | 0 | 0 | 0.0 | H |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/AdminContractParityTests.cs` | 97 | 21 | 4 | 17 | 2.4 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/AuditPathTests.cs` | 176 | 10 | 1 | 25 | 3.1 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/CommandPhase3HttpTests.cs` | 288 | 13 | 1 | 46 | 4.6 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/ConfigPhase2HttpTests.cs` | 446 | 17 | 0 | 91 | 5.4 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/Fakes.cs` | 291 | 0 | 0 | 0 | 0.0 | H | 与 AcceptanceHarness 的 FileAdminConfigStore 构成双套宿主语义 |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/LogsFilterExportTests.cs` | 257 | 18 | 1 | 49 | 4.5 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/LogsPolicyTests.cs` | 157 | 21 | 2 | 31 | 3.4 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/Phase1UnchangedTests.cs` | 200 | 9 | 0 | 48 | 5.3 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/ReadOnlyCodesTests.cs` | 154 | 6 | 0 | 21 | 3.5 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23/RoleEnforcementTests.cs` | 255 | 9 | 0 | 39 | 4.3 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/AcceptanceHarness.cs` | 552 | 0 | 0 | 0 | 0.0 | H | 重抄产品 ETag 算法（:214）；手写 FileAdminConfigStore（:24） |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/CommandPhase3AcceptanceTests.cs` | 311 | 14 | 1 | 53 | 4.1 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/ConfigPhase2AcceptanceTests.cs` | 305 | 18 | 1 | 57 | 4.4 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/HostContractAcceptanceTests.cs` | 137 | 12 | 2 | 23 | 3.3 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleAuditTests.cs` | 125 | 4 | 0 | 14 | 3.5 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleAuthTests.cs` | 118 | 8 | 0 | 34 | 4.2 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleCommandPolicyTests.cs` | 141 | 16 | 1 | 29 | 2.6 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleConfigEditTests.cs` | 142 | 17 | 1 | 37 | 3.4 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleDiagnosticsTests.cs` | 62 | 2 | 0 | 17 | 8.5 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleHostPortTests.cs` | 190 | 6 | 0 | 45 | 7.5 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleManifestTests.cs` | 130 | 7 | 0 | 33 | 4.7 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleOptionsTests.cs` | 163 | 23 | 3 | 22 | 1.6 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleRedactionTests.cs` | 96 | 13 | 2 | 20 | 2.9 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleRequestHandlerTests.cs` | 524 | 27 | 0 | 88 | 3.3 | A |  |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleRuntimeTests.cs` | 290 | 8 | 1 | 24 | 4.0 | C | FreePort TOCTOU；非清理位置吞异常 |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleServerTests.cs` | 132 | 4 | 0 | 23 | 5.8 | B | FreePort TOCTOU |
| `tests/Natter.Plugin.WebConsole.Tests/WebConsoleTestSupport.cs` | 311 | 0 | 0 | 0 | 0.0 | H |  |

评级分布：A 61 · B 31 · C 2 · H(helper) 15；共 109 个手写文件（94 个含用例 + 15 个 helper）。「断言/方法」按测试方法计（Theory 的多行 InlineData 复用同一断言序列），因此低于 1.5 只表示该方法断言偏少。相对自动启发式的两处人工下调：`ContextPipelineTests`（预算弱断言）→ B、`DiscordProbeLiveTests`（零断言用例）→ C，见发现 3/4。

---

## 6. Top 8 发现（按影响排序）

| # | 严重度 | 位置 | 一句话 |
| --- | --- | --- | --- |
| 1 | **P1** | `.github/workflows/ci.yml:44-49` | CI 只跑 3/4 套件；`Natter.Plugin.PromptLibrary.Tests`(36) 与 PromptLibrary/Android 两个产品工程从不构建，编译破坏可绿灯合并。 |
| 2 | **P1** | `README.md:27` + `docs/P5_AGENT_TASK_RUNTIME.md:546` + `docs/operations/2026-11-21-p4-5-nonsoak-status.md:12-13` | 验收 Go 依据的测试总数（264/410/236）与实测（557/303/36）不符，且仓库里并存 4 个互斥总数。 |
| 3 | **P1** | `README.md:275` vs `docs/testing/2026-11-15-live-dingtalk-spark.md:1-16` | 钉钉/星火「真实凭据未接入」与「端到端通过、Spark 7/7」相反，能力边界声称不可信。 |
| 4 | **P2** | `tests/Natter.Plugin.WebConsole.Tests/Phase23Acceptance/AcceptanceHarness.cs:214-215` | 验收 harness 逐字重抄 ETag 算法并手写 host store，产品契约漂移时测试仍绿（假验收）。 |
| 5 | **P2** | `tests/Natter.Framework.Tests/ContextPipelineTests.cs:178` | 预算测试只断言 `EstimatedCharacters <= 80`，空 bundle 也通过。 |
| 6 | **P2** | `tests/Natter.Framework.Tests/DeepSeekResponderTests.cs:121,133` | 串行化测试靠 `Task.Delay(50)` + 只断言 `MaxActive == 1`，丢弃/死锁都能过。 |
| 7 | **P2** | `tests/Natter.Plugin.WebConsole.Tests/WebConsoleServerTests.cs:118-125`、`WebConsoleRuntimeTests.cs:273-278` | `FreePort()` TOCTOU + 全仓无 `xunit.runner.json`，默认并行下的典型 flake。 |
| 8 | **P2** | `docs/LIVE_TEST_PLAN.md:3 vs :17、:15 vs :197` | 同一文档里 L5「待触发/已通过」、总数「27/27 / 26/26」互相矛盾，且漏列 12 个 live 用例。 |

补充（并列第 8 档，见正文）：发现 9（555 个 async 用例无超时/无 blame-hang）、发现 10（15 份 `TempDirectory`）、发现 11（ALC 卸载与 Android 宿主零覆盖）。

---

## 附录 A · 复现命令

```bash
cd /home/ndm/Projects/csharp/Natter

# 用例数（权威）
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -c Debug --no-build
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -c Debug --no-build
dotnet test tests/Natter.Plugin.PromptLibrary.Tests/Natter.Plugin.PromptLibrary.Tests.csproj -c Debug --no-build
NATTER_LIVE="" dotnet test tests/Natter.Live.Tests/Natter.Live.Tests.csproj -c Debug --no-build   # 期望 Skipped: 40

# 声明式统计
grep -rho '\[Fact\]' tests --include='*.cs' | wc -l          # 722
grep -rho '\[Theory\]' tests --include='*.cs' | wc -l        # 31
grep -rho '\[InlineData' tests --include='*.cs' | wc -l      # 174
grep -rho 'Assert\.[A-Za-z]*' tests --include='*.cs' | wc -l  # 3421

# 基础设施重复
grep -rn 'class TempDirectory' tests --include='*.cs' | grep -v /obj/ | wc -l   # 15
grep -rn 'Trait("Category"' tests/Natter.Live.Tests --include='*.cs' | wc -l     # 13

# CI 覆盖
grep -n 'dotnet restore\|dotnet test\|dotnet build' .github/workflows/ci.yml
grep -rn 'PromptLibrary' .github/workflows scripts README.md docs/DEVELOPMENT.md   # 0 命中

# 时序/端口风险
grep -rn 'Task\.Delay' tests --include='*.cs' | grep -v /obj/ | wc -l
grep -rn 'FreePort' tests --include='*.cs' | grep -v /obj/
grep -rn 'Fact(Timeout' tests --include='*.cs' | wc -l   # 0
```

---

*本报告只读产出，未修改任何源码、测试或既有文档。*
