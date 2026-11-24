# 依赖与类型演进策略

> **状态**：Accepted policy
> **日期**：2026-11-16
> **适用范围**：.NET SDK/runtime、NuGet 包、Android workload、C# 语言特性、NPS 插件 ABI、公共类型设计
> **目标基线**：**.NET 10 LTS 现在 → .NET 12 LTS 下一次 major upgrade**
> **关联**：[Harness 架构](HARNESS_ARCHITECTURE.md)、[P4 Tier 1 产品化](archive/P4-TIER1-PRODUCTIZATION.md)、[插件规范](PLUGIN_SPEC.md)

---

## 0. 决策摘要

1. **不升级到 .NET 11 STS 作为生产基线**；下一次 major upgrade 目标为 **.NET 12 LTS**。
2. **依赖不冻结**：每月安全补丁、每季度 minor 升级、每半年兼容性 review。
3. **不等到 .NET 12 才一次性升级所有包**；major upgrade 只处理 SDK/TFM/语言/ABI，不叠加 12 个月的包漂移。
4. **C# 15 union/closed hierarchy 只在 .NET 12 分支试点**，不进入 net10 公共插件契约。
5. **公共类型继续使用 C# record/interface/enum + `ChatAck`/异常**；未来可迁移到 closed hierarchy/union，但不破坏 NPS v1。
6. **不引入 F# 作为主语言**；C# 15/16 的 union/closed hierarchy 已覆盖大部分需求。
7. **Result 只用于预期失败的内部边界**；不全局替换异常/`ChatAck`。
8. **当前 `NuGetAudit=false` 是已知风险**，必须在 CI 中恢复依赖漏洞审计。
9. **插件 ABI 升级必须单独设计**；host TFM 升级不能悄悄破坏旧插件。
10. **Android/发布链与 .NET 主版本同步验证**，不能只跑桌面测试。

---

## 1. 当前基线

| 项 | 当前值 |
| --- | --- |
| SDK | `global.json` → `10.0.100`，`rollForward=latestFeature` |
| TFM | `net10.0`（Android 项目使用对应 net10 Android TFM） |
| LangVersion | `latest` |
| 包管理 | Central Package Management，`Directory.Packages.props` |
| 关键包 | Microsoft.Extensions.* 10.0.14、Microsoft.Data.Sqlite 10.0.14、Discord.Net 3.20.2、M.E.AI 10.11.0、System.Reactive 6.0.1 |
| 测试 | Microsoft.NET.Test.Sdk 17.14.1、xUnit 2.9.3、coverlet 6.0.4 |
| 安全审计 | `NuGetAudit=false`（**待修复**） |
| 分析器 | `EnableNETAnalyzers=false`（**独立问题，不在本文范围**） |
| 插件 ABI | NPS v1，插件 target net10 |

---

## 2. 版本支持策略

### 2.1 .NET

- **生产基线**：.NET 10 LTS；
- **不采用**：.NET 11 STS 作为生产基线；
- **下一次 major**：.NET 12 LTS（以官方支持公告为准）；
- **升级窗口**：.NET 12 GA 后 **4–8 周内**完成，不长期双轨；
- **提前验证**：GA 前 1–2 个 preview/RC 周期建分支验证。

### 2.2 NuGet 包

- 运行时绑定的包（Microsoft.Extensions.*、Microsoft.Data.Sqlite 等）跟随 .NET LTS 主版本；
- 第三方包（Discord.Net、M.E.AI、Rx）按上游支持矩阵升级；
- 不长期停留在已停止安全维护的版本；
- 不为了“最新”而跳过回归。

### 2.3 Android

- Android workload 与 .NET 主版本同步；
- 每次 major upgrade 必须验证：
  - `dotnet build` Android；
  - AOT/Trim 配置；
  - 动态插件加载；
  - APK 大小/启动；
  - 目标 API level。

---

## 3. 依赖检查

### 3.1 每周自动检查

在 CI 或定时任务中执行：

```bash
dotnet --info
dotnet list Natter.slnx package --vulnerable --include-transitive
dotnet list Natter.slnx package --deprecated
dotnet list Natter.slnx package --outdated
```

要求：

- 漏洞：任何 High/Critical 必须在 7 天内处理；
- deprecated：记录替代包/迁移计划；
- outdated：按季度升级窗口处理，不要求每周升级；
- 结果存档到 CI artifact/issue。

### 3.2 每月安全窗口

```bash
dotnet list Natter.slnx package --vulnerable --include-transitive
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
```

