# 2026-11-22 · 本轮审查修复独立验收（S1-01/02/04/08/09/10 + task-29）

> **执行**：qa（task-30）
> **基线**：`HEAD=5736d7a`（修复合集 `4c3c425`/`670b3b6`/`67c02e6`/`82f6866`/`ebf54de`/`5736d7a` + task-29 系列 `f8f17b2`/`b7dc979`/`6aa2f46`/`874a187`/`8673e89`）
> **新增测试**：`tests/Natter.Framework.Tests/ReviewAcceptanceTests.cs`（**46 个用例**：10 Fact + 4 Theory）
> **端到端现场**：`artifacts/review-acceptance/`（隔离 config/state/plugins，已 gitignore）
> **关联**：[P5 task UX 验收](2026-11-22-p5-task-ux-acceptance.md)、[代码审查报告](../CODE_REVIEW_2026-11-22.md)

---

## 1. 结论

| 项 | 结论 | 关键证据 |
| --- | --- | --- |
| **S1-01** 插件卸载路径 | **通过** | 绝对路径/`../`/超长/大写/空格 id 全部 `PluginStoreException(plugin-id-invalid)` 且目标目录与文件数不变；已安装插件安装目录进 `.trash/txn-*`，data/config 按参数删除；未安装 id + `--delete-data` 不删（需 `--force`）；符号链接只删链接、target 文件保留 |
| **S1-02** SSRF + 代理 | **通过** | 宿主 handler `UseProxy=false`/`AllowAutoRedirect=false`；设 `HTTP(S)_PROXY`/`ALL_PROXY` 后 loopback/私网/元数据目标仍被拒，**代理与目标监听器均 0 连接**；`IsBlockedAddress` 19 组直连矩阵不回归 |
| **S1-08** Android Release 清单 | **通过** | `dotnet build Natter.slnx -c Release` **0 error**；`obj/Release/**` 三份生成清单各 **恰 1 个** `android.permission.INTERNET`、恰 1 个 `<uses-permission>` |
| **S1-04** validate 脱敏 | **通过（需最新 staging，见 §4）** | admin：diff 存在且 `apiKey`/`auth`/PEM 全部 `[redacted]`，`${secret:WEBCONSOLE_TOKEN}` **原样保留**；viewer：`diffOmitted=role:viewer`、无 diff、sk-/Bearer/PEM 零泄漏；readOnly 实例 validate 仍 200、保存 403 |
| **S1-09** CI 补全 | **通过** | `ci.yml` 含 `dotnet build Natter.slnx -c Release` 与「遍历全部 `tests/**/*.csproj` 逐个 `dotnet test`」；本地等价执行 PromptLibrary **36/36** 真跑到 |
| **S1-10** soak 判定 | **通过** | `--self-test` 35 断言全过；反向夹具「2 健康 + 20 error」→ **FAIL**（coverage 0.7%、error 90.91% > 5%、outage 窗口、status 拒绝）；`soak-once` 复跑 **0.3% FAIL**（不再是 100%/PASS） |
| **task-29** prompt 责任边界 | **通过** | safety 默认在 plan、`disabledFragments` 后消失且 **无 warning**、重开恢复且 `PlanHash` 变化；两插件同名 `shared` 片段互不影响（`alpha#shared`/`beta#shared`）；单 provider 超配额只截自己、另一插件片段保留；默认 profile 输出 `len=134`、`sha256=750fb332…f474` 与审查前基线逐字节一致 |

| 回归 | 结果 |
| --- | --- |
| 新增验收 `ReviewAcceptanceTests` | **46/46** |
| Framework 全量 | **618/618**（572 基线 + 46，连跑 3 次全绿） |
| PromptLibrary | **36/36** |
| WebConsole | **314/314** |
| Release 全解构建 | **0 error**（2 warning，Android SDK XA0033 环境告警） |

**判定：本轮 6 项 S1 修复与 task-29 边界全部达标，可签。** 唯一需要注意的不是代码缺陷而是**部署前提**：S1-04 只在重新 staging 的插件二进制里生效（§4）。

---

## 2. S1-01 卸载路径（隔离 store/config/data）

