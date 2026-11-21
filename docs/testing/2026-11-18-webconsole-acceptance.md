# WebConsole 插件验收测试计划

> **状态**：Ready for test  
> **日期**：2026-11-18  
> **基线**：`HEAD=2a13b8b`  
> **被测对象**：`plugins/Natter.Plugin.WebConsole`、WebConsole 单测/UI smoke、真实宿主加载与 HTTP 端点  
> **交付范围**：Phase 1 只读控制台已交付；Phase 2/3 插件侧策略已就绪，但宿主 `IAdminConfigStore` / `IAdminCommandRunner` 仍缺失，真实写入/执行返回 501。  
> **关联**：[WebConsole 设计](../WEBCONSOLE_PLUGIN_DESIGN.md)、[使用说明](../WEBCONSOLE_PLUGIN.md)、[宿主缺口](../WEBCONSOLE_HOST_API_GAP.md)、[Phase 1 记录](2026-11-18-webconsole-p1.md)、[Phase 2/3 记录](2026-11-18-webconsole-p2-ready.md)

---

## 0. 结论口径

这次验收必须分开签：

| 范围 | 目标 |
| --- | --- |
| Phase 1 只读控制台 | 可 Go |
| Phase 2 配置校验/编辑 | 插件侧可验证；真实保存因宿主 G5 只能 501，不是完整交付 |
| Phase 3 命令策略/终端 | 插件侧可验证；真实执行因宿主 G6 只能 501，不是完整交付 |
| 公网反向代理 | 可选，需 TLS/认证/Origins |
| Android | 不支持 |

不要把“Phase 2/3 端点已接线”写成“配置编辑/终端已交付”。

---

## 1. 前置

```bash
git rev-parse --short HEAD   # 2a13b8b 或后续
dotnet build plugins/Natter.Plugin.WebConsole/Natter.Plugin.WebConsole.csproj -c Release
dotnet test tests/Natter.Plugin.WebConsole.Tests/Natter.Plugin.WebConsole.Tests.csproj -c Release
node tests/Natter.Plugin.WebConsole.Tests/ui-smoke.mjs
```

预期：

- 构建成功；
- WebConsole 单测 **123/123**；
- UI smoke `result=ui smoke ok`，checks=6，gaps=7；
- 框架主回归不回归（当前 245/245）。

---

## 2. 环境

| 项 | 值 |
| --- | --- |
| OS | Linux 优先 |
| .NET | SDK 10.0.114+ |
| 宿主 | `src/Natter.Cli/bin/Debug/net10.0/natter` 或 Release |
| 插件 staging | `artifacts/webconsole-e2e/plugins/natter.plugin.webconsole` |
| 控制台 | `http://127.0.0.1:5090` |
| host health | `http://127.0.0.1:5099/health` |
| secret | `WEBCONSOLE_TOKEN`，环境/密钥库，0600 |
| 证据 | `artifacts/webconsole-test/<run>/` |

生成 token：

```bash
openssl rand -base64 48
```

不要写进 `config.json`、shell history、截图或测试记录。

---

## 3. 测试用例

### Gate A：构建、单测、UI smoke

| ID | 用例 | 命令 | 预期 |
| --- | --- | --- | --- |
| W-001 | 插件构建 | `dotnet build plugins/... -c Release` | 成功 |
| W-002 | 单测 | `dotnet test tests/Natter.Plugin.WebConsole.Tests` | 123/123 |
| W-003 | UI smoke | `node tests/.../ui-smoke.mjs` | `ui smoke ok`, checks=6, gaps=7 |
| W-004 | 主框架回归 | `dotnet test tests/Natter.Framework.Tests` | 245/245 |

### Gate B：staging / manifest / 启动

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| W-010 | staging | `scripts/stage-webconsole.sh <root>` | `plugin.json`/`lib`/`wwwroot` 完整 |
| W-011 | file plugin 加载 | `natter web status --json` | 插件 Activated，`server.state=listening` |
| W-012 | 缺 token | 不设 `WEBCONSOLE_TOKEN` | 激活失败 `webconsole.token-missing`，不启动服务器 |
| W-013 | token 太短 | 设短 token | `webconsole.token-too-short` |
| W-014 | 非 loopback bind | `bind=0.0.0.0` | `webconsole.bind-not-loopback` |
| W-015 | 端口占用 | 先占 5090，再启动 | `webconsole.port-in-use` |
| W-016 | external 模式 | 已有控制台占端口，CLI 执行 | `server.state=external`，`web status` 可用 |
| W-017 | 静态资源缺失 | 删 `wwwroot/app.js` | `webconsole.static-assets-missing` |
| W-018 | 插件停止 | 停 serve | 控制台端口释放，审计有 `plugin.stop` |

