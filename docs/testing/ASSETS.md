# 测试资产清单与归档规则

> 本文件是**测试资产的唯一清单**：代码在哪、脚本怎么跑、凭据放哪、临时目录与正式目录如何分开。
> 记录本身见 [索引](README.md#4-记录索引)。

---

## 1. 目录边界（正式 vs 测试）

| 类别 | 路径 | 归属 | 是否入库 | 说明 |
| --- | --- | --- | --- | --- |
| **正式插件目录** | `src/*/bin/<cfg>/net10.0/lib/`、`samples/*/bin/<cfg>/net10.0/lib/` | 构建产物 | 否（`bin/` 忽略） | 由 csproj 的 `Stage*Plugin` target 生成，发布脚本只认这里 |
| **正式发布目录** | `publish/<rid>/plugins/<id>/` | 发布产物 | 否 | `scripts/publish.sh` 输出 |
| **CLI 内置示例插件** | `src/Natter.Cli/bin/<cfg>/net10.0/plugins/{greetings,metrics}/` | 构建产物 | 否 | 仅示例，不参与真实联调 |
| **测试 staging（临时）** | `artifacts/live/plugins/`、`artifacts/live/plugins-dt/` | 测试临时目录 | 否（`artifacts/` 忽略） | 由脚本按需重建，**可随时删除**，绝不复制进 `publish/` |
| **测试运行证据** | `artifacts/live/serve/*.log` | 测试证据 | 否 | 保留现场（不含密钥）；按需清理 |
| **测试代码** | `tests/Natter.Live.Tests/`、`tests/Natter.Framework.Tests/` | 源码 | 是 | 必须留在 `tests/` 才能被 `dotnet test` 发现 |
| **测试脚本** | `scripts/live-test.sh`、`scripts/live-serve.sh` | 源码 | 是 | 入口固定；本文件是它们的权威说明 |
| **测试文档** | `docs/LIVE_TEST_PLAN.md`、`docs/testing/` | 源码 | 是 | 方案 + 每轮记录 + 模板 + 本清单 |

**规则**：真实联调只能加载 `artifacts/live/plugins*` 里的临时包；正式发布流程不得引用 `artifacts/` 下任何内容。

## 2. 资产清单

| 资产 | 路径 | 用途 |
| --- | --- | --- |
| 分层 live 套件 | `tests/Natter.Live.Tests/` | L1 provider、L2 transport、L2b 入站、L3/L3b/L3c 端到端、L4 部署、L5 人工闸门 |
| 凭据模板 | `tests/Natter.Live.Tests/.env.example` | 变量名与目标 ID 占位，无真实值 |
| 分层执行入口 | `scripts/live-test.sh` | `deepseek`/`discord`/`ingress`/`e2e`/`deploy`/`spark`/`dingtalk`/`manual`/`all` |
| 部署入口 | `scripts/live-serve.sh` | staging + 长驻（`--foreground`）/ `--status` / `--stop` |
| 方案 | `docs/LIVE_TEST_PLAN.md` | 分层矩阵、判据、真实结果 |
| 轮次记录 | `docs/testing/YYYY-MM-DD-<scope>.md` | 时间、内容、证据、问题、临时改动 |
| 记录模板 | `docs/testing/TEMPLATE.md` | 新记录直接复制 |
| 框架守护测试 | `tests/Natter.Framework.Tests/PluginLoadContextTests.cs`、`DiscordMentionGateTests.cs` | 加载策略与 mention 门控回归 |

## 3. 凭据处置

**位置与优先级**（`LiveSettings` 与两个脚本一致）：

1. 环境变量（进程内直接注入）
2. `NATTER_LIVE_ENV` 指定的文件
3. `~/.config/natter/live.env`（**推荐**，仓库外，`chmod 600`）
4. `artifacts/live/.env`（legacy 回退，默认不再创建）

```bash
mkdir -p ~/.config/natter
cp tests/Natter.Live.Tests/.env.example ~/.config/natter/live.env
chmod 600 ~/.config/natter/live.env
$EDITOR ~/.config/natter/live.env
```

**硬性规则**：

- 插件 `config.json` 只允许 `${secret:NAME}` 引用，禁止字面值；
- 凭据不得进入 git（含 `git commit --amend`、文档、截图、日志）；
- 测试输出统一脱敏，断言不得回显密钥；
- 平台侧轮换由账号持有人执行（见 §4 清单）。

## 4. 轮换清单（需账号持有人操作）

| 凭据 | 控制台 | 步骤 |
| --- | --- | --- |
| `DINGTALK_CLIENT_SECRET` | 钉钉开放平台 → 应用 → 凭据与基础信息 | 重置 Client Secret → 更新 `~/.config/natter/live.env` → 重启实例 |
| `SPARK_API_KEY` | 讯飞开放平台 → 星火 MaaS → API Key | 重新生成 → 更新同上 |
| `DEEPSEEK_API_KEY` | DeepSeek 控制台 → API keys | 吊销旧 key → 新建 → 更新同上 |
| `DISCORD_BOT_TOKEN` | Discord Developer Portal → Bot | Reset Token → 更新同上 |

轮换后跑一次 `scripts/live-test.sh spark` / `discord` 确认新凭据可用。

## 5. 常用命令

```bash
scripts/live-test.sh all          # 27 项自动 live（不含人工闸门）
scripts/live-test.sh spark        # Spark 7 项
scripts/live-test.sh dingtalk     # 钉钉 Stream 握手 2 项
scripts/live-test.sh manual       # 人工闸门（需真人 @机器人）

scripts/live-serve.sh --foreground 3600   # staging + 前台长驻（配合后台 job/终端）
scripts/live-serve.sh --status            # 后台模式状态
scripts/live-serve.sh --stop              # 停止后台实例

# 清理临时目录（可随时执行，脚本会重建）
rm -rf artifacts/live/plugins artifacts/live/plugins-dt
```

## 6. 归档规则

1. 每轮测试结束必须新增 `docs/testing/YYYY-MM-DD-<scope>.md`，包含**测试时间 / 内容 / 具体问题**；
2. 在 [索引](README.md#4-记录索引) 追加一行（日期、记录、范围、结果）；
3. 问题使用 `BUG-` / `OBS-` / `ENV-` / `FLAKE-` 编号；测试只记录与取证，不修改产品代码；
4. 临时改动（若有）单列「临时改动」表并给出回退命令；
5. 真实凭据只写「来源路径」，不写值。