| 输入 | 结果 |
| --- | --- |
| `uninstall <绝对路径> --delete-data --delete-config --force` | `PluginStoreException`，issue `plugin-id-invalid`；目标目录 2 个文件全部保留 |
| `../escape` / `../../etc` / `..` / `/etc/passwd` / `Natter.Upper` / `has space` / `a/b` / `natter/../x` / 空 / null | 全部拒绝，守卫目录与 marker 文件完好 |
| 65 字符 id | 拒绝；`PluginStoreIds.MaxLength=64`，`natter.plugin.sample` 仍合法 |
| 已安装 id + `--delete-data --delete-config` | 安装目录移入 `.trash/txn-*/<id>`（含 `plugin.json`），data 目录与 config 文件删除，`installed.json` 不再含该 id |
| 已安装 id + 只删 data | data 删除、config 保留 |
| 未安装 id + `--delete-data --delete-config`（无 force） | data/config **都保留**；加 `--force` 才删除 |
| 符号链接 data 目录（POSIX） | 只删链接本身；target 目录 `keep.txt` 保留、文件数不变 |

实现要点核对：`PluginStoreIds.Validate` 在任何路径推导/删除之前执行；`EnsureWithinDirectory` 拒绝越界删除；`TryDeleteDirectory` 对 `LinkTarget != null` 只做 `recursive:false` 删除。

---

## 3. S1-02 SSRF 与代理环境变量

```text
ChatCompositionModule.CreateHttpFetchHandler()
  UseProxy = false, AllowAutoRedirect = false, ConnectCallback != null
```

实测（进程内设置 `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY`/小写同名变量，`NO_PROXY` 清空）：

| 目标 | 结果 | 代理监听 | 目标监听 |
| --- | --- | --- | --- |
| `https://127.0.0.1:<port>/`（宿主 handler 直连） | `HttpRequestException: … private, loopback or metadata …` | **0 连接** | **0 连接** |
| `https://169.254.169.254/latest/meta-data/`（HttpFetchTool + allowlist 命中） | `address_not_allowed` | **0 连接** | — |
| `https://127.0.0.1:<port>/admin`、`https://[::1]:<port>/admin`、`https://10.1.2.3/internal` | `address_not_allowed` | **0 连接** | **0 连接** |

`HttpFetchTool.IsBlockedAddress` 矩阵（19 组）：`127.0.0.1`、`127.255.255.254`、`10/172.16-31/192.168`、`169.254.169.254`、`100.64.0.1`、`198.18.0.1`、`224.0.0.1`、`0.0.0.0`、`::1`、`fe80::1`、`fc00::1`、`::ffff:127.0.0.1` → 全部 blocked；`8.8.8.8`、`1.1.1.1`、`172.32.0.1`、`2606:4700:4700::1111` → 放行，无回归。

---

## 4. S1-04 validate 脱敏（HTTP 探针，含 viewer 角色）

隔离实例：`--plugin-dir/--config-dir/--state-dir` 全部指向 `artifacts/review-acceptance/wc*`，admin 与 viewer 使用两组临时 token（`WEBCONSOLE_TOKEN`/`WEBCONSOLE_VIEWER_TOKEN`），未触碰任何生产配置。

请求体（validate 内容）同时含：`${secret:WEBCONSOLE_TOKEN}` 引用、`sk-live-review-acceptance-…`、`Bearer …`、完整 PEM 私钥块。

| 角色 | HTTP | `valid` | `diffOmitted` | diff | sk- | Bearer | PEM | `${secret:…}` |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| admin | 200 | false（schema 拒绝额外字段） | — | 有 | **无泄漏** | **无泄漏** | **无泄漏** | **原样保留**（未被改成 `${secret:[redacted]}`） |
| viewer | 200 | false | `role:viewer` | 无 | 无 | 无 | 无 | 不适用（无 diff） |

admin diff 的 `+` 行实际形态：

```json
+"…", "apiKey": "[redacted]", "auth": "[redacted]", "key": "[redacted]"  … "token": "${secret:WEBCONSOLE_TOKEN}"
```

只读实例（`readOnly=true`、`allowConfigWrite=false`）：

| 角色 | validate | 保存 |
| --- | --- | --- |
| admin | 200（可用） | `403 console.read_only` |
| viewer | 200（`diffOmitted=role:viewer`） | `403 auth.forbidden` |