### Gate C：认证与会话

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| W-020 | 未登录 | `GET /api/snapshot` | 401 `auth.required` |
| W-021 | 错误 token | `POST /api/session` | 401 `auth.invalid_token` |
| W-022 | 登录限速 | 连续 5 次错误 | 429 + `Retry-After` |
| W-023 | 正确登录 | `POST /api/session` | 200 + `HttpOnly; SameSite=Strict` |
| W-024 | 登出 | `DELETE /api/session` + CSRF | 200；后续 401 |
| W-025 | CSRF 缺失 | POST 无 `X-WebConsole-CSRF` | 403 `csrf.invalid` |
| W-026 | Origin 非法 | 跨站 Origin | 拒绝 |
| W-027 | 会话过期 | 超过 `sessionMinutes` | 401，需要重新登录 |
| W-028 | 重启 | 重启插件/宿主 | 内存会话失效，需要重新登录 |

### Gate D：静态资源与安全

| ID | 用例 | 预期 |
| --- | --- | --- |
| W-030 | `/` `/app.js` `/app.css` | 200，CSP `default-src 'none'` |
| W-031 | HEAD | 200，无 body，Content-Length 正确 |
| W-032 | 路径穿越 | `/../etc/passwd` 等 | 404/拒绝，不读文件 |
| W-033 | 未知路径 | 404 |
| W-034 | 方法白名单 | PUT/PATCH 等 | 405 |
| W-035 | 安全头 | nosniff/HSTS（HTTPS 代理下） | 存在 |

### Gate E：只读 snapshot

| ID | 用例 | 预期 |
| --- | --- | --- |
| W-040 | `GET /api/snapshot` | 200，self/health/plugins/upstreams/profiles/config/audit |
| W-041 | health 桥接 | `hostDataSource=health-endpoint` |
| W-042 | plugins | 计数、失败项；明细缺失时 `host.plugin_diagnostics.unavailable` |
| W-043 | upstreams | 有 `connected/lastConnectedAt/lastDisconnectAt/reconnectCount/lastError` |
| W-044 | profiles | provider/tier/available |
| W-045 | logs | host logs 不可用时 `host.logs.unavailable`，audit 可见 |
| W-046 | config 预览 | secret 显示 `${secret:...}` 或 `***`，不显示明文 |
| W-047 | audit | 最近事件，无 token/secret/正文 |
| W-048 | HTML 渲染 | 登录后 dashboard 不白屏，checks/upstreams/profiles/plugins/config/audit 可见 |

### Gate F：Phase 2 配置校验

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| W-050 | valid JSON | `POST /api/config/profiles/validate` | 200，`valid=true`，ETag/size |
| W-051 | invalid JSON | 非法 JSON | 200，`valid=false` + issues |
| W-052 | 明文 secret | `"token":"plain"` | 200，`containsSecretLiteral=true`，`config.secret_literal` |
| W-053 | `${secret:...}` | 合法引用 | 放行 |
| W-054 | 路径 id | `../etc/passwd` | 拒绝 |
| W-055 | 大小上限 | >256 KiB | 413/拒绝 |
| W-056 | 保存无效 | `POST /api/config/profiles` 非法 | 400 `config.invalid` |
| W-057 | 保存有效但无宿主 | 合法 JSON | 501 `host.admin_config.unavailable` |
| W-058 | 写入关闭 | `allowConfigWrite=false` | 403 `config.write_disabled` |
| W-059 | ETag/diff | 同内容/变更内容 | ETag 稳定/变化，diff 有界 |

### Gate G：Phase 3 命令策略

