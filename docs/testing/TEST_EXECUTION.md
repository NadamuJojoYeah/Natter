# 测试执行与挂起诊断（S2-F5）

本文档说明本地与 CI 如何运行测试、如何设置超时，以及测试挂起（hang）时如何取证。

## 1. 标准命令

```bash
# 单次全量（框架）
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj

# Release 验收口径：先重建，再 --no-build（禁止复用旧 bin，见 2026-11-22-s2-batch1-acceptance.md §0）
dotnet build tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -c Release
dotnet test  tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj -c Release --no-build

# 全部套件（与 CI build-test 一致）
for p in $(find tests -name '*.csproj' -not -path '*/obj/*'); do
  dotnet test "$p" -c Release --no-build
done
```

## 2. 超时与长跑标记

| 层级 | 机制 | 说明 |
| --- | --- | --- |
| 单用例 | `[Fact(Timeout = 20_000)]` | 关键并发/网络用例显式设上限；超时直接失败而不是挂死 |
| 单用例 | `await task.WaitAsync(TimeSpan.FromSeconds(10))` | 等待型断言一律带超时，不用裸 `Task.Delay` 当同步手段 |
| 套件 | `tests/Natter.Framework.Tests/xunit.runner.json` → `longRunningTestSeconds: 30` | 超过 30s 的用例会在输出里被标记为 long-running，便于发现慢性挂起 |
| CI | `--blame-hang --blame-hang-timeout 10m` | 见 §3 |

> xunit v2 没有“整包全局超时”开关；全局兜底放在 CI 的 `--blame-hang-timeout`（杀进程 + 生成 dump），
> 单用例超时用 `[Fact(Timeout=…)]` 与 `WaitAsync` 表达。

## 3. 挂起取证（blame-hang）

```bash
# 本地复现挂起：保留 hang 时的进程与 dump
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --blame-hang --blame-hang-timeout 10m \
  --blame-hang-dump-type full --blame-hang-dump-filename natter-hang.dmp \
  --logger "console;verbosity=normal"

# CI：生产环境用 mini dump（体积小），产物随 TestResults/ 一起归档
dotnet test <project> -c Release --no-build \
  --blame-hang --blame-hang-timeout 10m --blame-hang-dump-type mini \
  --results-directory TestResults
```

排查顺序：

1. `dotnet test --list-tests` 拿到全量用例，`--filter` 二分定位挂起用例；
2. 看 `TestResults/*.trx` 里最后一个 started-not-finished 的用例；
3. 对并发用例检查是否混入了裸 `Task.Delay` 作为同步、是否缺少 `WaitAsync`；
4. dump 用 `dotnet-dump analyze` 看托管栈（`clrstack -all`）。

## 4. 已知会导致挂起的写法（避免）

- 用 `Task.Delay(ms)` 假设“另一路已完成”——改用 `TaskCompletionSource` 门 + `WaitAsync`；
- `await` 一个永不 complete 的 `TaskCompletionSource` 而没有取消/超时；
- 在 `lock` 内 `await`（同步上下文死锁）；
- 依赖并行执行顺序的断言（用 `MaxActive`/事件队列显式观测）。

## 5. Android 宿主（CI 不可执行，人工步骤）

`src/Natter.Hosting.Android` 需要 android workload + Android SDK/JDK，CI 只做 Release 编译（`solution-build`，受
`vars.ANDROID_BUILD` 门控）。真机清单与权限验证走人工：

1. `dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj -c Release`；
2. 解包 APK，确认 `AndroidManifest.xml` 恰含 1 个 `android.permission.INTERNET`、1 个 `<uses-permission>`；
3. 真机安装后确认：能发起 HTTPS 请求、无 `FOREGROUND_SERVICE` 权限申请、`allowBackup=false` 生效；
4. 把设备型号/Android 版本/操作步骤与截图贴进 `docs/testing/` 报告。

> 未在真机验证前，任何“Android 通过”的结论都应写成「编译通过 + 清单静态校验通过，真机待人工」。

## 6. 与验收报告的关系

- 每个验收报告都应写明：构建方式（Debug/Release、是否重建）、命令、用例总数、重复运行次数；
- 修改测试文件后必须整轮重建（F5/2026-11-22-s2-batch1-acceptance.md §0），否则可能出现「新测试 + 旧产品 DLL」的假失败。