- 只升 patch/security；
- 不做 minor/major；
- 需要时只升单个包；
- 记录 CVE/公告链接；
- 失败则回滚版本。

### 3.3 每季度兼容窗口

```bash
dotnet list Natter.slnx package --outdated
dotnet list Natter.slnx package --deprecated
dotnet build Natter.slnx
dotnet test tests/Natter.Framework.Tests/Natter.Framework.Tests.csproj
env -u NATTER_LIVE dotnet test tests/Natter.Live.Tests/Natter.Live.Tests.csproj
natter --no-plugins plugins inspect <plugin-dir> --json
```

额外：

- 发布矩阵：至少 linux-x64/win-x64/osx-arm64；
- Android build；
- 文件插件加载；
- 真实 live smoke（凭据允许时）；
- 记录升级 PR/结果。

### 3.4 每半年架构 review

- 上游支持周期；
- 是否有包准备放弃 net10；
- 是否有安全/性能问题；
- 是否进入 .NET 12 preview 分支；
- 插件 ABI 兼容矩阵。

### 3.5 紧急 CVE

- 不受季度窗口限制；
- 单独 PR；
- 最小版本升级；
- 验证后立即发布；
- 记录影响范围/回滚方案。

---

## 4. 升级周期

| 周期 | 内容 | 允许范围 | 验证 |
| --- | --- | --- | --- |
| 每周 | 漏洞扫描 | 不升级 | 报告 |
| 每月 | 安全 patch | patch | build + framework tests |
| 每季度 | minor 升级 | minor | 全量回归 + 发布 + Android |
| 每半年 | 兼容性 review | 评估 major | 计划/分支 |
| .NET LTS GA | SDK/TFM/语言/ABI | major | 全量 + live + 发布 + Android |
| 紧急 CVE | 最小升级 | patch/minor | 定向验证 |

### 4.1 冻结窗口

- P4/P5 发布前 1 周；
- 24h soak 期间；
- 真实 live 验收期间；
- 只允许安全 CVE 例外。

### 4.2 升级 PR 规则

- 一个 PR 一个主题（security / runtime / Discord / AI / test）；
- `Directory.Packages.props` 集中改版本；
- 不同时改 SDK + TFM + 多个第三方大版本；
- 必须附：
  - `dotnet list package` 结果；
  - build/test 结果；
  - 影响的包/API；
  - 回滚方式；
- 不提交 `bin/obj/artifacts`。

### 4.3 回滚

- 包版本回滚；
- `global.json` 回滚；
- TFM 升级必须保留上一版发布分支；
- 数据库 migration 只向前；升级前备份；
- 插件 ABI 升级保留上一版 host/插件包。

---

## 5. 当前必须处理的风险

### 5.1 `NuGetAudit=false`

当前 `Directory.Build.props` 关闭了 NuGet audit。
建议：

- CI 开启 `NuGetAudit=true`；
- 本地可保留静默，但 CI 必须报告；
- 处理 `NU1900` 等 warning，不要用 `NoWarn` 长期压制；
- 漏洞修复走每月/紧急窗口。

### 5.2 `EnableNETAnalyzers=false`

不是依赖问题，但会影响升级质量。
建议单独评估开启，至少对 `Natter.*` 核心项目启用。

### 5.3 插件 ABI

当前插件 target net10。
升级到 net12 后：

- net10 插件不能直接加载；
- 需要决定：
  - 所有插件重建 net12；或
  - 插件双目标 `net10;net12`；
  - hostApi major 升级；
- 不允许“host 升了但旧插件静默加载失败”。

---

## 6. .NET 12 升级计划

### 6.1 分支

```text
main          net10 LTS
net12-preview .NET 12 preview/RC 验证
net12         GA 后升级
```

### 6.2 阶段

1. **SDK/TFM**：global.json、所有 csproj、Android TFM；
2. **包**：Microsoft.Extensions.*、Sqlite、测试栈；
3. **第三方**：Discord.Net、M.E.AI、Rx；
4. **语言**：`LangVersion`、C# 16 特性评估；
5. **插件 ABI**：hostApi、插件包、加载器；
6. **发布**：RID、Docker、systemd、Android；
7. **测试**：232+/239+、live、24h soak；
8. **文档**：迁移说明/breaking changes。

### 6.3 验收