与 `docs/WEBCONSOLE_PLUGIN.md` §7.2.1 的「角色 × diff 矩阵」「validate 在只读与 viewer 下仍可用」逐条一致。

### 4.1 ⚠ 部署前提（非代码缺陷，但会让"已修复"在实机上失效）

首次探针我复用了 `artifacts/webconsole-e2e` 里**旧的**插件 staging（`lib/Natter.Plugin.WebConsole.dll` 不含 `DiffOmitted` 符号），结果：

```text
admin : sk 泄漏 = True, Bearer 泄漏 = True, PEM 泄漏 = True, diff 存在
viewer: sk 泄漏 = True, Bearer 泄漏 = True, PEM 泄漏 = True, diff 存在（未省略）
```

用 `dotnet build` 最新产物重新 staging（`lib/` 换成 `plugins/Natter.Plugin.WebConsole/bin/Debug/net10.0/`）后，上表全部合规。
**因此发布/运维必须重建插件 staging**（`scripts/stage-tier1-plugins.sh` 或等价流程），否则镜像里仍是旧 DLL。建议在发布清单里显式增加「重建 staging + 校验 `DiffOmitted` 存在」一步。

---

## 5. S1-08 Android Release 清单

```bash
dotnet build Natter.slnx -c Release --nologo      # 0 error（2 warning：XA0033 Java 版本探测）
dotnet build src/Natter.Hosting.Android/Natter.Hosting.Android.csproj -c Release   # 成功
```

`obj/Release/**` 三份生成清单：

| 路径 | `android.permission.INTERNET` | `<uses-permission>` |
| --- | --- | --- |
| `obj/Release/net10.0-android/android/manifest/AndroidManifest.xml` | 1 | 1 |
| `obj/Release/net10.0-android/android/AndroidManifest.xml` | 1 | 1 |
| `obj/Release/net10.0-android/AndroidManifest.xml` | 1 | 1 |

源码 `src/Natter.Hosting.Android/Properties/AndroidManifest.xml` 同样恰 1 个。测试对「源码」与「Release 生成物」都做了断言，并且**要求生成物存在**（不会像实现方测试那样在无产物时静默 return）。

---

## 6. S1-09 CI 补全

静态检查 `.github/workflows/ci.yml`：

- `solution-build` job：`dotnet build Natter.slnx -c Release --nologo`（受 `vars.ANDROID_BUILD` 门控，未启用时在 checks 列表显示 skipped，不会静默绿）；
- `build-test` job：从 `Natter.slnx` 枚举全部项目逐个 `dotnet build -c Release`，并 `find tests -name '*.csproj'` 枚举 **4 个测试套件**逐个 `dotnet test`；
- 本地等价执行：`dotnet test tests/Natter.Plugin.PromptLibrary.Tests` → **36/36 真正执行并通过**（不是"0 测试"）。

---

## 7. S1-10 soak 判定

`python3 scripts/soak-report.py --self-test` → **self-test passed (35 assertions)**，含 dead-host / status 异常 / error 占比三组新夹具。

反向夹具（本轮新建 `artifacts/review-acceptance/soak-2healthy-20error.jsonl`：header 声明 24h/300s，2 条 healthy + 20 条 error）：

```json
{"result":"FAIL","samples":22,"healthySamples":2,"errorSamples":20,
 "coveragePercent":0.7,"errorPercent":90.91,"expectedSamples":289,
 "reasons":["coverage 0.7% < 95% (basis: declared-header; 2 healthy of 289 expected samples)",
            "20/22 samples were unreachable/unparsable (90.91%, allowed 5.0%)",
            "1 outage window(s) from consecutive error samples, 0.35h total",
            "status not allowed in unknown×20 (allowed: healthy)"]}
```

历史数据复跑：

| 数据 | 审查前 | 现在 |
| --- | --- | --- |
| `closeout/2026-11-21/soak-once/soak-2026-11-21.jsonl` | 100% / PASS（假阳性） | **FAIL**，`coverage 0.3%`（1 healthy / 289 expected） |
| `closeout/2026-11-21/soak/soak-toolcheck.jsonl` | — | FAIL，`coverage 83.3%`，5 样本（合理：覆盖率不足） |
| `p2/2026-11-15-1647/evidence/soak-30min.jsonl` | — | FAIL，0 usable samples（旧格式不可解析，判 FAIL 而非假 PASS，合理） |

