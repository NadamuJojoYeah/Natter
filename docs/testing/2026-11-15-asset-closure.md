# 2026-11-15 · 测试资产收口

## 1. 概要

- **时间**：2026-11-15 15:55–16:01（Asia/Shanghai）
- **基线**：`git HEAD=969341d`；本轮**未修改产品代码**（`git diff src samples` 为空）
- **触发**：架构要求收口测试资产——归档、凭据处置、明文扫描、临时/正式目录分离
- **结论**：凭据已移出工作区（`~/.config/natter/live.env`，600）；`artifacts/live/.env` 与临时插件目录已清理；仓库与 git 历史**无明文凭据**；资产清单已文档化。平台侧密钥轮换需账号持有人执行（清单见 [ASSETS §4](ASSETS.md#4-轮换清单需账号持有人操作)）。

## 2. 动作

| # | 动作 | 结果 |
| --- | --- | --- |
| 1 | 停止使用测试凭据的长驻实例 | 已停止（job 取消，无残留 gateway/Stream 会话） |
| 2 | 凭据移出工作区 | `~/.config/natter/live.env`（`chmod 600`，仓库外） |
| 3 | 删除工作区明文 | `artifacts/live/.env` 已删除 |
| 4 | 读取路径收口 | `LiveSettings` 与 `scripts/live-test.sh`、`scripts/live-serve.sh` 统一为：环境变量 → `NATTER_LIVE_ENV` → `~/.config/natter/live.env` → `artifacts/live/.env`（legacy 回退） |
| 5 | 模板更新 | `tests/Natter.Live.Tests/.env.example` 增加钉钉/Spark 变量与「仓库外」说明 |
| 6 | 临时目录清理 | `artifacts/live/plugins`、`artifacts/live/plugins-dt` 删除（脚本按需重建）；保留 `artifacts/live/serve/*.log` 作为证据 |
| 7 | 资产清单 | 新增 [ASSETS.md](ASSETS.md)：目录边界、资产表、凭据处置、轮换清单、常用命令、归档规则 |
| 8 | 文档同步 | [README](README.md) 链接资产清单；[LIVE_TEST_PLAN](../LIVE_TEST_PLAN.md) 凭据来源与目录边界更新 |

## 3. 验证

清空进程环境变量（`DEEPSEEK_API_KEY` / `DISCORD_BOT_TOKEN` / `SPARK_API_KEY` 全部 unset）后，仅保留 `NATTER_LIVE=1`：

```bash
NATTER_LIVE=1 dotnet test tests/Natter.Live.Tests \
  --filter "FullyQualifiedName~SparkLiveTests.Models_endpoint"
```

结果：**Passed**（288ms）。说明测试确实从 `~/.config/natter/live.env` 读取凭据，而不是依赖工作区内文件。

## 4. 明文扫描

扫描范围：

- 工作区源码、脚本、文档、测试资产（排除 `artifacts/`、`bin/`、`obj/`、`.git/`）；
- git 跟踪文件中的 env/secret/credential 类文件；
- git 历史中的已知凭据模式。

结果：

- 未发现应提交的明文凭据；
- `.env.example` 仅含占位符；
- 本记录不保留任何密钥、token 或其片段；
- 凭据只允许放在 `~/.config/natter/live.env`（600）或进程环境变量中。

> 注意：早期版本的收口记录曾把扫描命令的匹配文本写进文档；该段已删除。含真实凭据的测试文档不得提交。

## 5. 目录边界

| 类别 | 路径 | 说明 |
| --- | --- | --- |
| 正式插件（构建） | `src/*/bin/<cfg>/net10.0/lib/`、`samples/*/bin/<cfg>/net10.0/lib/` | 由 csproj staging target 生成 |
| 正式发布 | `publish/<rid>/plugins/<id>/` | `scripts/publish.sh` 输出 |
| 测试 staging（临时） | `artifacts/live/plugins/`、`artifacts/live/plugins-dt/` | 脚本生成、可随时删除，**不进入 publish** |
| 测试证据 | `artifacts/live/serve/*.log` | 保留现场，无密钥 |
| 测试源码 | `tests/Natter.Live.Tests/`、`tests/Natter.Framework.Tests/` | 必须留在 `tests/` 才能被 `dotnet test` 发现 |
| 测试脚本 | `scripts/live-test.sh`、`scripts/live-serve.sh` | 入口固定，说明见 ASSETS |

## 6. 未完成项（需账号持有人）

平台侧密钥轮换无法由测试侧完成，清单与操作路径见 [ASSETS §4](ASSETS.md#4-轮换清单需账号持有人操作)：

- `DINGTALK_CLIENT_SECRET`（钉钉开放平台 → 应用凭据）
- `SPARK_API_KEY`（讯飞开放平台 → 星火 MaaS）
- `DEEPSEEK_API_KEY`、`DISCORD_BOT_TOKEN`（本轮未点名，但建议同批轮换）

轮换后更新 `~/.config/natter/live.env`，并跑 `scripts/live-test.sh spark` / `discord` 复验。

## 7. 结论

- 测试资产已收口：清单、脚本、记录、模板集中在 `docs/testing/` 索引下，代码与脚本保留在 `tests/`、`scripts/` 供执行；
- 仓库无明文凭据，历史无泄漏；
- 临时测试目录与正式插件目录已明确分离并清理；
- 剩余仅平台侧轮换（人工）。