| ID | 用例 | 方法 | 预期 |
| --- | --- | --- | --- |
| W-060 | allowlist 命中 | `plugins list --detail` | `allowed=true` |
| W-061 | 未授权命令 | `rm -rf /` | 403 `command.not_allowed` |
| W-062 | 参数上限 | >8 个/单个 >128 | 拒绝 |
| W-063 | 控制字符 | `\0`/换行注入 | 拒绝 |
| W-064 | 执行关闭 | `allowCommandConsole=false` | 403 |
| W-065 | 执行开启但无宿主 | `health --json` | 501 `host.admin_command.unavailable` |
| W-066 | 无子进程 | 观察进程树 | 不启动 shell/子进程 |
| W-067 | 输出脱敏/截断 | fake runner | token 脱敏，64 KiB 截断 |

### Gate H：反向代理 / 审计 / 运维

| ID | 用例 | 预期 |
| --- | --- | --- |
| W-070 | trusted proxy `X-Forwarded-Proto: https` | Secure cookie |
| W-071 | untrusted proxy header | 忽略 |
| W-072 | 审计权限 | 目录 0700、文件 0600 |
| W-073 | 只读挂载 | health `audit=error`，不崩溃 |
| W-074 | 审计内容 | 无 token/secret/prompt/memory |
| W-075 | external 模式 | 不重复绑定，web status 正常 |
| W-076 | 公网暴露 | 未代理/未 TLS 时有明确提示，不默认开放 |

### Gate I：真实宿主/浏览器

| ID | 用例 | 预期 |
| --- | --- | --- |
| W-080 | 真实 `natter serve --health-port` | 插件加载，控制台监听 |
| W-081 | 浏览器登录 | 登录页正常；token 登录后 dashboard |
| W-082 | dashboard 刷新 | 数据刷新；无 JS 错误 |
| W-083 | 配置只读 | UI 显示“未启用/只读”，不伪装保存成功 |
| W-084 | 命令终端 | 显示不可用/501，不显示为已启用 |
| W-085 | 退出登录 | 回到登录页 |
| W-086 | 停止宿主 | 控制台停止，端口释放 |

---

## 4. 不在本轮范围

- 宿主导入 `IAdminConfigStore` / `IAdminCommandRunner` 后的真实保存/执行；
- 公网真实 TLS/域名；
- 多用户/viewer 角色；
- 审计轮转；
- HTTP/2/WebSocket；
- Android；
- 真实 Discord/DingTalk 上游业务链路（WebConsole 只做诊断）。

---

## 5. 证据

```text
artifacts/webconsole-test/<run>/
├── build.log
├── unit.trx
├── ui-smoke.json
├── staging.txt
├── web-status.json
├── curl/
│   ├── login.txt
│   ├── snapshot.json
│   ├── config-validate.json
│   ├── command-policy.json
│   └── audit.json
├── screenshots/
│   ├── login.png
│   └── dashboard.png
└── audit/
    └── webconsole-audit.jsonl
```

不提交 `WEBCONSOLE_TOKEN`、secret、用户数据。

---

## 6. 缺陷分级

| 级别 | 定义 | 例子 |
| --- | --- | --- |
| Blocker | 无认证可访问、任意文件读取、命令执行、secret 泄漏 | bind 公网、路径穿越、shell |
| High | dashboard 白屏、登录不可用、CSRF 失效、审计缺失 | BUG-001 类 |
| Medium | 501 语义错误、gap 展示错误、限速/ETag 问题 | Phase 2/3 降级 |
| Low | UI 文案/表头/文档 | — |

---

## 7. 签收

### Phase 1

- [ ] W-001 ~ W-004；
- [ ] W-010 ~ W-018；
- [ ] W-020 ~ W-028；
- [ ] W-030 ~ W-035；
- [ ] W-040 ~ W-048；
- [ ] W-070 ~ W-076；
- [ ] W-080 ~ W-086；
- [ ] 无 Blocker/High；
- [ ] 浏览器 dashboard 真实截图；
- [ ] 审计无 secret。

### Phase 2/3（仅插件侧）

- [ ] W-050 ~ W-059；
- [ ] W-060 ~ W-067；
- [ ] 真实保存/执行明确为 501，不伪装通过；
- [ ] 等宿主 G5/G6 后另开验收。

---

## 8. 一句话

**Phase 1 可以按只读控制台签；Phase 2/3 现在只能签“插件侧策略与降级”，不能签“配置写入/命令执行已交付”。**