---

## 8. task-29 prompt 责任边界

| 场景 | 结果 |
| --- | --- |
| 默认 profile | plan 含 `host.safety.untrusted-data`；`PlanHash` 非空；`warnings` 空 |
| `prompt.disabledFragments` 关闭 safety | 片段从 plan 消失、system prompt 不含「untrusted data」、**无 warning**、`PlanHash` 变化 |
| 重新启用 | 片段恢复、`PlanHash` 回到原值 |
| 默认输出基线 | `len=134`、`sha256=750fb332407ebe809b2b15920ed513f8bcfa9a524028dfa2b5f5c0520e37f474`（与 `docs/PROMPT_COMPILER.md` §19.2 一致） |
| 两插件同名 local id `shared` | 聚合为 `alpha#shared` + `beta#shared`，两条都在、无 warning、`ProviderCount=2` |
| 单 provider 返回 20 个片段（超 16/8000 配额） | greedy 被截到 ≤16、`warnings` 含 `prompt.fragment_provider_limit`、`RejectedCount>0`；polite 的 `polite#kept` 仍保留 |
| 一个 provider 抛异常 | 只有 `healthy#kept` 进入结果，`warnings` 非空，编译不失败 |

---

## 9. 观察项（不阻塞签收）

| ID | 级别 | 内容 |
| --- | --- | --- |
| OBS-REVIEW-STAGING | **High（部署）** | S1-04 修复只存在于最新构建；用旧 staging（`artifacts/webconsole-e2e` 的 DLL，无 `DiffOmitted`）启动时，viewer 仍能拿到未脱敏 diff、sk-/Bearer/PEM 明文泄漏。发布必须重建插件 staging，建议发布检查项加「`strings <dll> \| grep DiffOmitted`」 |
| OBS-REVIEW-S104-READONLY | Info | 只读实例的 `diff` 为 `null` 且 `diffOmitted` 为 `null`（因 `profiles` 尚未落盘，宿主 diff 只对已存文档生成）；文档措辞「diff 只给 admin」在无基线文档时表现为空 diff，不是遗漏 |
| OBS-REVIEW-SOAK-P2 | Info | `p2` 的旧 soak 数据缺 header/采样字段，复跑为「0 usable samples → FAIL」；如需历史对照应在报告中注明格式不兼容，而非视为回归 |
| OBS-REVIEW-CI-ANDROID | Info | `solution-build`（含 Android）受 `vars.ANDROID_BUILD` 门控，默认 skipped；本地「全解 Release 构建」需自行安装 android workload（本机已装，0 error） |
| OBS-REVIEW-CLI-VS-STORE | Info | S1-01 的 CLI 错误码（`plugin-id-invalid`）由实现方测试覆盖；本轮从 store 层（唯一真正删除文件的位置）独立验证，两者一致 |

---

## 10. 未测 / 范围外

- 真实 Android 设备安装运行与 `INTERNET` 实际生效（只验生成清单）；
- 生产 `natter` 服务进程的插件热重载路径（本轮未部署、未重启）；
- `scripts/soak-report.py` 的 wallclock 24h 真实长跑（只跑 self-test 与历史数据复跑）；
- 依赖漏洞扫描（`ci.yml` 中有该步骤，本地未执行 `dotnet list package --vulnerable`）；
- 未提交/未推送、未修改 `src/`、`plugins/`、`scripts/`、`deploy/`。

---

## 11. 复现命令

```bash
# 新增验收（46 用例）
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj \
  --filter "FullyQualifiedName~ReviewAcceptanceTests"          # 46/46

# 全量
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj        # 618/618
dotnet test tests/Natter.Plugin.PromptLibrary.Tests/…csproj                  # 36/36
dotnet test tests/Natter.Plugin.WebConsole.Tests/…csproj                     # 314/314

# Release（CI 等价）
dotnet build Natter.slnx -c Release --nologo                                  # 0 error

# soak
python3 scripts/soak-report.py --self-test                                    # 35 assertions
python3 scripts/soak-report.py --json artifacts/review-acceptance/soak-2healthy-20error.jsonl   # FAIL
python3 scripts/soak-report.py --json artifacts/ops/p4.5/closeout/2026-11-21/soak-once/soak-2026-11-21.jsonl  # FAIL 0.3%
```