- [ ] `dotnet build Natter.slnx`；
- [ ] 全部框架测试；
- [ ] live no-credentials skip；
- [ ] 真实 Tier 1 smoke；
- [ ] Tier 2 smoke；
- [ ] Android build；
- [ ] publish 矩阵；
- [ ] 文件插件/动态插件加载；
- [ ] 备份/恢复；
- [ ] 24h soak（或明确豁免）；
- [ ] 迁移文档。

---

## 7. 类型演进方向

### 7.1 原则

1. **公共契约保持 C# record/interface/enum**；
2. **内部可以使用 closed hierarchy/union 试验**；
3. **不把 F#/OneOf/LanguageExt/FluentResults 类型放进公共 API**；
4. **错误处理以 `ChatAck` + `ChatError` + 异常为主**；
5. **Result 只用于预期失败的内部边界**；
6. **新增 case 时考虑未来 union/closed hierarchy 的可迁移性**；
7. **序列化格式保持显式、稳定**；
8. **插件 ABI 变更按 major 版本处理**。

### 7.2 当前类型的迁移方向

| 当前类型 | 现在 | 未来方向 | 约束 |
| --- | --- | --- | --- |
| `ChatEvent` 抽象 record | 公共 NLMP 契约 | 可评估 closed hierarchy/union | 不在 v1 改 ABI |
| `ChatCommand` 抽象 record | 公共 NLMP 契约 | 可评估 closed hierarchy | 同上 |
| `Message` / `Content` | 公共 DTO | 保持 record | 不引入 union |
| `ChatAck` / `ChatError` | 命令结果 | 保持；未来可作为 Result 的边界 | 不改成泛型 Result |
| `PluginState` | enum | 保持 enum | 状态机不需要 union |
| `NPS manifest` | JSON DTO | 保持 JSON schema | 不引入 union |
| `IChatUpstream` | 公共接口 | 保持接口 | 不暴露 union |
| `IAiContextAssembler` | 内部契约 | 可返回 closed result | 不暴露 F# 类型 |
| 新 validation/result | — | 内部 `Result<T, ChatError>` 试点 | 不进入公共插件 ABI |

### 7.3 向 closed hierarchy/union 靠齐的写法

推荐：

```csharp
public abstract record ChatOutcome;

public sealed record Success(string Value) : ChatOutcome;

public sealed record Failure(string Code, string Message) : ChatOutcome;
```

而不是：

```csharp
public interface IChatOutcome { }
public sealed class Success : IChatOutcome { }
public sealed class Failure : IChatOutcome { }
```

原因：

- `sealed record` case 更清晰；
- 未来可以迁移到 `closed` hierarchy；
- pattern matching/穷尽测试更容易；
- JSON discriminator 可以稳定设计。

### 7.4 未来 union 试点的边界

只在内部/新 API：

```csharp
// 仅 .NET 12 分支试验
public union ValidationResult(Valid, Invalid);
```

不进入：

- NPS `plugin.json`/公共接口；
- `IChatUpstream`/`IChatResponder`；
- `ChatEvent`/`ChatCommand` v1；
- 第三方插件必须实现的方法签名。

### 7.5 Result 边界

```text
Domain validation      → Result<T, ChatError>（内部）
Application service    → Result<T, ChatError>（内部）
NLMP command           → ChatAck
Infrastructure failure → Exception
Cancellation           → OperationCanceledException
Plugin startup          → fail fast + structured error
```

不要让四种错误模型穿透所有层。

---

## 8. 与 F#、C# union 的关系

- **不迁移 F#**；
- .NET 12 分支可评估 C# union/closed hierarchy；
- 如果 C# union 足够，F# 的价值只剩 computation expressions/不可变默认，收益不足以覆盖混合语言成本；
- F# 只在极端纯函数模块试点，不进入插件 ABI。

---

## 9. 责任与节奏

| 角色 | 职责 |
| --- | --- |
| Maintainer | 月度/季度升级、PR、回滚 |
| CI | 漏洞扫描、build/test、发布矩阵 |
| Plugin owner | 按 hostApi 重建插件 |
| Release owner | 冻结窗口、升级验收 |

节奏：

```text
每周：漏洞扫描
每月：安全 patch
每季度：minor + 全量回归
每半年：兼容性 review
.NET 12 GA：4–8 周 major upgrade
```

---

## 10. 一句话结论

**现在留在 .NET 10 LTS，依赖按月/季度维护；下一次 major 只等 .NET 12 LTS，不追 .NET 11 STS。**
**类型上保持 C# record/interface + `ChatAck`/异常，内部向 closed hierarchy/union/Result 小步靠齐，但不改 NPS v1 公共 ABI。**
